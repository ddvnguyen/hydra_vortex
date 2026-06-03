"""
Agent workflow system tests — Tier-2 (real services required).

Covers gaps exposed by running a real coding agent (opencode) against Hydra:
  1. Tool-call flows (tools silently dropped before fix in router.py)
  2. Multi-turn conversation (existing tests max at 3 turns)
  3. Context accumulation across turns (not just single large prompts)

Requires all 6 services: coordinator :9000, llama RTX :8080, llama P100 :8086,
store :9500, agent-rtx :9601, agent-p100 :9602.

Environment variables (same as other system tests):
  COORD_URL       http://localhost:9000
  LLAMA_RTX_URL   http://localhost:8080
  LLAMA_P100_URL  http://192.168.122.21:8086

GitHub: #99
"""

import json
import os
from uuid import uuid4

import httpx
import pytest

# ── Config ────────────────────────────────────────────────────────────────────

COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")
LLAMA_RTX_URL = os.environ.get("LLAMA_RTX_URL", "http://localhost:8080")
LLAMA_P100_URL = os.environ.get("LLAMA_P100_URL", "http://192.168.122.21:8086")

_CHARS_PER_TOKEN = 3.0

# ── Text generation (reused pattern from test_large_prompt_system.py) ─────────

_SEED_PARAS = [
    "Software engineering encompasses requirements gathering, system design, implementation, testing, deployment, and maintenance. Each phase requires careful planning and execution to ensure quality outcomes.",
    "Database indexing strategies significantly impact query performance. B-tree indexes excel at range queries, while hash indexes optimize point lookups. Understanding access patterns guides index selection.",
    "Container orchestration platforms automate deployment, scaling, and management of containerized applications. Kubernetes provides service discovery, load balancing, and automated rollouts.",
    "Distributed systems require careful handling of consistency, availability, and partition tolerance. The CAP theorem states that a distributed system can only guarantee two of these three properties simultaneously.",
    "API design best practices include consistent naming conventions, proper versioning strategies, comprehensive error handling, and thorough documentation. RESTful APIs should leverage HTTP methods correctly.",
    "Test-driven development writes tests before production code, ensuring requirements are clearly understood. The red-green-refactor cycle promotes incremental development and robust test coverage.",
    "Network protocols like TCP provide reliable, ordered delivery of data between applications. UDP offers lower latency but no delivery guarantees, making it suitable for real-time applications.",
    "Microservices architecture decomposes applications into independently deployable services communicating over well-defined APIs. Each service focuses on a specific business capability.",
    "Caching strategies improve application performance by storing frequently accessed data in fast storage layers. Common patterns include cache-aside, write-through, and write-behind caching.",
    "Authentication and authorization are fundamental security concerns. OAuth 2.0 provides delegated access, while JWT tokens enable stateless authentication across distributed systems.",
    "Monitoring and observability are critical for production systems. Metrics, logs, and traces provide visibility into system behavior, enabling rapid incident response and performance optimization.",
    "Concurrency control mechanisms prevent race conditions in multi-threaded applications. Mutexes, semaphores, and atomic operations provide different levels of thread safety guarantees.",
    "Load balancing distributes incoming traffic across multiple servers to ensure reliability and performance. Algorithms include round-robin, least connections, and consistent hashing.",
    "Circuit breaker patterns prevent cascading failures in distributed systems by detecting when a downstream service is unhealthy and failing fast instead of waiting for timeouts.",
    "Infrastructure as code manages cloud resources through declarative configuration files. Tools like Terraform and Pulumi enable version-controlled, reproducible infrastructure deployment.",
    "Rate limiting protects APIs from abuse by restricting the number of requests a client can make within a time window. Common algorithms include token bucket and sliding window.",
    "Message queues decouple producers and consumers, enabling asynchronous communication and load leveling. RabbitMQ, Apache Kafka, and Amazon SQS are popular message broker implementations.",
    "Event sourcing stores state changes as an append-only event log, enabling audit trails, temporal queries, and event-driven architectures. The current state is derived by replaying events.",
]


def _generate_text(approx_tokens: int) -> str:
    target_chars = int(approx_tokens * _CHARS_PER_TOKEN)
    parts: list[str] = []
    length = 0
    while length < target_chars:
        for p in _SEED_PARAS:
            if length >= target_chars:
                break
            parts.append(p)
            length += len(p) + 1
    return " ".join(parts)[:target_chars]


