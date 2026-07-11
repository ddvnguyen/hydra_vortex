#!/usr/bin/env bash
# utils/project-id.sh — Extract ProjectId from AGENT.MD or generate ULID-like sortable ID
# Usage: ./utils/project-id.sh [--project-id] [--taskid] [--generate-taskid]
# Output: Prints the ID to stdout
#
# Logic:
#   1. Read AGENT.MD for existing ProjectId
#   2. If not found, generate ULID-like sortable ID and write to AGENT.MD
#   3. TaskId is always regenerated per session (sortable by timestamp)
#
# ID Format: {prefix}_{epoch_ms}_{8_random_hex}
#   - Sortable by prefix + epoch (lexicographic = chronological)
#   - Random suffix ensures uniqueness within the same millisecond

# Resolve AGENT.MD path — use CWD-relative path (not @workspace:path syntax which only works in Cline tools)
AGENT_MD="./AGENT.md"

# Allow override via environment variable or first argument
if [ -n "$AGENT_MD_PATH" ]; then
  AGENT_MD="$AGENT_MD_PATH"
fi

# Generate a sortable ID: prefix + epoch_ms + random hex
generate_sortable_id() {
  local prefix="$1"
  python3 -c "
import secrets, time
ts = int(time.time() * 1000)
id_hex = secrets.token_hex(8)
print(f'{prefix}_{ts}{id_hex}')
" 2>/dev/null || echo "${prefix}_$(date +%s%N | cut -c1-13)_$(cat /proc/sys/kernel/random/uuid 2>/dev/null | tr -d '-' | cut -c1-16)"
}

# Extract ProjectId from AGENT.MD (persistent — generated once)
extract_project_id() {
  local pid
  if [ -f "$AGENT_MD" ]; then
    pid=$(grep -oP '(?<=# ProjectId: )[tp]_[0-9A-Za-z_]+' "$AGENT_MD" 2>/dev/null)
  fi
  if [ -z "$pid" ]; then
    # Generate ULID-like sortable ID for project — persistent, use first call only
    pid=$(generate_sortable_id "p")
    echo "Generating ProjectId: $pid" >&2
    # Write to AGENT.MD if file doesn't have ProjectId yet (create if needed)
    if ! grep -q '# ProjectId:' "$AGENT_MD" 2>/dev/null; then
      {
        echo "# ProjectId: $pid"
        echo ""
      } >> "$AGENT_MD"
      echo "Written ProjectId to $AGENT_MD" >&2
    fi
  fi
  echo "$pid"
}

# Generate ULID-like sortable TaskId (always new per session)
generate_task_id() {
  local tid
  tid=$(generate_sortable_id "t")
  echo "Generated TaskId: $tid" >&2
  # Write to AGENT.MD if file doesn't have TaskId yet (create if needed)
  if ! grep -q '# TaskId:' "$AGENT_MD" 2>/dev/null; then
    {
      echo "# TaskId: $tid"
      echo ""
    } >> "$AGENT_MD"
    echo "Written TaskId to $AGENT_MD" >&2
  fi
  echo "$tid"
}

case "${1:-}" in
  --project-id) extract_project_id ;;
  --taskid|--generate-taskid) generate_task_id ;;
  *) extract_project_id; generate_task_id ;;
esac