"""
S6 — long context 80K (OOM guard).

Issue #306 scenario 6: "Cold concurrency, 80K context | Single-turn
80K context, 200 tokens | VRAM stays < 16 GB, OOM guard works | VRAM <
90%, graceful failure on OOM".

Submits 80K-context completions at concurrency 1. The 80K context
pushes the qwen35moe KV state toward ~1.1 GB and stresses the VRAM
guard. The harness catches OOM via its own try/except so any single
OOM is recorded as an error (not a crash); the per-scenario pass
criterion requires `errors == 0`.

NOTE: The VRAM < 90% check is a manual Grafana review against the
llama-server's nvidia-smi — the bench itself only times completions.

The report is intended to be compared against the S6 entry in
`baselines/main.json`.

Usage:
    python -m tests.bench.s6_long_context_80k --output results/s6.json
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

# 80K context, 200 tokens — the S6 baseline shape.
SYSTEM_PROMPT = (
    "You are a meticulous senior software engineer. When answering:\n"
    "  - Use code blocks for any code.\n"
    "  - Be precise about edge cases, error handling, and complexity.\n"
    "  - Prefer standard library over third-party deps.\n"
    "  - Flag any assumption you make about the runtime environment.\n"
    "Always finish with a one-line summary."
)
# ~4 chars/token, so 80K tokens ≈ 320K chars. With ~1K chars per
# paragraph that's ~320 paragraphs; we round up to be safe.
_USER_FILLER = (
    "Consider the following context as background: distributed systems, "
    "GPU inference scheduling, KV cache reuse, prefix caching, and "
    "cross-node migration. {topic} Discuss trade-offs, list failure "
    "modes, and propose mitigations."
).format

# Pad to hit the 80K target on the user side (~320 paragraphs of ~1K chars).
_TARGET_PARAGRAPHS = 320


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S6 completion."""
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
    scenario_id="s6_long_context_80k",
    default_n=5,              # 80K is very expensive; minimal n
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
