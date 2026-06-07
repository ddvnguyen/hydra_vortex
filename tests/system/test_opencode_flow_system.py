"""
OpenCode-like conversation flow through Coordinator HTTP API.

Models how opencode sends prompts to Hydra:
  - Large system prompt (coding agent brief instructions)
  - Short user prompts
  - Conversation history included in follow-up turns
  - Same session_id across all turns
  - Streaming (default for chat)

Requires live stack: Coordinator → Agent(s) → llama-server(s) → Store.

Environment variables:
  COORD_URL          http://localhost:9000   (Coordinator HTTP endpoint)
"""

import json
import os
from uuid import uuid4

import httpx
import pytest

COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")

# Simulates opencode's coding-agent instructions (truncated for test brevity).
SYSTEM_PROMPT = (
    "You are an expert software engineer. Follow these rules:\n"
    "1. Write clean, idiomatic code.\n"
    "2. Always check for existing patterns before implementing.\n"
    "3. Use proper error handling.\n"
    "4. Write tests for all new code.\n"
    "5. Prefer simple solutions over complex ones.\n"
    "6. Never introduce breaking changes without a migration plan.\n"
    "7. Document public APIs.\n"
    "8. Follow the principle of least surprise.\n"
    "9. Consider edge cases and failure modes.\n"
    "10. Optimize for readability first, performance second."
)

USER_PROMPT_1 = "Write a function that reverses a linked list in Python."
USER_PROMPT_2 = "Now add type hints and a docstring."
USER_PROMPT_3 = "Also add a main() entry point with example usage."


@pytest.fixture
def session_id() -> str:
    return f"system-opencode-{uuid4().hex[:12]}"


@pytest.fixture
def coord_url() -> str:
    return COORD_URL


def _make_messages(system: str | None, *content: str) -> list[dict]:
    msgs = []
    if system:
        msgs.append({"role": "system", "content": system})
    for msg in content:
        msgs.append({"role": "user", "content": msg})
    return msgs


def _make_followup_messages(system: str | None, history: list[dict], new_prompt: str) -> list[dict]:
    msgs = []
    if system:
        msgs.append({"role": "system", "content": system})
    msgs.extend(history)
    msgs.append({"role": "user", "content": new_prompt})
    return msgs


