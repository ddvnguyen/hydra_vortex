"""Generate a Markdown comparison report from benchmark results.

Reads individual benchmark JSON result files from --results-dir and produces
a consolidated quality report with P/D split verification per benchmark.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone


def load_results(results_dir: str) -> list[dict]:
    """Load all benchmark result JSON files from a directory."""
    results: list[dict] = []
    for name in sorted(os.listdir(results_dir)):
        if name.endswith(".json") and not name.startswith("report"):
            path = os.path.join(results_dir, name)
            try:
                with open(path) as f:
                    results.append(json.load(f))
            except Exception as e:
                print(f"Warn: failed to read {path}: {e}", file=sys.stderr)
    return results


def generate_md(results: list[dict], model_name: str = "") -> str:
    """Generate a Markdown report from benchmark result dicts.

    Each result dict should have:
      name, num_samples, score, score_unit, reference_score,
      pd_pass, pd_summary, cached_avg, rtx_ppt_delta, p100_ppt_delta,
      rtx_tpt_delta, p100_tpt_delta, elapsed_s, reasoning_avg, content_avg
    """
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    label = model_name or "Qwopus3.6-35B-A3B (Split-mix)"

    lines: list[str] = []
    lines.append(f"## Hydra Quality Report — {ts}")
    lines.append("")
    lines.append(f"**Mode:** Split-mix (RTX Mini prefill → P100 Balanced decode)")
    lines.append(f"**Model:** {label}")
    lines.append("")

    # Quality scores table
    lines.append("### Quality Scores")
    lines.append("")
    lines.append(
        "| Benchmark | Samples | Score | Qwen3.6 Ref | P/D Split | "
        "Cached % | Reasoning | Content |"
    )
    lines.append(
        "|-----------|---------|-------|-------------|-----------|"
        "----------|-----------|---------|"
    )

    all_passed = True
    for r in results:
        name = r.get("name", "?")
        num = r.get("num_samples", "?")
        score = r.get("score", 0)
        unit = r.get("score_unit", "%")
        ref = r.get("reference_score", 0)
        pd_icon = chr(0x2713) if r.get("pd_pass", False) else chr(0x2717)
        cached = r.get("cached_avg", 0)
        reason = r.get("reasoning_avg", 0)
        content = r.get("content_avg", 0)

        score_str = f"{score:.1f}{unit}" if isinstance(score, float) else f"{score}{unit}"
        ref_str = f"{ref:.1f}{unit}" if isinstance(ref, float) else f"{ref}{unit}"

        lines.append(
            f"| {name} | {num} | {score_str} | {ref_str} | "
            f"{pd_icon} | {cached:.0f}% | {reason} ch | {content} ch |"
        )
        if not r.get("pd_pass", False):
            all_passed = False

    lines.append("")

    # P/D Split verification table
    lines.append("### P/D Split Verification")
    lines.append("")
    lines.append(
        "| Benchmark | RTX Prefill Δ | P100 Prompt Δ | RTX Decode Δ | "
        "P100 Decode Δ | KV Cached | Status |"
    )
    lines.append(
        "|-----------|---------------|---------------|--------------|"
        "---------------|-----------|--------|"
    )

    for r in results:
        name = r.get("name", "?")
        rtx_ppt = r.get("rtx_ppt_delta", 0)
        p100_ppt = r.get("p100_ppt_delta", 0)
        rtx_tpt = r.get("rtx_tpt_delta", 0)
        p100_tpt = r.get("p100_tpt_delta", 0)
        cached = r.get("cached_avg", 0)
        pd_icon = chr(0x2713) if r.get("pd_pass", False) else chr(0x2717)
        summary = r.get("pd_summary", "")

        p100_label = f"+{p100_ppt}"
        if r.get("p100_ppt_pass", False):
            p100_label += " (KV hit)"

        lines.append(
            f"| {name} | +{rtx_ppt} | {p100_label} | +{rtx_tpt} | "
            f"+{p100_tpt} | {cached:.0f}% | {pd_icon} {summary} |"
        )

    lines.append("")

    # Per-benchmark details
    lines.append("### Per-Benchmark Details")
    lines.append("")

    for r in results:
        name = r.get("name", "?")
        elapsed = r.get("elapsed_s", 0)
        lines.append(f"#### {name}")
        lines.append(f"- **Samples:** {r.get('num_samples', '?')}")
        lines.append(f"- **Score:** {r.get('score', 0):.1f}{r.get('score_unit', '%')}")
        lines.append(f"- **Elapsed:** {elapsed:.1f}s")
        lines.append(f"- **P/D Split:** {chr(0x2713) if r.get('pd_pass') else chr(0x2717)}")
        pd_detail = r.get("pd_summary", "?")
        lines.append(f"- **Verification:** {pd_detail}")
        issues = r.get("pd_issues", [])
        if issues:
            for issue in issues:
                lines.append(f"  - WARNING: {issue}")
        lines.append("")

    # Overall verdict
    lines.append("---")
    lines.append("")
    icon = chr(0x2713) if all_passed else chr(0x2717)
    status = "PASS" if all_passed else "FAIL"
    lines.append(f"### Overall: {icon} **{status}**")
    lines.append("")

    if not all_passed:
        failed = [r["name"] for r in results if not r.get("pd_pass", False)]
        lines.append(f"**Failed benchmarks:** {', '.join(failed)}")
        lines.append("")

    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser("Generate Hydra quality report")
    parser.add_argument("--results-dir", default="/tmp/hydra-quality-results")
    parser.add_argument("--output", default="", help="Output MD file path")
    parser.add_argument("--model", default="", help="Model label for report header")
    args = parser.parse_args()

    results = load_results(args.results_dir)
    if not results:
        print(f"No result files found in {args.results_dir}", file=sys.stderr)
        sys.exit(1)

    md = generate_md(results, args.model)
    if args.output:
        with open(args.output, "w") as f:
            f.write(md)
        print(f"Report: {args.output}")
    else:
        print(md)


if __name__ == "__main__":
    main()
