import json

import pytest
from unittest.mock import patch, AsyncMock, MagicMock

from coordinator.health import HealthMonitor
from coordinator.config import WorkerNodeConfig
from coordinator.lib.rpc_client import OpCode


NODES = [
    WorkerNodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080"),
    WorkerNodeConfig(name="p100", host="192.168.122.21", rpc_port=9602, llama_url="http://192.168.122.21:8086"),
]


def make_rpc_mock():
    instance = MagicMock()
    instance.request = AsyncMock()
    instance.close = AsyncMock()
    return instance


@pytest.mark.asyncio
async def test_initial_state_unhealthy():
    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3)
    assert monitor.is_healthy("rtx") is False
    assert monitor.is_healthy("p100") is False


@pytest.mark.asyncio
async def test_healthy_after_successful_poll():
    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3)

    with patch("coordinator.health.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "node_name": "rtx",
            "slots_total": 2,
            "slots_idle": 1,
            "gpu_type": "rtx5060ti",
            "llama_url": "http://localhost:8080",
        }
        monitor._node_configs = {"rtx": NODES[0]}
        await monitor._poll_all()

    assert monitor.is_healthy("rtx") is True


@pytest.mark.asyncio
async def test_unhealthy_after_three_failures():
    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3)

    with patch("coordinator.health.RpcClient") as MockRpc:
        mock_instance = MagicMock()
        mock_instance.request = AsyncMock(side_effect=ConnectionError("refused"))
        mock_instance.close = AsyncMock()
        MockRpc.return_value = mock_instance
        monitor._node_configs = {"rtx": NODES[0]}

        for _ in range(3):
            await monitor._poll_all()

    assert monitor.is_healthy("rtx") is False


@pytest.mark.asyncio
async def test_recovery_after_failure():
    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3)

    with patch("coordinator.health.RpcClient") as MockRpc:
        mock_instance = MagicMock()
        mock_instance.request = AsyncMock(side_effect=ConnectionError("refused"))
        mock_instance.close = AsyncMock()
        MockRpc.return_value = mock_instance
        monitor._node_configs = {"rtx": NODES[0]}

        for _ in range(3):
            await monitor._poll_all()

        assert monitor.is_healthy("rtx") is False

        mock_instance.request = AsyncMock()
        mock_instance.request.return_value.status = 0
        mock_instance.request.return_value.meta = {
            "node_name": "rtx",
            "slots_total": 2,
            "slots_idle": 1,
            "gpu_type": "rtx5060ti",
            "llama_url": "http://localhost:8080",
        }
        await monitor._poll_all()

    assert monitor.is_healthy("rtx") is True


@pytest.mark.asyncio
async def test_get_node_info():
    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3)

    with patch("coordinator.health.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "node_name": "rtx",
            "slots_total": 2,
            "slots_idle": 1,
            "gpu_type": "rtx5060ti",
            "llama_url": "http://localhost:8080",
        }
        monitor._node_configs = {"rtx": NODES[0]}
        await monitor._poll_all()

    info = monitor.get_node_info("rtx")
    assert info is not None
    assert info.node_name == "rtx"
    assert info.slots_total == 2
    assert info.slots_idle == 1


@pytest.mark.asyncio
async def test_get_health_summary():
    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3)

    with patch("coordinator.health.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "node_name": "rtx",
            "slots_total": 2,
            "slots_idle": 1,
            "gpu_type": "rtx5060ti",
            "llama_url": "http://localhost:8080",
        }
        monitor._node_configs = {"rtx": NODES[0]}
        await monitor._poll_all()

    summary = monitor.get_health_summary()
    assert "rtx" in summary
    assert summary["rtx"]["healthy"] is True


@pytest.mark.asyncio
async def test_store_not_probed_when_unconfigured():
    # No store host → store treated as healthy, never probed.
    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3)
    assert monitor.is_store_healthy() is True


