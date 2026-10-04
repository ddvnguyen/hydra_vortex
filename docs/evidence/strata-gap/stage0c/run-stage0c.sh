#!/usr/bin/env bash
# Stage 0c (Option C gate G-0b) runner — build-vs-itself KL floor, G-K2 denominator.
#
# Owner rules enforced here:
#   * GPU1 (RTX 3060) ONLY, CUDA_VISIBLE_DEVICES=1, GPU0 never touched, one GPU = one task.
#   * ONLY the CI artifact .local/perplexity-ci/bin/llama-perplexity is executed, and only
#     as a perplexity/KL tool. No builds, no podman, no CI triggers, never llama-server.
#   * NEVER /tmp: TMPDIR lives under .local/tmp, every log under docs/evidence/.../stage0c/
#     (scripts/hydra-kl-gate.sh hardcodes /tmp/opencode — this script is the path override).
#   * --override-kv banned; k=10 is not applicable to this flow (no sampler involved).
#   * exact-pid kills only; ports 8086/8091/8093 never bound; owner VM untouched.
#   * pre-arm: both GPUs <= 5 MiB, 8091 not listening, MemAvailable >= 67 GiB.
#
# usage: run-stage0c.sh prearm | A <label> <chunks> | B <label> <base.kld> | h2d <label> | final
set -u

CMD=${1:?usage: run-stage0c.sh prearm|A <label> <chunks>|B <label> <base.kld>|h2d <label>|final}

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)   # docs/evidence/strata-gap/stage0c
WT=$(cd -- "$HERE/../../../.." && pwd)                     # worktree root
EV="$HERE"
ART="$WT/../perplexity-ci/bin"
BIN="$ART/llama-perplexity"
TMP="$WT/.local/tmp"
BASEDIR="$WT/.local/stage0c"
CORPUS="$WT/scripts/eval/wikitext-2-raw/wiki.test.raw"
H2D="$WT/../pcie-probe/h2d"
SAMPLER="$EV/sample_link.sh"
MODEL=/mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
GPU=1
CTX=16384
THREADS=6
mkdir -p "$TMP" "$BASEDIR"

# ---- flags: Stage 0 F0 verbatim minus the server-only/example-rejected ones (PRE-REGISTERED.md §2) ----
COMMON_FLAGS=( -m "$MODEL"
  --split-mode layer -fit off -ngl 99
  --n-cpu-moe 99 --override-tensor per_layer_token_embd=CPU
  --moe-expert-cache-size 0
  -c "$CTX" -np 1 --flash-attn on -t "$THREADS"
  --experimental-logs --load-mode none
  -b 2048 -ub 2048 )

# zero-change env: every HYDRA_* / LLAMA_* stripped before exec
# (parity with scripts/hydra-kl-gate.sh step 1, which is otherwise unusable here
#  because it hardcodes /tmp/opencode for its logs).
CLEAN_VARS=( HYDRA_PIN_FILE HYDRA_E1_DRYRUN HYDRA_E1_TIMING HYDRA_E0_STATS
  LLAMA_ARG_THREADS LLAMA_ARG_CTX_SIZE LLAMA_ARG_N_GPU_LAYERS LLAMA_ARG_BATCH
  LLAMA_ARG_UBATCH LLAMA_ARG_FLASH_ATTN LLAMA_ARG_N_CPU_MOE
  LLAMA_ARG_MOE_EXPERT_CACHE_SIZE LLAMA_ARG_SPLIT_MODE LLAMA_ARG_FIT )

gpu_state() {
  nvidia-smi --query-gpu=index,memory.used,utilization.gpu --format=csv,noheader
}

gate_light() {   # checked before EVERY launch
  local g0 g1
  g0=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -i 0)
  g1=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -i 1)
  [ "${g0:-999}" -gt 5 ] && { echo "GATE FAIL: GPU0 busy (${g0} MiB) — GPU0 is never ours"; return 1; }
  [ "${g1:-999}" -gt 5 ] && { echo "GATE FAIL: GPU1 busy (${g1} MiB) — one GPU one task"; return 1; }
  if ss -H -ltn 'sport = :8091' | grep -q .; then echo "GATE FAIL: 8091 listening (not ours)"; return 1; fi
  if pgrep -x llama-server >/dev/null 2>&1 || pgrep -x llama-perplexity >/dev/null 2>&1; then
    echo "GATE FAIL: another llama process: $(pgrep -ax llama-server llama-perplexity)"; return 1
  fi
  return 0
}

