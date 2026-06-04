"""
Tests for concurrency-mode (P/D disaggregation) scheduling.

DEPRECATED — these tested the old inline path (route_request→proxy→save→restore→decode),
which has been replaced by the WorkerScheduler decision tree. The scheduler unit tests
in test_scheduler.py cover the new paths. Keep this file for reference; remove when
scheduler tests are mature.
"""
import pytest

pytest.skip("Rewrite needed for WorkerScheduler", allow_module_level=True)

from unittest.mock import patch, AsyncMock
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
    worker_type=WORKER_MIXED, slots=2, prefill_priority=1, decode_priority=2,
)
P100 = WorkerNodeConfig(
    name="p100", host="10.0.0.2", rpc_port=9602, llama_url="http://p100:8086",
    worker_type=WORKER_MIXED, slots=1, prefill_priority=2, decode_priority=1,
)


def _make_app(run_mode="concurrency"):
    config = CoordinatorConfig(workers=[RTX, P100], run_mode=run_mode)
    table = SessionTable()
    health = HealthMonitor(config.workers)
    for info in health._nodes.values():        # mark both workers healthy + idle
        info.healthy = True
        info.slots_total = 2
        info.slots_idle = 2
    sm = StateManager(table, "127.0.0.1", 9500)
    app = FastAPI()
    app.include_router(create_router(config, table, health, sm))
    app.state._table = table
    return app, table


def _prefill_decision():
    # New session, routed to the RTX prefill worker.
    return RoutingDecision(
        node_name="rtx", node_config=RTX, slot_id=0,
        action="route", session_id=None, session_found=False, n_past=0,
    )


async def _fake_decode_stream(*_args, **_kwargs):
    yield 'data: {"choices":[{"delta":{"content":"hi"}}],"usage":{"total_tokens":150}}\n\n'
    yield "data: [DONE]\n\n"


def test_concurrency_prefill_then_decode_on_other_worker():
    app, table = _make_app("concurrency")
    client = TestClient(app)

    with patch("coordinator.router.route_request", return_value=_prefill_decision()), \
         patch("coordinator.router.proxy_completion", new_callable=AsyncMock) as prefill, \
         patch("coordinator.router._resolve_slot_id", new_callable=AsyncMock, return_value=0), \
         patch.object(StateManager, "save_session", new_callable=AsyncMock, return_value={}) as save, \
         patch.object(StateManager, "restore_session", new_callable=AsyncMock, return_value={}) as restore, \
         patch("coordinator.router.proxy_completion_stream", _fake_decode_stream):
        prefill.return_value = {"usage": {"total_tokens": 100}}
        resp = client.post("/v1/chat/completions", json={
            "messages": [{"role": "user", "content": "a longer prompt to prefill"}],
            "stream": True,
        })

    assert resp.status_code == 200
    # Prefill ran (max_tokens=1, stream=False), KV was saved then restored elsewhere.
    prefill.assert_awaited()
    assert prefill.await_args.args[1]["max_tokens"] == 1
    assert prefill.await_args.args[1]["stream"] is False
    save.assert_awaited_once()
    restore.assert_awaited_once()
    # Decode happened on p100 (lower decode_priority), prefill on rtx.
    assert resp.headers["X-Hydra-Node"] == "p100"
    assert resp.headers["X-Hydra-Prefill-Node"] == "rtx"


def test_concurrency_prefill_failure_returns_503():
    app, _ = _make_app("concurrency")
    client = TestClient(app)

    with patch("coordinator.router.route_request", return_value=_prefill_decision()), \
         patch("coordinator.router.proxy_completion", new_callable=AsyncMock,
               side_effect=RuntimeError("prefill boom")):
        resp = client.post("/v1/chat/completions", json={
            "messages": [{"role": "user", "content": "hello"}],
            "stream": True,
        })

    assert resp.status_code == 503
    assert "Prefill failed" in resp.json()["detail"]


def test_concurrency_decode_restore_failure_returns_503():
    app, _ = _make_app("concurrency")
    client = TestClient(app)

    with patch("coordinator.router.route_request", return_value=_prefill_decision()), \
         patch("coordinator.router.proxy_completion", new_callable=AsyncMock,
               return_value={"usage": {"total_tokens": 100}}), \
         patch("coordinator.router._resolve_slot_id", new_callable=AsyncMock, return_value=0), \
         patch.object(StateManager, "save_session", new_callable=AsyncMock, return_value={}), \
         patch.object(StateManager, "restore_session", new_callable=AsyncMock,
                      side_effect=RuntimeError("restore boom")):
        resp = client.post("/v1/chat/completions", json={
            "messages": [{"role": "user", "content": "hello"}],
            "stream": True,
        })

    assert resp.status_code == 503
    assert "restore failed" in resp.json()["detail"].lower()
