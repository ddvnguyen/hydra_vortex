#!/usr/bin/env bash
# Run MMLU-Pro in batches of BATCH_SIZE until done.
# Each batch is a fresh run — no duplicate samples.
# Usage: bash scripts/eval/quality/run-batches.sh [--batch 10] [--bench mmlu-pro]
set -euo pipefail

BATCH_SIZE=10
BENCH=mmlu-pro
RESULT_DIR=/tmp/hydra-quality-results
SEED=42

while [[ $# -gt 0 ]]; do
  case $1 in
    --batch) BATCH_SIZE="$2"; shift 2 ;;
    --bench) BENCH="$2"; shift 2 ;;
    --seed)  SEED="$2"; shift 2 ;;
    *) echo "Unknown: $1"; exit 1 ;;
  esac
done

mkdir -p "$RESULT_DIR"
HERE="$(cd "$(dirname "$0")" && pwd)"

log() { echo "[$(date '+%H:%M:%S')] $*"; }

start=0
batch=1

while [[ $start -lt 150 ]]; do
  out="$RESULT_DIR/${BENCH}-batch${batch}.json"
  log "=== Batch $batch: samples $((start + 1))-$((start + BATCH_SIZE)) ==="

  if ! python3 "$HERE/$BENCH/evaluate.py" \
    --limit "$BATCH_SIZE" --seed "$SEED" --start "$start" \
    --output "$out" 2>&1; then
    log "Batch $batch FAILED — progress saved at $out"
    break
  fi

  python3 -c "
import json
d = json.load(open('$out'))
print(f'  Score: {d[\"score\"]}% ({d[\"num_samples\"]} samples)')
print(f'  P/D:   ', end='')
print('PASS' if d.get('pd_pass') else 'FAIL', end='')
issues = d.get('pd_issues', [])
if issues: print(f' [{', '.join(issues)}]', end='')
print()
print(f'  Elapsed: {d[\"elapsed_s\"]}s')
"

  start=$((start + BATCH_SIZE))
  batch=$((batch + 1))
done

echo
log "Done — $((batch - 1)) batches, ${start} samples total"
