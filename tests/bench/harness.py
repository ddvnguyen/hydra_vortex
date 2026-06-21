"""
Reusable benchmark harness for the P/D split scenarios in issue #306.

Goals (from the issue body):
  - Submit completion requests to Hydra.Core (:9000) and capture
    per-request TTFT, TPOT, total, and warm/cold routing labels.
  - Aggregate runs into P50 / P95 / P99 with optional warmup discard.
  - Persist results as JSON so `compare.py` can diff against a baseline.

This module is *advisory* — failure here never blocks CI. The harness is
sync-by-default (asyncio entry points are exposed for pytest) and lives
next to the scenarios so each one can stay short.

Public surface:
  - BenchmarkHarness  — submit + collect + report
  - Report            — P50/P95/P99 + throughput + warm hit rate
  - parse_sse         — small SSE chunk parser used by the scenarios
  - percentile        — plain (sorted, nearest-rank) percentile
"""

from __future__ import annotations

import asyncio
import json
import math
import os
import statistics
import time
from dataclasses import dataclass, field
from typing import Any, AsyncIterator, Awaitable, Callable, Iterable, Mapping, Sequence


# ─── Percentile + report helpers ────────────────────────────────────────

def percentile(values: Sequence[float], pct: float) -> float:
    """Nearest-rank percentile. Returns 0.0 for empty input."""
    if not values:
        return 0.0
    if not 0.0 <= pct <= 100.0:
        raise ValueError(f"pct must be 0..100, got {pct}")
    s = sorted(values)
    rank = max(0, min(len(s) - 1, int(math.ceil(pct / 100.0 * len(s))) - 1))
    return float(s[rank])


@dataclass
class Report:
    """P50/P95/P99 + throughput, with optional warm-hit and error counts."""
    n: int = 0
    errors: int = 0
    ttft_p50_ms: float = 0.0
    ttft_p95_ms: float = 0.0
    ttft_p99_ms: float = 0.0
    tpot_p50_ms: float = 0.0
    tpot_p95_ms: float = 0.0
    total_p50_ms: float = 0.0
    total_p95_ms: float = 0.0
    total_p99_ms: float = 0.0
    throughput_req_per_s: float = 0.0
    warm_hit_rate: float = 0.0
    warm_hits: int = 0
    cold_starts: int = 0
    extra: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "n":                  self.n,
            "errors":             self.errors,
            "ttft_p50_ms":        round(self.ttft_p50_ms, 2),
            "ttft_p95_ms":        round(self.ttft_p95_ms, 2),
            "ttft_p99_ms":        round(self.ttft_p99_ms, 2),
            "tpot_p50_ms":        round(self.tpot_p50_ms, 2),
            "tpot_p95_ms":        round(self.tpot_p95_ms, 2),
            "total_p50_ms":       round(self.total_p50_ms, 2),
            "total_p95_ms":       round(self.total_p95_ms, 2),
            "total_p99_ms":       round(self.total_p99_ms, 2),
            "throughput_req_per_s": round(self.throughput_req_per_s, 4),
            "warm_hit_rate":      round(self.warm_hit_rate, 4),
            "warm_hits":          self.warm_hits,
            "cold_starts":        self.cold_starts,
            **self.extra,
        }


# ─── SSE chunk parser ───────────────────────────────────────────────────

@dataclass
class StreamChunk:
    """A single SSE `data: ...` event from the OpenAI streaming endpoint."""
    raw: dict[str, Any]
    first_token_time: float | None = None  # set on the chunk that carries content


def parse_sse(body: str) -> list[StreamChunk]:
    """
    Parse a complete SSE body (the full text of a streamed completion,
    including the final `data: [DONE]`) into a list of StreamChunks.
    Useful when a test reads the body as a single string for determinism.
    """
    out: list[StreamChunk] = []
    for line in body.splitlines():
        line = line.strip()
        if not line.startswith("data: "):
            continue
        payload = line[len("data: "):]
        if payload == "[DONE]":
            break
        try:
            out.append(StreamChunk(raw=json.loads(payload)))
        except json.JSONDecodeError:
            continue
    return out


