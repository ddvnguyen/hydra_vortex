#!/usr/bin/env python3
"""
Sync Prometheus firing alerts to GitHub issues.

- Creates a GitHub issue for each newly-firing critical alert.
- Closes open monitoring issues whose alert is no longer firing.

Controlled by env vars:
    PROMETHEUS_URL          default: http://localhost:9091
    MONITOR_MIN_SEVERITY    default: critical  (critical | warning | info)
    GH_TOKEN                required (set automatically in GitHub Actions)

Usage:
    python scripts/create_monitoring_issues.py
"""

import json
import os
import subprocess
import urllib.request
from datetime import timezone, datetime

PROMETHEUS_URL = os.environ.get("PROMETHEUS_URL", "http://localhost:9091")
MIN_SEVERITY = os.environ.get("MONITOR_MIN_SEVERITY", "critical")
GRAFANA_URL = os.environ.get("GRAFANA_URL", "http://localhost:3000")

SEVERITY_ORDER = {"critical": 0, "warning": 1, "info": 2}
MIN_SEVERITY_LEVEL = SEVERITY_ORDER.get(MIN_SEVERITY, 0)


def gh(*args: str) -> str:
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args)}\n  {result.stderr.strip()}")
    return result.stdout.strip()


def gh_json(*args: str) -> list | dict:
    return json.loads(gh(*args))


def fetch_firing_alerts() -> list[dict]:
    url = f"{PROMETHEUS_URL}/api/v1/alerts"
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            data = json.loads(resp.read())
    except Exception as e:
        print(f"WARNING: could not reach Prometheus at {url}: {e}")
        return []

    alerts = data.get("data", {}).get("alerts", [])
    firing = []
    for alert in alerts:
        if alert.get("state") != "firing":
            continue
        labels = alert.get("labels", {})
        severity = labels.get("severity", "info")
        if SEVERITY_ORDER.get(severity, 99) > MIN_SEVERITY_LEVEL:
            continue
        firing.append(alert)
    return firing


def alert_issue_title(alert: dict) -> str:
    labels = alert.get("labels", {})
    alertname = labels.get("alertname", "UnknownAlert")
    instance = labels.get("instance", "")
    node = labels.get("node", "")
    suffix = node or instance
    return f"Alert: {alertname}" + (f" ({suffix})" if suffix else "")


def build_alert_body(alert: dict) -> str:
    labels = alert.get("labels", {})
    annotations = alert.get("annotations", {})
    active_at = alert.get("activeAt", "unknown")

    rows = "\n".join(f"| `{k}` | `{v}` |" for k, v in sorted(labels.items()))

    return f"""\
## Prometheus Alert

| Label | Value |
|-------|-------|
{rows}

**Summary:** {annotations.get('summary', '—')}

**Description:** {annotations.get('description', '—')}

**Firing since:** `{active_at}`

**Grafana:** {GRAFANA_URL}/explore

---
*Auto-created by `scripts/create_monitoring_issues.py`*"""


def get_open_monitoring_issues() -> list[dict]:
    issues = gh_json(
        "issue", "list",
        "--label", "monitoring",
        "--state", "open",
        "--json", "number,title",
        "--limit", "100",
    )
    return issues  # type: ignore[return-value]


def main() -> None:
    print("Fetching firing alerts from Prometheus…")
    firing = fetch_firing_alerts()
    firing_titles = {alert_issue_title(a) for a in firing}
    print(f"  {len(firing)} alert(s) firing at or above '{MIN_SEVERITY}' severity")

    print("Fetching open monitoring issues from GitHub…")
    open_issues = get_open_monitoring_issues()
    open_titles = {i["title"]: i["number"] for i in open_issues}
    print(f"  {len(open_issues)} open monitoring issue(s)")

    # --- Create issues for new alerts ---
    created = 0
    for alert in firing:
        title = alert_issue_title(alert)
        if title in open_titles:
            print(f"  exists  #{open_titles[title]}  {title}")
            continue

        labels = alert.get("labels", {})
        severity = labels.get("severity", "info")
        component = labels.get("component", "")

        gh_labels = ["monitoring", "auto-created"]
        if severity == "critical":
            gh_labels.append("p0-critical")
        elif severity == "warning":
            gh_labels.append("p1-high")

        print(f"  create  {title}")
        url = gh(
            "issue", "create",
            "--title", title,
            "--body", build_alert_body(alert),
            "--label", ",".join(gh_labels),
        )
        print(f"    → {url}")
        created += 1

    # --- Close issues for resolved alerts ---
    closed = 0
    for issue in open_issues:
        if issue["title"] not in firing_titles:
            now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            print(f"  close   #{issue['number']}  {issue['title']}")
            gh(
                "issue", "close", str(issue["number"]),
                "--comment", f"Alert is no longer firing as of `{now}`. Closing automatically.",
            )
            closed += 1

    print(f"\nDone: {created} created, {closed} closed.")


if __name__ == "__main__":
    main()