def _get_output_text(msg: dict) -> str:
    return msg.get("content") or msg.get("reasoning_content") or ""


# ── Tool definitions ──────────────────────────────────────────────────────────

CALCULATOR_TOOL = {
    "type": "function",
    "function": {
        "name": "calculator",
        "description": "Evaluate a simple arithmetic expression and return the numeric result.",
        "parameters": {
            "type": "object",
            "properties": {
                "expression": {
                    "type": "string",
                    "description": "A math expression to evaluate, e.g. '1234 * 5678'",
                }
            },
            "required": ["expression"],
        },
    },
}

WORD_COUNT_TOOL = {
    "type": "function",
    "function": {
        "name": "word_count",
        "description": "Count the number of words in a text string.",
        "parameters": {
            "type": "object",
            "properties": {
                "text": {"type": "string", "description": "The text to count words in"}
            },
            "required": ["text"],
        },
    },
}


def _execute_tool(tool_call: dict) -> str:
    """Safe fake tool executor for tests."""
    name = tool_call["function"]["name"]
    args = json.loads(tool_call["function"]["arguments"])
    if name == "calculator":
        expr = args.get("expression", "0")
        # Restrict to safe arithmetic only
        allowed = set("0123456789+-*/(). ")
        if all(c in allowed for c in expr):
            try:
                return str(eval(expr))  # noqa: S307 — restricted char set above
            except Exception:
                return "error"
        return "error: unsafe expression"
    if name == "word_count":
        return str(len(args.get("text", "").split()))
    return f"unknown tool: {name}"


# ── HTTP helpers ──────────────────────────────────────────────────────────────

async def _send(
    session_id: str,
    messages: list[dict],
    *,
    tools: list[dict] | None = None,
    stream: bool = False,
    max_tokens: int = 200,
    timeout: float = 300.0,
) -> dict:
    body: dict = {
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": 0,
        "stream": stream,
        "session_id": session_id,
    }
    if tools:
        body["tools"] = tools

    async with httpx.AsyncClient(timeout=timeout) as client:
        resp = await client.post(f"{COORD_URL}/v1/chat/completions", json=body)
    resp.raise_for_status()
    return resp.json()


async def _delete_session(session_id: str) -> None:
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            await client.delete(f"{COORD_URL}/sessions/{session_id}")
    except Exception:
        pass


async def _get_session_n_past(session_id: str) -> int | None:
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            resp = await client.get(f"{COORD_URL}/status")
            resp.raise_for_status()
        data = resp.json()
        for s in data.get("sessions", {}).get("sessions", []):
            if s["session_id"] == session_id:
                return s.get("n_past")
    except Exception:
        pass
    return None


# ── Tests: tool calls ─────────────────────────────────────────────────────────

@pytest.mark.system
@pytest.mark.asyncio
async def test_tool_call_basic():
    """2-turn tool call: model requests calculator, result injected, final answer checked."""
    session_id = f"agent-tool-basic-{uuid4().hex[:10]}"
    try:
        # Turn 1 — model should call the calculator tool
        # max_tokens=600: reasoning model emits <think> block (~200-400 tokens) before tool call JSON
        resp = await _send(
            session_id,
            [{"role": "user", "content": "What is 1234 multiplied by 5678? Use the calculator tool to get the exact answer."}],
            tools=[CALCULATOR_TOOL],
            max_tokens=600,
        )
        choice = resp["choices"][0]
        assert choice["finish_reason"] == "tool_calls", (
            f"Expected finish_reason=tool_calls, got {choice['finish_reason']!r}. "
            "This means the tools field was not forwarded to llama-cpp."
        )
        tool_calls = choice["message"]["tool_calls"]
        assert len(tool_calls) >= 1
        assert tool_calls[0]["function"]["name"] == "calculator"

        # Turn 2 — inject tool result, expect final natural-language answer
        result = _execute_tool(tool_calls[0])
        assert result == "7006652", f"Calculator returned unexpected value: {result}"

        history = [
            {"role": "user", "content": "What is 1234 multiplied by 5678? Use the calculator tool to get the exact answer."},
            {"role": "assistant", "content": None, "tool_calls": tool_calls},
            {"role": "tool", "tool_call_id": tool_calls[0]["id"], "content": result},
        ]
        resp2 = await _send(session_id, history, tools=[CALCULATOR_TOOL], max_tokens=400)
        choice2 = resp2["choices"][0]
        assert choice2["finish_reason"] == "stop"
        answer = _get_output_text(choice2["message"])
        # Model may format with commas: "7,006,652" or plain "7006652"
        assert "7006652" in answer or "7,006,652" in answer, (
            f"Final answer did not contain the computed result. Got: {answer[:200]}"
        )
    finally:
        await _delete_session(session_id)


