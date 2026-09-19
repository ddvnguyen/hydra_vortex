#!/bin/bash
# E1 KL-divergence gate (epic #148, design §6/A5): teacher-forced armed-vs-
# disarmed comparison using the fork's own --kl-divergence plumbing
# (perplexity.cpp:1949). Not sampled trajectories, not PPL.
#
# Usage: hydra-kl-gate.sh --binary <llama-perplexity> --model <gguf>
#   --corpus <wiki.test.raw> --ctx <N> --base-out <file.kld> --threshold <f>
#   [--pin-file <pins>] [--e1-mode dryrun]
# Exit 0 when Mean KLD < threshold, 1 otherwise. Threshold is a required
# arg: no default is provided on purpose (stated per leg, not inherited).
set -u
BIN=""; MODEL=""; CORPUS=""; CTX=""; BASE=""; THR=""; PIN=""; E1MODE="dryrun"
while [ $# -gt 0 ]; do
  case "$1" in
    --binary) BIN="$2"; shift 2;;
    --model) MODEL="$2"; shift 2;;
    --corpus) CORPUS="$2"; shift 2;;
    --ctx) CTX="$2"; shift 2;;
    --base-out) BASE="$2"; shift 2;;
    --threshold) THR="$2"; shift 2;;
    --pin-file) PIN="$2"; shift 2;;
    --e1-mode) E1MODE="$2"; shift 2;;
    *) echo "unknown arg: $1" >&2; exit 2;;
  esac
done
for v in BIN MODEL CORPUS CTX BASE THR PIN; do
  [ -n "${!v}" ] || { echo "missing --$(echo "$v" | tr 'A-Z' 'a-z')" >&2; exit 2; }
done
# 1. disarmed dump: teacher-forced base logits, fixed tokens.
env -u HYDRA_PIN_FILE -u HYDRA_E1_DRYRUN -u HYDRA_E1_TIMING \
  "$BIN" -m "$MODEL" -f "$CORPUS" -c "$CTX" --kl-divergence-base "$BASE" > /tmp/opencode/kl-dump.log 2>&1 || {
  echo "KL GATE: disarmed dump failed (see /tmp/opencode/kl-dump.log)" >&2; exit 1; }
# 2. armed compare: same binary, same tokens, engaged path on.
if [ "$E1MODE" = dryrun ]; then
  export HYDRA_E1_DRYRUN=1
else
  export HYDRA_E1_TIMING=1
fi
export HYDRA_PIN_FILE="$PIN"
"$BIN" -m "$MODEL" -c "$CTX" --kl-divergence --kl-divergence-base "$BASE" > /tmp/opencode/kl-cmp.log 2>&1 || {
  echo "KL GATE: armed compare failed (see /tmp/opencode/kl-cmp.log)" >&2; exit 1; }
MEAN=$(grep -m1 "Mean    KLD" /tmp/opencode/kl-cmp.log | awk '{print $3}')
[ -n "$MEAN" ] || { echo "KL GATE: no Mean KLD line in compare log" >&2; exit 1; }
echo "KL GATE: mean_kld=$MEAN threshold=$THR"
awk -v m="$MEAN" -v t="$THR" 'BEGIN { exit !(m < t) }' && {
  echo "KL GATE: PASS"; exit 0; } || { echo "KL GATE: FAIL"; exit 1; }
