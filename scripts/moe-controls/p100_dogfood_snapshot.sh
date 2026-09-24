#!/usr/bin/env bash
# P100 dogfood snapshots — LOCAL ONLY, never uploaded.
#   endpoints (default, hourly + manually before restart/redeploy):
#     read-only GETs of engine state that lives in memory (turns is lost
#     on engine restart, hence the cron) -> <dest>/<HHMM>-<endpoint>.json
#   sessions (daily):
#     copy OMP sessions whose model is p100/ornith-1.5-35b -> omp-<ns>.jsonl
set -u
MODE="${1:-endpoints}"
BASE="/mnt/WorkDisk/hydra-dogfood/p100"
DATE="$(date -u +%F)"
DEST="$BASE/$DATE"
mkdir -p "$DEST"
rc=0

case "$MODE" in
  endpoints)
    for ep in experts turns profile; do
      out="$DEST/$(date -u +%H%M)-$ep.json"
      if ! curl -fsS --max-time 15 "http://192.168.122.21:8086/$ep" -o "$out"; then
        echo "FAIL endpoint $ep" >&2
        rm -f "$out"
        rc=1
      fi
    done
    echo "endpoints -> $DEST"
    ;;
  sessions)
    n=0
    while IFS= read -r -d '' f; do
      grep -qF '"p100/ornith-1.5-35b"' "$f" || continue
      cp -p "$f" "$DEST/omp-$(date -u +%s%N).jsonl" && n=$((n + 1))
    done < <(find "$HOME/.omp/agent/sessions" -type f -name '*.jsonl' -print0 2>/dev/null)
    echo "sessions copied: $n -> $DEST"
    ;;
  *)
    echo "usage: $0 [endpoints|sessions]" >&2
    exit 2
    ;;
esac
exit $rc
