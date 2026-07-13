#!/usr/bin/env bash
# Emit a hydra_vortex worker lifecycle event and notify the owning lead.
#
# Two effects, both best-effort:
#   1) Append a durable event to the `hydra-events` Paseo chat bus (the queue).
#   2) `paseo send` the event to the lead agent that spawned this worker (LEAD_ID),
#      waking it (context intact) to react immediately — no polling.
#
# The lead drains the bus with `paseo chat read --since <cursor>`, so even if the
# wake in (2) is missed (lead busy/dead), the event is not lost.
#
# Usage:
#   emit-event.sh <DONE|BLOCKED|FAILED> <issue#|0> <worker-name> [green|red|na] [detail...]
# Env:
#   HYDRA_EVENTS_ROOM   chat room name (default: hydra-events)
#   LEAD_ID             owning lead agent id (injected via `paseo run --env` at spawn)
#
# CONTRACT: this script must NEVER abort the calling worker. No `set -e`; it
# always exits 0 so the worker can stop cleanly even if the daemon is down.
set -uo pipefail

EVENT="${1:?event type required (DONE|BLOCKED|FAILED)}"
ISSUE="${2:?issue number required (0 if none)}"
WORKER="${3:?worker name required}"
VERIFY="${4:-na}"
# Drop the first 4 positional args (EVENT ISSUE WORKER VERIFY); the rest is
# free-text detail. Shift no more than we actually have.
shift "$(( $# < 4 ? $# : 4 ))" || true
DETAIL="${*:-}"

ROOM="${HYDRA_EVENTS_ROOM:-hydra-events}"
TS="$(date --iso-8601=seconds)"
HOST="$(hostname -s 2>/dev/null || echo unknown)"

# Single-line, machine-parseable payload. Stable key order; detail last.
MSG="EVENT=${EVENT} issue=${ISSUE} worker=${WORKER} verify=${VERIFY} ts=${TS} host=${HOST} detail=${DETAIL}"

# 1) Durable enqueue onto the bus.
if paseo chat post "$ROOM" "$MSG" >/dev/null 2>&1; then
  echo "emit-event: posted ${EVENT} for issue ${ISSUE} (${WORKER}) -> ${ROOM}"
else
  echo "emit-event: WARN chat post to '${ROOM}' failed (event not durably recorded)" >&2
fi

# 2) Wake the owning lead. Prefer the injected LEAD_ID; else resolve the live
#    role=lead agent via server-side label filter. --no-wait so we don't block.
lead="${LEAD_ID:-}"
if [ -z "$lead" ]; then
  lead="$(paseo ls --label role=lead --json 2>/dev/null | python3 -c '
import sys, json
try:
    agents = json.load(sys.stdin) or []
except Exception:
    agents = []
print((agents[0].get("shortId") or agents[0].get("id", "")) if agents else "")
' 2>/dev/null)"
fi

if [ -n "$lead" ]; then
  if paseo send "$lead" --no-wait "$MSG" >/dev/null 2>&1; then
    echo "emit-event: notified lead ${lead}"
  else
    echo "emit-event: WARN paseo send to lead '${lead}' failed (10-min check-in will catch up)" >&2
  fi
else
  echo "emit-event: WARN no lead found to notify (10-min check-in will drain the bus)" >&2
fi

exit 0
