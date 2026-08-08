#!/usr/bin/env python3
"""
Regression guard for issue #552: live-hardware test/deploy paths must never
become reachable from an automatic `push` or `pull_request` trigger.

Context: on 2026-08-05 this host's ephemeral TCP port pool nearly exhausted
(~29k TIME_WAIT sockets, ~500 conns/sec sustained for 14 min) during a CI
window. The proximate cause was live-hardware test tooling opening unpooled
HTTP connections (fixed separately in ab_engine.py / Tests.LiveRig), but the
constraint that actually bounds the blast radius is: nothing that hits the
physical RTX/RTX3060/P100 rig may run without a human (or an explicit
tag/dispatch) choosing to trigger it. This script re-checks that invariant
on every CI run so a future edit can't silently regress it — e.g. adding a
`pull_request:` trigger to a live-hardware workflow, wiring one into a
push-triggered workflow via `uses:`, loosening ci.yml's deploy-llama `if:`
gate, or wiring tests/bench/ab_engine.py into a workflow.

Run: python3 scripts/ci/check-live-hardware-gating.py
"""

from __future__ import annotations

import sys
from pathlib import Path

import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS_DIR = REPO_ROOT / ".github" / "workflows"

# Workflows that run jobs against the physical GPU rig (RTX 5060 Ti, RTX
# 3060, Tesla P100) — either by running the live-rig test suites or by
# deploying llama-engine binaries to the heads. Extend this set if a new
# live-hardware workflow is added.
LIVE_HARDWARE_WORKFLOWS = {
    "test-system.yml",
    "test-agent-workload.yml",
    "test-live-rig.yml",
    "deploy-heads.yml",
}

# Trigger types that fire without a human explicitly choosing to run this
# workflow right now. workflow_dispatch / workflow_call / repository_dispatch
# all require an explicit action (a person clicking "Run workflow", another
# workflow deliberately calling in, or an external dispatch event) so they
# are intentionally excluded.
AUTO_TRIGGERS = {"push", "pull_request", "pull_request_target", "schedule"}

# ci.yml's `deploy-llama` job is the one job in a push/pull_request-triggered
# workflow that is allowed to touch live hardware — but only when its `if:`
# gate excludes plain branch pushes and PRs. All of these substrings must be
# present in that job's `if:` for the gate to still be doing its job.
CI_DEPLOY_LLAMA_REQUIRED_GATE_MARKERS = [
    "refs/tags/",
    "repository_dispatch",
    "workflow_dispatch",
]


def _load_workflow(path: Path) -> dict:
    return yaml.safe_load(path.read_text()) or {}


def _trigger_names(wf: dict) -> set[str]:
    # PyYAML (YAML 1.1) resolves the unquoted `on` key to the boolean True,
    # not the string "on" — every GitHub Actions workflow trips this.
    on = wf.get("on", wf.get(True))
    if isinstance(on, str):
        return {on}
    if isinstance(on, list):
        return set(on)
    if isinstance(on, dict):
        return set(on.keys())
    return set()


def _reusable_calls(wf: dict) -> list[str]:
    """Local reusable-workflow filenames invoked via `uses: ./.github/workflows/X.yml`."""
    calls = []
    for job in (wf.get("jobs") or {}).values():
        uses = job.get("uses", "")
        if isinstance(uses, str) and uses.startswith("./.github/workflows/"):
            calls.append(uses.rsplit("/", 1)[-1])
    return calls


def main() -> int:
    errors: list[str] = []

    if not WORKFLOWS_DIR.is_dir():
        print(f"error: workflows dir not found at {WORKFLOWS_DIR}", file=sys.stderr)
        return 2

    paths = sorted(WORKFLOWS_DIR.glob("*.yml"))
    workflows = {p.name: _load_workflow(p) for p in paths}

    for name, wf in workflows.items():
        auto = _trigger_names(wf) & AUTO_TRIGGERS
        if not auto:
            continue

        # Direct: this workflow file IS a live-hardware workflow with an
        # auto trigger — e.g. someone added `pull_request:` to
        # test-system.yml's `on:` block.
        if name in LIVE_HARDWARE_WORKFLOWS:
            errors.append(
                f"{name}: has auto trigger(s) {sorted(auto)} but is a "
                f"live-hardware workflow. Live-hardware workflows must be "
                f"workflow_call/workflow_dispatch only (see CI_DEPLOY_LLAMA "
                f"note below for the one sanctioned exception)."
            )

        # Transitive: BFS through `uses:` reusable-workflow calls to see
        # whether an auto-triggered workflow can reach a live-hardware
        # workflow via workflow_call, at any depth.
        seen: set[str] = set()
        queue = _reusable_calls(wf)
        while queue:
            called = queue.pop()
            if called in seen:
                continue
            seen.add(called)
            if called in LIVE_HARDWARE_WORKFLOWS:
                errors.append(
                    f"{name}: auto-triggered ({sorted(auto)}) and transitively "
                    f"calls live-hardware workflow '{called}' via `uses:`."
                )
            called_wf = workflows.get(called)
            if called_wf:
                queue.extend(_reusable_calls(called_wf))

    # ci.yml is the one workflow with a *tag-scoped* push trigger by design
    # (`tags: ["llama.cpp*"]`), whose deploy-llama job also nominally sees
    # plain branch-push and pull_request events (since the top-level `on:`
    # for ci.yml includes those too). The invariant we need isn't "no auto
    # trigger" — it's "the job's own `if:` excludes branch push and PRs".
    ci = workflows.get("ci.yml")
    if ci:
        job = (ci.get("jobs") or {}).get("deploy-llama", {})
        cond = job.get("if", "")
        if not job:
            errors.append("ci.yml: 'deploy-llama' job not found — update this check if it was renamed.")
        else:
            missing = [m for m in CI_DEPLOY_LLAMA_REQUIRED_GATE_MARKERS if m not in cond]
            if missing:
                errors.append(
                    "ci.yml: deploy-llama job's `if:` is missing required gate "
                    f"marker(s) {missing} — a plain branch push or PR could now "
                    f"trigger a live-hardware deploy. Got: {cond!r}"
                )
            if "pull_request" in cond:
                errors.append(
                    "ci.yml: deploy-llama `if:` references 'pull_request' — "
                    f"PRs must never trigger a live-hardware deploy. Got: {cond!r}"
                )

    # tests/bench/ab_engine.py must stay manual-only: no workflow may invoke
    # it (it opens direct HTTP/RPC connections to the live rig).
    for name in workflows:
        text = (WORKFLOWS_DIR / name).read_text()
        if "ab_engine" in text:
            errors.append(
                f"{name}: references ab_engine.py — it must stay manual-only "
                "and never be wired into a workflow (see issue #552)."
            )

    if errors:
        print("Live-hardware gating check FAILED:\n")
        for e in errors:
            print(f"  - {e}")
        print(
            "\nSee issue #552 — live-hardware test/deploy paths reachable from "
            "push/pull_request drove this host's TIME_WAIT sockets to ~29k, "
            "near ephemeral-port exhaustion."
        )
        return 1

    print(
        f"Live-hardware gating check OK — {len(workflows)} workflow(s) checked, "
        f"{len(LIVE_HARDWARE_WORKFLOWS)} live-hardware workflow(s) confirmed "
        "unreachable from push/pull_request."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
