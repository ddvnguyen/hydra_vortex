import pytest
from unittest.mock import patch, AsyncMock, MagicMock

from coordinator.state_manager import StateManager
from coordinator.session_table import SessionTable


def make_rpc_mock():
    instance = MagicMock()
    instance.request = AsyncMock()
    instance.close = AsyncMock()
    return instance


@pytest.mark.asyncio
async def test_save_prefix_checkpoint():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "session_id": "prefix/system_prompt",
            "n_past": 512,
            "size": 50000000,
            "save_ms": 500,
        }

        meta = await sm.save_prefix_checkpoint(
            "system_prompt", "127.0.0.1", 9601, slot_id=0
        )

    assert meta["session_id"] == "prefix/system_prompt"
    assert meta["n_past"] == 512


@pytest.mark.asyncio
async def test_restore_prefix_checkpoint():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "session_id": "prefix/system_prompt",
            "slot_id": 0,
            "n_past": 512,
            "restore_ms": 800,
        }

        meta = await sm.restore_prefix_checkpoint(
            "system_prompt", "192.168.122.21", 9602, slot_id=0
        )

    assert meta["slot_id"] == 0
    assert meta["n_past"] == 512


@pytest.mark.asyncio
async def test_restore_prefix_checkpoint_not_found():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.side_effect = RuntimeError("RPC error (status=0x01): not_found")

        with pytest.raises(RuntimeError, match="not_found"):
            await sm.restore_prefix_checkpoint(
                "nonexistent", "127.0.0.1", 9601, slot_id=0
            )


@pytest.mark.asyncio
async def test_save_prefix_checkpoint_custom_name():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "session_id": "prefix/my_custom_checkpoint",
            "n_past": 256,
            "size": 25000000,
            "save_ms": 300,
        }

        meta = await sm.save_prefix_checkpoint(
            "my_custom_checkpoint", "127.0.0.1", 9601, slot_id=1
        )

    assert meta["session_id"] == "prefix/my_custom_checkpoint"
    assert meta["n_past"] == 256