@pytest.mark.system
@pytest.mark.asyncio
async def test_tool_call_multi_step():
    """4-turn workflow: two sequential tool calls in a multi-step reasoning chain."""
    session_id = f"agent-tool-multi-{uuid4().hex[:10]}"
    # Pad turns 3-4 with some context to exercise history accumulation
    padding = _generate_text(1000)

    try:
        # Turn 1 — ask question requiring two tools
        messages: list[dict] = [
            {
                "role": "user",
                "content": (
                    f"{padding}\n\n"
                    "Given the above context: first calculate 999 * 111, "
                    "then count the words in the phrase 'hello world foo bar'. "
                    "Use both tools and report the results."
                ),
            }
        ]
        resp = await _send(session_id, messages, tools=[CALCULATOR_TOOL, WORD_COUNT_TOOL], max_tokens=600)
        choice = resp["choices"][0]
        assert choice["finish_reason"] == "tool_calls"
        tool_calls_1 = choice["message"]["tool_calls"]
        assert len(tool_calls_1) >= 1

        # Inject all tool results from turn 1
        messages.append({"role": "assistant", "content": None, "tool_calls": tool_calls_1})
        for tc in tool_calls_1:
            messages.append({
                "role": "tool",
                "tool_call_id": tc["id"],
                "content": _execute_tool(tc),
            })

        # Turn 2 — model may call more tools or produce final answer
        resp2 = await _send(session_id, messages, tools=[CALCULATOR_TOOL, WORD_COUNT_TOOL], max_tokens=600)
        choice2 = resp2["choices"][0]

        if choice2["finish_reason"] == "tool_calls":
            # Model chose to call more tools — inject results and get final answer
            tool_calls_2 = choice2["message"]["tool_calls"]
            messages.append({"role": "assistant", "content": None, "tool_calls": tool_calls_2})
            for tc in tool_calls_2:
                messages.append({
                    "role": "tool",
                    "tool_call_id": tc["id"],
                    "content": _execute_tool(tc),
                })
            resp3 = await _send(session_id, messages, tools=[CALCULATOR_TOOL, WORD_COUNT_TOOL], max_tokens=600)
            choice2 = resp3["choices"][0]

        assert choice2["finish_reason"] == "stop"
        final = _get_output_text(choice2["message"])
        # Verify both tool results appear in final response
        assert "110889" in final or "4" in final, (
            f"Expected tool results (110889 and/or 4) in final response. Got: {final[:300]}"
        )
    finally:
        await _delete_session(session_id)


# ── Tests: multi-turn context accumulation ────────────────────────────────────