gate_prearm() {
  local rc=0
  gate_light || rc=1
  local avail; avail=$(awk '/MemAvailable/{printf "%.1f", $2/1024/1024}' /proc/meminfo)
  if awk -v a="$avail" 'BEGIN{exit !(a >= 67.0)}'; then
    echo "GATE ok: MemAvailable ${avail} GiB (>= 67, pre-arm only)"
  else
    echo "GATE FAIL: MemAvailable ${avail} GiB (< 67 at pre-arm)"; rc=1
  fi
  local nproc_ threads=$THREADS spare
  nproc_=$(nproc); spare=$(( nproc_ - threads ))
  if [ "$spare" -ge 2 ]; then echo "GATE ok: spare $spare = nproc $nproc_ - threads $threads (>= 2)"
  else echo "GATE FAIL: spare $spare < 2"; rc=1; fi
  [ -x "$BIN" ] || { echo "GATE FAIL: $BIN missing/not executable"; rc=1; }
  [ -r "$CORPUS" ] || { echo "GATE FAIL: corpus $CORPUS missing"; rc=1; }
  local free_gb; free_gb=$(df -BG --output=avail "$WT" | tail -1 | tr -dc '0-9')
  if [ "${free_gb:-0}" -ge 30 ]; then echo "GATE ok: worktree disk ${free_gb} GiB free (>= 30)"
  else echo "GATE FAIL: worktree disk ${free_gb} GiB free (< 30)"; rc=1; fi
  return $rc
}

h2d_probe() {   # 6.00-6.30 GB/s required; caller reruns once if invalid
  "$H2D" "$GPU" 2>&1
}

h2d_ok() {      # parse "<dev> <name> H2D pinned: avg X.XX GB/s best ..."
  awk '{for(i=1;i<=NF;i++) if($i=="avg") {g=$(i+1); exit !(g>=6.00 && g<=6.30)}}' <<<"$1"
}

