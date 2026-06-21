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

## Engine A/B driver (`ab_engine.py`) — issue #306 reframed

`tests/bench/ab_engine.py` is the focused A/B confirmation asked for in
the reframed issue #306. It drives the same request through both
transports on the same llama-server binary:

| | New engine | Legacy fork |
|---|---|---|
| Transport | Binary RPC opcodes `0x40–0x46` (PREFILL 0x42, DECODE 0x43, INFO 0x41, …) | HTTP `/v1/chat/completions` + state opcodes `0x30–0x32` (STATE_GET/META) |
| Driven via | `RpcClient` (minimal Python wire-protocol client) | `urllib` HTTP path |

It runs 7 capability checks and prints an A/B table. Exits **1** if any
capability fails equivalence; **0** if all PASS or SKIP (an
unreachable engine is SKIP, not FAIL — "detect, don't assume").

| # | Capability | Engine side | Legacy side | Equivalence check |
|---|------------|-------------|-------------|-------------------|
| 1 | **Prefill only** | PREFILL `0x42` (n_predict=0) → `n_past` | HTTP prefill + STATE_META `0x32` | `n_past` within ±2 tokens |
| 2 | **Decode only** | DECODE `0x43` (token-id stream) | HTTP `/v1/chat/completions` (text) | engine ≤ 1.5× legacy latency; text-vs-token comparison noted as C#-side |
| 3 | **KV save** | PREFILL inline state blob | STATE_META `state_size` | sizes within 1 KB |
| 4 | **KV restore** | PREFILL → STATE_PUT `0x31` round-trip | (no Python binary legacy client — noted) | engine reports `restored=true` |
| 5 | **Metadata** | INFO `0x41` + STATE_META `0x32` | GET `/slots/0/state/meta` | `n_past` within ±2 |
| 6 | **Metrics** | scrape `:9501/metrics` for `hydra_prefill_seconds_count` + `hydra_migration_latency_seconds` (C5 fix) | same | required declared+>0, optional declared |
| 7 | **Observability** | RPC with explicit `trace_id` | HTTP POST with `session_id=trace` | both round-trip (no error) |

### Running

```bash
# Both paths reachable (engine RPC on :9503, legacy HTTP on :8080)
python -m tests.bench.ab_engine \
    --engine-rpc 127.0.0.1:9503 \
    --legacy-http http://127.0.0.1:8080 \
    --output /tmp/ab-engine-results.json

# Engine only (legacy skipped — useful for smoke testing the engine)
python -m tests.bench.ab_engine --engine-rpc 127.0.0.1:9503 --skip-legacy

# When the engine binary is pre-#289 (no `0x40–0x46`), the script
# detects `NOT_IMPLEMENTED` from `INFO 0x41` and SKIPs the engine
# column for capabilities that need it. No false failures.
```

### Notes for the operator

- **Engine RPC port not on the host by default.** The `hydra-head-rtx`
  container's `9503` (engine binary RPC) is only exposed inside the
  podman network. To run the A/B end-to-end from the host, either
  exec into the container, or add a port mapping to
  `infra/hydra-head/Dockerfile.rtx`. The script's connection failure
  is reported as a clean "engine column SKIP" rather than a crash.
- **Both transports on the same binary.** The reframed #306 assumes
  the same llama-server build supports both RPC and HTTP. This is
  true for the engine build (b9541-c357ad25b) — it serves
  `/v1/chat/completions` and the `0x30–0x32` state opcodes AND the
  `0x40–0x46` engine opcodes on the same port set.
- **C5 fix is verified by the metric being declared**, not by
  `migration_latency_count > 0`. The histogram is only emitted once
  a real migration runs; in a normal session the counter is 0 but
  the type declaration is enough to prove the fix shipped.
- **The full JSON output (`/tmp/ab-engine-results.json` by default)**
  is structured for later `compare.py` runs against a future
  baseline.

### Difference from the per-scenario suite (S1–S12)

| | `ab_engine.py` (issue #306 reframed) | `s1_*`–`s12_*` (issue #306 original) |
|---|---|---|
| Goal | Confirm the new engine produces equivalent output to the legacy fork | Measure P/D split perf under load |
| Scope | 7 capabilities, single request each | 12 scenarios with N=20–50 sustained load |
| Live load | No | Yes |
| Exit code | 0=PASS, 1=FAIL (advisory) | 0 (advisory per issue scoping) |
| Use | `python -m tests.bench.ab_engine` after deploy | `python -m tests.bench.s4_cold_concurrency --output ...` for periodic perf checks |

Both can coexist; `ab_engine.py` is a quick smoke-test post-deploy,
the per-scenario suite is the periodic perf regression check.
- **#201** (Qwen35MoE KV cache restore) — covered by S9 (n_past guard).