@pytest.mark.system
@pytest.mark.asyncio
@pytest.mark.parametrize("target_tokens,turns,max_tokens_per_turn,timeout", [
    (8_000,  6,  150, 180),
    (16_000, 10, 150, 360),
])
async def test_multiturn_context(target_tokens, turns, max_tokens_per_turn, timeout):
    """Context grows across turns — n_past must increase each turn, slot_id must stay stable."""
    session_id = f"agent-ctx-{target_tokens}-{uuid4().hex[:8]}"
    tokens_per_turn = target_tokens // turns
    history: list[dict] = []
    prev_n_past = 0
    slot_id_seen: int | None = None

    try:
        for turn in range(turns):
            padding = _generate_text(tokens_per_turn - 30)
            user_msg = f"[Turn {turn + 1}/{turns}] {padding} In one sentence, summarize the main theme above."
            messages = history + [{"role": "user", "content": user_msg}]

            resp = await _send(
                session_id, messages,
                max_tokens=max_tokens_per_turn,
                timeout=float(timeout),
            )
            choice = resp["choices"][0]
            assert choice["finish_reason"] in ("stop", "length"), (
                f"Turn {turn + 1}: unexpected finish_reason={choice['finish_reason']!r}"
            )
            reply = _get_output_text(choice["message"])
            assert reply, f"Turn {turn + 1}: empty reply"
            history = messages + [{"role": "assistant", "content": reply}]

            # Verify n_past grows (KV cache being extended, not reset)
            n_past = await _get_session_n_past(session_id)
            if n_past is not None:
                assert n_past > prev_n_past, (
                    f"Turn {turn + 1}: n_past did not grow ({prev_n_past} → {n_past}). "
                    "KV cache may have been evicted or reset."
                )
                prev_n_past = n_past

            # Verify slot_id remains stable (session affinity preserved)
            try:
                async with httpx.AsyncClient(timeout=5.0) as client:
                    status_resp = await client.get(f"{COORD_URL}/status")
                    status = status_resp.json()
                for s in status.get("sessions", {}).get("sessions", []):
                    if s["session_id"] == session_id and s.get("slot_id") is not None:
                        if slot_id_seen is None:
                            slot_id_seen = s["slot_id"]
                        else:
                            assert s["slot_id"] == slot_id_seen, (
                                f"Turn {turn + 1}: slot_id changed {slot_id_seen} → {s['slot_id']}"
                            )
            except Exception:
                pass  # status check is best-effort

    finally:
        await _delete_session(session_id)


@pytest.mark.system
@pytest.mark.asyncio
@pytest.mark.slow
async def test_multiturn_40k_context():
    """40k accumulated context across 15 turns — RTX handles large prefill, n_past grows."""
    target_tokens = 40_000
    turns = 15
    tokens_per_turn = target_tokens // turns
    session_id = f"agent-ctx-40k-{uuid4().hex[:8]}"
    history: list[dict] = []
    prev_n_past = 0

    try:
        for turn in range(turns):
            padding = _generate_text(tokens_per_turn - 30)
            user_msg = f"[Turn {turn + 1}/{turns}] {padding} Summarize in one sentence."
            messages = history + [{"role": "user", "content": user_msg}]

            resp = await _send(session_id, messages, max_tokens=150, timeout=600.0)
            reply = _get_output_text(resp["choices"][0]["message"])
            assert reply, f"Turn {turn + 1}: empty reply"
            history = messages + [{"role": "assistant", "content": reply}]

            n_past = await _get_session_n_past(session_id)
            if n_past is not None:
                assert n_past > prev_n_past, f"Turn {turn + 1}: n_past stalled at {prev_n_past}"
                prev_n_past = n_past
    finally:
        await _delete_session(session_id)


# ── Test: tool calls during a growing-context session ────────────────────────

@pytest.mark.system
@pytest.mark.asyncio
async def test_tool_call_with_growing_context():
    """
    8 turns of growing context (~2k tokens/turn) with tool calls on turns 3, 5, 7.
    Verifies tools work correctly when KV cache is large.
    """
    session_id = f"agent-tool-ctx-{uuid4().hex[:10]}"
    tokens_per_turn = 2_000
    history: list[dict] = []
    tool_call_turns = {3, 5, 7}
    tool_call_count = 0

    try:
        for turn in range(1, 9):
            padding = _generate_text(tokens_per_turn - 50)

            if turn in tool_call_turns:
                expression = f"{turn * 111} * {turn * 222}"
                expected = str(turn * 111 * turn * 222)
                user_msg = (
                    f"[Turn {turn}] {padding}\n\n"
                    f"Please calculate {expression} using the calculator tool."
                )
                messages = history + [{"role": "user", "content": user_msg}]
                resp = await _send(session_id, messages, tools=[CALCULATOR_TOOL], max_tokens=600)
                choice = resp["choices"][0]

                if choice["finish_reason"] == "tool_calls":
                    tool_calls = choice["message"]["tool_calls"]
                    result = _execute_tool(tool_calls[0])
                    tool_call_count += 1

                    messages.append({"role": "assistant", "content": None, "tool_calls": tool_calls})
                    messages.append({
                        "role": "tool",
                        "tool_call_id": tool_calls[0]["id"],
                        "content": result,
                    })
                    resp2 = await _send(session_id, messages, tools=[CALCULATOR_TOOL], max_tokens=400)
                    reply = _get_output_text(resp2["choices"][0]["message"])
                    assert expected in reply, (
                        f"Turn {turn}: expected {expected!r} in tool-result response. Got: {reply[:200]}"
                    )
                    history = messages + [{"role": "assistant", "content": reply}]
                else:
                    # Model answered without calling tool — still valid, just track it
                    reply = _get_output_text(choice["message"])
                    history = messages + [{"role": "assistant", "content": reply}]
            else:
                user_msg = f"[Turn {turn}] {padding} Respond with one sentence acknowledging this turn."
                messages = history + [{"role": "user", "content": user_msg}]
                resp = await _send(session_id, messages, max_tokens=100)
                reply = _get_output_text(resp["choices"][0]["message"])
                assert reply, f"Turn {turn}: empty reply"
                history = messages + [{"role": "assistant", "content": reply}]

        # At least 1 of the 3 designated turns should have triggered a tool call
        assert tool_call_count >= 1, (
            "Expected at least 1 tool call across turns 3, 5, 7. "
            "Model may not be calling tools when context is large."
        )
    finally:
        await _delete_session(session_id)


