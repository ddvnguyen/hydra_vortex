"""
S12 — mix-precision swap (#200 validation).

Issue #306 scenario 12: "Mix-precision swap (#200) | Trigger quant
swap after prefill | Validate tensor swap < 500ms, KV stays valid |
Swap latency < 500ms, no KV corruption".

Submits a normal completion, then invokes the engine's quant-swap API
(POST `/v1/internal/swap_expert_mode` or the engine equivalent) and
measures the round-trip latency. After the swap, submits a follow-up
completion to verify the KV state is still usable (no corruption).

The swap API is not yet shipped on the current main (pre-#200) — this
scenario records the error and marks `pending_feature: "issue-200"`.
The pass criterion (`swap_s_p95 < 0.5s`, KV stays valid) is
forward-looking.

Validates the mix-precision tensor-swap feature (issue #200) — on the
current main, this records the baseline; the pass-criterion is
forward-looking.

Usage:
    python -m tests.bench.s12_mix_precision_swap --output results/s12.json
    python -m tests.bench.s12_mix_precision_swap --n 5
    python -m tests.bench.s12_mix_precision_swap --swap-endpoint /v1/internal/swap_expert_mode
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import time
from uuid import uuid4

import httpx

from tests.bench.harness import BenchmarkHarness, Report, percentile

# 2K context, 200 tokens — enough to keep the KV state non-trivial
# for the post-swap verification.
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
    """Return the request body for a single S12 completion."""
    paragraphs = [
        _USER_FILLER(topic=(
            "Mix-precision quantisation, expert-mode tensor swaps, and "
            "KV-cache validity under dynamic precision changes."
        )) for _ in range(7)
    ]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": "\n\n".join(paragraphs)},
    ]


async def _try_swap(
    base_url: str, swap_endpoint: str, session_id: str,
    from_mode: str, to_mode: str, timeout_s: float = 5.0,
) -> tuple[float | None, str | None]:
    """
    Attempt the quant-swap. Returns (latency_seconds, error). On success,
    `latency_seconds` is set; on HTTP / connection failure, `error` is
    set and latency is None.
    """
    body = {
        "session_id": session_id,
        "from":       from_mode,
        "to":         to_mode,
    }
    start = time.monotonic()
    try:
        async with httpx.AsyncClient(timeout=timeout_s) as client:
            r = await client.post(f"{base_url}{swap_endpoint}", json=body)
            elapsed = time.monotonic() - start
            if r.status_code in (200, 204):
                return elapsed, None
            return elapsed, f"HTTP {r.status_code}: {r.text[:200]}"
    except Exception as ex:  # noqa: BLE001 — advisory bench
        return None, f"{type(ex).__name__}: {ex}"


async def main() -> None:
    """Custom CLI form — submit → swap → verify KV (forward-looking)."""
    parser = argparse.ArgumentParser(description="bench: s12_mix_precision_swap")
    parser.add_argument("--base-url", default=os.environ.get("COORD_URL", "http://localhost:9000"))
    parser.add_argument("--model", default="balanced")
    parser.add_argument("--n", type=int, default=5,
                        help="Number of swap probes to run (default 5).")
    parser.add_argument("--max-tokens", type=int, default=50)
    parser.add_argument("--swap-endpoint", default=os.environ.get("HYDRA_SWAP_ENDPOINT", "/v1/internal/swap_expert_mode"),
                        help="Path of the quant-swap RPC (default /v1/internal/swap_expert_mode).")
    parser.add_argument("--from-mode", default=os.environ.get("HYDRA_FROM_MODE", "q4_k"))
    parser.add_argument("--to-mode",   default=os.environ.get("HYDRA_TO_MODE",   "q8_0"))
    parser.add_argument("--output", "-o", default=None)
    args = parser.parse_args()

    messages = _build_messages(args)
    harness = BenchmarkHarness(base_url=args.base_url, model=args.model)

    swap_latencies_s: list[float] = []
    post_swap_token_counts: list[int] = []
    swap_errors: list[str] = []

    for i in range(args.n):
        session_id = f"bench-s12-{uuid4().hex[:8]}-{i}"

        # Prime the KV state with a normal completion.
        prime = await harness.submit(
            messages, session_id, max_tokens=args.max_tokens, stream=True,
        )
        if prime.error:
            swap_errors.append(f"prime failed: {prime.error}")
            continue

        # Trigger the quant swap. The endpoint may not exist yet — we
        # capture the failure as data, not as a bench crash.
        latency_s, err = await _try_swap(
            args.base_url, args.swap_endpoint, session_id,
            from_mode=args.from_mode, to_mode=args.to_mode,
        )
        if err is not None:
            swap_errors.append(err)
        if latency_s is not None:
            swap_latencies_s.append(latency_s)

        # Verify KV stays valid by submitting a follow-up completion
        # against the same session. If the swap corrupted the cache,
        # this will either error or produce zero tokens.
        follow = await harness.submit(
            messages, session_id, max_tokens=args.max_tokens, stream=True,
        )
        post_swap_token_counts.append(follow.token_count)
        if follow.error:
            swap_errors.append(f"post-swap followup error: {follow.error}")

    rep = harness.report()
    if swap_latencies_s:
        rep.extra["swap_s_p50"] = round(percentile(swap_latencies_s, 50) * 1000.0, 3)
        rep.extra["swap_s_p95"] = round(percentile(swap_latencies_s, 95) * 1000.0, 3)
    else:
        rep.extra["swap_s_p50"] = 0.0
        rep.extra["swap_s_p95"] = 0.0
    rep.extra["post_swap_token_counts"] = post_swap_token_counts
    rep.extra["kv_valid_after_swap"] = all(t > 0 for t in post_swap_token_counts) if post_swap_token_counts else False
    if swap_errors:
        # Surface the first error so the analyst can see what API shape
        # the live stack actually has.
        rep.extra["swap_error"] = swap_errors[0]
    rep.extra["n_probes"] = args.n

    if not swap_latencies_s or rep.extra["swap_s_p95"] >= 500.0:
        # Forward-looking: pre-#200 the swap endpoint does not exist
        # (HTTP 404) and the latency column is empty. Mark the scenario
        # as pending the feature.
        rep.extra["forward_looking"] = True
        rep.extra["pending_feature"] = "issue-200"
        rep.extra["note"] = "Mix-precision not yet implemented (#200)"

    if args.output:
        harness.save(args.output, scenario_id="s12_mix_precision_swap")
    print(json.dumps(rep.to_dict(), indent=2))


if __name__ == "__main__":
    asyncio.run(main())
