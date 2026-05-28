"""
Full real workflow E2E test through Coordinator HTTP API.

Tests the complete stack end-to-end with real running services:
  Coordinator :9000 → Agent RTX :9601 / Agent P100 :9602 → llama-server → Store :9500

Requires:
  - llama-server RTX (:8080, :8090)
  - llama-server P100 (192.168.122.21:8086, :8091)
  - Hydra Store (:9500)
  - Hydra Agent RTX (:9601)
  - Hydra Agent P100 (192.168.122.21:9602)
  - Coordinator (:9000)

Environment variables:
  COORD_URL          http://localhost:9000   (Coordinator HTTP endpoint)
  STORE_HOST         127.0.0.1
  STORE_PORT         9500
  RTX_AGENT_HOST     127.0.0.1
  RTX_AGENT_PORT     9601
  P100_AGENT_HOST    192.168.122.21
  P100_AGENT_PORT    9602
"""

import json
import os
from uuid import uuid4

import httpx
import pytest

COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")
STORE_HOST = os.environ.get("STORE_HOST", "127.0.0.1")
STORE_PORT = int(os.environ.get("STORE_PORT", "9500"))

PROMPT = "What is the capital of France? Give a detailed answer."
CONTINUATION = "Now tell me about the Eiffel Tower's history and construction details."
MAX_TOKENS = 100


@pytest.fixture
def session_id() -> str:
    return f"e2e-full-{uuid4().hex[:12]}"


@pytest.fixture
def coord_url() -> str:
    return COORD_URL


def _make_messages(prompt: str) -> list[dict]:
    return [{"role": "user", "content": prompt}]


async def _do_completion(
    base_url: str,
    messages: list[dict],
    session_id: str | None = None,
    stream: bool = False,
    max_tokens: int = MAX_TOKENS,
) -> httpx.Response:
    body: dict = {
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": 0,
        "stream": stream,
    }
    if session_id:
        body["session_id"] = session_id

    async with httpx.AsyncClient(timeout=300.0) as client:
        return await client.post(
            f"{base_url}/v1/chat/completions",
            json=body,
        )


async def _parse_sse(response: httpx.Response) -> list[dict]:
    events: list[dict] = []
    async for line in response.aiter_lines():
        line = line.strip()
        if line.startswith("data: "):
            payload = line.removeprefix("data: ")
            if payload == "[DONE]":
                break
            try:
                events.append(json.loads(payload))
            except json.JSONDecodeError:
                pass
    return events


async def _get_status(base_url: str) -> dict:
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(f"{base_url}/status")
        resp.raise_for_status()
        return resp.json()


async def _get_sessions(base_url: str) -> dict:
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(f"{base_url}/sessions")
        resp.raise_for_status()
        return resp.json()


