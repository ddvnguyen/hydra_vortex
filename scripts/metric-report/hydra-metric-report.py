#!/usr/bin/env python3
"""
hydra-metric-report.py — automatic validation metric report for the hydra rig.

Pulls:
  1. GitHub Actions run history for the validation workflows (Live Rig Tests,
     Deploy Heads, Deploy Hydra Core, System Tests) via `gh`.
  2. TRX artifacts from run results (extracts Counters + per-test durations).
  3. Live rig telemetry from the coordinator /metrics endpoint.

Emits a markdown report with tables: per-run summary, per-test durations,
TRX counters, and rig telemetry. The report generator NEVER judges pass/fail
from the GitHub badge — it reads the TRX counters (the user's eval standard);
a run whose TRX shows 0 executed is flagged FALSE-GREEN.

Usage:
  python3 hydra-metric-report.py [--runs N] [--out PATH] [--repo owner/repo]
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from datetime import datetime, timezone

REPO = "ddvnguyen/hydra_vortex"
WORKFLOWS = {
    "Live Rig Tests": "test-live-rig.yml",
    "Deploy Heads (llama-engine)": "deploy-heads.yml",
    "Deploy Hydra Core": "deploy-core.yml",
    "System Tests": "test-system.yml",
}
METRICS_URL = "http://localhost:9000/metrics"
HEALTH_URL = "http://localhost:9000/health"


def sh(cmd, **kw):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True, **kw)


def gh_json(args):
    r = sh(f"gh api {args}")
    if r.returncode != 0:
        return None
    return json.loads(r.stdout)


def fmt_dur(ts):
    """Convert a .NET TimeSpan-like '00:01:38.6259925' to seconds."""
    try:
        parts = ts.strip().split(":")
        h, m = int(parts[0]), int(parts[1])
        s = float(parts[2])
        return h * 3600 + m * 60 + s
    except Exception:
        return None


def parse_trx(path):
    """Extract counters + per-test rows from a TRX file."""
    t = open(path, encoding="utf-8", errors="replace").read()
    counters = {}
    m = re.search(r"<Counters([^>]*)/>", t)
    if m:
        for k, v in re.findall(r'(\w+)="(\d+)"', m.group(1)):
            counters[k] = int(v)
    tests = []
    for b in re.split(r"<UnitTestResult ", t)[1:]:
        tm = re.search(r'testName="([^"]+)"', b)
        if not tm:
            continue
        name = tm.group(1).split(".")[-1]
        dur = None
        dm = re.search(r'duration="([^"]+)"', b)
        if dm:
            dur = fmt_dur(dm.group(1))
        out = re.search(r'outcome="([^"]+)"', b)
        tests.append({
            "name": name,
            "duration_s": dur,
            "outcome": out.group(1) if out else "?",
        })
    return counters, tests


def fetch_trx(run_id):
    """Download the first TRX artifact of a run; return list of (trx_name, counters, tests)."""
    arts = gh_json(f"repos/{REPO}/actions/runs/{run_id}/artifacts")
    if not arts or not arts.get("artifacts"):
        return []
    results = []
    for art in arts["artifacts"]:
        if not art["name"].endswith(("results",)) and "live-rig-results" not in art["name"]:
            continue
        with tempfile.TemporaryDirectory() as td:
            r = sh(
                f'curl -sL -m 60 -H "Authorization: Bearer $(gh auth token)" '
                f'"https://api.github.com/repos/{REPO}/actions/artifacts/{art["id"]}/zip" -o {td}/a.zip'
            )
            if r.returncode != 0:
                continue
            try:
                with zipfile.ZipFile(f"{td}/a.zip") as z:
                    z.extractall(td)
            except Exception:
                continue
            for f in os.listdir(td):
                if f.endswith(".trx"):
                    counters, tests = parse_trx(os.path.join(td, f))
                    results.append({"trx": f, "counters": counters, "tests": tests})
    return results


def fetch_metrics():
    """Pull coordinator /metrics + /health; aggregate histogram summaries."""
    out = {"health": {}, "hist": {}, "counters": {}}
    try:
        with urllib.request.urlopen(HEALTH_URL, timeout=8) as r:
            out["health"] = json.loads(r.read())
    except Exception:
        pass
    try:
        with urllib.request.urlopen(METRICS_URL, timeout=8) as r:
            body = r.read().decode()
    except Exception:
        return out
    agg = {}
    for m in re.finditer(r"^(hydra_[a-z_]+)_(sum|count)\{([^}]*)\}\s+([\d.e+-]+)", body, re.M):
        base, kind, labels, val = m.group(1), m.group(2), m.group(3), float(m.group(4))
        agg.setdefault(base, {})[kind] = agg.get(base, {}).get(kind, 0) + val
    for k in sorted(agg):
        s = agg[k].get("sum", 0)
        c = agg[k].get("count", 0)
        if c:
            out["hist"][k.replace("hydra_", "")] = {"count": c, "avg_s": round(s / c, 2)}
    for m in re.finditer(r"^(hydra_\w+total)\{([^}]*)\}\s+([\d.e+-]+)", body, re.M):
        out["counters"].setdefault(m.group(1).replace("hydra_", ""), 0)
        out["counters"][m.group(1).replace("hydra_", "")] += int(float(m.group(3)))
    return out


def collect_runs(n):
    runs = []
    for title, wf in WORKFLOWS.items():
        data = gh_json(f"repos/{REPO}/actions/workflows/{wf}/runs?per_page={n}")
        if not data:
            continue
        for run in data.get("workflow_runs", []):
            runs.append({
                "id": run["id"],
                "workflow": title,
                "status": run["status"],
                "conclusion": run["conclusion"],
                "sha": run["head_sha"][:8],
                "created": run["created_at"],
            })
    runs.sort(key=lambda r: r["created"], reverse=True)
    return runs[:n]


def mark_false_green_flag(run, trx_results):
    """A run whose TRX shows 0 executed is a FALSE GREEN (deploy-race / skip)."""
    for tr in trx_results:
        c = tr["counters"]
        if c.get("executed", 0) == 0 and c.get("total", 0) > 0:
            return f"FALSE-GREEN ({c.get('total')}× NotExecuted)"
    return ""


def build_report(n, repo=REPO):
    lines = []
    lines.append(f"# Hydra Validation Metric Report")
    lines.append(f"\n_Generated: {datetime.now(timezone.utc).astimezone().isoformat(timespec='minutes')}_")
    lines.append(f"_Repo: {repo} | TRX-verified (badge never trusted)_\n")

    # 1. Runs table
    lines.append("## 1. GitHub Actions Runs")
    lines.append("")
    lines.append("| Run | Workflow | SHA | Conclusion | TRX (pass/fail) | Flag |")
    lines.append("|---|---|---|---|---|---|")
    runs = collect_runs(n)
    for run in runs:
        trxs = fetch_trx(run["id"])
        if trxs:
            summary = "; ".join(
                f"{tr['trx'][:18]}: {tr['counters'].get('passed','?')}/{tr['counters'].get('failed','?')}"
                for tr in trxs
            )
        else:
            summary = "—"
        flag = mark_false_green_flag(run, trxs)
        concl = run["conclusion"] or run["status"]
        lines.append(
            f"| {run['id']} | {run['workflow']} | {run['sha']} | {concl} | {summary} | {flag} |"
        )
    lines.append("")

    # 2. Per-test durations for the latest Live Rig Tests run
    lines.append("## 2. Latest Live Rig Tests — per-test durations")
    lines.append("")
    for run in runs:
        if run["workflow"] != "Live Rig Tests":
            continue
        trxs = fetch_trx(run["id"])
        lines.append(f"### Run {run['id']} ({run['created'][:16]}Z, conclusion={run['conclusion']})")
        lines.append("")
        for tr in trxs:
            c = tr["counters"]
            lines.append(f"**{tr['trx']}** — total={c.get('total','?')} executed={c.get('executed','?')} "
                         f"passed={c.get('passed','?')} failed={c.get('failed','?')}")
            lines.append("")
            if tr["tests"]:
                lines.append("| Test | Duration (s) | Outcome |")
                lines.append("|---|---|---|")
                for t in sorted(tr["tests"], key=lambda x: x.get("duration_s") or 0, reverse=True):
                    d = f"{t['duration_s']:.1f}" if t["duration_s"] else "—"
                    lines.append(f"| {t['name']} | {d} | {t['outcome']} |")
                lines.append("")
        break

    # 3. Rig telemetry
    met = fetch_metrics()
    lines.append("## 3. Rig Telemetry (live)")
    lines.append("")
    nodes = met["health"].get("nodes", {})
    if nodes:
        lines.append("| Node | Healthy | Slots idle | Stuck |")
        lines.append("|---|---|---|---|")
        for k, v in nodes.items():
            lines.append(f"| {k} | {v.get('healthy')} | {v.get('slots_idle')}/{v.get('slots_total')} | {v.get('stuck_slots')} |")
        lines.append("")
    if met["hist"]:
        lines.append("| Metric (avg) | Count | Avg (s) |")
        lines.append("|---|---|---|")
        for k, v in sorted(met["hist"].items()):
            lines.append(f"| {k} | {v['count']:.0f} | {v['avg_s']} |")
        lines.append("")
    else:
        lines.append("_No request traffic since core start (histograms appear after first observation)._\n")
    if met["counters"]:
        lines.append("| Counter | Total |")
        lines.append("|---|---|")
        for k, v in sorted(met["counters"].items()):
            lines.append(f"| {k} | {v} |")
        lines.append("")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", type=int, default=8)
    ap.add_argument("--out", default="")
    ap.add_argument("--repo", default=REPO)
    args = ap.parse_args()
    report = build_report(args.runs, args.repo)
    if args.out:
        with open(args.out, "w") as f:
            f.write(report)
        print(f"report written: {args.out}")
    else:
        print(report)


if __name__ == "__main__":
    main()
