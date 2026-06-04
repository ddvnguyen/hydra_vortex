"""
Tests for streaming completion, n_past guard, and migration.

DEPRECATED — these tested the old inline router path (route_request→proxy→n_past tracking),
which has been replaced by the WorkerScheduler. The scheduler unit tests cover these
paths. Keep for reference; remove when scheduler tests are mature.
"""
import pytest

pytest.skip("Rewrite needed for WorkerScheduler", allow_module_level=True)

from unittest.mock import patch, AsyncMock, MagicMock
from fastapi import FastAPI
from fastapi.testclient import TestClient

from coordinator.config import CoordinatorConfig, WorkerNodeConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router
from coordinator.routing import WORKER_MIXED


RTX = WorkerNodeConfig(
    name="rtx", host="10.0.0.1", rpc_port=9601, llama_url="http://rtx:8080",
    worker_type=WORKER_MIXED, slots=2, prefill_priority=1, decode_priority=1,
)
P100 = WorkerNodeConfig(
    name="p100", host="10.0.0.2", rpc_port=9602, llama_url="http://p100:8086",
    worker_type=WORKER_MIXED, slots=1, prefill_priority=2, decode_priority=2,
)


def _make_app():
    config = CoordinatorConfig(workers=[RTX, P100], run_mode="fast")
    table = SessionTable()
    health = HealthMonitor(config.workers)
    for info in health._nodes.values():
        info.healthy = True
        info.slots_total = 2
        info.slots_idle = 2
    sm = StateManager(table, "127.0.0.1", 9500)
    app = FastAPI()
    app.include_router(create_router(config, table, health, sm))
    app.state._table = table
    return app, table


def _decision(node="rtx", session_id="sess_x", n_past=0, found=False, slot_id=0):
    cfg = RTX if node == "rtx" else P100
    return RoutingDecision(
        node_name=node, node_config=cfg, slot_id=slot_id,
        action="route", session_id=session_id, session_found=found, n_past=n_past,
    )


async def _stream_with_usage(*_a, **_k):
    yield 'data: {"choices":[{"delta":{"content":"hi"}}]}\n\n'
    yield 'data: {"choices":[{"delta":{}}],"usage":{"total_tokens":150}}\n\n'
    yield "data: [DONE]\n\n"


def test_streaming_completion_updates_n_past():
    app, table = _make_app()
    client = TestClient(app)

    with patch("coordinator.router.route_request", return_value=_decision(slot_id=0)), \
         patch("coordinator.router._resolve_slot_id", new_callable=AsyncMock, return_value=0), \
         patch("coordinator.router.proxy_completion_stream", _stream_with_usage):
        resp = client.post("/v1/chat/completions", json={
            "messages": [{"role": "user", "content": "hello"}],
            "stream": True,
        })
        body = resp.text  # drain the stream so the generator's finally runs

    assert resp.status_code == 200
    assert "[DONE]" in body
    # n_past was updated from usage.total_tokens after the stream completed.
    entry = table.lookup("sess_x")
    assert entry is not None
    assert entry.n_past == 150


def test_n_past_guard_resets_and_erases_slot():
    app, table = _make_app()
    client = TestClient(app)
    table.register("sess_guard", "rtx", slot_id=0, n_past=5000)

    erase = AsyncMock()
    fake_rpc = MagicMock()
    fake_rpc.request = erase
    fake_rpc.close = AsyncMock()

    # Short prompt (estimated tokens << 5000*0.85) must trip the guard.
    with patch("coordinator.router.route_request",
               return_value=_decision(session_id="sess_guard", n_past=5000, found=True, slot_id=0)), \
         patch("coordinator.router.RpcClient", return_value=fake_rpc), \
         patch("coordinator.router._resolve_slot_id", new_callable=AsyncMock, return_value=0), \
         patch("coordinator.router.proxy_completion", new_callable=AsyncMock,
               return_value={"choices": [], "usage": {"total_tokens": 3}}):
        resp = client.post("/v1/chat/completions", json={
            "messages": [{"role": "user", "content": "hi"}],
            "stream": False,
        })

    assert resp.status_code == 200
    # The guard's observable effect is the SLOT_ERASE on the stale slot (so llama
    # re-prefills); n_past is reset to 0 and then re-set by this completion's usage.
    erase.assert_awaited()
    from python_shared.rpc_client import OpCode
    assert erase.await_args.args[0] == OpCode.SlotErase


def test_migrate_session_happy_path():
    app, table = _make_app()
    client = TestClient(app)
    table.register("sess_mig", "rtx", slot_id=0, n_past=2968)

    with patch.object(StateManager, "migrate_session", new_callable=AsyncMock,
                      return_value={"n_past": 2968, "restored": True}) as mig:
        resp = client.post("/sessions/sess_mig/migrate", json={"target_node": "p100"})

    assert resp.status_code == 200
    data = resp.json()
    assert data["migrated"] is True
    assert data["target"] == "p100"
    mig.assert_awaited_once()
