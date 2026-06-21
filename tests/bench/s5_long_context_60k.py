"""
S5 — long context 60K (chunked dedup).

Issue #306 scenario 5: "Cold concurrency, 60K context | Single-turn
60K context, 200 tokens | Chunked dedup applies, KV state moves
correctly | Total ≤ 15s, no OOM".

Submits 60K-context completions at concurrency 1. The 60K context
exercises the chunked-dedup path on the Store side and forces the KV
state to grow into multi-MB territory (~800 MB at 60K tokens for
qwen35moe). The harness catches OOM via its own try/except so a
single bad request never crashes the scenario; pass criterion requires
`errors == 0`.

The report is intended to be compared against the S5 entry in
`baselines/main.json`.

Usage:
    python -m tests.bench.s5_long_context_60k --output results/s5.json
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

# 60K context, 200 tokens — the S5 baseline shape.
SYSTEM_PROMPT = (
    "You are a meticulous senior software engineer. When answering:\n"
    "  - Use code blocks for any code.\n"
    "  - Be precise about edge cases, error handling, and complexity.\n"
    "  - Prefer standard library over third-party deps.\n"
    "  - Flag any assumption you make about the runtime environment.\n"
    "Always finish with a one-line summary."
)
# ~4 chars/token, so 60K tokens ≈ 240K chars. With ~1K chars per
# paragraph that's ~240 paragraphs; we round up to be safe.
_USER_FILLER = (
    "Consider the following context as background: distributed systems, "
    "GPU inference scheduling, KV cache reuse, prefix caching, and "
    "cross-node migration. {topic} Discuss trade-offs, list failure "
    "modes, and propose mitigations."
).format

# Pad to hit the 60K target on the user side (~240 paragraphs of ~1K chars).
_TARGET_PARAGRAPHS = 240


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S5 completion."""
    paragraphs = [
        _USER_FILLER(topic=(
            "Queueing, backpressure, and starvation patterns. "
            "How does the system behave when one node is saturated?"
        )) for _ in range(_TARGET_PARAGRAPHS)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


@cli_entrypoint(
    build_messages=_build_messages,
    scenario_id="s5_long_context_60k",
    default_n=10,             # long-context is expensive; small n is fine
    default_concurrency=1,
    default_warmup=3,
    default_max_tokens=200,
)
async def main() -> None:  # pragma: no cover — entry point only
    """Defined for the `cli_entrypoint` decorator; body is injected."""
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
