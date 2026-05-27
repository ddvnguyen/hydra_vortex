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
async def test_save_flow():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    table.register("sess_abc", "rtx", 0, n_past=100)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "session_id": "sess_abc",
            "n_past": 100,
            "size": 800000000,
            "store_ms": 1500,
        }

        meta = await sm.save_session("sess_abc", "127.0.0.1", 9601)

    assert meta["session_id"] == "sess_abc"
    entry = table.lookup("sess_abc")
    assert entry.has_store_state is True
    assert entry.slot_id is None
    instance.request.assert_called_once()


@pytest.mark.asyncio
async def test_restore_flow():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    table.register("sess_abc", "rtx", 0, n_past=100)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "session_id": "sess_abc",
            "slot_id": 0,
            "n_past": 100,
            "restore_ms": 2000,
        }

        meta = await sm.restore_session("sess_abc", "192.168.122.21", 9602)

    assert meta["slot_id"] == 0
    entry = table.lookup("sess_abc")
    assert entry.slot_id == 0
    assert entry.has_store_state is False


@pytest.mark.asyncio
async def test_migrate_flow():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    table.register("sess_abc", "rtx", 0, n_past=100)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        mock1 = make_rpc_mock()
        mock1.request = AsyncMock(side_effect=[
            MagicMock(
                status=0,
                meta={"session_id": "sess_abc", "n_past": 100, "size": 800000000, "store_ms": 1500},
            ),
            MagicMock(
                status=0,
                meta={"slot_id": 0, "erased": True},
            ),
            MagicMock(
                status=0,
                meta={"session_id": "sess_abc", "slot_id": 1, "n_past": 100, "restore_ms": 2000},
            ),
        ])
        MockRpc.return_value = mock1

        result = await sm.migrate_session(
            "sess_abc",
            "127.0.0.1", 9601,
            "192.168.122.21", 9602,
            "p100",
        )

    assert result.get("restore_ms") == 2000
    entry = table.lookup("sess_abc")
    assert entry.node_name == "p100"


@pytest.mark.asyncio
async def test_evict_lru():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    table.register("sess_a", "rtx", 0, n_past=50)
    table.register("sess_b", "rtx", 1, n_past=100)

    with patch("coordinator.state_manager.RpcClient") as MockRpc:
        MockRpc.return_value = make_rpc_mock()
        instance = MockRpc.return_value
        instance.request.return_value.status = 0
        instance.request.return_value.meta = {
            "session_id": "sess_a",
            "n_past": 50,
            "size": 400000000,
            "store_ms": 1000,
        }

        freed_slot = await sm.evict_lru("rtx", "127.0.0.1", 9601)

    assert freed_slot == 0
    entry = table.lookup("sess_a")
    assert entry.has_store_state is True
    assert entry.slot_id is None


@pytest.mark.asyncio
async def test_evict_lru_empty():
    table = SessionTable()
    sm = StateManager(table, "127.0.0.1", 9500)

    freed = await sm.evict_lru("rtx", "127.0.0.1", 9601)
    assert freed is None
