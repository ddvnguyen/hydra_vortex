"""
S1 — warm affinity (same-node multi-turn).

Issue #306 scenario 1: "Warm affinity (same node) | Multi-turn chat,
10 users × 20 turns | TTFT P95 < 50ms (no re-prefill), slot stays
leased across turns | KV stays in VRAM, lease not released prematurely".

Drives 5 users × 10 turns of chat against a single Coordinator, reusing
the same `session_id` per user across turns. Turn 1 is the cold
prefill; turns 2-10 should be warm (< 50ms TTFT, no re-prefill). The
synthetic 5×10 grid is reduced from the spec's 10×20 to keep the run
under ~60s on RTX — the 10×20 form would push us past two minutes,
which is overkill for a regression bench.

The report is intended to be compared against the S1 entry in
`baselines/main.json`.

Usage:
    python -m tests.bench.s1_warm_affinity --output results/s1.json
    python -m tests.bench.s1_warm_affinity --users 5 --turns 10 --pause-s 2
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import BenchmarkHarness, Report, cli_entrypoint

SYSTEM_PROMPT = (
    "You are a meticulous senior software engineer. When answering:\n"
    "  - Use code blocks for any code.\n"
    "  - Be precise about edge cases, error handling, and complexity.\n"
    "  - Prefer standard library over third-party deps.\n"
    "  - Flag any assumption you make about the runtime environment.\n"
    "Always finish with a one-line summary."
)
# ~4 chars/token, so 1K chars per paragraph ≈ 250 tokens. 4 paragraphs
# of filler per turn is plenty for a warm-affinity test (we want the
# slot to be reused, not the literal prompt).
_USER_FILLER = (
    "Consider the following context as background: distributed systems, "
    "GPU inference scheduling, KV cache reuse, prefix caching, and "
    "cross-node migration. {topic} Discuss trade-offs, list failure "
    "modes, and propose mitigations."
).format


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S1 turn.

    `args` is unused — the per-turn content is built by
    `_build_turn_messages(user_idx, turn_idx)` so the warm-affinity
    loop can vary the question per turn. This stub keeps the
    `_build_messages` shape consistent with s4.
    """
    paragraphs = [
        _USER_FILLER(topic=(
            "Queueing, backpressure, and starvation patterns. "
            "How does the system behave when one node is saturated?"
        )) for _ in range(4)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


def _build_turn_messages(user_idx: int, turn_idx: int) -> list[dict]:
    """Return the request body for one turn of one user.

    Varies the topic with `user_idx` and the sub-question with
    `turn_idx` so the slot reuse test isn't hitting the exact same
    prompt — we want the lease + KV cache to be reused, not the
    literal tokens.
    """
    topics = (
        "queueing and backpressure",
        "KV cache reuse and prefix caching",
        "cross-node migration and slot transfer",
        "lease management and watchdog reclaim",
        "P/D split and heterogeneous scheduling",
    )
    topic = topics[user_idx % len(topics)]
    paragraphs = [
        _USER_FILLER(topic=(
            f"{topic}, user {user_idx}, turn {turn_idx}: "
            "discuss the trade-offs and failure modes."
        )) for _ in range(4)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


async def _s1_runner(
    harness: BenchmarkHarness,
    args,
    _messages: list[dict] | None,
) -> Report:
    """Run 5 users × 10 turns of warm-affinity chat and aggregate.

    The outer loop is users; the inner loop is turns. Each user gets
    a single stable `session_id` so the slot stays leased across
    turns; turn 1 of each user is the cold prefill, turns 2..N should
    be warm (TTFT dominated by decode, not prefill).
    """
    users = args.users
    turns = args.turns
    pause_s = args.pause_s

    for u in range(users):
        session_id = f"s1-user-{u:03d}"
        for t in range(turns):
            if t > 0 and pause_s > 0:
                await asyncio.sleep(pause_s)
            messages = _build_turn_messages(u, t)
            await harness.submit(
                messages, session_id,
                max_tokens=args.max_tokens, stream=True,
            )

    return harness.report()


def _s1_extra_args(p) -> None:
    """Register S1-specific CLI flags (--users / --turns / --pause-s)."""
    p.add_argument(
        "--users", type=int, default=5,
        help="Number of distinct user sessions (default 5; spec says 10).",
    )
    p.add_argument(
        "--turns", type=int, default=10,
        help="Number of chat turns per user (default 10; spec says 20).",
    )
    p.add_argument(
        "--pause-s", type=float, default=2.0,
        help="Pause between turns in seconds (default 2.0).",
    )


@cli_entrypoint(
    scenario_id="s1_warm_affinity",
    default_n=50,             # 5 users × 10 turns (cosmetic — runner drives the loop)
    default_concurrency=1,    # per-user turns run sequentially
    default_warmup=3,
    default_max_tokens=200,
    extra_args=_s1_extra_args,
    runner=_s1_runner,
)
async def main() -> None:  # pragma: no cover — entry point only
    """Defined for the `cli_entrypoint` decorator; body is injected."""
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
