import pytest
from unittest.mock import patch, AsyncMock, MagicMock

from coordinator.health import HealthMonitor
from coordinator.config import WorkerNodeConfig


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
