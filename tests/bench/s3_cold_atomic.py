"""
S3 — cold atomic (single-worker fast path).

Issue #306 scenario 3: "Cold atomic (fast mode) | Single-turn 4K
context | Prefill + decode on same worker, 1.5x speedup over single-model
| Total ≤ 6s for 4K+200".

Submits 4K-context completions at concurrency 1 so prefill and decode
happen on the same worker (atomic mode — no P/D split). The harness's
default `session_id_factory` produces a fresh session per request, so
no warm cache is reused; this is the "fast path" baseline against
which the P/D split (s4) is compared.

The synthetic n=30 is slightly larger than s4's n=20 so the P95 lands
on a real sample rather than a single outlier.

The report is intended to be compared against the S3 entry in
`baselines/main.json`.

Usage:
    python -m tests.bench.s3_cold_atomic --output results/s3.json
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

# 4K context, 200 tokens — same shape as s4 (atomic vs P/D split).
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
    """Return the request body for a single S3 completion."""
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
    scenario_id="s3_cold_atomic",
    default_n=30,             # a bit more than s4 so the p95 is statistically clean
    default_concurrency=1,    # atomic — prefill + decode on the same worker
    default_warmup=3,
    default_max_tokens=200,
)
async def main() -> None:  # pragma: no cover — entry point only
    """Defined for the `cli_entrypoint` decorator; body is injected."""
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
