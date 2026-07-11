#!/usr/bin/env bash
# Creates the Paseo schedules that make the system autonomic.
# Idempotent: deletes any schedule with the same name before recreating.
# Run on (or against, via PASEO_HOST) the machine where the paseo daemon lives.
set -euo pipefail

# ─── EDIT THESE ──────────────────────────────────────────────────────────────
REPO_DIR="${REPO_DIR:-/mnt/WorkDisk/Workplace/llm-server-monitoring}"   # repo working copy
TZ_NAME="${TZ_NAME:-Asia/Ho_Chi_Minh}"               # IANA timezone for cron
LEAD_PROVIDER="${LEAD_PROVIDER:-claude/claude-sonnet-5}"   # heartbeat + triage (tier-1)
MONITOR_PROVIDER="${MONITOR_PROVIDER:-opencode/mimo-v2.5-free}"  # tier-3 free local model
HEARTBEAT_EVERY="${HEARTBEAT_EVERY:-20m}"
MONITOR_EVERY="${MONITOR_EVERY:-30m}"
TRIAGE_CRON="${TRIAGE_CRON:-0 8 * * 1-5}"            # weekdays 08:00 local
# Create schedules paused so a human approves before any autonomous run spends
# quota. Set START_PAUSED=false to create them already running.
START_PAUSED="${START_PAUSED:-true}"
# ─────────────────────────────────────────────────────────────────────────────

command -v paseo >/dev/null || { echo "paseo CLI not found"; exit 1; }
[ -d "$REPO_DIR/orchestration" ] || { echo "orchestration/ not found in $REPO_DIR"; exit 1; }

# Resolve a schedule ID from its name (pause/delete need IDs, not names).
schedule_id() {  # schedule_id <name>
  paseo schedule ls --json 2>/dev/null | python3 -c \
    "import sys,json;d=json.load(sys.stdin);print(next((s.get('id','') for s in d if s.get('name')=='$1'),''))" 2>/dev/null
}

recreate() {  # recreate <name> <args...>
  local name="$1"; shift
  local old_id; old_id="$(schedule_id "$name")"
  [ -n "$old_id" ] && paseo schedule delete "$old_id" >/dev/null 2>&1 || true
  local out id
  out="$(paseo schedule create --name "$name" --json "$@")"
  id="$(echo "$out" | python3 -c "import sys,json;print(json.load(sys.stdin).get('id',''))" 2>/dev/null)"
  if [ "$START_PAUSED" = "true" ] && [ -n "$id" ]; then
    paseo schedule pause "$id" >/dev/null 2>&1 || true
    echo "✓ schedule (paused): $name"
  else
    echo "✓ schedule (running): $name"
  fi
}

# 1) Team-lead heartbeat — background supervision of all workers
recreate lead-heartbeat \
  --every "$HEARTBEAT_EVERY" \
  --provider "$LEAD_PROVIDER" \
  --cwd "$REPO_DIR" \
  "HEARTBEAT RUN. You are the team lead; your charter is orchestration/LEAD_CHARTER.md (re-read the DEVELOP and CLOSE sections). Idempotent sweep, cheap tokens, no code exploration: (1) paseo ls; for each active worker: paseo logs <id> --tail 10. (2) Handle per charter: nudge stalled workers, verify finished ones with their VERIFY command, execute orchestration/QUOTA.md for any rate-limited agent, summarize permission requests for the user. (3) Advance issue labels for any completed stage (PR, review, deploy, soak-close). (4) Worktree + state-file hygiene for closed issues. (5) If nothing needs action, end the run immediately."

# 2) Morning triage — pull new work into the cycle
recreate issue-triage \
  --cron "$TRIAGE_CRON" \
  --timezone "$TZ_NAME" \
  --provider "$LEAD_PROVIDER" \
  --cwd "$REPO_DIR" \
  "TRIAGE RUN. You are the team lead; charter: orchestration/LEAD_CHARTER.md. First check capacity: gh issue list --label status:planning --label status:in-progress; respect max_issues_in_flight in orchestration/providers.yaml — if at capacity, end the run. Otherwise: gh issue list --label status:ready, rank against orchestration/GOALS.md, pick the top item(s) up to capacity, and execute the dev-cycle protocol from PICKUP. Big-change gate applies: propose and WAIT for user approval where required."

# 3) Monitoring — closes the loop by filing issues from production signals
recreate monitor \
  --every "$MONITOR_EVERY" \
  --provider "$MONITOR_PROVIDER" \
  --cwd "$REPO_DIR" \
  "MONITORING RUN. Your charter is orchestration/MONITOR_CHARTER.md — follow it exactly. Check staging health, new log errors since the cursor in orchestration/state/monitor-cursor.md, and CI status. File/comment GitHub issues per the charter (labels source:monitoring + status:ready), give SOAK verdicts on status:monitoring issues, update the cursor. Never edit code."

echo
echo "All schedules created. Verify with: paseo schedule ls"
if [ "$START_PAUSED" = "true" ]; then
  echo "Schedules are PAUSED. Resume individually with: paseo schedule resume <name>"
fi
echo "Pause everything anytime with:      ./teardown.sh (or paseo schedule pause <name>)"
