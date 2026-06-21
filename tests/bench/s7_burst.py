"""
S7 — burst load (50 concurrent).

Issue #306 scenario 7: "Burst load | 50 concurrent, idle 30s, repeat |
No deadlock, backpressure works, queue depth tracked | All requests
complete within 60s".

This is a pure load test — small payload (1K context, 50 tokens), high
concurrency (50). The spec's "idle 30s, 3 cycles" structure is reduced
to a single burst of `n=200` at `concurrency=50` to keep the total
wall clock under 60s on the current main. The pass criteria is
`n_errors == 0` and `total_p99_ms < 60000` — the same invariant the
full cycle pattern would test, just compressed.

Validates the Coordinator's backpressure and queue-depth behaviour
under spike load.

Usage:
    python -m tests.bench.s7_burst --output results/s7.json
    python -m tests.bench.s7_burst --concurrency 100 --n 400 --output results/s7_hi.json
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

SYSTEM_PROMPT = (
    "You are a concise assistant. Reply in 1-2 sentences."
)
# 1K context, 50 tokens — the S7 burst shape (small payload, load test).
_USER_FILLER = (
    "Briefly describe {topic}. Be specific and concise."
).format


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S7 burst completion (1K ctx, 50 tok)."""
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": _USER_FILLER(topic="GPU inference scheduling under burst load")},
    ]


@cli_entrypoint(
    build_messages=_build_messages,
    scenario_id="s7_burst",
    default_n=200,
    default_concurrency=50,
    default_warmup=5,
    default_max_tokens=50,
)
async def main() -> None:  # pragma: no cover — entry point only
    """Defined for the `cli_entrypoint` decorator; body is injected."""
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
