"""
Advisory A/B comparator for the bench suite (issue #306).

Compares a current bench result (the JSON `BenchmarkHarness.save` wrote)
against a baseline (the committed `tests/bench/baselines/main.json` or a
scenario-specific slice). Reports per-metric deltas and flags regressions
beyond `--max-regression` (default 10%). Exit code is **0 by default**
(advisory per the issue scope); pass `--fail-on-regression` to make
regressions fail the run.

Usage:
    python -m tests.bench.compare \\
        tests/bench/baselines/main.json results/s4.json \\
        --scenario s4_cold_concurrency \\
        --max-regression 0.10

    # A/B (no baseline): two PR results
    python -m tests.bench.compare results/265-agent.json results/265-engine.json \\
        --scenario s4_cold_concurrency

The comparator only looks at the metrics both files actually carry —
omitting a metric in either side is treated as a no-op, not a failure.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any


# ─── Metric registry ────────────────────────────────────────────────────

# Each entry maps a baseline JSON key to the corresponding key in the
# result JSON's `report` block. The comparator is forgiving — if either
# side is missing a metric, that metric is skipped (and reported as
# "absent") rather than treated as a regression.
LATENCY_METRICS: tuple[str, ...] = (
    "ttft_p50_ms", "ttft_p95_ms", "ttft_p99_ms",
    "tpot_p50_ms", "tpot_p95_ms",
    "total_p50_ms", "total_p95_ms", "total_p99_ms",
)
THROUGHPUT_METRICS: tuple[str, ...] = (
    "throughput_req_per_s", "warm_hit_rate",
)
EXTRAS_METRICS: tuple[str, ...] = (
    # The baseline JSON can also carry scenario-specific extras (e.g.
    # `swap_s_p95` for s12). These are compared as plain numbers.
    "detection_s_p95", "failover_success_rate", "restore_triggered_rate",
    "watchdog_reclaim_s_p95", "swap_s_p50", "swap_s_p95",
)


# ─── Resolution helpers ────────────────────────────────────────────────

def _read_json(path: str) -> dict[str, Any]:
    with open(path) as f:
        return json.load(f)


def _resolve_baseline_metric(baseline: dict[str, Any], scenario_id: str, key: str) -> float | None:
    """Look up a metric in the baseline JSON, supporting both the
    per-scenario shape (`scenarios[scenario_id][key]`) and the flat
    top-level shape (`key`)."""
    if "scenarios" in baseline:
        scenario = baseline["scenarios"].get(scenario_id, {})
        if key in scenario:
            v = scenario[key]
            return float(v) if isinstance(v, (int, float)) else None
    if key in baseline:
        v = baseline[key]
        return float(v) if isinstance(v, (int, float)) else None
    return None


def _resolve_result_metric(result: dict[str, Any], key: str) -> float | None:
    """Look up a metric in a harness result file's `report` block."""
    rep = result.get("report", {})
    if key in rep:
        v = rep[key]
        return float(v) if isinstance(v, (int, float)) else None
    return None


# ─── Diff logic ─────────────────────────────────────────────────────────

def _classify_delta(baseline: float, current: float, max_regression: float) -> tuple[float, str]:
    """
    Returns (delta_pct, verdict) where verdict is one of
      "absent"    — at least one side is missing the metric
      "improved"  — current is strictly better than baseline
                    (lower for latencies, higher for throughput / rate)
      "ok"        — current is within +-max_regression of baseline
      "regressed" — current is more than max_regression worse than baseline
                    (advisory; only fails with --fail-on-regression)
    """
    if baseline == 0:
        # Treat zero baseline as "absent" so we don't divide by zero.
        # Real baselines should never be exactly 0 for a meaningful metric.
        if current == 0:
            return 0.0, "absent"
        return float("inf") if current > 0 else 0.0, "ok"

    return_pct = (current - baseline) / baseline * 100.0
    return return_pct, "ok"


def _is_improvement(key: str, baseline: float, current: float) -> bool:
    """Latencies: lower is better. Throughput / rate: higher is better."""
    if key in THROUGHPUT_METRICS or key.endswith("_rate"):
        return current > baseline
    return current < baseline