async def iter_sse(response: Any) -> AsyncIterator[StreamChunk]:
    """
    Async iterator over an httpx streaming response that yields StreamChunks.
    Tags the first chunk that carries content with `first_token_time` (set
    by the caller — see BenchmarkHarness.submit for the timing pattern).
    """
    async for line in response.aiter_lines():
        line = line.strip()
        if not line.startswith("data: "):
            continue
        payload = line[len("data: "):]
        if payload == "[DONE]":
            break
        try:
            yield StreamChunk(raw=json.loads(payload))
        except json.JSONDecodeError:
            continue


# ─── Request record ─────────────────────────────────────────────────────

@dataclass
class _RequestResult:
    submit_time: float
    first_token_time: float | None
    end_time: float
    token_count: int
    warm_hit: bool
    error: str | None = None

    @property
    def ttft_s(self) -> float:
        if self.first_token_time is None:
            return 0.0
        return self.first_token_time - self.submit_time

    @property
    def total_s(self) -> float:
        return self.end_time - self.submit_time

    @property
    def tpot_s(self) -> float:
        if self.token_count <= 0:
            return 0.0
        decode_time = self.end_time - (self.first_token_time or self.submit_time)
        return decode_time / self.token_count


# ─── Benchmark harness ─────────────────────────────────────────────────

