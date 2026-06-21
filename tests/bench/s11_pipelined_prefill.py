"""
S11 — pipelined prefill (E4 #269 validation).

Issue #306 scenario 11: "Pipelined prefill (E4 #269) | 2 concurrent
requests | Pipelined overlap reduces total wall time | Throughput ≥
1.5x single-request baseline".

For each pair, runs two 4K completions:
  - Concurrent: both requests in parallel (`--concurrent`).
  - Sequential: both requests back-to-back.

Records `speedup = wall_time_sequential / wall_time_concurrent`. The
pass criterion is `speedup_p95 >= 1.5` (on E4); the current main
(pre-E4) will not achieve this and is marked forward-looking.

Validates the pipelined-prefill feature (issue #269) — on the current
main, this records the baseline; the pass-criterion is forward-looking.

Usage:
    python -m tests.bench.s11_pipelined_prefill --output results/s11.json
    python -m tests.bench.s11_pipelined_prefill --n 6 --concurrent 3
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import BenchmarkHarness, Report, percentile

# 4K context, 200 tokens — the pipelined-prefill candidate workload.
SYSTEM_PROMPT = (
    "You are a meticulous senior software engineer. When answering:\n"
    "  - Use code blocks for any code.\n"
    "  - Be precise about edge cases, error handling, and complexity.\n"
    "  - Prefer standard library over third-party deps.\n"
    "Always finish with a one-line summary."
)
_USER_FILLER = (
    "Consider the following context as background: distributed systems, "
    "GPU inference scheduling, KV cache reuse, prefix caching, and "
    "cross-node migration. {topic} Discuss trade-offs, list failure "
    "modes, and propose mitigations."
).format


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S11 4K completion."""
    paragraphs = [
        _USER_FILLER(topic=(
            "Pipelined prefill, request batching, and decode overlap. "
            "How much wall time can be saved by overlapping prefill of "
            "request N+1 with decode of request N?"
        )) for _ in range(14)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


async def _run_concurrent(
    harness: BenchmarkHarness, messages: list[dict], n: int, max_tokens: int, pair_idx: int,
) -> float:
    """Submit `n` requests in parallel; return wall time in seconds."""
    start = time.monotonic()
    tasks = [
        harness.submit(
            messages, f"bench-s11-conc-{pair_idx}-{i}",
            max_tokens=max_tokens, stream=True,
        )
        for i in range(n)
    ]
    await asyncio.gather(*tasks, return_exceptions=True)
    return time.monotonic() - start


async def _run_sequential(
    harness: BenchmarkHarness, messages: list[dict], n: int, max_tokens: int, pair_idx: int,
) -> float:
    """Submit `n` requests back-to-back; return wall time in seconds."""
    start = time.monotonic()
    for i in range(n):
        await harness.submit(
            messages, f"bench-s11-seq-{pair_idx}-{i}",
            max_tokens=max_tokens, stream=True,
        )
    return time.monotonic() - start


async def main() -> None:
    """Custom CLI form — concurrent vs sequential pairs + speedup computation."""
    parser = argparse.ArgumentParser(description="bench: s11_pipelined_prefill")
    parser.add_argument("--base-url", default=os.environ.get("COORD_URL", "http://localhost:9000"))
    parser.add_argument("--model", default="balanced")
    parser.add_argument("--n", type=int, default=4,
                        help="Number of concurrent-vs-sequential pairs to run (default 4).")
    parser.add_argument("--concurrent", type=int, default=2,
                        help="Requests per pair (default 2).")
    parser.add_argument("--max-tokens", type=int, default=200)
    parser.add_argument("--output", "-o", default=None)
    args = parser.parse_args()

    messages = _build_messages(args)
    harness = BenchmarkHarness(base_url=args.base_url, model=args.model)

    speedups: list[float] = []
    conc_walls: list[float] = []
    seq_walls: list[float] = []

    for pair_idx in range(args.n):
        conc_wall = await _run_concurrent(
            harness, messages, args.concurrent, args.max_tokens, pair_idx,
        )
        seq_wall = await _run_sequential(
            harness, messages, args.concurrent, args.max_tokens, pair_idx,
        )
        conc_walls.append(conc_wall)
        seq_walls.append(seq_wall)
        if conc_wall > 0:
            speedups.append(seq_wall / conc_wall)
        else:
            speedups.append(1.0)

    rep = harness.report()
    rep.extra["speedup_p50"] = round(percentile(speedups, 50), 3)
    rep.extra["speedup_p95"] = round(percentile(speedups, 95), 3)
    rep.extra["wall_concurrent_p50_s"] = round(percentile(conc_walls, 50), 3)
    rep.extra["wall_concurrent_p95_s"] = round(percentile(conc_walls, 95), 3)
    rep.extra["wall_sequential_p50_s"] = round(percentile(seq_walls, 50), 3)
    rep.extra["wall_sequential_p95_s"] = round(percentile(seq_walls, 95), 3)
    rep.extra["n_pairs"] = args.n
    rep.extra["concurrent_per_pair"] = args.concurrent

    if rep.extra["speedup_p95"] < 1.5:
        # Forward-looking: pre-E4 the two runs are essentially identical
        # wall time (no pipelining yet). Record the actual number and
        # mark the scenario as pending the feature.
        rep.extra["forward_looking"] = True
        rep.extra["pending_feature"] = "issue-269"
        rep.extra["note"] = "E4 not yet implemented (#269)"

    if args.output:
        harness.save(args.output, scenario_id="s11_pipelined_prefill")
    print(json.dumps(rep.to_dict(), indent=2))


if __name__ == "__main__":
    asyncio.run(main())