# ─── Report rendering ──────────────────────────────────────────────────

def _fmt_delta(pct: float) -> str:
    if pct == float("inf"):
        return "   inf%"
    sign = "+" if pct >= 0 else ""
    return f"{sign}{pct:5.1f}%"


def render_report(
    baseline: dict[str, Any],
    current: dict[str, Any],
    scenario_id: str,
    max_regression: float,
) -> tuple[str, list[tuple[str, float, float, float, str]]]:
    """
    Build a human-readable report. Returns (text, rows) where rows is a
    list of (metric, baseline, current, delta_pct, verdict) tuples for
    programmatic use.
    """
    all_keys = list(LATENCY_METRICS) + list(THROUGHPUT_METRICS) + list(EXTRAS_METRICS)
    rows: list[tuple[str, float, float, float, str]] = []

    lines: list[str] = []
    label = f"S{scenario_id[1:]}" if scenario_id.startswith("s") and scenario_id[1:].isdigit() else scenario_id
    lines.append(f"{label}:")
    lines.append(f"  {'metric':<28} {'baseline':>12} {'current':>12} {'delta':>8}  verdict")
    lines.append("  " + "-" * 76)

    for key in all_keys:
        b = _resolve_baseline_metric(baseline, scenario_id, key)
        c = _resolve_result_metric(current, key)
        if b is None or c is None:
            continue
        delta_pct, verdict = _classify_delta(b, c, max_regression)
        if verdict == "ok":
            if _is_improvement(key, b, c):
                verdict = "improved"
            elif delta_pct > max_regression * 100.0:
                verdict = "regressed"
        rows.append((key, b, c, delta_pct, verdict))
        verdict_marker = "ADVISORY" if verdict == "regressed" else verdict.upper()
        lines.append(
            f"  {key:<28} {b:>12.2f} {c:>12.2f} {_fmt_delta(delta_pct):>8}  {verdict_marker}"
        )

    if not rows:
        lines.append("  (no comparable metrics found — check scenario_id and file shape)")

    return "\n".join(lines), rows


# ─── CLI ────────────────────────────────────────────────────────────────

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Advisory A/B comparator for the bench suite.")
    parser.add_argument("baseline", help="Path to baseline JSON (or current JSON in A/B mode).")
    parser.add_argument("current", help="Path to current result JSON.")
    parser.add_argument(
        "--scenario", required=True,
        help="Scenario id (e.g. s4_cold_concurrency). Required for the per-scenario baseline shape.",
    )
    parser.add_argument(
        "--max-regression", type=float, default=float(os.environ.get("BENCH_MAX_REGRESSION", "0.10")),
        help="Maximum allowed regression as a fraction (0.10 = 10%%). Default 0.10.",
    )
    parser.add_argument(
        "--fail-on-regression", action="store_true",
        help="Exit 1 if any metric regresses beyond --max-regression. Default is advisory (exit 0).",
    )
    parser.add_argument(
        "--ab", action="store_true",
        help="Treat baseline as another current result (A/B mode). Useful for engine-vs-agent comparisons.",
    )
    parser.add_argument(
        "--json", action="store_true",
        help="Emit JSON instead of text. Useful for CI consumption.",
    )
    args = parser.parse_args(argv)

    baseline_raw = _read_json(args.baseline)
    current_raw = _read_json(args.current)

    if args.ab:
        # In A/B mode the baseline file is itself a harness result; pull
        # the report out and wrap it in the per-scenario shape so the
        # comparator's resolution code can stay unified.
        baseline = {"scenarios": {args.scenario: baseline_raw.get("report", {})}}
    else:
        baseline = baseline_raw

    text, rows = render_report(baseline, current_raw, args.scenario, args.max_regression)

    if args.json:
        print(json.dumps({
            "scenario":         args.scenario,
            "max_regression":   args.max_regression,
            "rows": [
                {"metric": k, "baseline": b, "current": c, "delta_pct": d, "verdict": v}
                for k, b, c, d, v in rows
            ],
            "any_regressed":    any(v == "regressed" for *_, v in rows),
        }, indent=2))
    else:
        print(text)

    if args.fail_on_regression and any(v == "regressed" for *_, v in rows):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
