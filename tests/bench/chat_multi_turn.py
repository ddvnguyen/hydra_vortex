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


@cli_entrypoint(
    build_messages=lambda args: build_turn_messages(),
    scenario_id="chat_multi_turn",
    default_n=20,
    default_concurrency=1,
    default_warmup=3,
    default_max_tokens=120,
)
async def main() -> None:  # pragma: no cover
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


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
