#!/usr/bin/env bash
# Schedule a one-shot resume of a rate-limited task at quota reset.
# Usage: quota-resume.sh "<reset-time>" <issue-number> "<worker-name>" [provider]
#   reset-time: anything `date -d` accepts, e.g. "2026-07-05 14:30" or "+5 hours"
# Called by the team lead (per QUOTA.md) or by you manually.
set -euo pipefail

RESET_AT="${1:?reset time required, e.g. '+5 hours' or '2026-07-05 14:30'}"
ISSUE="${2:?issue number required}"
WORKER="${3:?worker name required}"
PROVIDER="${4:-claude/sonnet}"
REPO_DIR="${REPO_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"

# Normalize + add 10 min safety margin past the reset boundary
AT_ISO="$(date -d "$RESET_AT + 10 minutes" --iso-8601=seconds)"
NAME="quota-resume-issue-${ISSUE}-$(date +%s)"

paseo schedule run-once \
  --name "$NAME" \
  --at "$AT_ISO" \
  --provider "$PROVIDER" \
  --cwd "$REPO_DIR" \
  "QUOTA RESUME for issue #${ISSUE}. Read orchestration/state/issue-${ISSUE}.md (the checkpoint). Then: if agent '${WORKER}' still exists in 'paseo ls', send it: paseo send ${WORKER} 'Quota window reset. Continue from the checkpoint in orchestration/state/issue-${ISSUE}.md'. If it no longer exists, spawn a fresh worker in the SAME worktree with the checkpoint's briefing and remaining-work list. If a tier-3 draft was produced meanwhile (label draft:needs-review on issue #${ISSUE}), review the draft first per LEAD_CHARTER.md instead of re-implementing."

echo "✓ resume scheduled: $NAME at $AT_ISO"
echo "  inspect: paseo schedule ls | cancel: paseo schedule delete $NAME"
