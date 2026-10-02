#!/usr/bin/env bash
# t0006 strata arm runner (S0 / S1) on RTX 3060 GPU1, port 8092.
#
# Lifecycle per arm:
#   pre-gates (8091 down + MemAvailable >= 67 GiB) -> h2d pre -> 1 Hz sampler
#   -> server.py launch -> wait /health -> load-time link check -> warmup+bench
#   -> stop (exact pids) -> h2d post -> stop sampler -> parse engine log
#   -> validity verdict -> one summary line to stdout.
#
# usage: arm-strata.sh S0 | S1
# Owner/leader rules: nothing on /tmp (TMPDIR pinned), no sudo, never pkill -f.
set -u

ARM=${1:?usage: arm-strata.sh S0|S1}
case "$ARM" in S0|S1) ;; *) echo "unknown arm $ARM" >&2; exit 2;; esac

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)      # <worktree>/.local/strata-research
WT=$(cd -- "$HERE/../.." && pwd)
EV="$WT/docs/evidence/strata-3060-ab"
ARMS="$EV/arms"; LOGS="$EV/logs"
CFG="$ARMS/strata-$(echo "$ARM" | tr 'A-Z' 'a-z').json"
STRATA=/mnt/WorkDisk/strata/src-v0.1.29
H2D="$WT/.local/pcie-probe/h2d"
PORT=8092; GPU=1
export TMPDIR=/mnt/WorkDisk/strata/tmp
mkdir -p "$TMPDIR" "$LOGS" "$ARMS" "$HERE"

# per-run label: ABBA repeats (S0,S0 / F0,F0) must not overwrite each other
RUN=$(( $(ls "$ARMS"/"$ARM"-*.bench.json 2>/dev/null | wc -l) + 1 ))
LABEL="$ARM-$RUN"
CFG_LOG=$(python3 -c "import json;print(json.load(open('$CFG'))['log'])")

SERVER_OUT="$LOGS/$LABEL-server.out"
LINK_TSV="$LOGS/$LABEL.link.tsv"
H2D_OUT="$LOGS/$LABEL.h2d.txt"
PIDFILE="$LOGS/$LABEL.engine.pid"     # sampler RSS target
SUMMARY="$LOGS/$LABEL.summary.txt"
ENGINE_LOG_COPY="$LOGS/$LABEL-engine.log"
rm -f "$PIDFILE" "$SUMMARY"

say() { echo "$*" | tee "$SUMMARY"; }

# ---------- pre-arm gates ----------
mem_available_gib() {
  awk '/MemAvailable/{printf "%.1f", $2/1024/1024}' /proc/meminfo
}
gate_8091() {
  if ss -H -ltn "sport = :8091" | grep -q .; then
    echo "GATE FAIL: 8091 is listening"; return 1
  fi
  return 0
}
gate_mem() {
  local avail; avail=$(mem_available_gib)
  # awk float compare
  if awk -v a="$avail" 'BEGIN{exit !(a >= 67.0)}'; then
    echo "GATE ok: MemAvailable ${avail} GiB (>= 67)"; return 0
  fi
  echo "GATE FAIL: MemAvailable ${avail} GiB (< 67)"; return 1
}
echo "[$ARM] pre-arm gates:"
gate_8091 || exit 3
gate_mem || exit 3

# ---------- h2d pre ----------
"$H2D" "$GPU" > "$H2D_OUT" 2>&1 || { echo "H2D pre probe failed"; exit 4; }
echo "[$ARM] h2d pre: $(cat "$H2D_OUT")"

# ---------- sampler (covers load + bench) ----------
bash "$HERE/sample_link.sh" "$LINK_TSV" "$GPU" "$PIDFILE" &
SAMPLER_PID=$!
cleanup() {
  kill "$SAMPLER_PID" 2>/dev/null || true
  wait "$SAMPLER_PID" 2>/dev/null || true
}
trap cleanup EXIT

# ---------- launch ----------
# server.py needs `regex` -> only available in the strata prep venv (prep-iq3s.sh)
STRATA_PY=/mnt/WorkDisk/strata/src/.venv/bin/python
[ -x "$STRATA_PY" ] || { echo "missing $STRATA_PY"; exit 5; }
: > "$SERVER_OUT"
: > "$CFG_LOG"   # server.py opens the config log in append mode - truncate per run
( cd "$STRATA" && exec "$STRATA_PY" -m serve.server --engine strata --config "$CFG" \
    --port "$PORT" --gpu "$GPU" ) >> "$SERVER_OUT" 2>&1 &
