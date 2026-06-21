"""
S8 — worker death mid-request (P100 destructive test).

Issue #306 scenario 8: "Worker death mid-request | Kill P100 VM during
decode | Coordinator detects, fails over to warm slot or 503s |
Detection < 5s, no silent failure".

Submits 3 long-running completions in parallel, then attempts to kill
the P100 hydra-head mid-flight via SSH. Polls `/health` to measure how
fast the Coordinator detects the dead worker and how the in-flight
requests resolve (failover to RTX or 503).

This is a DESTRUCTIVE test. The bench is wrapped in try/except so it
NEVER crashes the test runner. If SSH is not available, the host is
unreachable, or the kill fails, the error is recorded in the report
and the bench still produces JSON. Pass `--no-kill` to skip the
destructive step entirely (safe dry-run).

Validates the Coordinator's worker-death detection and failover path.

Usage:
    python -m tests.bench.s8_worker_death --output results/s8.json
    P100_HOST=192.168.122.21 python -m tests.bench.s8_worker_death
    python -m tests.bench.s8_worker_death --no-kill    # safe dry-run
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time
from uuid import uuid4

import httpx

from tests.bench.harness import BenchmarkHarness, Report, percentile

# 4K context, 200 tokens — long-running, gives the kill time to land mid-decode.
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
    """Return the request body for a single S8 long-running completion."""
    paragraphs = [
        _USER_FILLER(topic=(
            "Queueing, backpressure, and starvation patterns. "
            "How does the system behave when one node dies mid-request?"
        )) for _ in range(14)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


async def _poll_health_unhealthy(
    base_url: str, node: str, timeout_s: float = 30.0, poll_interval_s: float = 0.5,
) -> float | None:
    """
    Poll GET /health until `node` reports healthy=false or the timeout
    elapses. Returns elapsed seconds to detection, or None on timeout.
    """
    deadline = time.monotonic() + timeout_s
    detect_start = time.monotonic()
    async with httpx.AsyncClient(timeout=2.0) as client:
        while time.monotonic() < deadline:
            try:
                r = await client.get(f"{base_url}/health")
                if r.status_code == 200:
                    health = r.json()
                    nodes = health.get("nodes", {}) or {}
                    node_info = nodes.get(node) or {}
                    if node_info.get("healthy") is False:
                        return time.monotonic() - detect_start
            except Exception:
                # Health endpoint may transiently fail during the kill —
                # treat that as a detection signal if it persists, but
                # ignore isolated failures.
                if time.monotonic() - detect_start > 2.0:
                    return time.monotonic() - detect_start
            await asyncio.sleep(poll_interval_s)
    return None


async def _try_ssh_kill(p100_host: str, timeout_s: float = 15.0) -> tuple[bool, str | None]:
    """
    Best-effort SSH to the P100 host and `systemctl --user stop
    hydra-head.service`. Returns (ok, error). `ok=True` means the remote
    command exited 0.
    """
    cmd = [
        "ssh",
        "-o", "BatchMode=yes",
        "-o", f"ConnectTimeout=5",
        p100_host,
        "systemctl --user stop hydra-head.service",
    ]
    try:
        proc = await asyncio.create_subprocess_exec(
            *cmd,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        try:
            stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=timeout_s)
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()
            return False, f"ssh timed out after {timeout_s}s"
        if proc.returncode == 0:
            return True, None
        err = (stderr or b"").decode("utf-8", "replace").strip()
        return False, f"ssh exit={proc.returncode}: {err or '(no stderr)'}"
    except FileNotFoundError:
        return False, "ssh binary not found in PATH"
    except Exception as ex:  # noqa: BLE001 — advisory bench
        return False, f"{type(ex).__name__}: {ex}"


async def main() -> None:
    """Custom CLI form — the destructive step needs flags the harness decorator doesn't expose."""
    parser = argparse.ArgumentParser(description="bench: s8_worker_death")
    parser.add_argument("--base-url", default=os.environ.get("COORD_URL", "http://localhost:9000"))
    parser.add_argument("--model", default="balanced")
    parser.add_argument("--n", type=int, default=3,
                        help="Number of in-flight long-running requests to submit (default 3).")
    parser.add_argument("--p100-host", default=os.environ.get("P100_HOST", "192.168.122.21"),
                        help="SSH target for the P100 hydra-head. Override with $P100_HOST.")
    parser.add_argument("--p100-node-name", default=os.environ.get("P100_NODE_NAME", "p100"),
                        help="Node name in /health response (default 'p100').")
    parser.add_argument("--no-kill", action="store_true",
                        help="Skip the destructive SSH kill (safe dry-run).")
    parser.add_argument("--kill-delay-s", type=float, default=3.0,
                        help="Seconds after submit before triggering the kill (default 3).")
    parser.add_argument("--max-tokens", type=int, default=200)
    parser.add_argument("--output", "-o", default=None)
    args = parser.parse_args()

    messages = _build_messages(args)
    harness = BenchmarkHarness(base_url=args.base_url, model=args.model)

    # Submit N long-running requests in background. We use raw tasks so
    # we can fire the kill *mid-flight* while the harness's
    # gather-and-collect is blocked on these coroutines.
    async def _do_submit(i: int):
        return await harness.submit(
            messages, f"bench-s8-{uuid4().hex[:8]}-{i}",
            max_tokens=args.max_tokens, stream=True,
        )

    tasks = [asyncio.create_task(_do_submit(i)) for i in range(args.n)]

    # Let the requests get into decode before we kill.
    if not args.no_kill:
        await asyncio.sleep(args.kill_delay_s)

    kill_ok, kill_error = (True, "no-kill flag set; skipped")
    if not args.no_kill:
        kill_ok, kill_error = await _try_ssh_kill(args.p100_host)

    # Poll /health for detection.
    detection_s: float | None = None
    if not args.no_kill:
        detection_s = await _poll_health_unhealthy(
            args.base_url, args.p100_node_name, timeout_s=30.0,
        )

    # Now wait for the in-flight requests to settle (success or error).
    results = await asyncio.gather(*tasks, return_exceptions=True)

    # Aggregate.
    rep = harness.report()
    rep.extra["kill_ok"] = kill_ok
    if kill_error:
        rep.extra["kill_error"] = kill_error
    if detection_s is not None:
        rep.extra["detection_s_p95"] = round(detection_s, 3)
    else:
        # Could not detect within 30s. Surface as 30.0 (the timeout) so
        # the comparator still has a number; analyst can flag the fail.
        rep.extra["detection_s_p95"] = 30.0
        rep.extra["detection_note"] = "did not detect within 30s"

    ok_results = [
        r for r in results
        if not isinstance(r, BaseException) and getattr(r, "error", None) is None
    ]
    failover_success_rate = (len(ok_results) / max(1, args.n)) if args.n > 0 else 0.0
    rep.extra["failover_success_rate"] = round(failover_success_rate, 4)
    rep.extra["n_inflight"] = args.n
    rep.extra["n_completed"] = len(ok_results)

    if args.output:
        harness.save(args.output, scenario_id="s8_worker_death")
    print(json.dumps(rep.to_dict(), indent=2))


if __name__ == "__main__":
    asyncio.run(main())
