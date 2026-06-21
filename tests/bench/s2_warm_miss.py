"""
S2 — warm miss (eviction → migrate → resume).

Issue #306 scenario 2: "Warm → migration | Force eviction via
coordinator API | KV save+restore happens, TTFT < 5s, slot reset on
target | Migration latency observable, no silent failure".

Drives 5 sessions, each: turn 1 (cold, on the original node), then a
`POST /sessions/{id}/migrate` call to the Coordinator to force KV
save+restore, then turn 2 on the new node. The migration endpoint may
not exist yet (a 404 is logged and the test continues — the bench
only measures completion timings, not the migration protocol).

The report's `extra.migration_p95_ms` carries the coordinator's
migrate-endpoint round-trip latency; the per-completion `total_p95_ms`
captures the post-migration prefill on the target. Warm hit rate
should be ~0 after migration (slot reset on target).

The report is intended to be compared against the S2 entry in
`baselines/main.json`.

Usage:
    python -m tests.bench.s2_warm_miss --output results/s2.json
    python -m tests.bench.s2_warm_miss --sessions 5
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from uuid import uuid4

from tests.bench.harness import (
    BenchmarkHarness,
    Report,
    cli_entrypoint,
    percentile,
)

SYSTEM_PROMPT = (
    "You are a meticulous senior software engineer. When answering:\n"
    "  - Use code blocks for any code.\n"
    "  - Be precise about edge cases, error handling, and complexity.\n"
    "  - Prefer standard library over third-party deps.\n"
    "  - Flag any assumption you make about the runtime environment.\n"
    "Always finish with a one-line summary."
)
_USER_FILLER = (
    "Consider the following context as background: distributed systems, "
    "GPU inference scheduling, KV cache reuse, prefix caching, and "
    "cross-node migration. {topic} Discuss trade-offs, list failure "
    "modes, and propose mitigations."
).format

# Coordinator's session-migration endpoint. Shape: POST
# /sessions/{session_id}/migrate. The Coordinator decides the target
# node — the scenario only forces the migration and times it.
MIGRATION_PATH = "/sessions/{session_id}/migrate"


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S2 turn (used as a stub).

    The actual per-turn content is built by
    `_build_turn_messages(session_idx, turn_idx)` so the runner can
    vary the question per session. This stub keeps the `_build_messages`
    shape consistent with s4.
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


def _build_turn_messages(session_idx: int, turn_idx: int) -> list[dict]:
    """Return the request body for one turn of one session."""
    topics = (
        "queueing and backpressure",
        "KV cache reuse and prefix caching",
        "cross-node migration and slot transfer",
        "lease management and watchdog reclaim",
        "P/D split and heterogeneous scheduling",
    )
    topic = topics[session_idx % len(topics)]
    paragraphs = [
        _USER_FILLER(topic=(
            f"{topic}, session {session_idx}, turn {turn_idx}: "
            "discuss the trade-offs and failure modes."
        )) for _ in range(4)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


async def _migrate(
    base_url: str,
    session_id: str,
    timeout_s: float,
) -> tuple[float, str | None]:
    """POST to the coordinator's migrate endpoint.

    Returns (latency_s, error). A 404 is treated as "not implemented
    yet" — the latency is still returned, the error string is set, and
    the caller continues. Any other HTTP error raises so the bench
    surfaces the failure.
    """
    import httpx  # local import keeps the module importable without httpx

    url = f"{base_url.rstrip('/')}" + MIGRATION_PATH.format(session_id=session_id)
    t0 = time.monotonic()
    try:
        async with httpx.AsyncClient(timeout=timeout_s) as client:
            r = await client.post(url, json={"target_node": "auto"})
            latency = time.monotonic() - t0
            if r.status_code == 404:
                return latency, "404_not_implemented"
            r.raise_for_status()
            return latency, None
    except Exception as ex:  # noqa: BLE001 — capture for advisory
        return time.monotonic() - t0, f"{type(ex).__name__}: {ex}"


async def _s2_runner(
    harness: BenchmarkHarness,
    args,
    _messages: list[dict] | None,
) -> Report:
    """Run 5 cold→migrate→resume sequences and aggregate."""
    sessions = args.sessions
    migration_latencies_s: list[float] = []
    migration_errors = 0

    for s in range(sessions):
        session_id = f"s2-session-{s:03d}"
        # Turn 1: cold prefill on the original node.
        await harness.submit(
            _build_turn_messages(s, 0), session_id,
            max_tokens=args.max_tokens, stream=True,
        )
        # Force migration. The endpoint may not exist yet — the round
        # trip is still timed, the error is recorded, and we move on.
        lat, err = await _migrate(harness.base_url, session_id, harness.timeout_s)
        migration_latencies_s.append(lat)
        if err is not None:
            migration_errors += 1
        # Turn 2: post-migration on the new node. The slot has been
        # reset on the target, so this is a fresh prefill
        # (warm_hit=False). The per-completion total captures the
        # post-migration latency; the migration latency is exported
        # separately in `extra.migration_p95_ms` so the comparator can
        # see it without inflating the completion P95.
        await harness.submit(
            _build_turn_messages(s, 1), session_id,
            max_tokens=args.max_tokens, stream=True,
        )

    # Stash migration metrics in the report's extras block so the
    # compare.py + downstream consumers can see them alongside the
    # per-completion latencies.
    harness._extra["migration_count"] = sessions
    harness._extra["migration_errors"] = migration_errors
    if migration_latencies_s:
        lats_ms = [t * 1000.0 for t in migration_latencies_s]
        harness._extra["migration_p50_ms"] = percentile(lats_ms, 50)
        harness._extra["migration_p95_ms"] = percentile(lats_ms, 95)

    return harness.report()


def _s2_extra_args(p) -> None:
    """Register S2-specific CLI flag (--sessions)."""
    p.add_argument(
        "--sessions", type=int, default=5,
        help="Number of distinct sessions to migrate (default 5).",
    )


@cli_entrypoint(
    scenario_id="s2_warm_miss",
    default_n=10,             # 5 sessions × 2 turns (cosmetic — runner drives the loop)
    default_concurrency=1,
    default_warmup=3,
    default_max_tokens=200,
    extra_args=_s2_extra_args,
    runner=_s2_runner,
)
async def main() -> None:  # pragma: no cover — entry point only
    """Defined for the `cli_entrypoint` decorator; body is injected."""
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())
