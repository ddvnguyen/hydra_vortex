"""
Workload generator 3 — burst load.

Issue #306 generator 3: "N concurrent requests at once, then idle P
seconds, repeat. Tests backpressure and queue depth."

A load-test pattern: fire N requests in parallel, wait for them all,
sleep P seconds, repeat. The default uses a smaller context (1K) so the
per-request prefill is cheap — this is about throughput, not context.

Usage:
    python -m tests.bench.burst --concurrent 50 --cycles 3 --idle-s 10 \\
        --output results/burst.json
"""

from __future__ import annotations

import asyncio
import os
from typing import Any

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

# Smaller than the other generators — this is a load test, not a context test.
SYSTEM_PROMPT = "You are a helpful assistant. Reply briefly."
USER_PROMPT = (
    "List three trade-offs between throughput and latency in a "
    "distributed LLM serving system. One sentence each."
)


def build_messages() -> list[dict[str, str]]:
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": USER_PROMPT},
    ]


@cli_entrypoint(
    build_messages=lambda args: build_messages(),
    scenario_id="burst",
    default_n=20,
    default_concurrency=20,
    default_warmup=3,
    default_max_tokens=50,
)
async def main() -> None:  # pragma: no cover
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


async def run(
    *,
    n_per_cycle: int = 20,
    cycles: int = 3,
    idle_s: float = 10.0,
    max_tokens: int = 50,
    base_url: str | None = None,
    output: str | None = None,
) -> Any:
    """
    Programmatic entry point — useful for pytest parametrisation.

    Runs `cycles` rounds of `n_per_cycle` concurrent requests, then
    `idle_s` seconds idle, then the next round. All requests feed into
    one harness so the report aggregates across cycles.
    """
    from uuid import uuid4
    harness = BenchmarkHarness(
        base_url=base_url or os.environ.get("COORD_URL", "http://localhost:9000"),
    )
    msgs = build_messages()
    sem = asyncio.Semaphore(n_per_cycle)

    async def _one(req_idx: int) -> None:
        async with sem:
            sid = f"burst-{uuid4().hex[:10]}"
            await harness.submit(messages=msgs, session_id=sid, max_tokens=max_tokens)

    for _ in range(cycles):
        await asyncio.gather(*[_one(i) for i in range(n_per_cycle)])
        await asyncio.sleep(idle_s)

    rep = harness.report()
    if output:
        harness.save(output, scenario_id="burst")
    return rep


__all__ = ["build_messages", "run", "main"]


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
