#!/usr/bin/env bash
# One-time script to create all GitHub labels used by the review/monitoring workflow.
# Safe to re-run (--force updates existing labels).
set -euo pipefail

create() {
    local name="$1" color="$2" desc="$3"
    echo "  $name"
    gh label create "$name" --color "$color" --description "$desc" --force
}

echo "=== Review severity labels ==="
create "p0-critical"    "d73a4a" "P0: data loss / correctness bug"
create "p1-high"        "e4e669" "P1: significant behavioral bug"
create "p2-low"         "cfd3d7" "P2: minor / performance / maintainability"

echo "=== Milestone labels ==="
create "milestone-m0"   "0052cc" "Milestone 0: llama fork + Store + Agent"
create "milestone-m1"   "0075ca" "Milestone 1: Coordinator + routing + migration"
create "milestone-m2"   "1d76db" "Milestone 2: Chunked dedup + prefix checkpoints"
create "milestone-m3"   "58a6ff" "Milestone 3: Persistence + Grafana + Langfuse"

echo "=== Source labels ==="
create "review-finding" "0075ca" "Finding created from code review"
create "monitoring"     "fbca04" "Created from Prometheus alert"
create "ci-failure"     "d73a4a" "Created from CI test failure"
create "auto-created"   "eeeeee" "Automatically created by tooling"

echo "Done."
