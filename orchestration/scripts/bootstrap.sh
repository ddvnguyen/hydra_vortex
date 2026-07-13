#!/usr/bin/env bash
# Creates the Paseo schedules that make the system autonomic.
# Idempotent: deletes any schedule with the same name before recreating.
# Run on (or against, via PASEO_HOST) the machine where the paseo daemon lives.
set -euo pipefail

# ─── EDIT THESE ──────────────────────────────────────────────────────────────
REPO_DIR="${REPO_DIR:-/mnt/WorkDisk/Workplace/hydra_vortex}"   # repo working copy
TZ_NAME="${TZ_NAME:-Asia/Ho_Chi_Minh}"               # IANA timezone for cron
LEAD_PROVIDER="${LEAD_PROVIDER:-claude/claude-sonnet-5}"   # heartbeat + triage (tier-1)
MONITOR_PROVIDER="${MONITOR_PROVIDER:-opencode/mimo-v2.5-free}"  # tier-3 free local model
# Supervision is now event-driven (workers wake the lead via emit-event.sh); this
# is only a slow steering check-in / lead-alive watchdog, not the primary trigger.
HEARTBEAT_EVERY="${HEARTBEAT_EVERY:-10m}"
MONITOR_EVERY="${MONITOR_EVERY:-30m}"
EVENTS_ROOM="${EVENTS_ROOM:-hydra-events}"     # durable worker->lead event bus
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

# 0) Durable event bus for worker->lead notifications (idempotent — a second
#    create fails on an existing room, which is fine).
if paseo chat create "$EVENTS_ROOM" \
     --purpose "hydra_vortex worker lifecycle events (DONE/BLOCKED/FAILED); the lead drains via 'paseo chat read --since <cursor>'" \
     >/dev/null 2>&1; then
  echo "✓ chat room created: $EVENTS_ROOM"
else
  echo "• chat room exists (or create skipped): $EVENTS_ROOM"
fi

# 1) Lead steering check-in + watchdog — NOT the primary completion trigger.
#    Workers wake the lead directly on finish (emit-event.sh: paseo send +
#    hydra-events bus). This schedule (a) pings the live role=lead agent for a
#    cheap steering scan of active workers, and (b) re-spawns the lead if none is
#    alive (self-heal), draining any events missed while it was down.
recreate lead-heartbeat \
  --every "$HEARTBEAT_EVERY" \
  --provider "$LEAD_PROVIDER" \
  --cwd "$REPO_DIR" \
  "LEAD CHECK-IN + WATCHDOG. First: lead=\$(paseo ls --label role=lead --json | python3 -c 'import sys,json;d=json.load(sys.stdin) or [];print((d[0].get(\"shortId\") or d[0].get(\"id\",\"\")) if d else \"\")'). If a lead is alive, run: paseo send \"\$lead\" --no-wait 'CHECK-IN: run /lead-supervise — drain hydra-events since the cursor, then steering-scan active workers (paseo ls + logs --tail 10) and nudge any that lost track. Then go idle.' and end this run. If NO lead is alive, spawn one: paseo run --provider $LEAD_PROVIDER --cwd $REPO_DIR --label role=lead --detach 'You are the hydra_vortex team lead; charter orchestration/LEAD_CHARTER.md. A supervision wake fired. Run /lead-supervise (event-drain-first: read orchestration/state/events-cursor.md, paseo chat read hydra-events --since <cursor> --json, act on that batch, advance the cursor), then handle any in-flight workers, then GO IDLE. Do not loop or block.' — then end this run."

# 2) Morning triage — pull new work into the cycle. Routed THROUGH the durable
#    lead so every worker it spawns is owned by the live role=lead agent (whose
#    id workers notify on finish). Wakes the lead if alive; spawns it otherwise.
recreate issue-triage \
  --cron "$TRIAGE_CRON" \
  --timezone "$TZ_NAME" \
  --provider "$LEAD_PROVIDER" \
  --cwd "$REPO_DIR" \
  "TRIAGE DISPATCH. lead=\$(paseo ls --label role=lead --json | python3 -c 'import sys,json;d=json.load(sys.stdin) or [];print((d[0].get(\"shortId\") or d[0].get(\"id\",\"\")) if d else \"\")'). TRIAGE INSTRUCTION = 'MORNING TRIAGE per orchestration/LEAD_CHARTER.md: check capacity (gh issue list --label status:planning --label status:in-progress vs max_issues_in_flight in orchestration/providers.yaml) — if at capacity, go idle. Else gh issue list --label status:ready, rank against orchestration/GOALS.md, pick the top item(s) up to capacity, run the dev-cycle from PICKUP. Spawn each worker with --env LEAD_ID=\$PASEO_AGENT_ID --label role=lead-child and the emit-event.sh final step. Big-change gate applies — propose and WAIT for user approval where required. Then go idle.'. If a lead is alive: paseo send \"\$lead\" --no-wait \"\$TRIAGE_INSTRUCTION\". Else: paseo run --provider $LEAD_PROVIDER --cwd $REPO_DIR --label role=lead --detach \"You are the hydra_vortex team lead; charter orchestration/LEAD_CHARTER.md. \$TRIAGE_INSTRUCTION\". End this run."

# 3) Comment watcher — polls hydra_vortex AND llama.cpp for @hydra commands
#    Every 2 minutes, detects new @hydra /X mentions and posts HUMAN_CMD events.
COMMENT_WATCHER_EVERY="${COMMENT_WATCHER_EVERY:-2m}"
COMMENT_WATCHER_REPOS="${COMMENT_WATCHER_REPOS:-ddvnguyen/hydra_vortex,ddvnguyen/llama.cpp}"
recreate comment-watcher \
  --every "$COMMENT_WATCHER_EVERY" \
  --provider "$MONITOR_PROVIDER" \
  --cwd "$REPO_DIR" \
  "COMMENT WATCHER RUN. Poll GitHub for new @hydra commands across repos: $COMMENT_WATCHER_REPOS. For each repo, run: gh issue list --repo <repo> --state open --json number,title,comments --paginate. Search the latest comment on each issue for patterns: @hydra /plan, @hydra /skip-pm, @hydra /approve, @hydra /implement, @hydra /merge. For each match found that hasn't been processed yet (check orchestration/state/comment-watcher-cursor.md for last processed timestamp), post a HUMAN_CMD event to the hydra-events bus via: paseo chat send hydra-events 'HUMAN_CMD issue=<N> repo=<repo> command=<cmd> ts=<timestamp>'. Update the cursor file with the latest processed timestamp. If no new commands found, end immediately. Never edit code or take action on issues directly — only relay commands to the event bus."

# 4) Monitoring — closes the loop by filing issues from production signals
recreate monitor \
  --every "$MONITOR_EVERY" \
  --provider "$MONITOR_PROVIDER" \
  --cwd "$REPO_DIR" \
  "MONITORING RUN. Your charter is orchestration/MONITOR_CHARTER.md — follow it exactly. Check staging health, new log errors since the cursor in orchestration/state/monitor-cursor.md, and CI status. File/comment GitHub issues per the charter (labels source:monitoring + status:ready), give SOAK verdicts on status:monitoring issues, update the cursor. Never edit code."

echo
echo "All schedules created. Verify with: paseo schedule ls"
echo "Event bus '$EVENTS_ROOM': paseo chat read $EVENTS_ROOM --json"
if [ "$START_PAUSED" = "true" ]; then
  echo "Schedules are PAUSED. Resume individually with: paseo schedule resume <name>"
fi
echo "Pause everything anytime with:      ./teardown.sh (or paseo schedule pause <name>)"