@pytest.mark.asyncio
async def test_store_healthy_after_successful_probe():
    monitor = HealthMonitor(
        NODES, poll_interval_s=1, max_failures=3,
        store_host="127.0.0.1", store_port=9500,
    )
    monitor._node_configs = {}  # isolate the store probe from agent polling
    with patch("coordinator.health.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()  # request() returns → store alive
        await monitor._poll_all()
    assert monitor.is_store_healthy() is True
    assert monitor.store_health()["consecutive_failures"] == 0


@pytest.mark.asyncio
async def test_store_unhealthy_after_three_failures():
    monitor = HealthMonitor(
        NODES, poll_interval_s=1, max_failures=3,
        store_host="127.0.0.1", store_port=9500,
    )
    monitor._node_configs = {}
    with patch("coordinator.health.RpcClient") as MockRpc:
        inst = MagicMock()
        inst.request = AsyncMock(side_effect=ConnectionError("refused"))
        inst.close = AsyncMock()
        MockRpc.return_value = inst
        for _ in range(3):
            await monitor._poll_all()
    assert monitor.is_store_healthy() is False


@staticmethod
def _make_health_payload(node_name: str, stuck_slots: int = 0, slots=None):
    if slots is None:
        slots = [{"id": 0, "n_past": 100, "is_processing": False, "n_remain": 0}]
    return json.dumps({
        "healthy": True,
        "node_name": node_name,
        "slots_total": len(slots),
        "slots_idle": sum(1 for s in slots if not s["is_processing"]),
        "stuck_slots": stuck_slots,
        "llama_url": "http://localhost:8080",
        "slots": slots,
    })


@staticmethod
def _make_health_resp(payload_json: str):
    resp = MagicMock()
    resp.status = 0
    resp.meta = {"component": "health"}
    resp.payload = payload_json.encode()
    return resp


@pytest.mark.asyncio
async def test_stuck_slot_suspect_tracking():
    from coordinator.worker_tracker import WorkerTracker
    tracker = WorkerTracker()
    tracker.init_worker("rtx")
    tracker.acquire("rtx", "decode")

    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3, tracker=tracker)

    stuck_slots = [
        {"id": 0, "n_past": 512, "is_processing": True, "n_remain": 0},
    ]
    payload = _make_health_payload("rtx", stuck_slots=1, slots=stuck_slots)
    resp = _make_health_resp(payload)

    with patch("coordinator.health.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        MockRpc.return_value.request.return_value = resp
        monitor._node_configs = {"rtx": NODES[0]}
        await monitor._poll_all()

    assert monitor._nodes["rtx"].stuck_slots == 1
    assert "rtx" in monitor._suspect
    assert monitor._suspect["rtx"]["baseline_n_past"] == 512
    assert monitor._suspect["rtx"]["stall_count"] == 0


@pytest.mark.asyncio
async def test_stuck_slot_recovery_after_max_stalls():
    from coordinator.worker_tracker import WorkerTracker
    from coordinator.health import MAX_STALL_CYCLES

    tracker = WorkerTracker()
    tracker.init_worker("rtx")
    tracker.acquire("rtx", "decode")

    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3, tracker=tracker)

    health_resp = _make_health_resp(
        _make_health_payload("rtx", stuck_slots=1,
                             slots=[{"id": 0, "n_past": 512, "is_processing": True, "n_remain": 0}]))

    erase_called = False
    erase_result = MagicMock()
    erase_result.status = 0
    erase_result.meta = {"erased": True}

    async def fake_request(op, key, trace_id=""):
        nonlocal erase_called
        if op == OpCode.SlotErase:
            erase_called = True
            return erase_result
        return health_resp

    with patch("coordinator.health.RpcClient") as MockRpc:
        instance = MagicMock()
        instance.request = AsyncMock(side_effect=fake_request)
        instance.close = AsyncMock()
        MockRpc.return_value = instance
        monitor._node_configs = {"rtx": NODES[0]}

        # First poll establishes suspect
        await monitor._poll_all()
        assert "rtx" in monitor._suspect
        assert monitor._suspect["rtx"]["stall_count"] == 0

        # Subsequent polls with no progress increment stall count
        for _ in range(MAX_STALL_CYCLES):
            await monitor._poll_all()

    assert "rtx" not in monitor._suspect
    assert monitor._nodes["rtx"].stuck_slots == 0
    assert erase_called, "SlotErase should have been called during recovery"


@pytest.mark.asyncio
async def test_stuck_slot_progress_resets_stall_count():
    from coordinator.worker_tracker import WorkerTracker

    tracker = WorkerTracker()
    tracker.init_worker("rtx")
    tracker.acquire("rtx", "decode")

    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3, tracker=tracker)

    def make_resp(slots):
        return _make_health_resp(_make_health_payload("rtx", stuck_slots=1, slots=slots))

    with patch("coordinator.health.RpcClient") as MockRpc:
        instance = MagicMock()
        instance.request = AsyncMock(return_value=make_resp([]))  # placeholder
        instance.close = AsyncMock()
        MockRpc.return_value = instance
        monitor._node_configs = {"rtx": NODES[0]}

        # Poll 1: stuck slot at n_past=512
        instance.request = AsyncMock(return_value=make_resp(
            [{"id": 0, "n_past": 512, "is_processing": True, "n_remain": 0}]))
        await monitor._poll_all()
        assert monitor._suspect["rtx"]["stall_count"] == 0
        assert monitor._suspect["rtx"]["baseline_n_past"] == 512

        # Poll 2: no progress → stall_count=1
        instance.request = AsyncMock(return_value=make_resp(
            [{"id": 0, "n_past": 512, "is_processing": True, "n_remain": 0}]))
        await monitor._poll_all()
        assert monitor._suspect["rtx"]["stall_count"] == 1

        # Poll 3: progress made → n_past increased → should reset
        instance.request = AsyncMock(return_value=make_resp(
            [{"id": 0, "n_past": 600, "is_processing": True, "n_remain": 0}]))
        await monitor._poll_all()
        assert monitor._suspect["rtx"]["stall_count"] == 0
        assert monitor._suspect["rtx"]["baseline_n_past"] == 600


@pytest.mark.asyncio
async def test_stuck_slot_cleared_when_no_longer_stuck():
    from coordinator.worker_tracker import WorkerTracker

    tracker = WorkerTracker()
    tracker.init_worker("rtx")
    tracker.acquire("rtx", "decode")

    monitor = HealthMonitor(NODES, poll_interval_s=1, max_failures=3, tracker=tracker)

    def make_resp(slots, stuck=1):
        return _make_health_resp(_make_health_payload("rtx", stuck_slots=stuck, slots=slots))

    with patch("coordinator.health.RpcClient") as MockRpc:
        instance = MagicMock()
        instance.request = AsyncMock()
        instance.close = AsyncMock()
        MockRpc.return_value = instance
        monitor._node_configs = {"rtx": NODES[0]}

        # Poll: stuck slot
        instance.request = AsyncMock(return_value=make_resp(
            [{"id": 0, "n_past": 512, "is_processing": True, "n_remain": 0}]))
        await monitor._poll_all()
        assert "rtx" in monitor._suspect

        # Poll: no longer stuck
        instance.request = AsyncMock(return_value=make_resp(
            [{"id": 0, "n_past": 600, "is_processing": False, "n_remain": 0}], stuck=0))
        await monitor._poll_all()
        assert "rtx" not in monitor._suspect
