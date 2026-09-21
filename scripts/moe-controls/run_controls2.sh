#!/usr/bin/env bash
# Holds the rig lock for the whole campaign; holder pid = this shell.
set -u
OUT=${1:?out dir}; ARMS=${2:?arms}; REPS=${3:?reps}
TOK=$(/tmp/opencode/rig-lock.sh acquire controls123 $$ 14400) || { echo ACQUIRE_FAIL; exit 1; }
echo "HOLDING $TOK $(date -u +%FT%TZ)"
trap '/tmp/opencode/rig-lock.sh release "$TOK"; pkill -f "llama-server.*--port 18437" 2>/dev/null; true' EXIT
python3 /tmp/opencode/controls/arm_controls2.py "$OUT" "$ARMS" "$REPS"
echo "CAMPAIGN_DONE $(date -u +%FT%TZ)"
