"""
Workload generator 2 — multi-turn chat (warm sessions).

Issue #306 generator 2: "N users × M turns each, P seconds between
turns. Critical for measuring warm-slot hit rate."

The Coordinator reuses a warm slot across turns when the session_id is
stable — turn 1 is cold (full prefill), turns 2..M should hit the warm
slot. This is the canonical workload for measuring the S1 / S2 success
criteria.

Usage:
    python -m tests.bench.chat_multi_turn --output results/multi_turn.json

Deterministic growing-context mode (arm 015):
    python -m tests.bench.chat_multi_turn \
        --deterministic --base-url http://localhost:8081 \
        --n-turns 10 --max-tokens 120
"""

from __future__ import annotations

import asyncio
import os
from typing import Any

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

SYSTEM_PROMPT = (
    "You are a helpful, concise assistant. Answer in one short paragraph."
)

# The 4 followup questions for the multi-turn session. Each is short
# enough to keep the cumulative context manageable, long enough to
# require a real decode step.
FOLLOWUP_QUESTIONS: tuple[str, ...] = (
    "What is KV cache reuse and why does it matter for LLM inference?",
    "How does prefix caching work at the llama.cpp level?",
    "What are the challenges of migrating KV cache between two GPUs?",
    "How would you implement a P/D disaggregated serving system?",
)

# Repeating filler for deterministic growing-context tests.
# Each filler "token" is a single unique word (~1 token each) to hit target size.
_FILLER_CHARS = "abcdefghijklmnopqrstuvwxyz"


def _generate_filler(target_tokens: int, turn: int) -> str:
    """Generate a filler text of approximately `target_tokens` tokens.

    Uses single-character words separated by spaces, each ~1 token.
    Turn number is embedded via a prefix to ensure unique per-turn content
    while keeping the token count predictable.
    """
    # Each word is ~1 token (single char + space).  We generate slightly
    # under target to stay within budget.
    n_words = max(1, target_tokens - 10)  # leave room for the task prompt overhead
    words = []
    for i in range(n_words):
        # Cycle through single chars; each is ~1 token
        ch = _FILLER_CHARS[i % len(_FILLER_CHARS)]
        # Every 100 words, insert turn marker to ensure uniqueness
        if i % 100 == 0:
            words.append(f"t{turn}")
        words.append(ch)
    return " ".join(words)


def build_turn_messages(
    *,
    history: list[dict[str, str]] | None = None,
    user_msg: str = FOLLOWUP_QUESTIONS[0],
) -> list[dict[str, str]]:
    """Return the messages list for one turn of a multi-turn session."""
    msgs: list[dict[str, str]] = [{"role": "system", "content": SYSTEM_PROMPT}]
    if history:
        msgs.extend(history)
    msgs.append({"role": "user", "content": user_msg})
    return msgs


def _register_deterministic_args(p: Any) -> None:
    """Register CLI args for the deterministic growing-context mode."""
    p.add_argument("--deterministic", action="store_true",
                    help="Run deterministic growing-context multi-turn "
                         "(arm 015 style: fixed max_tokens, filler prompts, "
                         "single session, per-turn inline output)")
    p.add_argument("--n-turns", type=int, default=10,
                    help="Number of turns in deterministic mode (default: 10)")
    p.add_argument("--first-turn-tokens", type=int, default=5000,
                    help="Target tokens for first turn prompt (default: 5000)")
    p.add_argument("--growth-tokens", type=int, default=2000,
                    help="Additional tokens per turn (default: 2000)")


@cli_entrypoint(
    build_messages=lambda args: build_turn_messages(),
    scenario_id="chat_multi_turn",
    default_n=20,
    default_concurrency=1,
    default_warmup=3,
    default_max_tokens=120,
    extra_args=_register_deterministic_args,
    runner=lambda harness, args, messages: _deterministic_runner(harness, args, messages)
    if getattr(args, "deterministic", False) else None,
)
async def main() -> None:  # pragma: no cover
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