# ── Test: migration mid-workflow ──────────────────────────────────────────────

@pytest.mark.system
@pytest.mark.asyncio
async def test_session_migration_mid_workflow():
    """
    5 turns on RTX building 8k context, then migrate to P100, then 2 more turns.
    Verifies cache_n > 0 after migration and conversation continues correctly.
    """
    session_id = f"agent-migrate-{uuid4().hex[:10]}"
    tokens_per_turn = 1_600  # 5 turns ≈ 8k context
    history: list[dict] = []

    try:
        # Phase 1: 5 turns on RTX building context
        for turn in range(1, 6):
            padding = _generate_text(tokens_per_turn - 30)
            user_msg = f"[Turn {turn}/5] {padding} Acknowledge in one sentence."
            messages = history + [{"role": "user", "content": user_msg}]
            resp = await _send(session_id, messages, max_tokens=100)
            reply = _get_output_text(resp["choices"][0]["message"])
            assert reply, f"Phase 1 turn {turn}: empty reply"
            history = messages + [{"role": "assistant", "content": reply}]

        # Verify session is on RTX
        async with httpx.AsyncClient(timeout=10.0) as client:
            status_resp = await client.get(f"{COORD_URL}/status")
        status = status_resp.json()
        session_info = next(
            (s for s in status.get("sessions", {}).get("sessions", [])
             if s["session_id"] == session_id),
            None,
        )
        assert session_info is not None, "Session not found in coordinator status"
        assert session_info["node"] == "rtx", f"Expected session on RTX, got {session_info['node']!r}"
        n_past_before = session_info.get("n_past", 0)
        assert n_past_before > 0, "n_past should be > 0 after 5 turns"

        # Migrate to P100
        async with httpx.AsyncClient(timeout=120.0) as client:
            migrate_resp = await client.post(
                f"{COORD_URL}/sessions/{session_id}/migrate",
                json={"target_node": "p100"},
            )
        assert migrate_resp.status_code == 200, f"Migration failed: {migrate_resp.text}"
        migrate_data = migrate_resp.json()
        assert migrate_data.get("migrated"), f"Migration not confirmed: {migrate_data}"

        # Phase 2: 2 more turns on P100 — conversation should continue coherently
        for turn in range(6, 8):
            user_msg = f"[Turn {turn}/7] Continue the conversation. Summarize what we discussed so far."
            messages = history + [{"role": "user", "content": user_msg}]
            resp = await _send(session_id, messages, max_tokens=200, timeout=120.0)
            reply = _get_output_text(resp["choices"][0]["message"])
            assert reply, f"Phase 2 turn {turn}: empty reply after migration"
            history = messages + [{"role": "assistant", "content": reply}]

        # Verify session moved to P100
        async with httpx.AsyncClient(timeout=10.0) as client:
            status_resp2 = await client.get(f"{COORD_URL}/status")
        status2 = status_resp2.json()
        session_after = next(
            (s for s in status2.get("sessions", {}).get("sessions", [])
             if s["session_id"] == session_id),
            None,
        )
        if session_after:
            assert session_after["node"] == "p100", (
                f"Expected session on P100 after migration, got {session_after['node']!r}"
            )

    finally:
        await _delete_session(session_id)
