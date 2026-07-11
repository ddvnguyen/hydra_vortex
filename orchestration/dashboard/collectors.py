#!/usr/bin/env python3
"""collectors.py — read-only data collection for the hydra_vortex dashboard.

Pulls three sources and returns plain dicts:
  - Paseo:  `paseo ls --json`, `paseo schedule ls --json`, `paseo status`
  - GitHub: `gh issue list` bucketed by `status:*` labels
  - State:  orchestration/state/*.md checkpoints, monitor-cursor, instrumentor

No writes, no `.env` access. Failures are isolated per-collector.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import time
from typing import Any, Dict, List

REPO_DIR = os.environ.get(
    "REPO_DIR", "/mnt/WorkDisk/Workplace/llm-server-monitoring"
)
STATE_DIR = os.environ.get(
    "STATE_DIR", os.path.join(REPO_DIR, "orchestration", "state")
)
GH_REPO = os.environ.get("GH_REPO", "ddvnguyen/hydra_vortex")

STATUS_LABELS = [
    "status:ready",
    "status:planning",
    "status:in-progress",
    "status:review",
    "status:deployed",
    "status:monitoring",
]

# --------------------------------------------------------------------------- #
# helpers
# --------------------------------------------------------------------------- #


def _run(cmd: List[str], timeout: int = 20) -> str:
    """Run a command, return stdout. Raises on non-zero / timeout."""
    res = subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        cwd=REPO_DIR,
        timeout=timeout,
    )
    if res.returncode != 0:
        raise RuntimeError(
            f"{cmd[0]} exited {res.returncode}: {res.stderr.strip()[:200]}"
        )
    return res.stdout


def _safe(label: str, fn) -> Any:
    """Run a collector; on failure return an error-shaped dict, never raise."""
    try:
        return fn()
    except Exception as exc:  # noqa: BLE001 - dashboard must never crash
        return {"error": f"{label}: {type(exc).__name__}: {exc}"}


# --------------------------------------------------------------------------- #
# Paseo
# --------------------------------------------------------------------------- #


def collect_paseo() -> Dict[str, Any]:
    raw = _run(["paseo", "ls", "--json"])
    agents = json.loads(raw) if raw.strip() else []
    # normalise: ensure expected keys exist
    norm_agents = []
    for a in agents:
        norm_agents.append(
            {
                "id": a.get("shortId") or a.get("id"),
                "name": (a.get("name") or "").strip() or "(unnamed)",
                "provider": a.get("provider", "unknown"),
                "status": a.get("status", "unknown"),
                "cwd": a.get("cwd", ""),
                "created": a.get("created", ""),
            }
        )

    schedules: List[Dict[str, Any]] = []
    try:
        sraw = _run(["paseo", "schedule", "ls", "--json"])
        schedules = json.loads(sraw) if sraw.strip() else []
    except Exception:
        schedules = []

    # daemon up?
    daemon_up = 0
    try:
        s = _run(["paseo", "status"], timeout=10)
        daemon_up = 1 if "running" in s.lower() else 0
    except Exception:
        daemon_up = 0

    return {
        "agents": norm_agents,
        "schedules": schedules,
        "daemon_up": daemon_up,
    }


# --------------------------------------------------------------------------- #
# GitHub issues
# --------------------------------------------------------------------------- #


def _bucket_status(labels: List[Dict[str, Any]]) -> str:
    names = [l.get("name", "") for l in labels]
    for name in names:
        if name in STATUS_LABELS:
            return name
    return "status:none"


def collect_gh() -> Dict[str, Any]:
    fields = "number,title,labels,state,assignees,updatedAt"
    raw = _run(
        ["gh", "issue", "list", "--repo", GH_REPO, "--state", "all",
         "--json", fields],
        timeout=30,
    )
    issues = json.loads(raw) if raw.strip() else []

    board: Dict[str, List[Dict[str, Any]]] = {s: [] for s in STATUS_LABELS}
    board["status:none"] = []
    for it in issues:
        status = _bucket_status(it.get("labels", []))
        board[status].append(
            {
                "number": it.get("number"),
                "title": it.get("title", ""),
                "state": it.get("state", ""),
                "assignees": [a.get("login", "") for a in it.get("assignees", [])],
                "updatedAt": it.get("updatedAt", ""),
                "labels": [l.get("name", "") for l in it.get("labels", [])],
            }
        )
    # sort each column by updatedAt desc (newest first)
    for col in board.values():
        col.sort(key=lambda x: x.get("updatedAt", ""), reverse=True)
    return {"board": board, "total": len(issues)}


# --------------------------------------------------------------------------- #
# local state files
# --------------------------------------------------------------------------- #


def _read(path: str, limit: int = 4000) -> str:
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read(limit)
    except Exception:
        return ""


def _first_heading(text: str) -> str:
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("#"):
            return line.lstrip("# ").strip()
    return ""


def collect_state() -> Dict[str, Any]:
    checkpoints: List[Dict[str, Any]] = []
    monitor_cursor = ""
    instrumentor = {"verdict": "unknown", "report": "", "history": []}

    if os.path.isdir(STATE_DIR):
        for name in sorted(os.listdir(STATE_DIR)):
            if name in (".gitkeep",):
                continue
            full = os.path.join(STATE_DIR, name)
            if not os.path.isfile(full):
                continue
            text = _read(full)
            if name == "monitor-cursor.md":
                monitor_cursor = text.strip()
                continue
            if name == "instrumentor-report.md":
                instrumentor["report"] = text.strip()
                m = re.search(r"\b(PASS|WARN|FAIL)\b", text)
                instrumentor["verdict"] = m.group(1) if m else "unknown"
                continue
            if name == "instrumentor-history.log":
                lines = [l.strip() for l in text.splitlines() if l.strip()]
                instrumentor["history"] = lines[-12:]
                continue
            # generic checkpoint file (issue-<N>.md etc.)
            checkpoints.append(
                {
                    "file": name,
                    "heading": _first_heading(text),
                    "bytes": len(text),
                }
            )

    return {
        "checkpoints": checkpoints,
        "monitor_cursor": monitor_cursor,
        "instrumentor": instrumentor,
    }


# --------------------------------------------------------------------------- #
# aggregate
# --------------------------------------------------------------------------- #


def collect_all() -> Dict[str, Any]:
    paseo = _safe("paseo", collect_paseo)
    gh = _safe("github", collect_gh)
    state = _safe("state", collect_state)
    return {
        "generated_at": int(time.time()),
        "repo": GH_REPO,
        "paseo": paseo,
        "github": gh,
        "state": state,
    }


if __name__ == "__main__":
    print(json.dumps(collect_all(), indent=2, default=str))
