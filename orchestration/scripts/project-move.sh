#!/usr/bin/env bash
# project-move.sh — move a project item to a lane.
#
# Usage:
#   project-move.sh <item-id> "To Do"
#   project-move.sh --issue 84 --to "Ready For Development"
#
# The script looks up the single-select option id for the target lane in
# orchestration/state/project-v2.json, then issues an
# updateProjectV2ItemFieldValue mutation.
#
# Optional flags:
#   --dry-run    print the GraphQL that would run, don't execute
#   --by-issue   treat the first arg as a GitHub issue number, not an item-id;
#                we resolve the project's item-id for that issue first.
#
# Requires: gh CLI with `project` scope, orchestration/state/project-v2.json.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
STATE_FILE="$REPO_DIR/orchestration/state/project-v2.json"

dry_run=0
by_issue=0
positional=()
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run)  dry_run=1; shift ;;
    --by-issue) by_issue=1; shift ;;
    --issue)    by_issue=1; positional+=("$2"); shift 2 ;;
    --to)       positional+=("$2"); shift 2 ;;
    -h|--help)
      sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)          positional+=("$1"); shift ;;
  esac
done

# Argument shape: positional = [item_or_issue, lane] OR [lane] (when --issue N is used)
if [ "$by_issue" = "1" ]; then
  [ "${#positional[@]}" -eq 1 ] || { echo "✗ --issue <N> --to <lane>" >&2; exit 1; }
  arg1="${positional[0]}"
  target_lane=""
else
  [ "${#positional[@]}" -eq 2 ] || { echo "✗ usage: project-move.sh <item-id> \"<lane>\"" >&2; exit 1; }
  arg1="${positional[0]}"
  target_lane="${positional[1]}"
fi

# ─── guards ──────────────────────────────────────────────────────────────────
[ -f "$STATE_FILE" ] || { echo "✗ $STATE_FILE not found. Run bootstrap-project.sh --adopt first." >&2; exit 1; }
# Source the helper that prefers the keyring token (which has the 'project' scope)
# over the env-var / credential-store token (which often does not).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "$SCRIPT_DIR/_gh-with-project.sh"

# ─── load cached project metadata ───────────────────────────────────────────
project_id="$(python3 -c "import json;print(json.load(open('$STATE_FILE'))['project_node_id'])")"
status_field_id="$(python3 -c "import json;d=json.load(open('$STATE_FILE'));print(d['custom_fields']['Status']['field_id'])")"
option_id="$(python3 -c "import json,sys;d=json.load(open('$STATE_FILE'));print(d['lanes'].get(sys.argv[1],''))" "$target_lane")"
if [ -z "$option_id" ]; then
  echo "✗ lane '$target_lane' is not in the Status field. Known lanes:" >&2
  python3 -c "import json;d=json.load(open('$STATE_FILE'));[print('  -',k) for k in d['lanes']]" >&2
  exit 3
fi

# ─── optionally resolve an issue number to a project item-id ────────────────
if [ "$by_issue" = "1" ]; then
  # Find the project-item that references issue #$arg1. We do this by listing
  # all project items and matching on content.number.
  item_id="$(
    "$SCRIPT_DIR/project-intake.sh" --json \
      | python3 -c "import json,sys;items=json.load(sys.stdin);match=[i for i in items if i.get('number')==$arg1];print(match[0]['item_id'] if match else '')"
  )"
  if [ -z "$item_id" ]; then
    echo "✗ issue #$arg1 is not in the project" >&2
    exit 4
  fi
else
  item_id="$arg1"
fi

# ─── GraphQL mutation ────────────────────────────────────────────────────────
mutation='mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
  updateProjectV2ItemFieldValue(input: {
    projectId: $project,
    itemId: $item,
    fieldId: $field,
    value: { singleSelectOptionId: $option }
  }) {
    projectV2Item { id }
  }
}'

if [ "$dry_run" = "1" ]; then
  echo "DRY RUN — would execute:"
  echo "  gh api graphql \\"
  echo "    -f project=$project_id \\"
  echo "    -f item=$item_id \\"
  echo "    -f field=$status_field_id \\"
  echo "    -f option=$option_id \\"
  echo "    -f query=<updateProjectV2ItemFieldValue mutation>"
  exit 0
fi

gh api graphql \
  -f project="$project_id" \
  -f item="$item_id" \
  -f field="$status_field_id" \
  -f option="$option_id" \
  -f query="$mutation" \
  --jq '.data.updateProjectV2ItemFieldValue.projectV2Item.id // empty' \
  | grep -q . \
  || { echo "✗ move failed (no item id returned)" >&2; exit 5; }

echo "✓ moved item $item_id → \"$target_lane\""
