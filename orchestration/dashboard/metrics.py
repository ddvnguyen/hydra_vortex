#!/usr/bin/env python3
"""metrics.py — expose orchestration state as Prometheus metrics.

Mirrors the dashboard snapshot so the same signals appear in Grafana.
"""
from __future__ import annotations

from prometheus_client import CollectorRegistry, Counter, Gauge, generate_latest

REGISTRY = CollectorRegistry()

AGENTS_TOTAL = Gauge(
    "hydra_agents_total", "Paseo agents by status", ["status"], registry=REGISTRY
)
AGENTS_ACTIVE = Gauge(
    "hydra_agents_active", "Number of non-idle Paseo agents", registry=REGISTRY
)
SCHEDULES_TOTAL = Gauge(
    "hydra_schedules_total", "Number of Paseo schedules", registry=REGISTRY
)
SCHEDULES_PAUSED = Gauge(
    "hydra_schedules_paused", "Number of paused Paseo schedules", registry=REGISTRY
)
ISSUES_TOTAL = Gauge(
    "hydra_issues_total", "GitHub issues by status label", ["status"], registry=REGISTRY
)
INSTRUMENTOR_VERDICT = Gauge(
    "hydra_instrumentor_verdict",
    "Current instrumentor verdict (1 = current)",
    ["verdict"],
    registry=REGISTRY,
)
DAEMON_UP = Gauge(
    "hydra_daemon_up", "Paseo local daemon reachable (1/0)", registry=REGISTRY
)
COLLECT_ERRORS = Counter(
    "hydra_collect_errors_total",
    "Collector failures by source",
    ["source"],
    registry=REGISTRY,
)

_VERDICTS = ["PASS", "WARN", "FAIL"]


def update(snapshot: dict) -> None:
    paseo = snapshot.get("paseo", {})
    github = snapshot.get("github", {})
    state = snapshot.get("state", {})

    # daemon
    DAEMON_UP.set(paseo.get("daemon_up", 0) if isinstance(paseo, dict) else 0)

    # agents
    AGENTS_TOTAL.clear()
    active = 0
    agents = paseo.get("agents", []) if isinstance(paseo, dict) else []
    if isinstance(paseo, dict) and "error" in paseo:
        COLLECT_ERRORS.labels(source="paseo").inc()
    for a in agents:
        AGENTS_TOTAL.labels(status=a.get("status", "unknown")).inc()
        if a.get("status") not in ("idle", "closed"):
            active += 1
    AGENTS_ACTIVE.set(active)

    # schedules
    schedules = paseo.get("schedules", []) if isinstance(paseo, dict) else []
    SCHEDULES_TOTAL.set(len(schedules))
    paused = sum(1 for s in schedules if _is_paused(s))
    SCHEDULES_PAUSED.set(paused)

    # issues
    ISSUES_TOTAL.clear()
    board = github.get("board", {}) if isinstance(github, dict) else {}
    if isinstance(github, dict) and "error" in github:
        COLLECT_ERRORS.labels(source="github").inc()
    for status, items in board.items():
        ISSUES_TOTAL.labels(status=status).set(len(items))

    # instrumentor
    INSTRUMENTOR_VERDICT.clear()
    cur = (
        state.get("instrumentor", {}).get("verdict", "unknown")
        if isinstance(state, dict)
        else "unknown"
    )
    if isinstance(state, dict) and "error" in state:
        COLLECT_ERRORS.labels(source="state").inc()
    for v in _VERDICTS:
        INSTRUMENTOR_VERDICT.labels(verdict=v).set(1 if cur == v else 0)


def _is_paused(sched: dict) -> bool:
    if not isinstance(sched, dict):
        return False
    if sched.get("paused") is True:
        return True
    if sched.get("enabled") is False:
        return True
    # paseo schedule ls json may use 'status'
    return str(sched.get("status", "")).lower() in ("paused", "disabled")


def render() -> bytes:
    return generate_latest(REGISTRY)
