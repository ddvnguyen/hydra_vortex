#!/usr/bin/env bash
# t0006 fork arm runner (F0 / F1 / F2) on RTX 3060 GPU1, port 8093.
#
# F0/F1 = NOMTP=1 (no MTP spec flags); F2 = F1 + MTP block (like-for-like vs S1).
# N (moe-expert-cache-size): F0 = 0; F1/F2 read from $HERE/f1-n.txt (computed
# from the first S0 strata fill, +/-10% target) or F1_N env override.
#
# Same lifecycle as arm-strata.sh: pre-gates -> h2d pre -> sampler -> launch ->
# health -> load link check -> warmup+bench -> stop (exact pid) -> h2d post ->
# parse -> verdict line.
# usage: arm-fork.sh F0 | F1 | F2
set -u

ARM=${1:?usage: arm-fork.sh F0|F1|F2}
case "$ARM" in F0|F1|F2) ;; *) echo "unknown arm $ARM" >&2; exit 2;; esac

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd -- "$HERE/../.." && pwd)
EV="$WT/docs/evidence/strata-3060-ab"
ARMS="$EV/arms"; LOGS="$EV/logs"
BIN="$WT/.local/q2g/src/build/bin"
H2D="$WT/.local/pcie-probe/h2d"
PORT=8093; GPU=1; CTX=16384
MODEL=/mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
DRAFT=/mnt/SSD/MTP/mtp-Qwen3.8-Flash-Next-shared-Q4_K_M.gguf
mkdir -p "$LOGS" "$ARMS"

# per-run label: ABBA repeats must not overwrite each other
RUN=$(( $(ls "$ARMS"/"$ARM"-*.bench.json 2>/dev/null | wc -l) + 1 ))
LABEL="$ARM-$RUN"

SERVER_OUT="$LOGS/$LABEL-engine.log"
LINK_TSV="$LOGS/$LABEL.link.tsv"
H2D_OUT="$LOGS/$LABEL.h2d.txt"
PIDFILE="$LOGS/$LABEL.engine.pid"
SUMMARY="$LOGS/$LABEL.summary.txt"
rm -f "$PIDFILE" "$SUMMARY"

say() { echo "$*" | tee "$SUMMARY"; }

# ---------- cache N ----------
case "$ARM" in
  F0) N=0 ;;
  F1|F2)
    N=${F1_N:-}
    if [ -z "$N" ]; then
      if [ -s "$HERE/f1-n.txt" ]; then N=$(cat "$HERE/f1-n.txt")
      else echo "F1_N / f1-n.txt missing (run first S0 + compute step)"; exit 3; fi
    fi ;;
esac

# ---------- pre-arm gates ----------
gate_8091() {
  if ss -H -ltn "sport = :8091" | grep -q .; then echo "GATE FAIL: 8091 listening"; return 1; fi
  return 0
}
gate_mem() {
  local avail; avail=$(awk '/MemAvailable/{printf "%.1f", $2/1024/1024}' /proc/meminfo)
  if awk -v a="$avail" 'BEGIN{exit !(a >= 67.0)}'; then
    echo "GATE ok: MemAvailable ${avail} GiB (>= 67)"; return 0
  fi
  echo "GATE FAIL: MemAvailable ${avail} GiB (< 67)"; return 1
}
echo "[$ARM] pre-arm gates (N=$N):"
gate_8091 || exit 3
gate_mem || exit 3

# ---------- h2d pre ----------
"$H2D" "$GPU" > "$H2D_OUT" 2>&1 || { echo "H2D pre probe failed"; exit 4; }
echo "[$ARM] h2d pre: $(cat "$H2D_OUT")"

# ---------- sampler ----------
bash "$HERE/sample_link.sh" "$LINK_TSV" "$GPU" "$PIDFILE" &
SAMPLER_PID=$!
cleanup() { kill "$SAMPLER_PID" 2>/dev/null || true; wait "$SAMPLER_PID" 2>/dev/null || true; }
trap cleanup EXIT

# ---------- flags ----------
FLAGS=(-m "$MODEL" --split-mode layer -fit off -ngl 99
  --n-cpu-moe 99 --override-tensor per_layer_token_embd=CPU
  --moe-expert-cache-size "$N"
  -c "$CTX" --parallel 1 --flash-attn on --jinja -t 6
  --experimental-logs --load-mode none --ple-prefetch
  -b 2048 -ub 2048 --host 127.0.0.1 --port "$PORT"
  --alias gsq-rco-iq3_s)
