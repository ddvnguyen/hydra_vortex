#!/usr/bin/env bash
# Deterministic pipeline vitals. No LLM judgment here — just evidence.
# Called by the Instrumentor driver; safe to run manually anytime.
set -uo pipefail
REPO_DIR="${REPO_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
cd "$REPO_DIR" || exit 1
T() { timeout 20 "$@" 2>&1 || echo "(command failed: $*)"; }

echo "== time =="
date --iso-8601=seconds

echo "== schedules =="
T paseo schedule ls

echo "== active agents =="
T paseo ls

echo "== issue queue (label: count) =="
for l in status:ready status:planning status:in-progress status:review \
         status:deployed status:monitoring draft:needs-review source:monitoring; do
  c=$(timeout 20 gh issue list --label "$l" --state open --json number --jq 'length' 2>/dev/null || echo "?")
  echo "$l: $c"
done

echo "== worktrees =="
git worktree list 2>/dev/null | sed 's/  */ /g'

echo "== checkpoints (state/) =="
ls -lt orchestration/state/ 2>/dev/null | head -8

echo "== last instrumentor report =="
head -3 orchestration/state/instrumentor-report.md 2>/dev/null || echo "(none yet)"
