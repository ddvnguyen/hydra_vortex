import pytest
from unittest.mock import AsyncMock, MagicMock

from coordinator.routing import verify_warm_slot, WORKER_MIXED
from coordinator.session_table import SessionEntry
from coordinator.config import WorkerNodeConfig


RTX_CFG = WorkerNodeConfig(
    name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080",
    worker_type=WORKER_MIXED, slots=2, prefill_priority=1, decode_priority=2,
    decode_speed_tps=200,
)


def mk_entry(slot_id: int = 0, n_past: int = 100, prefix_hash: str | None = None) -> SessionEntry:
    return SessionEntry(
        session_id="test", node_name="rtx",
        slot_id=slot_id, n_past=n_past, prefix_hash=prefix_hash,
    )


def _mock_slots(data: list) -> AsyncMock:
    """Create a mock AsyncClient where await client.get(url) returns a response with data."""
    mock_resp = AsyncMock()
    mock_resp.status_code = 200
    mock_resp.json = MagicMock(return_value=data)
    client = AsyncMock()
    client.get = AsyncMock(return_value=mock_resp)
    return client


@pytest.mark.asyncio
async def test_verify_warm_slot_returns_true():
    client = _mock_slots([
        {"id": 0, "state": 1, "n_past": 100},
        {"id": 1, "state": 0},
    ])
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=100), "trace", client)
    assert result is True


@pytest.mark.asyncio
async def test_verify_warm_slot_stale():
    client = _mock_slots([
        {"id": 0, "state": 1, "n_past": 50},
    ])
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=100), "trace", client)
    assert result is False


@pytest.mark.asyncio
async def test_verify_warm_slot_nonexistent():
    client = _mock_slots([
        {"id": 0, "state": 1, "n_past": 100},
    ])
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=3, n_past=50), "trace", client)
    assert result is False


@pytest.mark.asyncio
async def test_verify_warm_slot_http_failure():
    mock_resp = AsyncMock()
    mock_resp.status_code = 500
    client = AsyncMock()
    client.get = AsyncMock(return_value=mock_resp)
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=50), "trace", client)
    assert result is False


@pytest.mark.asyncio
async def test_verify_warm_slot_timeout():
    client = AsyncMock()
    client.get.side_effect = TimeoutError("connection timeout")
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=50), "trace", client)
    assert result is False


@pytest.mark.asyncio
async def test_verify_warm_slot_empty_response():
    client = _mock_slots([])
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=50), "trace", client)
    assert result is False


@pytest.mark.asyncio
async def test_verify_warm_slot_idle_slot():
    client = _mock_slots([
        {"id": 0, "state": 0},
    ])
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=0), "trace", client)
    assert result is True


@pytest.mark.asyncio
async def test_verify_warm_slot_stuck():
    client = _mock_slots([
        {"id": 0, "is_processing": True, "n_past": 50, "n_remain": 0},
    ])
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=50), "trace", client)
    assert result is False


@pytest.mark.asyncio
async def test_verify_warm_slot_prefix_mismatch():
    client = _mock_slots([
        {"id": 0, "state": 1, "n_past": 100, "prefix_hash": "abc123"},
    ])
    entry = mk_entry(slot_id=0, n_past=100, prefix_hash="different")
    result = await verify_warm_slot(RTX_CFG, entry, "trace", client)
    assert result is False


@pytest.mark.asyncio
async def test_verify_warm_slot_prefix_match():
    client = _mock_slots([
        {"id": 0, "state": 1, "n_past": 100, "prefix_hash": "abc123"},
    ])
    entry = mk_entry(slot_id=0, n_past=100, prefix_hash="abc123")
    result = await verify_warm_slot(RTX_CFG, entry, "trace", client)
    assert result is True


@pytest.mark.asyncio
async def test_verify_warm_slot_is_processing_key():
    """Slot with is_processing: true but n_remain > 0 is not stuck."""
    client = _mock_slots([
        {"id": 0, "is_processing": True, "n_past": 100, "n_remain": 10},
    ])
    result = await verify_warm_slot(RTX_CFG, mk_entry(slot_id=0, n_past=100), "trace", client)
    assert result is True
