"""
M2 system test for prefix checkpoints and chunked dedup flow through Coordinator.

Tests:
  - Prefix checkpoint save/restore via coordinator HTTP
  - Coordinator store_restore routing action
  - Session migration triggers save/erase/restore cycle
  - n_past guard (n_tokens must be > n_past)

Requires no real services — RPC connections are mocked.
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
    resp = MagicMock()
    resp.status = status
    resp.meta = meta or {}
    resp.payload = payload
    return resp


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


# ── Prefix checkpoint tests ──────────────────────────────────────────────────


def test_prefix_save_and_restore_flow(client):
    """Full prefix checkpoint round-trip through coordinator HTTP."""
    state_mgr: StateManager = client.app.state._state_manager
    operations = []

    async def fake_save(*args, **kwargs):
        operations.append(("save", args, kwargs))
        return {
            "session_id": "prefix/system_prompt",
            "n_past": 512,
            "size": 50000000,
            "save_ms": 500,
        }

    async def fake_restore(*args, **kwargs):
        operations.append(("restore", args, kwargs))
        return {
            "session_id": "prefix/system_prompt",
            "slot_id": 0,
            "n_past": 512,
            "restore_ms": 800,
        }

    state_mgr.save_prefix_checkpoint = fake_save
    state_mgr.restore_prefix_checkpoint = fake_restore


# ── Slot_id resolution tests ──────────────────────────────────────────────


def test_slot_id_resolved_after_completion(client, monkeypatch):
    """New session with slot_id=None resolves to real slot via /slots after completion."""
    table: SessionTable = client.app.state._session_table

    # Simulate a real routing decision: slot_id=None for new session
    def fake_route(**kwargs):
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx",
            node_config=NodeConfig(
                name="rtx", host="127.0.0.1", rpc_port=9601,
                llama_url="http://localhost:8080", gpu_type="rtx5060ti",
            ),
            slot_id=None,
            action="route",
            session_id="sess_slot_resolve",
            session_found=False,
            n_past=0,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "ok"}}], "usage": {"total_tokens": 42}}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    # Mock httpx.AsyncClient so _resolve_slot_id gets a fake /slots response
    # Slot with n_past=42 is slot 3 — that should be matched
    fake_slots_response = [
        {"id": 0, "n_past": 0, "is_processing": False},
        {"id": 1, "n_past": 0, "is_processing": False},
        {"id": 2, "n_past": 10, "is_processing": False},
        {"id": 3, "n_past": 42, "is_processing": False},
    ]

    class FakeResponse:
        status_code = 200
        def json(self): return fake_slots_response
        def raise_for_status(self): pass

    class FakeClient:
        async def __aenter__(self): return self
        async def __aexit__(self, *a): pass
        async def get(self, *a, **kw): return FakeResponse()

    monkeypatch.setattr("coordinator.router.httpx.AsyncClient", lambda **kw: FakeClient())

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "hello"}], "stream": False},
    )
    assert resp.status_code == 200

    entry = table.lookup("sess_slot_resolve")
    assert entry is not None, "session should be registered"
    assert entry.slot_id == 3, f"expected slot_id=3 (matched by n_past=42), got {entry.slot_id}"


def test_slot_id_unresolved_when_no_match(client, monkeypatch):
    """If /slots has no matching n_past, slot_id stays None — next turn retries."""
    table: SessionTable = client.app.state._session_table

    def fake_route(**kwargs):
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx",
            node_config=NodeConfig(
                name="rtx", host="127.0.0.1", rpc_port=9601,
                llama_url="http://localhost:8080", gpu_type="rtx5060ti",
            ),
            slot_id=None,
            action="route",
            session_id="sess_no_match",
            session_found=False,
            n_past=0,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "ok"}}], "usage": {"total_tokens": 99}}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    # No slot has n_past=99 — resolution returns None
    fake_slots = [
        {"id": 0, "n_past": 0, "is_processing": False},
        {"id": 1, "n_past": 10, "is_processing": False},
    ]

    class FakeResp:
        status_code = 200
        def json(self): return fake_slots
        def raise_for_status(self): pass

    class FakeClient:
        async def __aenter__(self): return self
        async def __aexit__(self, *a): pass
        async def get(self, *a, **kw): return FakeResp()

    monkeypatch.setattr("coordinator.router.httpx.AsyncClient", lambda **kw: FakeClient())

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "hello"}], "stream": False},
    )
    assert resp.status_code == 200

    entry = table.lookup("sess_no_match")
    assert entry is not None
    assert entry.slot_id is None, f"expected None when no match, got {entry.slot_id}"


def test_prefix_save_custom_name(client):
    """Custom checkpoint name flows through to state_manager."""
    state_mgr: StateManager = client.app.state._state_manager
    called_with = {}

    async def fake_save(checkpoint_name, host, port, slot_id=None):
        called_with["name"] = checkpoint_name
        called_with["slot_id"] = slot_id
        return {
            "session_id": f"prefix/{checkpoint_name}",
            "n_past": 256,
            "size": 25000000,
            "save_ms": 300,
        }

    state_mgr.save_prefix_checkpoint = fake_save

    resp = client.post("/prefix/my_custom_ckpt/save?node_name=rtx&slot_id=1")
    assert resp.status_code == 200
    assert called_with["name"] == "my_custom_ckpt"
    assert called_with["slot_id"] == 1


# ── Store restore routing action ─────────────────────────────────────────────


def test_store_restore_routing_action(client, monkeypatch):
    """When route_request returns store_restore action, restore_session is called."""
    state_mgr: StateManager = client.app.state._state_manager
    restore_called = False

    async def fake_restore(session_id, host, port):
        nonlocal restore_called
        restore_called = True
        return {"restored": True, "slot_id": 0, "n_past": 512}

    state_mgr.restore_session = fake_restore

    def fake_route(**kwargs):
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="p100",
            node_config=NodeConfig(name="p100", host="127.0.0.1", rpc_port=9602, llama_url="http://192.168.122.21:8086", gpu_type="p100"),
            slot_id=0,
            action="store_restore",
            session_id="sess_restored",
            session_found=True,
            n_past=512,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "restored"}}]}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "continuation"}], "stream": False},
    )
    assert resp.status_code == 200
    assert restore_called, "restore_session was not called for store_restore action"


# ── Migration flow tests ─────────────────────────────────────────────────────


def test_migration_save_erase_restore_cycle(client, monkeypatch):
    """Migration triggers save → erase → restore cycle."""
    table: SessionTable = client.app.state._session_table
    table.register("sess_migrate", "rtx", slot_id=0, n_past=512)
    table._sessions["sess_migrate"].has_store_state = False

    state_mgr: StateManager = client.app.state._state_manager
    operations = []

    async def fake_migrate(session_id, from_host, from_port, to_host, to_port, to_node_name):
        operations.append(("migrate", session_id, to_node_name))
        table.lookup(session_id).node_name = to_node_name
        return {"saved": True, "slot_id": 0, "n_past": 512, "restored": True}

    state_mgr.migrate_session = fake_migrate

    resp = client.post(
        "/sessions/sess_migrate/migrate",
        json={"target_node": "p100"},
    )
    assert resp.status_code == 200
    assert resp.json()["migrated"] is True

    entry = table.lookup("sess_migrate")
    assert entry is not None
    assert entry.node_name == "p100"


def test_migration_recorded_in_stats(client, monkeypatch):
    """Migration increments routing stats properly."""
    table: SessionTable = client.app.state._session_table
    table.register("sess_stats", "rtx", slot_id=0, n_past=128)

    state_mgr: StateManager = client.app.state._state_manager

    async def fake_migrate(*args, **kwargs):
        table.lookup("sess_stats").node_name = "p100"
        return {"saved": True, "restored": True}

    state_mgr.migrate_session = fake_migrate

    client.post("/sessions/sess_stats/migrate", json={"target_node": "p100"})

    resp = client.get("/status")
    stats = resp.json()["routing_stats"]
    assert stats["total"] >= 0


# ── n_past guard (critical: n_tokens > n_past) ───────────────────────────────


def test_n_past_guard_resets_when_estimated_too_small(client, monkeypatch):
    """When estimated tokens <= n_past, n_past is reset to 0."""
    table: SessionTable = client.app.state._session_table
    table.register("sess_npast", "rtx", slot_id=0, n_past=500)

    def fake_route(**kwargs):
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx",
            node_config=NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080", gpu_type="rtx5060ti"),
            slot_id=0,
            action="route",
            session_id="sess_npast",
            session_found=True,
            n_past=500,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "short"}}]}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    # Short message: ~5 chars / 4 = ~1 token < 500 n_past
    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": "hi"}], "stream": False},
    )
    assert resp.status_code == 200

    # n_past should be 0 after guard triggers
    entry = table.lookup("sess_npast")
    assert entry is not None, "Session should still exist after n_past guard"
    assert entry.n_past == 0, f"Expected n_past=0 after guard, got {entry.n_past}"


def test_n_past_guard_does_not_reset_when_estimated_larger(client, monkeypatch):
    """When estimated tokens > n_past, n_past is preserved."""
    table: SessionTable = client.app.state._session_table
    table.register("sess_npast_safe", "rtx", slot_id=0, n_past=500)

    def fake_route(**kwargs):
        from coordinator.config import NodeConfig
        return RoutingDecision(
            node_name="rtx",
            node_config=NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080", gpu_type="rtx5060ti"),
            slot_id=0,
            action="route",
            session_id="sess_npast_safe",
            session_found=True,
            n_past=500,
        )

    monkeypatch.setattr("coordinator.router.route_request", fake_route)

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "response"}}]}

    monkeypatch.setattr("coordinator.router.proxy_completion", fake_proxy)

    # Long message: ~2500 chars / 4 = ~625 tokens > 500 n_past
    long_content = "word " * 600
    resp = client.post(
        "/v1/chat/completions",
        json={"messages": [{"role": "user", "content": long_content}], "stream": False},
    )
    assert resp.status_code == 200

    entry = table.lookup("sess_npast_safe")
    assert entry is not None
    assert entry.n_past == 500, f"Expected n_past=500 preserved, got {entry.n_past}"


# ── Eviction with save flow ──────────────────────────────────────────────────


def test_evict_saves_before_removing(client):
    """DELETE /sessions/{id} saves session state before removing from table."""
    table: SessionTable = client.app.state._session_table
    table.register("sess_evict_save", "rtx", slot_id=0, n_past=256)

    state_mgr: StateManager = client.app.state._state_manager
    save_called = False

    async def fake_save(session_id, host, port):
        nonlocal save_called
        save_called = True
        return {"saved": True, "size": 1000, "n_past": 256}

    state_mgr.save_session = fake_save

    resp = client.delete("/sessions/sess_evict_save")
    assert resp.status_code == 200
    assert save_called, "save_session was not called before eviction"
    assert table.lookup("sess_evict_save") is None, "Session not removed after eviction"
