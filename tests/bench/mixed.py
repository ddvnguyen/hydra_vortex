"""
Workload generator 5 — mixed workload (synthetic).

Issue #306 generator 5: "Replay a recorded session log (opencode
traffic) or generate synthetic mixed-pattern traffic."

The issue notes that real opencode traffic capture is not available
("if not, we generate synthetic traffic"), so this generator emits a
synthetic mix: 60% single-turn chat, 20% short multi-turn (2 turns),
20% long-context (8K). Every request uses a unique session_id so warm
slots do not accumulate — the report's warm_hit_rate should stay low.

Usage:
    python -m tests.bench.mixed --seed 0 --output results/mixed.json
"""

from __future__ import annotations

import asyncio
import os
import random
from typing import Any

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

SYSTEM_PROMPT = "You are a helpful, concise assistant."

_SHORT_PROMPTS: tuple[str, ...] = (
    "What is 2+2? Reply with just the number.",
    "Define backpressure in one sentence.",
    "Name three HTTP status codes and what they mean.",
    "Why is warm-slot reuse faster than cold prefill?",
    "List two P/D split benefits.",
)

_LONG_FILLER = (
    "Consider a distributed LLM serving system with a prefill/decode "
    "split, prefix caching, and cross-node KV migration. Discuss the "
    "trade-offs of placement, scheduling, and failure recovery."
)


def _short_messages(prompt: str) -> list[dict[str, str]]:
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": prompt},
    ]


def _long_messages(target_tokens: int = 8_000) -> list[dict[str, str]]:
    target_chars = target_tokens * 4
    paragraphs: list[str] = []
    while sum(len(p) for p in paragraphs) < target_chars:
        paragraphs.append(_LONG_FILLER)
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "".join(paragraphs)[:target_chars]},
    ]


def _pick_shape(rng: random.Random) -> tuple[str, list[dict[str, str]]]:
    """Pick a shape and return (shape_name, messages). Distribution:
    60% short, 20% multi-turn (2 turns handled by caller), 20% long.
    """
    roll = rng.random()
    if roll < 0.6:
        return "short", _short_messages(rng.choice(_SHORT_PROMPTS))
    if roll < 0.8:
        return "multi", _short_messages(rng.choice(_SHORT_PROMPTS))
    return "long", _long_messages(8_000)


@cli_entrypoint(
    build_messages=lambda args: _short_messages(_SHORT_PROMPTS[0]),
    scenario_id="mixed",
    default_n=20,
    default_concurrency=4,
    default_warmup=3,
    default_max_tokens=100,
)
async def main() -> None:  # pragma: no cover
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


async def run(
    *,
    n: int = 20,
    concurrency: int = 4,
    seed: int = 0,
    max_tokens: int = 100,
    base_url: str | None = None,
    output: str | None = None,
) -> Any:
    """Programmatic entry point — pick shape per request from a seeded RNG."""
    from uuid import uuid4
    rng = random.Random(seed)
    harness = BenchmarkHarness(
        base_url=base_url or os.environ.get("COORD_URL", "http://localhost:9000"),
    )
    sem = asyncio.Semaphore(concurrency)

    async def _one() -> None:
        async with sem:
            shape, msgs = _pick_shape(rng)
            sid = f"mixed-{shape}-{uuid4().hex[:10]}"
            await harness.submit(messages=msgs, session_id=sid, max_tokens=max_tokens)

    await asyncio.gather(*[_one() for _ in range(n)])
    rep = harness.report()
    if output:
        harness.save(output, scenario_id="mixed")
    return rep


__all__ = ["_pick_shape", "run", "main"]


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
