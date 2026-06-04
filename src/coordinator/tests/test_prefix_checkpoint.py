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
async def test_warmup_and_save_prefix():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    with patch("coordinator.state_manager.warmup_prefix", new=AsyncMock(return_value=45)) as mock_warm, \
         patch.object(sm, "_resolve_warm_slot", new=AsyncMock(return_value=2)), \
         patch.object(sm, "save_prefix_checkpoint", new=AsyncMock(return_value={"n_past": 45})) as mock_save:

        n_past = await sm.warmup_and_save_prefix(
            "abc123",
            "You are a helpful assistant.",
            "127.0.0.1",
            9601,
            "http://127.0.0.1:8080",
        )

    assert n_past == 45
    mock_warm.assert_awaited_once()
    # checkpoint saved with the resolved warm slot, not slot 0
    mock_save.assert_awaited_once_with("abc123", "127.0.0.1", 9601, slot_id=2)


@pytest.mark.asyncio
async def test_warmup_and_save_prefix_zero_npast_raises():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    with patch("coordinator.state_manager.warmup_prefix", new=AsyncMock(return_value=0)), \
         patch.object(sm, "save_prefix_checkpoint", new=AsyncMock()) as mock_save:
        with pytest.raises(RuntimeError, match="n_past=0"):
            await sm.warmup_and_save_prefix(
                "abc123", "sys", "127.0.0.1", 9601, "http://127.0.0.1:8080",
            )
    mock_save.assert_not_awaited()


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
