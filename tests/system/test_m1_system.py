"""
M1 system test for Coordinator HTTP layer — routing, session affinity, migration.

Tests the full Coordinator HTTP interface with mocked scheduler backend:
  POST /v1/chat/completions → routes to correct node, tracks sessions
  GET  /health              → aggregated health
  GET  /status              → sessions + routing stats
  DELETE /sessions/{id}     → evict
  POST /sessions/{id}/migrate → force migration

Requires no real services — scheduler and RPC connections are mocked.
"""

from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient
from fastapi.responses import JSONResponse, StreamingResponse

from coordinator.config import CoordinatorConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router


# ── helpers ──────────────────────────────────────────────────────────────────


def _mock_rpc_response(status: int = 0, meta: dict | None = None, payload: bytes = b""):
    resp = MagicMock()
    resp.status = status
    resp.meta = meta or {}
    resp.payload = payload
    return resp


def _make_rpc_mock(responses: list | None = None):
    instance = MagicMock()
    instance.request = AsyncMock()
    if responses:
        instance.request.side_effect = responses
    else:
        instance.request.return_value = _mock_rpc_response(0, {"stored": True})
    instance.close = AsyncMock()
    instance.request_stream_body = AsyncMock()
    instance.request_stream_body.return_value = _mock_rpc_response(0, {"stored": True})
    return instance


def _make_app_config() -> CoordinatorConfig:
    """Create a config with test-friendly settings."""
    from coordinator.config import WorkerNodeConfig
    from coordinator.routing import WORKER_PREFILL, WORKER_MIXED
    cfg = CoordinatorConfig(
        host="127.0.0.1",
        port=0,
        workers=[
            WorkerNodeConfig(
                name="rtx",
                host="127.0.0.1",
                rpc_port=9601,
                llama_url="http://localhost:8080",
                worker_type=WORKER_MIXED,
                slots=2,
                prefill_priority=10,
                decode_priority=10,
                max_prefill_tokens=-1,
            ),
            WorkerNodeConfig(
                name="p100",
                host="127.0.0.1",
                rpc_port=9602,
                llama_url="http://192.168.122.21:8086",
                worker_type=WORKER_PREFILL,
                slots=1,
                prefill_priority=5,
                decode_priority=1,
                max_prefill_tokens=8000,
            ),
        ],
        store_host="127.0.0.1",
        store_port=9500,
        health_poll_interval_s=9999,
        health_max_failures=3,
        long_prompt_threshold=4096,
        prefix_checkpoint_enabled=True,
        prefix_checkpoint_name="system_prompt",
    )
    return cfg


class MockScheduler:
    def __init__(self, session_table: SessionTable):
        self._session_table = session_table
        self._queue = []
        self._tracker = MagicMock()
        self._tracker.status = MagicMock(return_value="ok")
        self._running = True

    async def submit(self, req, messages, session_id, max_tokens, prefix_hash=None):
        """Default mock: register session and return a simple non-streaming response."""
        self._session_table.register(session_id, "rtx", slot_id=0, n_past=0)
        return JSONResponse(
            content={
                "choices": [{"message": {"content": "Paris"}}],
                "usage": {"total_tokens": 15},
                "hydra": {"trace_id": "test-trace", "node": "rtx", "proxy": "hydra-coordinator"},
            },
            headers={"X-Hydra-Node": "rtx"},
        )

    async def start(self):
        pass

    async def stop(self):
        self._running = False


@pytest.fixture
def mock_rpc():
    def _mock_factory(*args, **kwargs):
        instance = MagicMock()
        instance.request = AsyncMock(return_value=_mock_rpc_response(0, {"stored": True}))
        instance.close = AsyncMock()
        instance.request_stream_body = AsyncMock(return_value=_mock_rpc_response(0, {"stored": True}))
        return instance

    patcher = patch("coordinator.health.RpcClient", new=_mock_factory)
    patcher.start()
    patcher2 = patch("coordinator.state_manager.RpcClient", new=_mock_factory)
    patcher2.start()
    yield
    patcher2.stop()
    patcher.stop()


