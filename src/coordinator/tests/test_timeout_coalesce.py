"""
Tests for #134 — prefill thrash loop fix.

DEPRECATED — the single-flight coalescing logic (_proxy_completion_coalesced)
was removed in the WorkerScheduler refactor. The configure_timeout test is still
valid. The timeout→504 path is now in the scheduler. Keep for reference; remove
when scheduler tests are mature.
"""
import pytest

pytest.skip("Rewrite needed for WorkerScheduler", allow_module_level=True)

from unittest.mock import patch, AsyncMock
import httpx
from fastapi import FastAPI
from fastapi.testclient import TestClient

import coordinator.proxy as proxy
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


def _make_app(run_mode="fast", timeout_s=1800):
    config = CoordinatorConfig(workers=[RTX, P100], run_mode=run_mode,
                               llama_request_timeout_s=timeout_s)
    table = SessionTable()
    health = HealthMonitor(config.workers)
    for info in health._nodes.values():
        info.healthy = True
        info.slots_total = 2
        info.slots_idle = 2
    sm = StateManager(table, "127.0.0.1", 9500)
    app = FastAPI()
    app.include_router(create_router(config, table, health, sm))
    return app, table


def _new_session_decision():
    return RoutingDecision(
        node_name="rtx", node_config=RTX, slot_id=0,
        action="route", session_id=None, session_found=False, n_past=0,
    )


# ── 1. configurable timeout ────────────────────────────────────────────────
def test_configure_timeout_sets_read_budget_and_resets_client():
    try:
        proxy.configure_timeout(1234)
        assert proxy._read_timeout_s == 1234.0
        assert proxy._http_client is None  # reset so the new budget takes effect
    finally:
        proxy.configure_timeout(1800)  # restore default for other tests


# ── 2. single-flight coalescing ────────────────────────────────────────────
@pytest.mark.asyncio
async def test_coalesces_identical_inflight_calls():
    router_mod._inflight_upstream.clear()
    started = asyncio.Event()
    release = asyncio.Event()
    calls = 0

    async def slow_completion(url, body, trace_id):
        nonlocal calls
        calls += 1
        started.set()
        await release.wait()
        return {"usage": {"total_tokens": 5}}

    with patch("coordinator.router.proxy_completion", new=slow_completion):
        body = {"messages": [{"role": "user", "content": "x"}], "max_tokens": 1}
        t1 = asyncio.ensure_future(_proxy_completion_coalesced("http://n", body, "t1"))
        await started.wait()                 # t1 is now in-flight + registered
        t2 = asyncio.ensure_future(_proxy_completion_coalesced("http://n", dict(body), "t2"))
        await asyncio.sleep(0)               # let t2 reach the coalesce branch
        release.set()
        r1, r2 = await asyncio.gather(t1, t2)

    assert calls == 1                        # one upstream call served both
    assert r1 == r2 == {"usage": {"total_tokens": 5}}
    assert router_mod._inflight_upstream == {}  # cleaned up


@pytest.mark.asyncio
async def test_distinct_bodies_are_not_coalesced():
    router_mod._inflight_upstream.clear()
    calls = 0

    async def completion(url, body, trace_id):
        nonlocal calls
        calls += 1
        return {"body": body}

    with patch("coordinator.router.proxy_completion", new=completion):
        await asyncio.gather(
            _proxy_completion_coalesced("http://n", {"messages": [{"role": "user", "content": "a"}]}, "t1"),
            _proxy_completion_coalesced("http://n", {"messages": [{"role": "user", "content": "b"}]}, "t2"),
        )

    assert calls == 2


# ── 3. timeout → 504 ───────────────────────────────────────────────────────
def test_read_timeout_maps_to_504():
    app, _ = _make_app("fast")
    client = TestClient(app, raise_server_exceptions=False)

    async def boom(*_a, **_k):
        raise httpx.ReadTimeout("read timed out")

    with patch("coordinator.router.route_request", return_value=_new_session_decision()), \
         patch("coordinator.router.proxy_completion", new=boom), \
         patch("coordinator.router._resolve_slot_id", new_callable=AsyncMock, return_value=0):
        resp = client.post("/v1/chat/completions", json={
            "messages": [{"role": "user", "content": "hi"}],
            "stream": False,
        })

    assert resp.status_code == 504
    assert "exceeded" in resp.json()["detail"]
