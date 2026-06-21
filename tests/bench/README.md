# Hydra Bench Suite (Issue #306)

A reusable benchmark suite for validating the **M-Perf** heterogeneous
P/D split. The suite is **advisory** — runs produce reports and the
`compare.py` exit code is advisory, never CI-blocking. CI integration
is tracked in a follow-up issue (see "Adding to CI" below).

## Quick start

```bash
# From the project root, with Hydra.Core + llama-server(s) up:
python -m tests.bench.s4_cold_concurrency --output results/s4.json

# Compare against the committed baseline:
python -m tests.bench.compare tests/bench/baselines/main.json results/s4.json
```

The CLI form of every scenario is `python -m tests.bench.<scenario>`. Each
script also exposes an `async def main()` so pytest can collect it.

## Layout

```
tests/bench/
  __init__.py
  conftest.py              # pytest fixtures + result-saving hook
  harness.py               # BenchmarkHarness, Report, parse_sse, percentile
  compare.py               # A/B comparator with --max-regression
  baselines/main.json      # captured main-branch reference
  chat_single_turn.py      # Generator 1
  chat_multi_turn.py       # Generator 2 (warm sessions)
  burst.py                 # Generator 3
  long_context.py          # Generator 4 (40K-80K)
  mixed.py                 # Generator 5 (synthetic or replay)
  s1_warm_affinity.py      # Scenario 1
  s2_warm_miss.py          # Scenario 2 (eviction + restore)
  s3_cold_atomic.py        # Scenario 3
  s4_cold_concurrency.py   # Scenario 4 (full P/D baseline)
  s5_long_context_60k.py   # Scenario 5
  s6_long_context_80k.py   # Scenario 6
  s7_burst.py              # Scenario 7 (50 concurrent)
  s8_worker_death.py       # Scenario 8 (mid-request failover)
  s9_npast_guard.py        # Scenario 9 (Bug #201 validation)
  s10_stale_lease.py       # Scenario 10 (V2 #299 watchdog)
  s11_pipelined_prefill.py # Scenario 11 (E4 #269)
  s12_mix_precision_swap.py# Scenario 12 (tensor swap)
  README.md
```

## Running all scenarios

```bash
# All scenarios in series (~10 min for S1..S10, S11/S12 land with their features)
for s in s1_warm_affinity s2_warm_miss s3_cold_atomic s4_cold_concurrency \
         s5_long_context_60k s6_long_context_80k s7_burst s8_worker_death \
         s9_npast_guard s10_stale_lease; do
  python -m tests.bench.$s --output results/${s}.json --warmup 5
done

python -m tests.bench.compare tests/bench/baselines/main.json results/s1_warm_affinity.json
# repeat for each result file
```

## Methodology

- **Warmup**: 30 s of discarded traffic (caches, JIT, model load) —
  `--warmup` controls request count; `--duration-s` switches to wall-clock.
- **Measurement**: each scenario is its own request budget (`--n`).
- **Concurrency**: `--concurrency` (default 1) caps in-flight requests.
- **P50/P95/P99**: nearest-rank percentile on the per-request samples.
- **Throughput**: completed requests / wall clock (excludes warmup).
- **Warm-hit rate**: share of requests the Coordinator served from a
  warm slot (signalled by the chunk metadata if the server reports it;
  see `harness.submit` for the detection point).

## Interpreting `compare.py`

```
S4 cold_concurrency:
  ttft_p50_ms:  3800 -> 3950  (+3.9%, OK)
  total_p95_ms: 8500 -> 9300  (+9.4%, OK)
  throughput:   0.45 -> 0.40  (-11.1%, ADVISORY)
```

The comparator always exits **0**. Pass `--fail-on-regression` to make
it exit **1** when any metric regresses beyond `--max-regression`
(default 0.10 = 10%) — useful for manual `make bench` runs.

## Updating the baseline

```bash
# After an intentional perf change, recapture the main-branch baseline:
python -m tests.bench.s4_cold_concurrency --output tests/bench/baselines/main.json.s4
# Edit baselines/main.json with the new values, commit, push.
```

Treat the baseline as a contract: update it only when the perf change is
intentional and reviewed.

## Adding to CI

A `perf.yml` workflow is tracked in a follow-up issue (the issue body
left this open: "GPU runner in CI?"). The current bench is designed so
that a future workflow can simply call each scenario with
`--duration-s 60 --output results/${{ matrix.scenario }}.json` and pipe
through `compare.py --fail-on-regression --max-regression 0.10` against
`baselines/main.json`.

## Coordinator-side wiring

Three Prometheus metrics are added to `CoordinatorMetrics.cs` so the
bench can assert cross-cutting invariants:

| Metric                                    | Type      | Use                        |
|-------------------------------------------|-----------|----------------------------|
| `hydra_warm_lease_max_age_seconds`        | gauge     | oldest warm lease age (C10)|
| `hydra_stuck_warm_leases_total`           | counter   | watchdog reclaims (S10)    |
| `hydra_queue_head_age_seconds`            | gauge     | main-queue head age (C9)   |

`MigrationLatency` is also now observed in `MigrateSessionAsync` (the
issue's **C5 fix**), so the comparator can read it directly from
`:9501/metrics` rather than instrumenting each scenario.

## Issues this validates

- **#265** (engine mode) — covered by S3/S4.
- **#269** (pipelined prefill) — covered by S11 (land with the feature).
- **#299** (V2 coordinator) — S10 is the watchdog validation.
- **#200** (mix-precision swap) — covered by S12.
- **#201** (Qwen35MoE KV cache restore) — covered by S9 (n_past guard).
