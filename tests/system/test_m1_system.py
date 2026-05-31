"""
M1 system test for Coordinator HTTP layer — routing, session affinity, migration.

Tests the full Coordinator HTTP interface with mocked RPC backends:
  POST /v1/chat/completions → routes to correct node, tracks sessions
  GET  /health              → aggregated health
  GET  /status              → sessions + routing stats
  DELETE /sessions/{id}     → evict
  POST /sessions/{id}/migrate → force migration

Requires no real services — RPC connections are mocked at the
RpcClient level so the Coordinator's routing/HTTP logic is
tested end-to-end.
"""

from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from coordinator.config import CoordinatorConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router
from coordinator.routing import RoutingDecision


# ── helpers ──────────────────────────────────────────────────────────────────


def _mock_rpc_response(status: int = 0, meta: dict | None = None, payload: bytes = b""):
    """Return a minimal RPC-like response object."""
    resp = MagicMock()
    resp.status = status
    resp.meta = meta or {}
    resp.payload = payload
    return resp


def _make_rpc_mock(responses: list | None = None):
    """Return an RpcClient mock whose request() returns responses in sequence."""
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
    cfg = CoordinatorConfig(
        host="127.0.0.1",
        port=0,
        rtx_host="127.0.0.1",
        rtx_port=9601,
        rtx_llama_url="http://localhost:8080",
        p100_host="127.0.0.1",
        p100_port=9602,
        p100_llama_url="http://192.168.122.21:8086",
        store_host="127.0.0.1",
        store_port=9500,
        health_poll_interval_s=9999,
        health_max_failures=3,
        long_prompt_threshold=4096,
        prefix_checkpoint_enabled=True,
        prefix_checkpoint_name="system_prompt",
    )
    return cfg


@pytest.fixture
def mock_rpc():
    """Patch RpcClient globally so no real connections are made."""
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
    health = HealthMonitor(cfg.nodes, poll_interval_s=9999)
    state_mgr = StateManager(table, cfg.store_host, cfg.store_port)
    app = FastAPI()
    router = create_router(cfg, table, health, state_mgr)
    app.include_router(router)
    app.state._config = cfg
    app.state._session_table = table
    app.state._health_monitor = health
    app.state._state_manager = state_mgr
    return app


@pytest.fixture
def client(app):
    return TestClient(app)


@pytest.fixture
def populated_session_table(app):
    """Pre-populate session table with a session for migration tests."""
    table: SessionTable = app.state._session_table
    table.register("sess_existing", "rtx", slot_id=0, n_past=512)
    table._sessions["sess_existing"].has_store_state = False
    return table


# ── /v1/chat/completions tests ──────────────────────────────────────────────


def test_completion_missing_messages_returns_422(client):
    resp = client.post("/v1/chat/completions", json={})
    assert resp.status_code == 422


def test_completion_basic_non_streaming(client, monkeypatch):
    """Send a completion, verify it's routed and a session is registered."""
    called = {"route": False}

    def fake_route(*args, **kwargs):
        called["route"] = True
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx",
            node_config=NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080", gpu_type="rtx5060ti"),
            slot_id=0,
            action="route",
            session_id="sess_test",
            session_found=False,
            n_past=0,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "Paris"}}], "usage": {"total_tokens": 15}, "hydra": {"trace_id": "test-trace", "node": "rtx", "proxy": "hydra-coordinator"}}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "What is the capital of France?"}], "stream": False},
    )
    assert resp.status_code == 200, resp.text
    data = resp.json()
    assert "choices" in data
    assert data["choices"][0]["message"]["content"] == "Paris"
    assert data["hydra"]["trace_id"] == "test-trace"

    # Session should be registered
    table: SessionTable = client.app.state._session_table
    assert table.active_count > 0


def test_completion_streaming(client, monkeypatch):
    """Streaming completion returns SSE stream."""
    def fake_route(*args, **kwargs):
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx",
            node_config=NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080", gpu_type="rtx5060ti"),
            slot_id=0,
            action="route",
            session_id="sess_stream",
            session_found=False,
            n_past=0,
        )

    async def fake_stream(*a, **kw):
        yield "data: hello\n\n"

    monkeypatch.setattr("coordinator.router.route_request", fake_route)
    monkeypatch.setattr("coordinator.router.proxy_completion_stream", fake_stream)

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "Hi"}], "stream": True},
    )
    assert resp.status_code == 200
    assert "text/event-stream" in resp.headers.get("content-type", "")
    assert resp.headers.get("x-hydra-node") == "rtx"


def test_completion_session_id_provided(client, monkeypatch):
    """Client-provided session_id overrides derivation."""
    route_args = {}

    def fake_route(**kwargs):
        route_args.update(kwargs)
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx",
            node_config=NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080", gpu_type="rtx5060ti"),
            slot_id=0,
            action="route",
            session_id="sess_client_provided",
            session_found=False,
            n_past=0,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "ok"}}]}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "hello"}], "session_id": "sess_client_provided", "stream": False},
    )
    assert resp.status_code == 200


def test_completion_no_healthy_nodes_returns_503(client, monkeypatch):
    def fake_route(*args, **kwargs):
        raise RuntimeError("No healthy nodes available")

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "hello"}], "stream": False},
    )
    assert resp.status_code == 503


# ── Session affinity tests ──────────────────────────────────────────────────


def test_multi_turn_session_affinity(client, monkeypatch):
    """Multiple turns on same session stick to the same node."""
    table: SessionTable = client.app.state._session_table
    calls = []

    def fake_route(**kwargs):
        sess_id = kwargs.get("session_id", "sess_affinity")
        entry = table.lookup(sess_id)
        n_past = entry.n_past if entry else 0
        slot_id = entry.slot_id if entry else 0
        calls.append(sess_id)
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx" if not entry else entry.node_name,
            node_config=NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080", gpu_type="rtx5060ti"),
            slot_id=slot_id or 0,
            action="route",
            session_id=sess_id,
            session_found=entry is not None,
            n_past=n_past,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "ok"}}]}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    # Turn 1
    client.post("/v1/chat/completions", json={"messages": [{"role": "user", "content": "hello"}], "stream": False})
    assert table.active_count == 1

    # Turn 2 - same session
    client.post("/v1/chat/completions", json={"messages": [{"role": "user", "content": "hello"}], "stream": False})
    assert table.active_count == 1


# ── Session migration via HTTP ──────────────────────────────────────────────


def test_migrate_session(client, populated_session_table):
    """POST /sessions/{id}/migrate triggers migration."""
    table: SessionTable = client.app.state._session_table
    state_mgr: StateManager = client.app.state._state_manager

    # Mock the state_manager's migrate_session
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

    # Session should be removed from table
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
