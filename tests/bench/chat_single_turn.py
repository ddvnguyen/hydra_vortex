"""
Workload generator 1 — single-turn chat (4K context, 200 tokens).

Issue #306 generator 1: "1 user, 1 request, 4K context, 200 tokens, no
followups, 5 min sustained."

A thin convenience wrapper over `BenchmarkHarness` for the most basic
shape — one completion, no followups, fixed context size. Used by S3
and S4 in particular, and is the canonical "smoke test" workload when
the live stack comes up.

Usage (script form):
    python -m tests.bench.chat_single_turn --output results/single_turn.json

Usage (programmatic form):
    from tests.bench.chat_single_turn import build_messages, run
    await run(n=10, concurrency=1, output="results/single_turn.json")
"""

from __future__ import annotations

import asyncio
from typing import Any

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

SYSTEM_PROMPT = (
    "You are a helpful, concise assistant. Answer in a single short paragraph."
)

# Pad to ~4K tokens of context.
_FILLER = (
    "Background: distributed GPU inference, KV cache reuse, prefill/decode "
    "split, prefix caching, and request scheduling. Consider both happy "
    "paths and failure modes."
)


def build_messages(*, topic: str = "queueing and backpressure") -> list[dict[str, Any]]:
    """Return a 4K-context single-turn message list."""
    paragraphs = "\n\n".join(
        f"{_FILLER} Topic: {topic} (#{i}). Discuss trade-offs."
        for i in range(14)
    )
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": paragraphs},
    ]


@cli_entrypoint(
    build_messages=lambda args: build_messages(),
    scenario_id="chat_single_turn",
    default_n=20,
    default_concurrency=1,
    default_warmup=3,
    default_max_tokens=200,
)
async def main() -> None:  # pragma: no cover — entry point only
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


async def run(
    *,
    n: int = 20,
    concurrency: int = 1,
    warmup: int = 3,
    max_tokens: int = 200,
    base_url: str | None = None,
    output: str | None = None,
) -> Any:
    """Programmatic entry point — useful for pytest parametrisation."""
    harness = BenchmarkHarness(
        base_url=base_url or os.environ.get("COORD_URL", "http://localhost:9000"),
    )
    await harness.run(
        build_messages(), max_tokens=max_tokens, n=n,
        concurrency=concurrency, warmup=warmup,
    )
    rep = harness.report()
    if output:
        harness.save(output, scenario_id="chat_single_turn")
    return rep


if __name__ == "__main__":
    import asyncio as _asyncio
    import os
    _asyncio.run(main())


# Re-export for the from-import in __init__.py
__all__ = ["build_messages", "run", "main"]
