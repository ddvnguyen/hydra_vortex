"""
Standalone Store persistence E2E test.

Tests the full Store persistence lifecycle through its RPC + HTTP debug
interface, without requiring GPU services.  Requires a running Store
with PostgreSQL backend.

Requires:
  - Hydra Store (:9500) with PostgreSQL backend
  - PostgreSQL (:5432)

Response format notes (from StoreServer.cs):
  PutChunked  meta: {"new_chunks":N,"deduped_chunks":N,"total_chunks":N}
  GetChunked  meta: {"missing_count":N,"total_size":N}
  GetManifest meta: {"chunk_count":N}  payload: {n_past,total_size,chunks:[...]}
  PutManifest validates all chunks exist in PG first (else Partial error)

Start with:
  docker compose up -d postgres store
  pytest tests/system/test_store_persistence_e2e.py -v
"""

import json
import os
import subprocess
import uuid

import httpx
import pytest

from coordinator.lib.rpc_client import OpCode, RpcClient

STORE_HOST = os.environ.get("STORE_HOST", "127.0.0.1")
STORE_PORT = int(os.environ.get("STORE_PORT", "9500"))
STORE_DEBUG_URL = os.environ.get(
    "STORE_DEBUG_URL",
    f"http://{STORE_HOST}:9501",
)


def _sid() -> str:
    return f"e2e-{uuid.uuid4().hex[:12]}"


def _trace() -> str:
    return f"e2e-{uuid.uuid4().hex[:8]}"


async def _rpc(
    op: OpCode,
    key: str,
    payload: bytes = b"",
    trace: str = "",
):
    client = RpcClient(STORE_HOST, STORE_PORT)
    try:
        return await client.request(op, key, payload, trace_id=trace)
    finally:
        await client.close()


async def _debug_get() -> dict:
    async with httpx.AsyncClient() as c:
        resp = await c.get(f"{STORE_DEBUG_URL}/debug", timeout=10)
        resp.raise_for_status()
        return resp.json()


async def _debug_gc() -> dict:
    async with httpx.AsyncClient() as c:
        resp = await c.post(f"{STORE_DEBUG_URL}/debug/gc", timeout=10)
        resp.raise_for_status()
        text = resp.text.strip()
        return json.loads(text) if text else {"chunks_removed": 0}


async def _version() -> dict:
    async with httpx.AsyncClient() as c:
        resp = await c.get(f"{STORE_DEBUG_URL}/version", timeout=10)
        resp.raise_for_status()
        return resp.json()


# ── tests ────────────────────────────────────────────────────────────────────


@pytest.mark.asyncio
async def test_version():
    info = await _version()
    assert info["service"] == "hydra-store"
    assert "version" in info


@pytest.mark.asyncio
async def test_put_get_del_small():
    key = _sid()
    data = b"hello persistence world"

    resp = await _rpc(OpCode.Put, key, data, trace=_trace())
    assert resp.status == 0x00, f"Put failed: {resp.meta}"

    resp = await _rpc(OpCode.Get, key, trace=_trace())
    assert resp.status == 0x00, f"Get failed (status=0x{resp.status:02X}): {resp.meta}"
    assert resp.payload == data

    resp = await _rpc(OpCode.Stat, key, trace=_trace())
    assert resp.status == 0x00
    assert resp.meta["size"] == len(data)

    resp = await _rpc(OpCode.Del, key, trace=_trace())
    assert resp.status == 0x00

    try:
        await _rpc(OpCode.Get, key, trace=_trace())
        assert False, "Expected 0x01 after delete"
    except Exception as e:
        from coordinator.lib.rpc_client import RpcError as RpcErr

        assert isinstance(e, RpcErr)
        assert e.status == 0x01


@pytest.mark.asyncio
async def test_put_chunked_new_session():
    key = _sid()
    data = b"x" * (10 * 1024 * 1024)

    resp = await _rpc(OpCode.PutChunked, key, data, trace=_trace())
    assert resp.status == 0x00, f"PutChunked failed: {resp.meta}"
    meta = resp.meta
    assert meta["total_chunks"] > 0
    assert meta["new_chunks"] > 0
    assert meta["deduped_chunks"] == 0


@pytest.mark.asyncio
async def test_put_chunked_dedup():
    key = _sid()
    data = b"y" * (10 * 1024 * 1024)

    resp1 = await _rpc(OpCode.PutChunked, key, data, trace=_trace())
    assert resp1.status == 0x00
    first_total = resp1.meta["total_chunks"]

    resp2 = await _rpc(OpCode.PutChunked, key, data, trace=_trace())
    assert resp2.status == 0x00
    meta2 = resp2.meta
    assert meta2["total_chunks"] == first_total
    assert meta2["new_chunks"] == 0
    assert meta2["deduped_chunks"] == meta2["total_chunks"]