class BenchmarkHarness:
    """
    Submit N completions against the Coordinator and aggregate.

    Usage (script form):
        h = BenchmarkHarness(base_url="http://localhost:9000", model="balanced")
        asyncio.run(h.run(messages=[...], max_tokens=200, n=20, concurrency=2,
                          warmup=5))
        h.save("results/s4.json")

    Usage (pytest form):
        async def test_s4(harness, messages_factory):
            await harness.run(messages_factory(), max_tokens=200, n=10)
            rep = harness.report()
            request.node._bench_payload = rep.to_dict()
    """

    def __init__(
        self,
        base_url: str = "http://localhost:9000",
        model: str = "balanced",
        timeout_s: float = 600.0,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.model = model
        self.timeout_s = timeout_s
        self._results: list[_RequestResult] = []
        self._start_wall: float | None = None
        self._end_wall: float | None = None
        self._extra: dict[str, Any] = {}

    # ── Public entry points ──────────────────────────────────────────

    async def submit(
        self,
        messages: list[dict[str, Any]],
        session_id: str,
        max_tokens: int = 200,
        stream: bool = True,
        temperature: float = 0.0,
        extra_body: Mapping[str, Any] | None = None,
    ) -> _RequestResult:
        """Submit one completion and capture timing/token data."""
        body: dict[str, Any] = {
            "model":       self.model,
            "messages":    messages,
            "max_tokens":  max_tokens,
            "temperature": temperature,
            "stream":      stream,
            "session_id":  session_id,
        }
        if extra_body:
            body.update(extra_body)

        submit_time = time.monotonic()
        first_token_time: float | None = None
        token_count = 0
        warm_hit = False
        error: str | None = None
        import httpx  # local import keeps the module importable without httpx
        try:
            async with httpx.AsyncClient(timeout=self.timeout_s) as client:
                async with client.stream(
                    "POST", f"{self.base_url}/v1/chat/completions", json=body,
                ) as resp:
                    resp.raise_for_status()
                    async for chunk in iter_sse(resp):
                        choices = chunk.raw.get("choices") or []
                        if not choices:
                            continue
                        delta = choices[0].get("delta", {}) or {}
                        content = delta.get("content") or delta.get("reasoning_content") or ""
                        if content:
                            token_count += 1
                            if first_token_time is None:
                                first_token_time = time.monotonic()
                        # Detect warm hit from the response if the server
                        # signals it (e.g. via custom metadata in the chunk).
                        meta = chunk.raw.get("hydra") or {}
                        if meta.get("warm_hit"):
                            warm_hit = True
        except Exception as ex:  # noqa: BLE001 — capture all for advisory bench
            error = f"{type(ex).__name__}: {ex}"
        end_time = time.monotonic()
        return _RequestResult(
            submit_time=submit_time,
            first_token_time=first_token_time,
            end_time=end_time,
            token_count=token_count,
            warm_hit=warm_hit,
            error=error,
        )

    async def run(
        self,
        messages: list[dict[str, Any]],
        *,
        max_tokens: int = 200,
        n: int = 20,
        concurrency: int = 1,
        warmup: int = 0,
        session_id_factory=None,
        stream: bool = True,
    ) -> Report:
        """
        Run `n` completions, optionally discarding `warmup` results, and
        return an aggregated `Report`. Uses an asyncio.Semaphore to cap
        in-flight requests to `concurrency`.
        """
        sid_factory = session_id_factory or (lambda: f"bench-{os.urandom(4).hex()}")

        # Warmup — fire-and-forget, never recorded
        for _ in range(warmup):
            await self.submit(messages, sid_factory(), max_tokens=max_tokens, stream=stream)

        self._start_wall = time.monotonic()
        sem = asyncio.Semaphore(concurrency)

        async def _one(i: int) -> _RequestResult:
            async with sem:
                return await self.submit(
                    messages, sid_factory(), max_tokens=max_tokens, stream=stream,
                )

        tasks = [asyncio.create_task(_one(i)) for i in range(n)]
        results = await asyncio.gather(*tasks, return_exceptions=False)
        self._end_wall = time.monotonic()
        self._results.extend(results)
        return self.report()

    def report(self) -> Report:
        if not self._results:
            return Report()
        ok = [r for r in self._results if r.error is None and r.first_token_time is not None]
        ttfst = [r.ttft_s * 1000.0 for r in ok]
        tpots = [r.tpot_s * 1000.0 for r in ok for _ in range(max(1, r.token_count))]
        totals = [r.total_s * 1000.0 for r in ok]
        elapsed = (self._end_wall or time.monotonic()) - (self._start_wall or self._end_wall or time.monotonic())
        elapsed = max(elapsed, 1e-9)
        warm = sum(1 for r in self._results if r.warm_hit)
        return Report(
            n=len(self._results),
            errors=sum(1 for r in self._results if r.error is not None),
            ttft_p50_ms=percentile(ttfst, 50),
            ttft_p95_ms=percentile(ttfst, 95),
            ttft_p99_ms=percentile(ttfst, 99),
            tpot_p50_ms=percentile(tpots, 50) if tpots else 0.0,
            tpot_p95_ms=percentile(tpots, 95) if tpots else 0.0,
            total_p50_ms=percentile(totals, 50),
            total_p95_ms=percentile(totals, 95),
            total_p99_ms=percentile(totals, 99),
            throughput_req_per_s=len(self._results) / elapsed,
            warm_hits=warm,
            cold_starts=max(0, len(self._results) - warm),
            warm_hit_rate=(warm / len(self._results)) if self._results else 0.0,
            extra=self._extra,
        )

    # ── Persistence ─────────────────────────────────────────────────

    def save(self, path: str, scenario_id: str = "ad-hoc", **extra: Any) -> None:
        """Write the current report + raw results to JSON for later comparison."""
        rep = self.report()
        payload = {
            "scenario_id": scenario_id,
            "model":       self.model,
            "base_url":    self.base_url,
            "n_results":   len(self._results),
            "report":      rep.to_dict(),
            "results": [
                {
                    "submit_time":     r.submit_time,
                    "first_token_time": r.first_token_time,
                    "end_time":        r.end_time,
                    "token_count":     r.token_count,
                    "ttft_s":          r.ttft_s,
                    "tpot_s":          r.tpot_s,
                    "total_s":         r.total_s,
                    "warm_hit":        r.warm_hit,
                    "error":           r.error,
                }
                for r in self._results
            ],
            **extra,
        }
        out_dir = os.path.dirname(path) or "."
        os.makedirs(out_dir, exist_ok=True)
        with open(path, "w") as f:
            json.dump(payload, f, indent=2)


# ─── CLI form (so a scenario can be `python -m tests.bench.s4 ...`) ────

async def _cli_run(
    harness: BenchmarkHarness,
    messages: list[dict[str, Any]],
    *,
    n: int,
    concurrency: int,
    warmup: int,
    max_tokens: int,
    output: str | None,
    scenario_id: str,
) -> Report:
    await harness.run(
        messages, max_tokens=max_tokens, n=n, concurrency=concurrency, warmup=warmup,
    )
    rep = harness.report()
    if output:
        harness.save(output, scenario_id=scenario_id)
    return rep


def cli_entrypoint(  # noqa: PLR0913 — argparse-bridge helper
    *,
    build_messages: Callable[[Any], list[dict[str, Any]]] | None = None,
    scenario_id: str,
    model: str = "balanced",
    base_url: str | None = None,
    default_n: int = 20,
    default_concurrency: int = 1,
    default_warmup: int = 3,
    default_max_tokens: int = 200,
    extra_args: Callable[[Any], None] | None = None,
    runner: Callable[["BenchmarkHarness", Any, list[dict[str, Any]] | None], "Awaitable[Report]"] | None = None,
):
    """
    Decorator that wires a scenario's `build_messages` to argparse so a
    scenario file is both a pytest entry point and a runnable script.

    Usage (simple — most scenarios):
        @cli_entrypoint(
            build_messages=lambda args: [...],
            scenario_id="s4_cold_concurrency",
        )
        async def main(): ...

    Usage (custom — extra CLI flags + a per-request driver):
        @cli_entrypoint(
            scenario_id="s1_warm_affinity",
            extra_args=lambda p: (
                p.add_argument("--users",   type=int,   default=5),
                p.add_argument("--turns",   type=int,   default=10),
                p.add_argument("--pause-s", type=float, default=2.0),
            ),
            runner=my_runner,
        )
        async def main(): ...

    `extra_args(parser)` registers scenario-specific CLI flags; the
    `runner(harness, args, messages)` coroutine drives the workload
    (e.g. a multi-turn chat loop) and returns a `Report`. The wrapper
    still handles `harness.save()` and JSON output around the runner.
    `build_messages` is optional when `runner` is supplied.
    """
    import argparse
    import functools

    def _decorator(main_coro):
        @functools.wraps(main_coro)
        async def _wrapper():
            parser = argparse.ArgumentParser(description=f"bench: {scenario_id}")
            parser.add_argument("--base-url", default=base_url or os.environ.get("COORD_URL", "http://localhost:9000"))
            parser.add_argument("--model", default=model)
            parser.add_argument("--n", type=int, default=default_n)
            parser.add_argument("--concurrency", type=int, default=default_concurrency)
            parser.add_argument("--warmup", type=int, default=default_warmup)
            parser.add_argument("--max-tokens", type=int, default=default_max_tokens)
            parser.add_argument("--duration-s", type=float, default=None,
                                help="Override n with a wall-clock duration.")
            parser.add_argument("--output", "-o", default=None)
            if extra_args is not None:
                extra_args(parser)
            args = parser.parse_args()

            harness = BenchmarkHarness(base_url=args.base_url, model=args.model)
            messages: list[dict[str, Any]] | None = (
                build_messages(args) if build_messages is not None else None
            )

            if runner is not None:
                # Custom driver — scenario owns the per-request loop.
                rep = await runner(harness, args, messages)
                if args.output:
                    harness.save(args.output, scenario_id=scenario_id)
            elif messages is None:
                raise RuntimeError(
                    f"cli_entrypoint({scenario_id!r}): build_messages is required "
                    "when no custom runner is provided"
                )
            else:
                # `messages` is non-None here; type checkers should narrow.
                if args.duration_s:
                    # Run for `duration_s` wall clock instead of fixed request count
                    deadline = time.monotonic() + args.duration_s
                    n = 0
                    while time.monotonic() < deadline:
                        await harness.submit(
                            messages, f"bench-{n}",
                            max_tokens=args.max_tokens, stream=True,
                        )
                        n += 1
                    rep = harness.report()
                    if args.output:
                        harness.save(args.output, scenario_id=scenario_id)
                else:
                    rep = await _cli_run(
                        harness, messages,
                        n=args.n, concurrency=args.concurrency, warmup=args.warmup,
                        max_tokens=args.max_tokens, output=args.output,
                        scenario_id=scenario_id,
                    )
            print(json.dumps(rep.to_dict(), indent=2))
        return _wrapper
    return _decorator


if __name__ == "__main__":
    # `python -m tests.bench.harness` is a tiny smoke test of the harness
    # itself: it submits a trivial completion against a live Coordinator
    # and prints the report. Useful for verifying the install.
    import sys

    async def _smoke() -> None:
        url = os.environ.get("COORD_URL", "http://localhost:9000")
        h = BenchmarkHarness(base_url=url)
        msgs = [{"role": "user", "content": "Reply with the single word OK."}]
        await h.run(msgs, max_tokens=4, n=1, concurrency=1, warmup=0)
        print(json.dumps(h.report().to_dict(), indent=2))

    try:
        asyncio.run(_smoke())
    except Exception as ex:  # noqa: BLE001
        print(f"smoke test failed: {ex}", file=sys.stderr)
        sys.exit(1)