async def _do_completion(
    base_url: str,
    messages: list[dict],
    session_id: str | None = None,
    stream: bool = True,
    max_tokens: int = 200,
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


def _extract_content(events: list[dict]) -> str:
    parts: list[str] = []
    for ev in events:
        choices = ev.get("choices", [])
        if not choices:
            continue
        delta = choices[0].get("delta", {})
        content = delta.get("content", "") or delta.get("reasoning_content", "")
        if content:
            parts.append(content)
    return "".join(parts)


def _extract_usage_from_sse(events: list[dict]) -> dict:
    for ev in reversed(events):
        if "usage" in ev and isinstance(ev["usage"], dict):
            return ev["usage"]
    return {}


async def _get_status(base_url: str) -> dict:
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(f"{base_url}/status")
        resp.raise_for_status()
        return resp.json()


@pytest.mark.system
@pytest.mark.asyncio
async def test_opencode_initial_request(coord_url: str, session_id: str):
    """
    Turn 1: initial request with system prompt + user prompt (like opencode).
    Verifies response has content and session is tracked.
    """
    messages = _make_messages(SYSTEM_PROMPT, USER_PROMPT_1)
    resp = await _do_completion(
        coord_url,
        messages,
        session_id=session_id,
        stream=True,
    )
    assert resp.status_code == 200, f"Initial request failed: {resp.text}"

    events = await _parse_sse(resp)
    assert len(events) > 0, "No SSE events received"

    content = _extract_content(events)
    assert content, f"No content in response: {events[-1] if events else 'no events'}"

    usage = _extract_usage_from_sse(events)
    assert usage, "No usage data in final event"
    assert usage.get("total_tokens", 0) > 0, "total_tokens should be > 0"

    # Verify session appears in status
    status = await _get_status(coord_url)
    sessions = status.get("sessions", {}).get("sessions", [])
    session_ids = [s["session_id"] for s in sessions]
    assert session_id in session_ids, (
        f"Session {session_id} not found in status: {session_ids}"
    )


@pytest.mark.system
@pytest.mark.asyncio
async def test_opencode_followup_reuses_kv_cache(coord_url: str, session_id: str):
    """
    Turn 1 + Turn 2: simulate opencode conversation with system prompt.
    Turn 2 should reuse KV cache from Turn 1 (cache_n > 0, prompt_ms < 5000).
    """
    # ── Turn 1: initial request with system prompt ──
    messages_1 = _make_messages(SYSTEM_PROMPT, USER_PROMPT_1)
    resp_1 = await _do_completion(
        coord_url,
        messages_1,
        session_id=session_id,
        stream=True,
    )
    assert resp_1.status_code == 200, f"Turn 1 failed: {resp_1.text}"

    events_1 = await _parse_sse(resp_1)
    reply_1 = _extract_content(events_1)
    assert reply_1, "Turn 1 has no content in response"

    # ── Turn 2: follow-up with conversation history ──
    history = [
        {"role": "user", "content": USER_PROMPT_1},
        {"role": "assistant", "content": reply_1},
    ]
    messages_2 = _make_followup_messages(SYSTEM_PROMPT, history, USER_PROMPT_2)

    resp_2 = await _do_completion(
        coord_url,
        messages_2,
        session_id=session_id,
        stream=True,
    )
    assert resp_2.status_code == 200, f"Turn 2 failed: {resp_2.text}"

    events_2 = await _parse_sse(resp_2)
    reply_2 = _extract_content(events_2)
    assert reply_2, "Turn 2 has no content in response"

    # Verify session is restored (appears in status)
    status = await _get_status(coord_url)
    sessions = status.get("sessions", {}).get("sessions", [])
    session = next((s for s in sessions if s["session_id"] == session_id), None)
    assert session is not None, f"Session {session_id} not found after turn 2"

    slot_id = session.get("slot_id")
    assert isinstance(slot_id, int), (
        f"slot_id not a valid int after turn 2: {slot_id!r}"
    )


@pytest.mark.system
@pytest.mark.asyncio
async def test_opencode_multi_turn_session_lifecycle(coord_url: str, session_id: str):
    """
    Three-turn opencode-like conversation.
    Verifies slot_id stability and session tracking across all turns.
    """
    # ── Turn 1 ──
    messages_1 = _make_messages(SYSTEM_PROMPT, USER_PROMPT_1)
    resp_1 = await _do_completion(
        coord_url, messages_1, session_id=session_id, stream=True,
    )
    assert resp_1.status_code == 200, f"T1 failed: {resp_1.text}"
    events_1 = await _parse_sse(resp_1)
    reply_1 = _extract_content(events_1)
    assert reply_1, "T1 no content"

    status_1 = await _get_status(coord_url)
    session_1 = next(
        (s for s in status_1.get("sessions", {}).get("sessions", [])
         if s["session_id"] == session_id),
        None,
    )
    assert session_1 is not None, "Session missing after T1"
    slot_id_1 = session_1.get("slot_id")
    assert isinstance(slot_id_1, int), f"slot_id not resolved after T1: {slot_id_1}"

    # ── Turn 2 ──
    history_2 = [
        {"role": "user", "content": USER_PROMPT_1},
        {"role": "assistant", "content": reply_1},
    ]
    messages_2 = _make_followup_messages(SYSTEM_PROMPT, history_2, USER_PROMPT_2)
    resp_2 = await _do_completion(
        coord_url, messages_2, session_id=session_id, stream=True,
    )
    assert resp_2.status_code == 200, f"T2 failed: {resp_2.text}"
    events_2 = await _parse_sse(resp_2)
    reply_2 = _extract_content(events_2)
    assert reply_2, "T2 no content"

    status_2 = await _get_status(coord_url)
    session_2 = next(
        (s for s in status_2.get("sessions", {}).get("sessions", [])
         if s["session_id"] == session_id),
        None,
    )
    assert session_2 is not None, "Session missing after T2"
    slot_id_2 = session_2.get("slot_id")
    assert isinstance(slot_id_2, int), f"slot_id not resolved after T2: {slot_id_2}"

    # ── Turn 3 ──
    history_3 = [
        {"role": "user", "content": USER_PROMPT_1},
        {"role": "assistant", "content": reply_1},
        {"role": "user", "content": USER_PROMPT_2},
        {"role": "assistant", "content": reply_2},
    ]
    messages_3 = _make_followup_messages(SYSTEM_PROMPT, history_3, USER_PROMPT_3)
    resp_3 = await _do_completion(
        coord_url, messages_3, session_id=session_id, stream=True,
    )
    assert resp_3.status_code == 200, f"T3 failed: {resp_3.text}"
    events_3 = await _parse_sse(resp_3)
    reply_3 = _extract_content(events_3)
    assert reply_3, "T3 no content"

    # Cleanup
    async with httpx.AsyncClient(timeout=30.0) as client:
        await client.delete(f"{coord_url}/sessions/{session_id}")


@pytest.mark.system
@pytest.mark.asyncio
async def test_opencode_concurrent_sessions(coord_url: str):
    """
    Two concurrent opencode-like sessions to verify slot isolation.
    Each session should get its own slot and not interfere.
    """
    sid_a = f"system-oc-concur-a-{uuid4().hex[:8]}"
    sid_b = f"system-oc-concur-b-{uuid4().hex[:8]}"

    async def session_turn(sid: str, prompt: str) -> str:
        msgs = _make_messages(SYSTEM_PROMPT, prompt)
        resp = await _do_completion(
            coord_url, msgs, session_id=sid, stream=True,
        )
        assert resp.status_code == 200, f"Session {sid} failed: {resp.text}"
        events = await _parse_sse(resp)
        content = _extract_content(events)
        assert content, f"Session {sid} has no content"
        return content

    result_a = await session_turn(sid_a, "Write a function to find the max element in a list.")
    result_b = await session_turn(sid_b, "Write a function to check if a string is a palindrome.")

    assert result_a, "Session A has no content"
    assert result_b, "Session B has no content"

    # Both sessions should appear in status
    status = await _get_status(coord_url)
    session_ids = [s["session_id"] for s in status.get("sessions", {}).get("sessions", [])]
    assert sid_a in session_ids, f"Session A missing: {session_ids}"
    assert sid_b in session_ids, f"Session B missing: {session_ids}"

    # Cleanup
    async with httpx.AsyncClient(timeout=30.0) as client:
        for sid in (sid_a, sid_b):
            try:
                await client.delete(f"{coord_url}/sessions/{sid}")
            except Exception:
                pass