run_binary() {  # run_binary <label> <logfile-suffix> -- <args...>
  local label=$1; shift
  local log="$EV/$label.log"
  local h2d_out="$EV/$label.h2d.txt"
  local link="$EV/$label.link.tsv"

  gate_light || exit 3

  # h2d pre (rerun once if invalid)
  local pre; pre=$(h2d_probe)
  printf 'h2d_pre: %s\n' "$pre" > "$h2d_out"
  if ! h2d_ok "$pre"; then
    pre=$(h2d_probe); printf 'h2d_pre_rerun: %s\n' "$pre" >> "$h2d_out"
    if ! h2d_ok "$pre"; then echo "GATE FAIL: h2d pre invalid after rerun: $pre"; exit 4; fi
  fi

  {
    echo "=== stage0c $label ==="
    echo "date: $(date -Iseconds)"
    echo "host: $(hostname)"
    echo "binary: $BIN"
    echo "binary_sha256: $(sha256sum "$BIN" | cut -d' ' -f1)"
    echo "cmd: $*"
    echo "cwd: $WT"
    echo "TMPDIR: $TMP"
    echo "env_HYDRA_LLAMA: $(env | grep -E '^(HYDRA|LLAMA)_' || echo '(none — zero-change)')"
    echo "nproc=$(nproc) threads=$THREADS load1=$(cut -d' ' -f1 /proc/loadavg)"
    echo "memavailable_gib: $(awk '/MemAvailable/{printf "%.1f", $2/1024/1024}' /proc/meminfo)"
    echo "gpu_state_before:"; gpu_state
    echo "$pre"
    echo "--- output ---"
  } > "$log"

  # 1 Hz link sampler across the whole run (no pid file: rss column left empty)
  bash "$SAMPLER" "$link" "$GPU" &
  local sampler_pid=$!

  local t0 t1 rc
  t0=$(date +%s.%N)
  ( export TMPDIR="$TMP"
    export CUDA_VISIBLE_DEVICES="$GPU" CUDA_DEVICE_ORDER=PCI_BUS_ID
    export LD_LIBRARY_PATH="$ART:/opt/software/cuda/13.2.1/lib64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    unset "${CLEAN_VARS[@]}"
    "$BIN" "$@" ) >> "$log" 2>&1
  rc=$?
  t1=$(date +%s.%N)

  kill "$sampler_pid" 2>/dev/null || true
  wait "$sampler_pid" 2>/dev/null || true

  # h2d post
  local post; post=$(h2d_probe)
  printf 'h2d_post: %s\n' "$post" >> "$h2d_out"
  if ! h2d_ok "$post"; then
    post=$(h2d_probe); printf 'h2d_post_rerun: %s\n' "$post" >> "$h2d_out"
    h2d_ok "$post" || { echo "GATE FAIL: h2d post invalid after rerun: $post"; exit 4; }
  fi

  local wall; wall=$(awk -v a="$t0" -v b="$t1" 'BEGIN{printf "%.1f", b-a}')
  {
    echo "--- rc=$rc wall_s=$wall ($(date -Iseconds)) ---"
    echo "gpu_state_after:"; gpu_state
    echo "h2d_post: $post"
  } >> "$log"
  # link-sampler gate: gen1 AND util>=20 must be zero samples
  local bad
  bad=$(awk -F'\t' 'NR>1 && $2==1 && $3+0>=20 {c++} END{print c+0}' "$link")
  echo "[$label] rc=$rc wall=${wall}s h2d_post_ok=$(h2d_ok "$post" && echo yes || echo no) gen1_util20_samples=$bad log=$log"
  [ "$rc" -eq 0 ] || { echo "RUN FAILED rc=$rc — see $log"; exit "$rc"; }
  [ "$bad" -eq 0 ] || { echo "GATE FAIL: gen1 ∧ util>=20 sampled $bad times — see $link"; exit 4; }
}

case "$CMD" in
  prearm)
    echo "=== stage0c pre-arm $(date -Iseconds) ==="
    gate_prearm || exit 3
    echo "gpu_state:"; gpu_state
    echo "env_HYDRA_LLAMA: $(env | grep -E '^(HYDRA|LLAMA)_' || echo '(none)')"
    echo "h2d_pre_check: $(h2d_probe)"
    echo "binary: $(sha256sum "$BIN")"
    echo "corpus: $(sha256sum "$CORPUS")"
    echo "ldd_not_found: $(LD_LIBRARY_PATH="$ART:/opt/software/cuda/13.2.1/lib64" ldd "$BIN" 2>/dev/null | grep -c 'not found')"
    ;;
  A)   # A <label> <chunks>
    LABEL=${2:?label}; K=${3:?chunks}
    run_binary "$LABEL" \
      "${COMMON_FLAGS[@]}" -f "$CORPUS" --kl-divergence-base "$BASEDIR/$LABEL.kld" --chunks "$K"
    ls -l "$BASEDIR/$LABEL.kld"
    ;;
  B)   # B <label> <base file>
    LABEL=${2:?label}; BASE=${3:?base file}
    [ -f "$BASE" ] || { echo "missing base file $BASE"; exit 2; }
    run_binary "$LABEL" \
      "${COMMON_FLAGS[@]}" --kl-divergence --kl-divergence-base "$BASE"
    ;;
  h2d)
    LABEL=${2:?label}
    out=$(h2d_probe); printf '%s\n' "$out"
    h2d_ok "$out" || { echo "h2d INVALID (need 6.00-6.30 GB/s)"; exit 4; }
    ;;
  final)
    echo "=== stage0c final GPU state $(date -Iseconds) ==="
    gpu_state
    echo "expect: both GPUs ~1 MiB"
    ;;
  *) echo "unknown command $CMD" >&2; exit 2;;
esac