if [ "$ARM" = F2 ]; then
  FLAGS+=(--spec-draft-moe-expert-cache-size "$N"
          --spec-type draft-mtp --spec-draft-model "$DRAFT"
          --spec-draft-n-max 3 --spec-draft-p-min 0.75 --spec-draft-ngl 0)
else
  FLAGS+=(--spec-type none)
fi

# ---------- launch ----------
: > "$SERVER_OUT"
( export LD_LIBRARY_PATH="$BIN:/opt/software/cuda/13.2.1/lib64:${LD_LIBRARY_PATH:-}"
  export CUDA_VISIBLE_DEVICES="$GPU" CUDA_DEVICE_ORDER=PCI_BUS_ID
  exec "$BIN/llama-server" "${FLAGS[@]}" ) >> "$SERVER_OUT" 2>&1 &
SERVER_PID=$!
echo "$SERVER_PID" > "$PIDFILE"
echo "[$ARM] llama-server pid $SERVER_PID, waiting for /health (max 900s)"

READY=0
for i in $(seq 1 900); do
  if curl -fsS -m 2 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then READY=1; break; fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "[$ARM] llama-server exited during startup"; tail -20 "$SERVER_OUT"; exit 5
  fi
  sleep 1
done
[ "$READY" = 1 ] || { echo "[$ARM] health timeout"; exit 5; }
echo "[$ARM] ready"

sleep 3
LOAD_OK=$(awk -F'\t' 'NR>1 && $4+0>=20 {n++} END{print n+0}' "$LINK_TSV")
LINK_BAD=$(awk -F'\t' 'NR>1 && $4+0>=10 && $2+0!=4 {n++} END{print n+0}' "$LINK_TSV")
[ "$LOAD_OK" -eq 0 ] && echo "[$ARM] VALIDITY WARN: no util>=20 sample during load"
[ "$LINK_BAD" -gt 0 ] && { echo "[$ARM] LINK_INVALID: $LINK_BAD bad samples"; echo "link_invalid" > "$LOGS/$LABEL.invalid"; }

# VRAM after load (for F1 fill verification vs strata fill)
nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -i "$GPU" > "$LOGS/$LABEL.vram_after_load.txt"
echo "[$ARM] vram_after_load_mib=$(cat "$LOGS/$LABEL.vram_after_load.txt")"

# ---------- bench ----------
python3 "$HERE/bench3060.py" \
  --engine fork --arm "$LABEL" --port "$PORT" \
  --prompts "$EV/prompts" --outdir "$ARMS" \
  --reps 3 --max-tokens 256 --warmup
BENCH_RC=$?
echo "[$ARM] bench rc=$BENCH_RC"

# ---------- stop (exact pid) ----------
kill "$SERVER_PID" 2>/dev/null || true
sleep 3
if kill -0 "$SERVER_PID" 2>/dev/null; then
  kill -9 "$SERVER_PID" 2>/dev/null || true
fi
wait "$SERVER_PID" 2>/dev/null || true
rm -f "$PIDFILE"
sleep 2

# ---------- h2d post ----------
"$H2D" "$GPU" 2>&1 | tee -a "$H2D_OUT"

# ---------- parse ----------
parse_rc=0
if [ -f "$ARMS/$LABEL.bench.json" ]; then
  python3 "$HERE/parse_arm_logs.py" --engine fork --arm "$LABEL" \
    --log "$SERVER_OUT" --bench "$ARMS/$LABEL.bench.json" \
    --out "$ARMS/$LABEL.logparse.json" || parse_rc=$?
fi

# ---------- verdict ----------
LINK_BAD_ALL=$(awk -F'\t' 'NR>1 && $4+0>=10 && $2+0!=4 {n++} END{print n+0}' "$LINK_TSV")
STATUS="ok"
[ "$BENCH_RC" -ne 0 ] && STATUS="bench_failed"
[ "$LINK_BAD_ALL" -gt 0 ] && STATUS="link_invalid"
[ "$parse_rc" -ne 0 ] && [ "$STATUS" = ok ] && STATUS="parse_mismatch"

MEM_NOW=$(awk '/MemAvailable/{printf "%.1f", $2/1024/1024}' /proc/meminfo)
H2D_LINE=$(grep -o 'dev [0-9].*' "$H2D_OUT" | tail -2 | tr '\n' '; ')
say "$LABEL done status=$STATUS N=$N bench_rc=$BENCH_RC parse_rc=$parse_rc mem_avail_after=${MEM_NOW}GiB h2d=[${H2D_LINE}] link_bad_samples=$LINK_BAD_ALL summary=$SUMMARY"
[ "$STATUS" = ok ]
