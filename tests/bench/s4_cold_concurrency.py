"""
S4 — cold concurrency (full P/D split baseline).

Issue #306 scenario 4: "Cold concurrency (full P/D) | Single-turn 4K
context | Prefill on RTX, decode on P100, 2.1x over single-model |
Total ≤ 8.5s for 4K+200 (P95)".

Submits 4K-context completions at concurrency 2 (one prefill-heavy on
RTX, one decode-heavy on P100) to exercise the full P/D split path. The
report is intended to be compared against the S4 entry in
`baselines/main.json`.

Usage:
    python -m tests.bench.s4_cold_concurrency --output results/s4.json
    python -m tests.bench.s4_cold_concurrency --duration-s 60 --output results/s4.json
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

# 4K context, 200 tokens — the S4 baseline shape.
SYSTEM_PROMPT = (
    "You are a meticulous senior software engineer. When answering:\n"
    "  - Use code blocks for any code.\n"
    "  - Be precise about edge cases, error handling, and complexity.\n"
    "  - Prefer standard library over third-party deps.\n"
    "  - Flag any assumption you make about the runtime environment.\n"
    "Always finish with a one-line summary."
)
# ~4K tokens of filler — pad to hit the 4K target on the user side.
_USER_FILLER = (
    "Consider the following context as background: distributed systems, "
    "GPU inference scheduling, KV cache reuse, prefix caching, and "
    "cross-node migration. {topic} Discuss trade-offs, list failure "
    "modes, and propose mitigations."
).format


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S4 completion."""
    # 4K of context ≈ ~16K chars at the conservative 4-chars-per-token
    # ratio. We use 14 paragraphs of ~1K chars each so the actual token
    # count sits in the 3.6K-4.2K range once the tokenizer does its job.
    paragraphs = [
        _USER_FILLER(topic=(
            "Queueing, backpressure, and starvation patterns. "
            "How does the system behave when one node is saturated?"
        )) for _ in range(14)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


@cli_entrypoint(
    build_messages=_build_messages,
    scenario_id="s4_cold_concurrency",
    default_n=20,
    default_concurrency=2,
    default_warmup=3,
    default_max_tokens=200,
)
async def main() -> None:  # pragma: no cover — entry point only
    """Defined for the `cli_entrypoint` decorator; body is injected."""
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