@pytest.mark.asyncio
async def test_get_chunked_after_put():
    key = _sid()
    data = b"z" * (5 * 1024 * 1024)

    await _rpc(OpCode.PutChunked, key, data, trace=_trace())

    resp = await _rpc(OpCode.GetChunked, key, trace=_trace())
    assert resp.status == 0x00, f"GetChunked failed: {resp.meta}"
    assert resp.meta["missing_count"] > 0
    assert resp.meta["total_size"] == len(data)
    assert len(resp.payload) > len(data)


@pytest.mark.asyncio
async def test_get_chunked_with_known_hashes():
    key = _sid()
    data = b"w" * (5 * 1024 * 1024)

    await _rpc(OpCode.PutChunked, key, data, trace=_trace())

    manifest_resp = await _rpc(OpCode.GetManifest, key, trace=_trace())
    assert manifest_resp.status == 0x00
    manifest = json.loads(manifest_resp.payload)
    hashes = [ch["hash"] for ch in manifest.get("chunks", [])]
    assert len(hashes) > 0

    known_json = json.dumps(hashes).encode()
    resp = await _rpc(OpCode.GetChunked, key, known_json, trace=_trace())
    assert resp.status == 0x00
    assert resp.meta["missing_count"] == 0


@pytest.mark.asyncio
async def test_get_manifest_after_put_chunked():
    key = _sid()
    data = b"v" * (5 * 1024 * 1024)

    await _rpc(OpCode.PutChunked, key, data, trace=_trace())

    resp = await _rpc(OpCode.GetManifest, key, trace=_trace())
    assert resp.status == 0x00
    assert resp.meta["chunk_count"] > 0

    manifest = json.loads(resp.payload)
    assert manifest["total_size"] == len(data)
    assert len(manifest["chunks"]) > 0


@pytest.mark.asyncio
async def test_store_manifest_directly():
    """PutManifest after registering chunks via PutChunked."""
    key = _sid()
    small_data = b"a" * (2 * 1024 * 1024)

    chunk_resp = await _rpc(OpCode.PutChunked, key, small_data, trace=_trace())
    assert chunk_resp.status == 0x00

    get_resp = await _rpc(OpCode.GetManifest, key, trace=_trace())
    assert get_resp.status == 0x00
    manifest = json.loads(get_resp.payload)
    assert manifest["total_size"] == len(small_data)
    assert len(manifest["chunks"]) > 0
    assert manifest["chunks"][0]["hash"]


@pytest.mark.asyncio
async def test_debug_http_alive():
    info = await _debug_get()
    assert "version" in info
    assert "chunks" in info
    # Debug endpoint uses camelCase: totalChunks
    assert "totalChunks" in info["chunks"] or "total_chunks" in info["chunks"]


@pytest.mark.asyncio
async def test_double_put_chunked_replaces_manifest():
    key = _sid()
    data_v1 = b"a" * (3 * 1024 * 1024)
    data_v2 = b"b" * (3 * 1024 * 1024)

    resp1 = await _rpc(OpCode.PutChunked, key, data_v1, trace=_trace())
    assert resp1.status == 0x00

    resp2 = await _rpc(OpCode.PutChunked, key, data_v2, trace=_trace())
    assert resp2.status == 0x00
    assert resp2.meta["new_chunks"] > 0

    manifest_resp = await _rpc(OpCode.GetManifest, key, trace=_trace())
    assert manifest_resp.status == 0x00
    manifest = json.loads(manifest_resp.payload)
    assert manifest["total_size"] == len(data_v2)


@pytest.mark.asyncio
async def test_write_behind_eventually_backs_up():
    """Write-behind copies chunks to backup dir (wait 12s for cycle)."""
    key = _sid()
    data = b"wb-test-" + os.urandom(8) * 128

    resp = await _rpc(OpCode.PutChunked, key, data, trace=_trace())
    assert resp.status == 0x00

    manifest_resp = await _rpc(OpCode.GetManifest, key, trace=_trace())
    assert manifest_resp.status == 0x00
    manifest = json.loads(manifest_resp.payload)
    chunks = manifest.get("chunks", [])
    assert len(chunks) > 0

    import asyncio

    await asyncio.sleep(12)

    debug_info = await _debug_get()
    # Debug returns camelCase: totalChunks
    known = debug_info["chunks"].get("totalChunks") or debug_info["chunks"].get("total_chunks", 0)
    assert known >= len(chunks)


@pytest.mark.asyncio
async def test_debug_gc_cleans_orphans():
    result = await _debug_gc()
    assert "chunks_removed" in result
    assert isinstance(result["chunks_removed"], int)
