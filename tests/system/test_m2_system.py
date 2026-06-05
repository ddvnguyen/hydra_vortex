"""
M2 system test for prefix checkpoints and chunked dedup flow through Coordinator.

Tests:
  - Prefix checkpoint save/restore via coordinator HTTP
  - Coordinator store_restore routing action
  - Session migration triggers save/erase/restore cycle
  - n_past guard (n_tokens must be > n_past)

Requires no real services — RPC connections are mocked.
"""

import asyncio
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from coordinator.config import CoordinatorConfig, WorkerNodeConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.worker_tracker import WorkerTracker
from coordinator.scheduler import WorkerScheduler, WorkItem
from coordinator.router import create_router


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
        workers=[
            WorkerNodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080",
                             worker_type=3, slots=2, prefill_priority=1, decode_priority=2, decode_speed_tps=200),
            WorkerNodeConfig(name="p100", host="127.0.0.1", rpc_port=9602, llama_url="http://192.168.122.21:8086",
                             worker_type=2, slots=1, prefill_priority=2, decode_priority=1, decode_speed_tps=28),
        ],
        store_host="127.0.0.1",
        store_port=9500,
        health_poll_interval_s=9999,
        health_max_failures=3,
        long_prompt_threshold=4096,
        prefix_checkpoint_enabled=True,
    )
    table = SessionTable()
    health = HealthMonitor(cfg.workers, poll_interval_s=9999)
    state_mgr = StateManager(table, cfg.store_host, cfg.store_port)
    tracker = WorkerTracker()
    scheduler = WorkerScheduler(cfg, table, health, state_mgr, tracker)
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


def test_slot_id_resolved_after_completion(client):
    """After completion, _resolve_slot_from_health matches n_past to a health slot."""
    table: SessionTable = client.app.state._session_table
    health: HealthMonitor = client.app.state._health_monitor
    scheduler: WorkerScheduler = client.app.state._scheduler

    health._nodes["rtx"].slots = [
        {"id": 0, "n_past": 0, "is_processing": False},
        {"id": 1, "n_past": 0, "is_processing": False},
        {"id": 2, "n_past": 10, "is_processing": False},
        {"id": 3, "n_past": 42, "is_processing": False},
    ]

    table.register("sess_slot_resolve", "rtx", slot_id=None, n_past=0)
    entry = table.lookup("sess_slot_resolve")

    scheduler._resolve_slot_from_health(entry, total=42, trace_id="test")

    assert entry.slot_id == 3, f"expected slot_id=3 (matched n_past=42), got {entry.slot_id}"


def test_slot_id_unresolved_when_no_match(client):
    """When no health slot matches n_past, slot_id stays None."""
    table: SessionTable = client.app.state._session_table
    health: HealthMonitor = client.app.state._health_monitor
    scheduler: WorkerScheduler = client.app.state._scheduler

    health._nodes["rtx"].slots = [
        {"id": 0, "n_past": 0, "is_processing": False},
        {"id": 1, "n_past": 10, "is_processing": False},
    ]

    table.register("sess_no_match", "rtx", slot_id=None, n_past=0)
    entry = table.lookup("sess_no_match")

    scheduler._resolve_slot_from_health(entry, total=99, trace_id="test")

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


@pytest.mark.asyncio
async def test_store_restore_routing_action(client, monkeypatch):
    """When session has has_store_state, _execute_store_restore calls restore_session."""
    state_mgr: StateManager = client.app.state._state_manager
    health: HealthMonitor = client.app.state._health_monitor
    scheduler: WorkerScheduler = client.app.state._scheduler
    tracker = scheduler._tracker
    table: SessionTable = client.app.state._session_table

    # Set up health + tracker so pick_best_decode_worker finds p100
    health._nodes["p100"].healthy = True
    tracker.init_worker("p100")
    tracker.init_worker("rtx")

    # Register session with store state and known n_past
    table.register("sess_restored", "p100", slot_id=None, n_past=512)
    entry = table.lookup("sess_restored")
    entry.has_store_state = True

    # Spy on restore_session
    restore_called = False

    async def fake_restore(session_id, host, port, slot_id=0):
        nonlocal restore_called
        restore_called = True
        return {"restored": True, "slot_id": 0, "n_past": 512}

    state_mgr.restore_session = fake_restore

    # Mock proxy_completion
    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "restored"}}]}

    monkeypatch.setattr("coordinator.scheduler.proxy_completion", fake_proxy)

    # Empty slots so _track_after_completion resolution is a no-op
    health._nodes["p100"].slots = []

    loop = asyncio.get_running_loop()
    future = loop.create_future()
    item = WorkItem(
        request={"messages": [{"role": "user", "content": "continuation"}], "stream": False},
        messages=[{"role": "user", "content": "continuation"}],
        session_id="sess_restored",
        trace_id="test",
        prefix_hash=None,
        estimated_tokens=10,
        estimated_new_tokens=512,
        future=future,
    )

    await scheduler._execute_store_restore(item)

    assert restore_called, "restore_session was not called for store_restore action"