@pytest.fixture
def app(mock_rpc):
    cfg = _make_app_config()
    table = SessionTable()
    health = HealthMonitor(cfg.workers, poll_interval_s=9999)
    state_mgr = StateManager(table, cfg.store_host, cfg.store_port)
    scheduler = MockScheduler(table)
    app = FastAPI()
    router = create_router(cfg, table, health, state_mgr, scheduler)
    app.include_router(router)
    app.state._config = cfg
    app.state._session_table = table
    app.state._health_monitor = health
    app.state._state_manager = state_mgr
    app.state._scheduler = scheduler
    return app


@pytest.fixture
def client(app):
    return TestClient(app)


@pytest.fixture
def populated_session_table(app):
    table: SessionTable = app.state._session_table
    table.register("sess_existing", "rtx", slot_id=0, n_past=512)
    table._sessions["sess_existing"].has_store_state = False
    return table


# ── /v1/chat/completions tests ──────────────────────────────────────────────


def test_completion_missing_messages_returns_422(client):
    resp = client.post("/v1/chat/completions", json={})
    assert resp.status_code == 422


def test_completion_basic_non_streaming(client):
    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "What is the capital of France?"}], "stream": False},
    )
    assert resp.status_code == 200, resp.text
    data = resp.json()
    assert "choices" in data
    assert data["choices"][0]["message"]["content"] == "Paris"
    assert data["hydra"]["trace_id"] == "test-trace"

    # Session registration is now handled inside scheduler.submit(),
    # which is mocked here — no active session expected at the HTTP layer.


def test_completion_streaming(client):
    """Streaming completion returns SSE stream."""
    async def fake_submit(**kwargs):
        async def gen():
            yield "data: hello\n\n"
        return StreamingResponse(gen(), media_type="text/event-stream", headers={"X-Hydra-Node": "rtx"})

    scheduler = client.app.state._scheduler
    scheduler.submit = fake_submit

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "Hi"}], "stream": True},
    )
    assert resp.status_code == 200
    assert "text/event-stream" in resp.headers.get("content-type", "")
    assert resp.headers.get("x-hydra-node") == "rtx"


def test_completion_session_id_provided(client):
    """Client-provided session_id is forwarded."""
    scheduler = client.app.state._scheduler
    captured = {}

    async def fake_submit(**kwargs):
        captured.update(kwargs)
        return JSONResponse(content={"choices": [{"message": {"content": "ok"}}]})

    scheduler.submit = fake_submit

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "hello"}], "session_id": "sess_client_provided", "stream": False},
    )
    assert resp.status_code == 200
    assert captured.get("session_id") == "sess_client_provided"


def test_completion_no_healthy_nodes_returns_503(client):
    async def fake_submit(**kwargs):
        from fastapi import HTTPException
        raise HTTPException(status_code=503, detail="No healthy nodes available")

    scheduler = client.app.state._scheduler
    scheduler.submit = fake_submit

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "hello"}], "stream": False},
    )
    assert resp.status_code == 503


# ── Session affinity tests ──────────────────────────────────────────────────


def test_multi_turn_session_affinity(client):
    """Multiple turns on same session get routed through same scheduler."""
    table: SessionTable = client.app.state._session_table
    call_count = 0

    async def fake_submit(**kwargs):
        nonlocal call_count
        call_count += 1
        return JSONResponse(content={"choices": [{"message": {"content": "ok"}}]})

    scheduler = client.app.state._scheduler
    scheduler.submit = fake_submit

    client.post("/v1/chat/completions", json={"messages": [{"role": "user", "content": "hello"}], "stream": False})
    assert call_count == 1

    client.post("/v1/chat/completions", json={"messages": [{"role": "user", "content": "hello"}], "stream": False})
    assert call_count == 2


# ── Session migration via HTTP ──────────────────────────────────────────────


