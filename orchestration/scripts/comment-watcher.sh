#!/usr/bin/env bash
# Poll GitHub repos for new @hydra commands in issue comments.
# For each new match, post a HUMAN_CMD event to the hydra-events bus.
# Never edits code or takes action on issues — only relays commands.
set -uo pipefail

REPOS="${COMMENT_WATCHER_REPOS:-ddvnguyen/hydra_vortex,ddvnguyen/llama.cpp}"
EVENTS_ROOM="${HYDRA_EVENTS_ROOM:-hydra-events}"
CURSOR_FILE="${CURSOR_FILE:-orchestration/state/comment-watcher-cursor.md}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="${REPO_DIR:-$(dirname "$(dirname "$SCRIPT_DIR")")}"
PARSER="$SCRIPT_DIR/comment-watcher-parse.py"

# Read last processed timestamp (ISO 8601). If file missing or empty, process everything.
CURSOR=""
if [ -f "$REPO_DIR/$CURSOR_FILE" ]; then
  CURSOR="$(head -1 "$REPO_DIR/$CURSOR_FILE" 2>/dev/null | tr -d '[:space:]')"
fi

echo "comment-watcher: cursor=${CURSOR:-<none>}"

NEW_COMMANDS=0
LATEST_TS="$CURSOR"

for REPO in $(echo "$REPOS" | tr ',' ' '); do
  echo "comment-watcher: polling $REPO ..."

  # Fetch open issues with comments (limit 100 — enough for active repos).
  ISSUES_FILE="$(mktemp /tmp/comment-watcher-XXXXXX.json)"
  gh issue list --repo "$REPO" --state open --json number,title,comments -L 100 > "$ISSUES_FILE" 2>/dev/null

  RESULT="$(python3 "$PARSER" "$REPO" "$CURSOR" < "$ISSUES_FILE" 2>/dev/null)" || true
  rm -f "$ISSUES_FILE"

  while IFS= read -r line; do
    case "$line" in
      FOUND) continue ;;
      CURSOR=*)
        NEW_TS="${line#CURSOR=}"
        if [ -z "$LATEST_TS" ] || [[ "$NEW_TS" > "$LATEST_TS" ]]; then
          LATEST_TS="$NEW_TS"
        fi
        ;;
      HUMAN_CMD*)
        NEW_COMMANDS=$((NEW_COMMANDS + 1))
        echo "comment-watcher: $line"
        paseo chat post "$EVENTS_ROOM" "$line" >/dev/null 2>&1 || \
          echo "comment-watcher: WARN failed to post to $EVENTS_ROOM" >&2
        ;;
    esac
  done <<< "$RESULT"
done

# Update cursor
if [ -n "$LATEST_TS" ] && [ "$LATEST_TS" != "$CURSOR" ]; then
  mkdir -p "$(dirname "$REPO_DIR/$CURSOR_FILE")"
  echo "$LATEST_TS" > "$REPO_DIR/$CURSOR_FILE"
  echo "comment-watcher: cursor updated to $LATEST_TS"
fi

if [ "$NEW_COMMANDS" -eq 0 ]; then
  echo "comment-watcher: no new commands found"
fi

exit 0