SERVER_PID=$!
echo "[$ARM] server.py pid $SERVER_PID, waiting for /health (max 900s)"

READY=0
for i in $(seq 1 900); do
  if curl -fsS -m 2 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then READY=1; break; fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "[$ARM] server.py exited during startup"; tail -20 "$SERVER_OUT"; exit 5
  fi
  sleep 1
done
[ "$READY" = 1 ] || { echo "[$ARM] health timeout"; exit 5; }

# readiness: /health alone may answer early - also require the engine's final
# init marker in the engine log (last startup line before serving).
for i in $(seq 1 120); do
  if grep -q "VRAM free with everything loaded" "$CFG_LOG" 2>/dev/null; then break; fi
  [ "$i" = 120 ] && { echo "[$ARM] engine load marker timeout"; exit 5; }
  sleep 1
done

# engine pid for RSS sampling (exact-match pattern, no pgrep -f kill use)
ENGINE_PID=$(pgrep -x strata | head -1 || true)
[ -n "$ENGINE_PID" ] && echo "$ENGINE_PID" > "$PIDFILE"
echo "[$ARM] ready, engine pid ${ENGINE_PID:-unknown}"

# ---------- load-time link check ----------
sleep 3   # let the sampler take a few post-load rows
LOAD_OK=$(awk -F'\t' 'NR>1 && $4+0>=20 {n++} END{print n+0}' "$LINK_TSV")
LINK_BAD=$(awk -F'\t' 'NR>1 && $4+0>=10 && $2+0!=4 {n++} END{print n+0}' "$LINK_TSV")
if [ "$LOAD_OK" -eq 0 ]; then
  echo "[$ARM] VALIDITY WARN: no util>=20 sample during load (link not exercised?)"
fi
if [ "$LINK_BAD" -gt 0 ]; then
  echo "[$ARM] LINK_INVALID: $LINK_BAD samples with util>=10 and gen!=4"
  echo "link_invalid load gen samples=$LINK_BAD" > "$LOGS/$LABEL.invalid"
  # keep going to gather evidence, validity decided at verdict
fi

# ---------- bench ----------
python3 "$HERE/bench3060.py" \
  --engine strata --arm "$LABEL" --port "$PORT" \
  --prompts "$EV/prompts" --outdir "$ARMS" \
  --reps 3 --max-tokens 256 --warmup
BENCH_RC=$?
echo "[$ARM] bench rc=$BENCH_RC"

# ---------- stop (exact pids only) ----------
kill "$SERVER_PID" 2>/dev/null || true
sleep 3
if [ -n "${ENGINE_PID:-}" ] && kill -0 "$ENGINE_PID" 2>/dev/null; then
  kill "$ENGINE_PID" 2>/dev/null || true
  sleep 3
  kill -9 "$ENGINE_PID" 2>/dev/null || true
fi
wait "$SERVER_PID" 2>/dev/null || true
rm -f "$PIDFILE"
sleep 2

# preserve this run's engine log (config log is truncated at next launch)
cp "$CFG_LOG" "$ENGINE_LOG_COPY"

# ---------- h2d post ----------
"$H2D" "$GPU" 2>&1 | tee -a "$H2D_OUT"

# ---------- parse ----------
parse_rc=0
if [ -f "$ARMS/$LABEL.bench.json" ]; then
  python3 "$HERE/parse_arm_logs.py" --engine strata --arm "$LABEL" \
    --log "$ENGINE_LOG_COPY" \
    --bench "$ARMS/$LABEL.bench.json" --out "$ARMS/$LABEL.logparse.json" || parse_rc=$?
fi

# ---------- verdict ----------
LINK_BAD_ALL=$(awk -F'\t' 'NR>1 && $4+0>=10 && $2+0!=4 {n++} END{print n+0}' "$LINK_TSV")
STATUS="ok"
if [ "$BENCH_RC" -ne 0 ]; then STATUS="bench_failed"; fi
if [ "$LINK_BAD_ALL" -gt 0 ]; then STATUS="link_invalid"; fi
if [ "$parse_rc" -ne 0 ] && [ "$STATUS" = ok ]; then STATUS="parse_mismatch"; fi

MEM_NOW=$(mem_available_gib)
H2D_LINE=$(grep -o 'dev [0-9].*' "$H2D_OUT" | tail -2 | tr '\n' '; ')
say "$LABEL done status=$STATUS bench_rc=$BENCH_RC parse_rc=$parse_rc mem_avail_after=${MEM_NOW}GiB h2d=[${H2D_LINE}] link_bad_samples=$LINK_BAD_ALL summary=$SUMMARY"
[ "$STATUS" = ok ]
