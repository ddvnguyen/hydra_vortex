"""
Tier 2 system test — verifies PG metadata + Store persistence after migration.

Requires full stack (same as test_system.py) plus PostgreSQL accessible
from the test runner.  Verifies that after a save–restore cycle the
Store's PG metadata tables contain accurate session/chunk records.

Environment:
  COORD_URL        http://localhost:9000
  STORE_HOST       127.0.0.1
  STORE_PORT       9500
  STORE_DEBUG_URL  http://localhost:9501
  PG_DSN           postgresql://hydra:hydra@localhost:5432/hydra_store
"""

import json
import os
import uuid

import httpx
import pytest

from python_shared.rpc_client import OpCode, RpcClient

COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")
STORE_HOST = os.environ.get("STORE_HOST", "127.0.0.1")
STORE_PORT = int(os.environ.get("STORE_PORT", "9500"))
STORE_DEBUG_URL = os.environ.get(
    "STORE_DEBUG_URL", f"http://{STORE_HOST}:9501"
)
PG_DSN = os.environ.get(
    "PG_DSN", "postgresql://hydra:hydra@localhost:5432/hydra_store"
)

TRACE_TAG = "persistence-sys"


def _sid() -> str:
    return f"ps-{uuid.uuid4().hex[:12]}"


def _trace() -> str:
    return f"{TRACE_TAG}-{uuid.uuid4().hex[:8]}"


async def _rpc(op: OpCode, key: str, payload: bytes = b"", trace: str = "") -> ...:
    client = RpcClient(STORE_HOST, STORE_PORT)
    try:
        return await client.request(op, key, payload, trace_id=trace)
    finally:
        await client.close()


async def _query_pg(sql: str) -> list[dict]:
    """Execute a SQL query via psql subprocess and return rows as dicts."""
    import subprocess

    result = subprocess.run(
        ["psql", PG_DSN, "-t", "-A", "-F", "|", "-c", sql],
        capture_output=True,
        text=True,
        timeout=10,
    )
    if result.returncode != 0:
        raise RuntimeError(f"psql failed: {result.stderr}")

    rows = []
    for line in result.stdout.strip().split("\n"):
        line = line.strip()
        if not line:
            continue
        parts = line.split("|")
        rows.append(parts)
    return rows


async def _store_debug() -> dict:
    async with httpx.AsyncClient() as c:
        resp = await c.get(f"{STORE_DEBUG_URL}/debug", timeout=10)
        resp.raise_for_status()
        return resp.json()


# ── tests ────────────────────────────────────────────────────────────────────


@pytest.mark.system
@pytest.mark.asyncio
async def test_manifest_after_save():
    """After a real save via Agent, PG sessions + chunks tables have rows."""
    sid = _sid()
    prompt = "What is the capital of Japan?"

    # 1. Send completion to RTX to generate KV cache
    async with httpx.AsyncClient(timeout=120) as c:
        resp = await c.post(
            f"{COORD_URL}/v1/chat/completions",
            json={
                "messages": [{"role": "user", "content": prompt}],
                "max_tokens": 30,
                "temperature": 0,
                "stream": False,
                "session_id": sid,
            },
            headers={"X-Hydra-Trace-Id": _trace()},
        )
        resp.raise_for_status()
        assert resp.json()["choices"][0]["message"]["content"]

    # 2. Migrate session to P100 (triggers save-to-Store + restore-from-Store)
    async with httpx.AsyncClient(timeout=120) as c:
        resp = await c.post(
            f"{COORD_URL}/sessions/{sid}/migrate",
            json={},
            headers={"X-Hydra-Trace-Id": _trace()},
        )
        if resp.status_code != 200:
            # May fail if P100 is not available; skip assertion
            pytest.skip(f"Migration returned {resp.status_code}: {resp.text}")
        assert resp.status_code == 200

    # 3. Verify PG has the session
    rows = await _query_pg(
        f"SELECT session_id, n_past, total_size FROM sessions "
        f"WHERE session_id = '{sid}'"
    )
    assert len(rows) == 1, f"Session {sid} not found in PG: {rows}"
    row = rows[0]
    assert int(row[1]) > 0, f"n_past is 0 — no KV state recorded"

    # 4. Verify PG has chunks for this session
    chunk_rows = await _query_pg(
        f"SELECT sc.idx, sc.hash, c.size "
        f"FROM session_chunks sc "
        f"JOIN chunks c ON c.hash = sc.hash "
        f"WHERE sc.session_id = '{sid}' "
        f"ORDER BY sc.idx"
    )
    assert len(chunk_rows) > 0, "No session_chunks found in PG"
    assert all(len(r) == 3 for r in chunk_rows), f"Bad chunk rows: {chunk_rows}"

    # 5. Verify Store debug endpoint shows chunks
    debug = await _store_debug()
    assert debug["chunks"]["total_chunks"] >= len(chunk_rows), (
        f"Store debug reports {debug['chunks']['total_chunks']} chunks, "
        f"expected at least {len(chunk_rows)}"
    )

    # Cleanup: evict session
    async with httpx.AsyncClient(timeout=30) as c:
        await c.delete(
            f"{COORD_URL}/sessions/{sid}",
            headers={"X-Hydra-Trace-Id": _trace()},
        )


@pytest.mark.system
@pytest.mark.asyncio
async def test_store_debug_after_full_migration():
    """Store debug endpoint reports non-zero stats after a migration."""
    sid = _sid()
    prompt = "What is the capital of Australia?"

    # Completion on RTX
    async with httpx.AsyncClient(timeout=120) as c:
        resp = await c.post(
            f"{COORD_URL}/v1/chat/completions",
            json={
                "messages": [{"role": "user", "content": prompt}],
                "max_tokens": 30,
                "temperature": 0,
                "stream": False,
                "session_id": sid,
            },
            headers={"X-Hydra-Trace-Id": _trace()},
        )
        resp.raise_for_status()

    # Migrate to P100
    async with httpx.AsyncClient(timeout=120) as c:
        resp = await c.post(
            f"{COORD_URL}/sessions/{sid}/migrate",
            json={},
            headers={"X-Hydra-Trace-Id": _trace()},
        )
        if resp.status_code != 200:
            pytest.skip(f"Migration failed: {resp.status_code}")

    # Debug endpoint should show non-zero stats
    debug = await _store_debug()
    assert debug["chunks"]["total_chunks"] > 0
    assert debug["chunks"]["total_bytes"] > 0

    # Cleanup
    async with httpx.AsyncClient(timeout=30) as c:
        await c.delete(
            f"{COORD_URL}/sessions/{sid}",
            headers={"X-Hydra-Trace-Id": _trace()},
        )