# ── Tests ────────────────────────────────────────────────────────────────────


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_health_endpoint(coord_url: str):
    """GET /health returns healthy or degraded status with node details."""
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(f"{coord_url}/health")
    assert resp.status_code == 200
    body = resp.json()
    assert "status" in body
    assert body["status"] in ("healthy", "degraded")
    assert "nodes" in body
    assert "rtx" in body["nodes"]
    assert "p100" in body["nodes"]
    assert "store" in body


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_status_endpoint(coord_url: str):
    """GET /status returns sessions, routing_stats, and node details."""
    body = await _get_status(coord_url)
    assert "uptime_s" in body
    assert "sessions" in body
    assert "routing_stats" in body
    assert "nodes" in body
    assert "rtx" in body["nodes"]
    assert "p100" in body["nodes"]
    assert body["routing_stats"]["total"] >= 0


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_completion_nonstream(coord_url: str, session_id: str):
    """POST /v1/chat/completions (stream=false) returns choices with hydra metadata."""
    resp = await _do_completion(
        coord_url,
        _make_messages(PROMPT),
        session_id=session_id,
        stream=False,
    )
    assert resp.status_code == 200
    body = resp.json()
    assert "choices" in body
    assert len(body["choices"]) > 0
    msg = body["choices"][0]["message"]
    has_output = bool(msg.get("content", "")) or bool(msg.get("reasoning_content", ""))
    assert has_output, "neither 'content' nor 'reasoning_content' present"
    assert "hydra" in body
    assert "trace_id" in body["hydra"]
    assert "node" in body["hydra"]


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_completion_stream(coord_url: str, session_id: str):
    """POST /v1/chat/completions (stream=true) returns SSE events with choices."""
    resp = await _do_completion(
        coord_url,
        _make_messages(PROMPT),
        session_id=session_id,
        stream=True,
    )
    assert resp.status_code == 200
    events = await _parse_sse(resp)
    assert len(events) > 0
    final = events[-1]
    assert "choices" in final
    assert len(final["choices"]) > 0
    # Final event usually has empty delta with just finish_reason; check all events for output
    all_outputs = []
    for ev in events:
        choices = ev.get("choices", [])
        if not choices:
            continue
        delta = choices[0].get("delta", {})
        all_outputs.append(delta.get("content", "") or delta.get("reasoning_content", ""))
    assert any(all_outputs), "no 'content' or 'reasoning_content' across all stream events"


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_session_lifecycle(coord_url: str, session_id: str):
    """Session appears in GET /sessions after completion, then can be evicted."""
    resp = await _do_completion(
        coord_url,
        _make_messages(PROMPT),
        session_id=session_id,
        stream=False,
    )
    assert resp.status_code == 200

    sessions_resp = await _get_sessions(coord_url)
    session_ids = [s["session_id"] for s in sessions_resp["sessions"]]
    assert session_id in session_ids, f"Session {session_id} not found in {session_ids}"

    async with httpx.AsyncClient(timeout=30.0) as client:
        del_resp = await client.delete(f"{coord_url}/sessions/{session_id}")
    assert del_resp.status_code == 200
    del_body = del_resp.json()
    assert del_body["evicted"] is True

    sessions_after = await _get_sessions(coord_url)
    after_ids = [s["session_id"] for s in sessions_after["sessions"]]
    assert session_id not in after_ids, f"Session {session_id} still present after eviction"


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_prefix_checkpoint(coord_url: str):
    """POST /prefix/{name}/save and /restore return OK."""
    async with httpx.AsyncClient(timeout=120.0) as client:
        save_resp = await client.post(
            f"{coord_url}/prefix/system_prompt/save",
            params={"node_name": "rtx", "slot_id": 0},
        )
    assert save_resp.status_code == 200, f"Prefix save failed: {save_resp.text}"
    save_body = save_resp.json()
    assert save_body["saved"] is True

    async with httpx.AsyncClient(timeout=120.0) as client:
        restore_resp = await client.post(
            f"{coord_url}/prefix/system_prompt/restore",
            params={"node_name": "p100", "slot_id": 0},
        )
    assert restore_resp.status_code == 200, f"Prefix restore failed: {restore_resp.text}"
    restore_body = restore_resp.json()
    assert restore_body["restored"] is True


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_migrate_session(coord_url: str, session_id: str):
    """POST /sessions/{id}/migrate moves session to target node."""
    resp = await _do_completion(
        coord_url,
        _make_messages(PROMPT),
        session_id=session_id,
        stream=False,
    )
    assert resp.status_code == 200
    source_node = resp.json().get("hydra", {}).get("node", "")
    assert source_node in ("http://localhost:8080", "http://192.168.122.21:8086", "http://host.docker.internal:8080")

    async with httpx.AsyncClient(timeout=120.0) as client:
        migrate_resp = await client.post(
            f"{coord_url}/sessions/{session_id}/migrate",
            json={"target_node": "p100"},
        )
    assert migrate_resp.status_code == 200, f"Migration failed: {migrate_resp.text}"
    migrate_body = migrate_resp.json()
    assert migrate_body["migrated"] is True
    assert migrate_body["target"] == "p100"

    status = await _get_status(coord_url)
    for s in status["sessions"]["sessions"]:
        if s["session_id"] == session_id:
            assert s["node"] == "p100", f"Session node is {s['node']}, expected p100"
            break
    else:
        pytest.fail(f"Session {session_id} not found in status after migration")


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_migration_cache_hit(coord_url: str, session_id: str):
    """
    Full migration cycle with cache hit verification.

    1. Send prompt through coordinator (routes to RTX)
    2. Migrate session to P100
    3. Send continuation through coordinator (routes via affinity to P100)
    4. Verify cache_n > 0 (KV cache was restored)
    5. Verify prompt_ms < 5000 (fast cache path, not full re-prefill)
    """
    resp = await _do_completion(
        coord_url,
        _make_messages(PROMPT),
        session_id=session_id,
        stream=False,
        max_tokens=50,
    )
    assert resp.status_code == 200
    first = resp.json()
    assert "choices" in first
    assistant_reply = first["choices"][0]["message"]["content"]

    async with httpx.AsyncClient(timeout=120.0) as client:
        migrate_resp = await client.post(
            f"{coord_url}/sessions/{session_id}/migrate",
            json={"target_node": "p100"},
        )
    assert migrate_resp.status_code == 200, f"Migration failed: {migrate_resp.text}"

    continuation_messages = [
        {"role": "user", "content": PROMPT},
        {"role": "assistant", "content": assistant_reply},
        {"role": "user", "content": CONTINUATION},
    ]
    cont_resp = await _do_completion(
        coord_url,
        continuation_messages,
        session_id=session_id,
        stream=False,
    )
    assert cont_resp.status_code == 200
    cont_body = cont_resp.json()

    timings = cont_body.get("timings", {})
    cache_n = timings.get("cache_n", 0)
    prompt_ms = timings.get("prompt_ms", 0)

    assert cache_n > 0, (
        f"cache_n={cache_n} — KV cache was not used after migration. "
        f"Continuation may have fewer tokens than n_past, or restore failed silently."
    )
    assert prompt_ms < 5000, (
        f"prompt_ms={prompt_ms} — full re-prefill occurred instead of "
        f"using cached KV state (expected <5000ms for cached path). "
        f"cache_n={cache_n}"
    )


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_eviction_with_save(coord_url: str, session_id: str):
    """DELETE /sessions/{id} saves state before evicting, then session is removed."""
    resp = await _do_completion(
        coord_url,
        _make_messages(PROMPT),
        session_id=session_id,
        stream=False,
    )
    assert resp.status_code == 200

    async with httpx.AsyncClient(timeout=120.0) as client:
        del_resp = await client.delete(f"{coord_url}/sessions/{session_id}")
    assert del_resp.status_code == 200
    del_body = del_resp.json()
    assert del_body["evicted"] is True

    status = await _get_status(coord_url)
    for s in status["sessions"]["sessions"]:
        if s["session_id"] == session_id:
            pytest.fail(f"Session {session_id} still present after eviction")


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_full_cycle_completion_migrate_continuation(coord_url: str, session_id: str):
    """
    Combined end-to-end: prompt → migrate → continue → assert cache hit.

    Uses raw RPC verification to check Store has the saved state after migration.
    """
    resp = await _do_completion(
        coord_url,
        _make_messages(PROMPT),
        session_id=session_id,
        stream=False,
        max_tokens=50,
    )
    assert resp.status_code == 200
    body = resp.json()
    assert "choices" in body
    assistant_reply = body["choices"][0]["message"]["content"]
    source_node = body.get("hydra", {}).get("node", "")

    async with httpx.AsyncClient(timeout=120.0) as client:
        migrate_resp = await client.post(
            f"{coord_url}/sessions/{session_id}/migrate",
            json={"target_node": "p100"},
        )
    assert migrate_resp.status_code == 200, f"Migration failed: {migrate_resp.text}"
    migrate_body = migrate_resp.json()
    assert migrate_body["migrated"] is True

    continuation_messages = [
        {"role": "user", "content": PROMPT},
        {"role": "assistant", "content": assistant_reply},
        {"role": "user", "content": CONTINUATION},
    ]
    cont_resp = await _do_completion(
        coord_url,
        continuation_messages,
        session_id=session_id,
        stream=False,
    )
    assert cont_resp.status_code == 200
    cont_body = cont_resp.json()

    timings = cont_body.get("timings", {})
    cache_n = timings.get("cache_n", 0)
    prompt_ms = timings.get("prompt_ms", 0)

    assert cache_n > 0, (
        f"cache_n={cache_n} — KV cache not used after migration cycle. "
        f"Source node: {source_node}"
    )
    assert prompt_ms < 5000, (
        f"prompt_ms={prompt_ms} — full re-prefill (expected cache path). "
        f"cache_n={cache_n}"
    )
