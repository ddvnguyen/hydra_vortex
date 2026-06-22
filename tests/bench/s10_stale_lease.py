"""
S10 — stale lease (V2 #299 watchdog validation).

Issue #306 scenario 10: "Stale lease (V2 #299) | Simulate lost
NotifyStreamComplete | Watchdog reclaims within 60s | Lease gone within
60s, no slot leak".

Submits a single completion, abruptly closes the HTTP connection
mid-stream (simulating a lost NotifyStreamComplete), then waits up to
`--watch-timeout-s` (default 60s) and queries `/status` to see whether
the slot was reclaimed by the eviction watchdog.

On the current main (pre-V2), the watchdog is NOT yet implemented —
this scenario will report the lease as leaked and mark
`pending_feature: "issue-299"`. The pass criterion is forward-looking.

The scenario deliberately does NOT assert. It records the measured
reclaim time so the analyst can see when the watchdog starts working.

Validates the V2 coordinator's eviction watchdog (issue #299).

Usage:
    python -m tests.bench.s10_stale_lease --output results/s10.json
    python -m tests.bench.s10_stale_lease --watch-timeout-s 30    # shorter wait
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import time
from uuid import uuid4

import httpx

from tests.bench.harness import BenchmarkHarness, Report

# 2K context, 200 tokens — long enough to keep the stream open across
# the abrupt-close window.
SYSTEM_PROMPT = (
    "You are a meticulous senior software engineer. Discuss trade-offs, "
    "list failure modes, and propose mitigations."
)
_USER_FILLER = (
    "Consider the following context as background: distributed systems, "
    "GPU inference scheduling, KV cache reuse, prefix caching, "
    "cross-node migration. {topic} Discuss trade-offs."
).format


def _build_messages(args) -> list[dict]:
    """Return the request body for a single S10 long-streamed completion."""
    paragraphs = [
        _USER_FILLER(topic=(
            "Lease management, eviction, and stale-lease recovery. "
            "What happens when a client disconnects mid-stream?"
        )) for _ in range(7)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


async def _abrupt_close_stream(
    base_url: str, model: str, session_id: str, messages: list[dict],
    max_tokens: int, min_chunks: int = 5, open_timeout_s: float = 10.0,
) -> tuple[int, str | None]:
    """
    Open a streaming completion and abruptly close the HTTP connection
    after at least `min_chunks` SSE events have been received. Returns
    (chunks_received, error). This simulates a lost NotifyStreamComplete
    — the Coordinator sees the connection drop without a final done.
    """
    body = {
        "model":      model,
        "messages":   messages,
        "max_tokens": max_tokens,
        "stream":     True,
        "session_id": session_id,
    }
    chunks = 0
    try:
        async with httpx.AsyncClient(timeout=open_timeout_s) as client:
            async with client.stream(
                "POST", f"{base_url}/v1/chat/completions", json=body,
            ) as resp:
                resp.raise_for_status()
                # Read enough chunks to ensure we're mid-decode, then
                # break out of the context manager — httpx will close
                # the underlying TCP connection without sending a clean
                # EOF, which is the abrupt-close we want.
                async for line in resp.aiter_lines():
                    line = line.strip()
                    if not line.startswith("data: "):
                        continue
                    payload = line[len("data: "):]
                    if payload == "[DONE]":
                        break
                    chunks += 1
                    if chunks >= min_chunks:
                        break
    except Exception as ex:  # noqa: BLE001 — advisory bench
        return chunks, f"{type(ex).__name__}: {ex}"
    return chunks, None


async def _is_lease_present(base_url: str, session_id: str, timeout_s: float = 5.0) -> bool:
    """Query /status and return True if `session_id` is still in the active list."""
    try:
        async with httpx.AsyncClient(timeout=timeout_s) as client:
            r = await client.get(f"{base_url}/status")
            r.raise_for_status()
            payload = r.json()
            sessions = ((payload.get("sessions") or {}).get("sessions")) or []
            for s in sessions:
                sid = s.get("id") or s.get("session_id") or s.get("SessionId")
                if sid == session_id:
                    return True
    except Exception:  # noqa: BLE001
        # If /status is unreachable, treat as "unknown / not reclaimed".
        return True
    return False


async def main() -> None:
    """Custom CLI form — abrupt-close + watchdog wait + /status probe."""
    parser = argparse.ArgumentParser(description="bench: s10_stale_lease")
    parser.add_argument("--base-url", default=os.environ.get("COORD_URL", "http://localhost:9000"))
    parser.add_argument("--model", default="balanced")
    parser.add_argument("--n", type=int, default=1,
                        help="Number of stale-lease probes to run (default 1).")
    parser.add_argument("--max-tokens", type=int, default=200)
    parser.add_argument("--watch-timeout-s", type=float, default=60.0,
                        help="How long to wait for the watchdog to reclaim (default 60).")
    parser.add_argument("--poll-interval-s", type=float, default=5.0,
                        help="How often to re-query /status (default 5s).")
    parser.add_argument("--output", "-o", default=None)
    args = parser.parse_args()

    messages = _build_messages(args)
    harness = BenchmarkHarness(base_url=args.base_url, model=args.model)

    reclaim_samples_s: list[float] = []
    leases_still_held: list[str] = []

    for i in range(args.n):
        session_id = f"bench-s10-{uuid4().hex[:8]}-{i}"

        # Open the stream, read a few chunks, then abruptly close.
        chunks, err = await _abrupt_close_stream(
            args.base_url, args.model, session_id, messages,
            max_tokens=args.max_tokens, min_chunks=5,
        )

        # Record the abrupt-close metadata on the harness so it lands in
        # the report's `extra` block.
        rep_partial = harness.report()
        rep_partial.extra[f"session_{i}_chunks"] = chunks
        if err:
            rep_partial.extra[f"session_{i}_error"] = err

        # Watch for the watchdog to reclaim the slot.
        watch_start = time.monotonic()
        deadline = watch_start + args.watch_timeout_s
        reclaimed = False
        while time.monotonic() < deadline:
            still_held = await _is_lease_present(args.base_url, session_id)
            if not still_held:
                reclaim_samples_s.append(time.monotonic() - watch_start)
                reclaimed = True
                break
            await asyncio.sleep(args.poll_interval_s)

        if not reclaimed:
            leases_still_held.append(session_id)
            # Use the watch-timeout as the measured (failed) reclaim time
            # so the comparator still has a number.
            reclaim_samples_s.append(args.watch_timeout_s)

    from tests.bench.harness import percentile

    rep = harness.report()
    rep.extra["watchdog_reclaim_s_p95"] = round(percentile(reclaim_samples_s, 95), 3)
    rep.extra["n_probes"] = args.n
    rep.extra["leases_still_held"] = leases_still_held
    if leases_still_held:
        # Forward-looking flag — the watchdog is not yet reclaiming.
        rep.extra["forward_looking"] = True
        rep.extra["pending_feature"] = "issue-299"
        rep.extra["note"] = "watchdog not yet implemented (V2 #299)"

    if args.output:
        harness.save(args.output, scenario_id="s10_stale_lease")
    print(json.dumps(rep.to_dict(), indent=2))


if __name__ == "__main__":
    asyncio.run(main())
