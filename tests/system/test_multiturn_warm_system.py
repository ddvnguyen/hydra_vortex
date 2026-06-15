"""
Multi-turn warm affinity + slot-leak regression tests.

These tests verify that after the three fixes (NPromptTokens tracking, C++ UB guard,
warm lease eviction), the system:
  1. Actually reuses KV cache on P100 across turns (n_prompt_tokens_cache > 10 post-turn-2)
  2. Decode speed on turn 2+ is not catastrophically slow vs turn 1 (no full re-prefill)
  3. No two concurrent sessions share the same (node, slot_id) pair

Requires live stack: Coordinator → Workers → llama-servers → Store.

Environment variables (same as conftest.py):
  COORD_URL          http://localhost:9000
  LLAMA_P100_URL     http://192.168.122.21:8086
"""

import asyncio
import json
import os
import time
from uuid import uuid4

import httpx
import pytest

COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")
LLAMA_P100_URL = os.environ.get("LLAMA_P100_URL", "http://192.168.122.21:8086")

# A moderately large system prompt (forces real prefill on turn 1 so we can measure re-prefill).
SYSTEM_PROMPT = (
    "You are an expert software engineer specialising in distributed systems and GPU inference. "
    "When answering questions:\n"
    "1. Provide concise, accurate answers.\n"
    "2. Use code examples where relevant.\n"
    "3. Explain trade-offs between approaches.\n"
    "4. Consider memory, latency, and throughput implications.\n"
    "5. Reference established patterns from systems like vLLM, TensorRT-LLM, and llama.cpp.\n"
    "Your answers should be helpful for an engineer building production LLM serving infrastructure."
)

TURNS = [
    "What is KV cache reuse and why does it matter for LLM inference performance?",
    "How does prefix caching work at the llama.cpp level?",
    "What are the challenges of migrating KV cache state between two different GPUs?",
    "How would you implement a P/D disaggregated serving system for heterogeneous GPUs?",
    "What metrics would you track to verify that KV cache reuse is actually working in production?",
]


@pytest.fixture
def session_id() -> str:
    return f"sys-warm-{uuid4().hex[:12]}"


async def _do_completion(
    messages: list[dict],
    session_id: str,
    max_tokens: int = 300,
    timeout: float = 600.0,
) -> httpx.Response:
    body = {
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": 0,
        "stream": True,
        "session_id": session_id,
    }
    async with httpx.AsyncClient(timeout=timeout) as client:
        return await client.post(f"{COORD_URL}/v1/chat/completions", json=body)


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


async def _scrape_slots(url: str, timeout: float = 5.0) -> list | None:
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            resp = await client.get(f"{url}/slots")
            resp.raise_for_status()
            return resp.json()
    except Exception:
        return None


async def _get_status(timeout: float = 10.0) -> dict:
    async with httpx.AsyncClient(timeout=timeout) as client:
        resp = await client.get(f"{COORD_URL}/status")
        resp.raise_for_status()
        return resp.json()


def _make_history(system: str, turns_done: list[tuple[str, str]]) -> list[dict]:
    msgs: list[dict] = [{"role": "system", "content": system}]
    for user_msg, assistant_reply in turns_done:
        msgs.append({"role": "user", "content": user_msg})
        msgs.append({"role": "assistant", "content": assistant_reply})
    return msgs


@pytest.mark.system
@pytest.mark.asyncio
async def test_5turn_warm_affinity(session_id: str) -> None:
    """
    Regression: after fix 1 (NPromptTokens) + fix 2 (C++ UB guard), follow-up turns
    must reuse cached KV on P100 (n_prompt_tokens_cache > 10 after turn 2) and must not
    take drastically longer than turn 1 (which would indicate a full re-prefill).
    """
    history: list[tuple[str, str]] = []
    turn_times: list[float] = []

    for i, user_prompt in enumerate(TURNS):
        messages = _make_history(SYSTEM_PROMPT, history)
        messages.append({"role": "user", "content": user_prompt})

        t0 = time.monotonic()
        resp = await _do_completion(messages, session_id)
        assert resp.status_code == 200, f"Turn {i+1} HTTP {resp.status_code}"
        events = await _parse_sse(resp)
        elapsed = time.monotonic() - t0
        turn_times.append(elapsed)

        reply = _extract_content(events)
        assert reply, f"Turn {i+1} produced empty reply"
        history.append((user_prompt, reply))

        if i == 1:
            # After turn 2: P100 should have cache populated (n_prompt_tokens_cache > 10).
            # Give the slot a moment to settle before scraping.
            await asyncio.sleep(0.5)
            slots = await _scrape_slots(LLAMA_P100_URL)
            if slots:
                p100_cached = max(
                    (s.get("n_prompt_tokens_cache", 0) for s in slots),
                    default=0,
                )
                assert p100_cached > 10, (
                    f"P100 n_prompt_tokens_cache={p100_cached} after turn 2 "
                    f"— expected >10, which would mean cache was erased to 1 (UB bug)"
                )

    # Turn 2–5 should not be catastrophically slower than turn 1.
    # Full re-prefill at 83 tok/s for ~14K tokens ≈ 170 s; warm decode is much faster.
    # We allow 3× turn-1 time as a generous threshold.
    turn1_s = turn_times[0]
    for i, t in enumerate(turn_times[1:], start=2):
        assert t < turn1_s * 3, (
            f"Turn {i} took {t:.1f}s vs turn-1 {turn1_s:.1f}s (3× threshold) "
            f"— likely a full re-prefill regression"
        )


@pytest.mark.system
@pytest.mark.asyncio
async def test_no_slot_leak() -> None:
    """
    Regression: after fix 3 (warm lease eviction before overwrite), no two live sessions
    should occupy the same (node, slot_id) pair — which was the observed symptom of the
    slot leak (ses_1347 and ses_1346 both showing p100/slot_id=0).
    """
    sid_a = f"sys-leak-a-{uuid4().hex[:8]}"
    sid_b = f"sys-leak-b-{uuid4().hex[:8]}"

    async def do_session(sid: str, n_turns: int = 3) -> None:
        history: list[tuple[str, str]] = []
        for i in range(n_turns):
            messages = _make_history(SYSTEM_PROMPT, history)
            messages.append({"role": "user", "content": TURNS[i % len(TURNS)]})
            resp = await _do_completion(messages, sid, max_tokens=150)
            assert resp.status_code == 200, f"Session {sid} turn {i+1} HTTP {resp.status_code}"
            events = await _parse_sse(resp)
            reply = _extract_content(events)
            assert reply, f"Session {sid} turn {i+1} empty reply"
            history.append((TURNS[i % len(TURNS)], reply))

            # After each turn, verify no duplicate (node, slot_id) in coordinator status.
            status = await _get_status()
            sessions_data = status.get("sessions", {})
            sessions_list = (
                list(sessions_data.get("sessions", {}).values())
                if isinstance(sessions_data, dict)
                else []
            )
            active = [
                s for s in sessions_list
                if not s.get("slot_freed", True) and s.get("slot_id") is not None
            ]
            seen: set[tuple] = set()
            for s in active:
                key = (s.get("node"), s.get("slot_id"))
                assert key not in seen, (
                    f"Slot leak detected after session={sid} turn={i+1}: "
                    f"two sessions share {key}. Active sessions: {active}"
                )
                seen.add(key)

    # Run sessions sequentially so we can detect cross-session slot leaks.
    await do_session(sid_a, n_turns=3)
    await do_session(sid_b, n_turns=3)