def test_migrate_session(client, populated_session_table):
    table: SessionTable = client.app.state._session_table
    state_mgr: StateManager = client.app.state._state_manager

    async def fake_migrate(*args, **kwargs):
        table.lookup("sess_existing").node_name = "p100"
        return {"saved": True, "restored": True}

    state_mgr.migrate_session = fake_migrate

    resp = client.post(
        "/sessions/sess_existing/migrate",
        json={"target_node": "p100"},
    )
    assert resp.status_code == 200
    data = resp.json()
    assert data["migrated"] is True
    assert data["target"] == "p100"


def test_migrate_session_not_found(client):
    resp = client.post("/sessions/nonexistent/migrate", json={"target_node": "p100"})
    assert resp.status_code == 404


def test_migrate_session_invalid_target(client, populated_session_table):
    resp = client.post("/sessions/sess_existing/migrate", json={"target_node": "nonexistent"})
    assert resp.status_code == 400


# ── Session eviction via HTTP ────────────────────────────────────────────────


def test_evict_session(client, populated_session_table):
    resp = client.delete("/sessions/sess_existing")
    assert resp.status_code == 200
    data = resp.json()
    assert data["evicted"] is True

    table: SessionTable = client.app.state._session_table
    assert table.lookup("sess_existing") is None


def test_evict_session_not_found(client):
    resp = client.delete("/sessions/nonexistent")
    assert resp.status_code == 404


# ── Health / Status endpoints ────────────────────────────────────────────────


def test_health_returns_ok(client):
    resp = client.get("/health")
    assert resp.status_code == 200
    data = resp.json()
    assert "status" in data
    assert "nodes" in data
    assert "rtx" in data["nodes"]
    assert "p100" in data["nodes"]


def test_status_returns_sessions_and_routing_stats(client):
    resp = client.get("/status")
    assert resp.status_code == 200
    data = resp.json()
    assert "uptime_s" in data
    assert "sessions" in data
    assert "routing_stats" in data
    assert "nodes" in data


def test_status_with_registered_session(client):
    table: SessionTable = client.app.state._session_table
    table.register("sess_status", "rtx", slot_id=0, n_past=128)

    resp = client.get("/status")
    data = resp.json()
    sessions = data["sessions"]["sessions"]
    assert any(s["session_id"] == "sess_status" for s in sessions)
    assert data["sessions"]["active"] >= 1


def test_list_sessions(client):
    table: SessionTable = client.app.state._session_table
    table.register("sess_list_1", "rtx", slot_id=0)
    table.register("sess_list_2", "p100", slot_id=0)

    resp = client.get("/sessions")
    assert resp.status_code == 200
    data = resp.json()
    session_ids = {s["session_id"] for s in data["sessions"]}
    assert "sess_list_1" in session_ids
    assert "sess_list_2" in session_ids


# ── Prefix Checkpoint endpoints ─────────────────────────────────────────────


def test_prefix_save(client):
    """POST /prefix/{name}/save triggers save_prefix_checkpoint."""
    state_mgr: StateManager = client.app.state._state_manager
    called = {"save": False}

    async def fake_save(*args, **kwargs):
        called["save"] = True
        return {"session_id": "prefix/system_prompt", "n_past": 512, "size": 50000000, "save_ms": 500}

    state_mgr.save_prefix_checkpoint = fake_save

    resp = client.post("/prefix/system_prompt/save?node_name=rtx&slot_id=0")
    assert resp.status_code == 200
    data = resp.json()
    assert data["saved"] is True
    assert called["save"] is True


def test_prefix_restore(client):
    """POST /prefix/{name}/restore triggers restore_prefix_checkpoint."""
    state_mgr: StateManager = client.app.state._state_manager
    called = {"restore": False}

    async def fake_restore(*args, **kwargs):
        called["restore"] = True
        return {"session_id": "prefix/system_prompt", "slot_id": 0, "n_past": 512, "restore_ms": 800}

    state_mgr.restore_prefix_checkpoint = fake_restore

    resp = client.post("/prefix/system_prompt/restore?node_name=p100&slot_id=0")
    assert resp.status_code == 200
    data = resp.json()
    assert data["restored"] is True
    assert called["restore"] is True


def test_prefix_invalid_node(client):
    resp = client.post("/prefix/test/save?node_name=nonexistent")
    assert resp.status_code == 400
