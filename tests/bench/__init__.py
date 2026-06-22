"""
Hydra benchmark suite — workloads + scenarios for P/D split validation.

Issue #306: ships a reusable harness (`harness.BenchmarkHarness`), five
workload generators, twelve scenarios that exercise the M-Perf P/D
surface, and an advisory A/B comparator (`compare.py`).

Entry points:

  from tests.bench.harness import BenchmarkHarness, Report
  from tests.bench import chat_single_turn, chat_multi_turn, burst
  from tests.bench import long_context, mixed
  from tests.bench import s1_warm_affinity, s2_warm_miss, s3_cold_atomic
  from tests.bench import s4_cold_concurrency, s5_long_context_60k
  from tests.bench import s6_long_context_80k, s7_burst, s8_worker_death
  from tests.bench import s9_npast_guard, s10_stale_lease
  from tests.bench import s11_pipelined_prefill, s12_mix_precision_swap

Each scenario is a runnable script (`python -m tests.bench.s4_cold_concurrency
--output results.json`) **and** an importable async entry point for pytest
parametrisation.

Compare baseline ↔ current with:

  python -m tests.bench.compare baselines/main.json results/pr.json \\
      --max-regression 0.10

Reports are advisory (no CI block per issue #306 scope) and exit 0 even
on regression. The CI integration lives in a follow-up issue.
"""