async def _deterministic_runner(
    harness: BenchmarkHarness,
    args: Any,
    _messages: list[dict[str, Any]] | None,
) -> Any:
    """Deterministic growing-context multi-turn runner.

    Sends n_turns requests in a single session with growing filler
    prompts.  max_tokens is FIXED across all turns (the key control
    variable).  Each turn's timing is printed inline as it completes.
    """
    n_turns = getattr(args, "n_turns", 10)
    first_turn_tokens = getattr(args, "first_turn_tokens", 5000)
    growth_tokens = getattr(args, "growth_tokens", 2000)
    max_tokens = args.max_tokens  # already parsed by cli_entrypoint

    session_id = f"det-multiturn-{os.urandom(4).hex()}"
    history: list[dict[str, str]] = []

    print(f"Deterministic growing-context multi-turn")
    print(f"  Session:   {session_id}")
    print(f"  Turns:     {n_turns}")
    print(f"  Max tokens: {max_tokens} (FIXED)")
    print(f"  Growth:    +{growth_tokens} tokens/turn starting at {first_turn_tokens}")
    print(f"  Base URL:  {harness.base_url}")
    print()

    for turn in range(n_turns):
        target_tokens = first_turn_tokens + turn * growth_tokens
        filler = _generate_filler(target_tokens, turn + 1)
        task_prompt = (
            f"Write a Python function called 'process_turn_{turn + 1}' "
            f"that takes a list of integers and returns their sum multiplied "
            f"by {turn + 1}. Here is some context to process: {filler}"
        )

        msgs: list[dict[str, str]] = [
            {"role": "system", "content": SYSTEM_PROMPT},
        ]
        if history:
            msgs.extend(history)
        msgs.append({"role": "user", "content": task_prompt})

        result = await harness.submit(
            messages=msgs,
            session_id=session_id,
            max_tokens=max_tokens,
        )

        # Capture assistant response for history (first 200 chars as summary)
        # The harness doesn't return the text, so we append a placeholder
        # that keeps the conversation growing.
        history.append({"role": "user", "content": task_prompt})
        history.append({"role": "assistant", "content": f"[turn {turn + 1} response]"})

        # Per-turn inline output
        status = "OK" if result.error is None else f"ERROR: {result.error}"
        print(
            f"  Turn {turn + 1}/{n_turns} "
            f"(target ~{target_tokens} tok): "
            f"ttft={result.ttft_s:.3f}s  "
            f"tpot={result.tpot_s:.3f}s  "
            f"total={result.total_s:.1f}s  "
            f"tokens={result.token_count}  "
            f"{status}"
        )

    print()
    rep = harness.report()
    return rep


async def run(
    *,
    n_users: int = 5,
    n_turns: int = 4,
    pause_s: float = 3.0,
    max_tokens: int = 120,
    base_url: str | None = None,
    output: str | None = None,
) -> Any:
    """
    Programmatic entry point — useful for pytest parametrisation.

    Runs `n_users` concurrent sessions, each doing `n_turns` turns with
    `pause_s` seconds between turns within a session. Concurrency is
    `n_users` (each user is independent), and per-turn completion is
    captured individually.
    """
    from uuid import uuid4
    harness = BenchmarkHarness(
        base_url=base_url or os.environ.get("COORD_URL", "http://localhost:9000"),
    )
    sem = asyncio.Semaphore(n_users)

    async def _user_session(user_idx: int) -> None:
        sid = f"multi-user-{user_idx:03d}-{uuid4().hex[:8]}"
        history: list[dict[str, str]] = []
        async with sem:
            for turn_idx in range(n_turns):
                msgs = build_turn_messages(
                    history=history if history else None,
                    user_msg=FOLLOWUP_QUESTIONS[turn_idx % len(FOLLOWUP_QUESTIONS)],
                )
                await harness.submit(messages=msgs, session_id=sid, max_tokens=max_tokens)
                if turn_idx + 1 < n_turns:
                    await asyncio.sleep(pause_s)

    await asyncio.gather(*[_user_session(i) for i in range(n_users)])
    rep = harness.report()
    if output:
        harness.save(output, scenario_id="chat_multi_turn")
    return rep


__all__ = ["build_turn_messages", "run", "main"]


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