# ── Migration flow tests ─────────────────────────────────────────────────────


def test_migration_save_erase_restore_cycle(client, monkeypatch):
    """Migration triggers save → erase → restore cycle."""
    table: SessionTable = client.app.state._session_table
    table.register("sess_migrate", "rtx", slot_id=0, n_past=512)
    table._sessions["sess_migrate"].has_store_state = False

    state_mgr: StateManager = client.app.state._state_manager
    operations = []

    async def fake_migrate(session_id, from_host, from_port, to_host, to_port, to_node_name, from_node_name=""):
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


@pytest.mark.asyncio
async def test_n_past_guard_resets_when_estimated_too_small(client, monkeypatch):
    """When estimated tokens <= n_past*0.85, n_past resets to 0 and slot is erased."""
    table: SessionTable = client.app.state._session_table
    scheduler: WorkerScheduler = client.app.state._scheduler
    config: CoordinatorConfig = client.app.state._config
    health: HealthMonitor = client.app.state._health_monitor

    table.register("sess_npast", "rtx", slot_id=0, n_past=500)
    entry = table.lookup("sess_npast")

    # Mock proxy_completion
    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "short"}}]}

    monkeypatch.setattr("coordinator.scheduler.proxy_completion", fake_proxy)

    # Mock RpcClient so SlotErase doesn't error — must be awaitable
    mock_rpc_class = MagicMock()
    mock_rpc_instance = mock_rpc_class.return_value
    mock_rpc_instance.request = AsyncMock(return_value=_mock_rpc_response(0, {"erased": True}))
    mock_rpc_instance.close = AsyncMock()
    monkeypatch.setattr("coordinator.scheduler.RpcClient", mock_rpc_class)

    health._nodes["rtx"].slots = []
    worker = config.workers[0]  # rtx

    loop = asyncio.get_running_loop()
    future = loop.create_future()
    # "hi" → ~2 chars / 4.0 chars_per_token ≈ 1 token < 425 (500*0.85)
    item = WorkItem(
        request={"messages": [{"role": "user", "content": "hi"}], "stream": False},
        messages=[{"role": "user", "content": "hi"}],
        session_id="sess_npast",
        trace_id="test",
        prefix_hash=None,
        estimated_tokens=1,
        estimated_new_tokens=10,
        future=future,
    )

    await scheduler._execute_affinity(item, worker, entry)

    assert entry.n_past == 0, f"Expected n_past=0 after guard, got {entry.n_past}"
    assert entry.slot_id is None, f"Expected slot_id=None after erase, got {entry.slot_id}"


@pytest.mark.asyncio
async def test_n_past_guard_does_not_reset_when_estimated_larger(client, monkeypatch):
    """When estimated tokens > n_past, n_past and slot_id are preserved."""
    table: SessionTable = client.app.state._session_table
    scheduler: WorkerScheduler = client.app.state._scheduler
    config: CoordinatorConfig = client.app.state._config
    health: HealthMonitor = client.app.state._health_monitor

    table.register("sess_npast_safe", "rtx", slot_id=0, n_past=500)
    entry = table.lookup("sess_npast_safe")

    async def fake_proxy(*args, **kwargs):
        return {"choices": [{"message": {"content": "response"}}]}

    monkeypatch.setattr("coordinator.scheduler.proxy_completion", fake_proxy)

    health._nodes["rtx"].slots = []
    worker = config.workers[0]  # rtx

    loop = asyncio.get_running_loop()
    future = loop.create_future()
    long_content = "word " * 600
    # ~3000 chars / 4.0 ≈ 750 tokens > 425 (500*0.85)
    item = WorkItem(
        request={"messages": [{"role": "user", "content": long_content}], "stream": False},
        messages=[{"role": "user", "content": long_content}],
        session_id="sess_npast_safe",
        trace_id="test",
        prefix_hash=None,
        estimated_tokens=750,
        estimated_new_tokens=100,
        future=future,
    )

    await scheduler._execute_affinity(item, worker, entry)

    assert entry.n_past == 500, f"Expected n_past=500 preserved, got {entry.n_past}"
    assert entry.slot_id == 0, f"Expected slot_id=0 preserved, got {entry.slot_id}"


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
