#!/usr/bin/env bash
# run-with-params.sh — Parameterized RPC / baseline test runner for issue #703.
#
# Reads a YAML params file, starts ggml-rpc-server + llama-server with those
# parameters, runs a curl completion loop, checks dmesg/journalctl for Xid errors,
# and writes a reproducible result directory.
#
# Usage:
#   bash infra/llama-baseline/run-with-params.sh <params.yml> [--dry-run] [--no-cleanup]
#
# Requirements: python3 + pyyaml
# Constraints:   staging only, research-only, do not merge
set -uo pipefail

# ── Argument parsing ──────────────────────────────────────────────────────────
PARAMS_FILE="${1:?$0: usage: $0 <params.yml> [--dry-run] [--no-cleanup]}"
DRY_RUN=0
NO_CLEANUP=0
shift
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)   DRY_RUN=1; shift ;;
    --no-cleanup) NO_CLEANUP=1; shift ;;
    *) echo "Unknown flag: $1" >&2; exit 1 ;;
  esac
done

if [[ ! -f "$PARAMS_FILE" ]]; then
  echo "ERROR: params file not found: $PARAMS_FILE" >&2
  exit 1
fi

# ── Parse YAML to shell vars via python3 (pyyaml) ─────────────────────────────
eval "$(python3 - "$PARAMS_FILE" <<'PYEOF'
import sys, yaml

with open(sys.argv[1]) as f:
    p = yaml.safe_load(f)

def q(v):
    """Quote a string for shell eval."""
    if v is None:
        return '""'
    s = str(v)
    s = s.replace("'", "'\\''")
    return f"'{s}'"

for key in ('name', 'description', 'model_path', 'cuda_home', 'rpc_port',
            'rpc_cuda_device', 'server_port', 'server_cuda_device', 'ctx',
            'flash_attn', 'cache_type_k', 'cache_type_v', 'n_gpu_layers',
            'rpc_endpoint', 'n_predict', 'requests', 'prompt_path',
            'timeout', 'expect', 'tensor_split', 'split_mode',
            'rope_scaling', 'rope_scale', 'yarn_orig_ctx',
            'parallel', 'cont_batching', 'jinja', 'chat_template_file',
            'spec_type', 'spec_draft_n_max', 'spec_draft_p_min',
            'spec_draft_model', 'spec_draft_device', 'spec_draft_ngl',
            'spec_draft_type_k', 'spec_draft_type_v',
            'kv_unified', 'llama_bin', 'device', 'ubatch',
            'cache_reuse', 'cache_prompt', 'prio', 'prio_batch', 'context_shift', 'mlock', 'checkpoint_min_step',
            'cache_idle_slots', 'cache_ram_mib'):
    val = p.get(key)
    if val is not None:
        print(f"P_{key.upper()}={q(val)}")

tensors = p.get('override_tensors') or []
print(f"P_OVERRIDE_TENSORS={q(','.join(str(t) for t in tensors))}")
PYEOF
)"

# ── Derived variables ─────────────────────────────────────────────────────────
PARAMS_NAME="${P_NAME:-unknown}"
LLAMA_CPP="${LLAMA_CPP:-/mnt/WorkDisk/workspace/worktree/1q3ry0vb/majestic-toad/src/llama-cpp}"
SHA=$(git -C "$LLAMA_CPP" rev-parse --short HEAD 2>/dev/null || echo "unknown")
RESULTS_DIR="/tmp/rpc-test/results/${PARAMS_NAME}-${SHA}"

mkdir -p "$RESULTS_DIR"

# Copy params + compose into results for reproducibility
cp "$PARAMS_FILE" "$RESULTS_DIR/params.yml"
COMPOSE_FILE="infra/llama-baseline/docker-compose.baseline.yml"
if [[ -f "$COMPOSE_FILE" ]]; then
  cp "$COMPOSE_FILE" "$RESULTS_DIR/docker-compose.baseline.yml"
fi

# Find llama-cpp binaries
LLAMA_CPP_DIR="$LLAMA_CPP"
find_binary() {
  local name="$1"
  # Derive build dir from cuda_home (e.g. /opt/software/cuda/13.2 → build-cuda132)
  local cuda_tag=""
  if [[ -n "$P_CUDA_HOME" ]]; then
    cuda_tag=$(basename "$P_CUDA_HOME" | tr -d '.')  # "13.2" → "132", "13.2.2" → "1322", "13.3" → "133"
  fi
  for dir in "$LLAMA_CPP_DIR/build-cuda${cuda_tag}/bin" "$LLAMA_CPP_DIR/build-cuda1322/bin" "$LLAMA_CPP_DIR/build/bin" "$LLAMA_CPP_DIR/build"; do
    if [[ -x "$dir/$name" ]]; then echo "$dir/$name"; return; fi
  done
  local found
  found=$(find "$LLAMA_CPP_DIR" -name "$name" -type f -executable 2>/dev/null | head -1)
  if [[ -n "$found" ]]; then echo "$found"; return; fi
  echo ""
}

RPC_BIN=$(find_binary "ggml-rpc-server")
if [[ -n "${P_LLAMA_BIN:-}" && -x "$P_LLAMA_BIN" ]]; then
  LLAMA_BIN="$P_LLAMA_BIN"
else
  LLAMA_BIN=$(find_binary "llama-server")
fi

echo "=== run-with-params: $PARAMS_NAME (SHA: $SHA) ==="
echo "Date: $(date --iso-8601=seconds)"
echo "Params: $PARAMS_FILE"
echo "Results: $RESULTS_DIR"
echo "RPC binary: ${RPC_BIN:-NOT FOUND}"
echo "Llama binary: ${LLAMA_BIN:-NOT FOUND}"
echo "Model: $P_MODEL_PATH"
echo ""

if [[ -z "$LLAMA_BIN" ]]; then
  echo "ERROR: llama-server not found. Build first." | tee "$RESULTS_DIR/error.log"
  exit 1
fi

# ── Log files ─────────────────────────────────────────────────────────────────
RPC_LOG="$RESULTS_DIR/rpc-server.log"
LLAMA_LOG="$RESULTS_DIR/llama-server.log"            # live log (server writes here continuously)
CRASHLOOP_LOG="$RESULTS_DIR/llama-server-crashloop.log"  # snapshot after crash-loop phase
MULTITURN_LOG="$RESULTS_DIR/llama-server-multiturn.log"  # snapshot after multi-turn phase
TEST_LOG="$RESULTS_DIR/test.log"
XID_LOG="$RESULTS_DIR/xid-check.log"

# ── Cleanup function ──────────────────────────────────────────────────────────
PIDS_TO_KILL=()
cleanup() {
  if [[ $NO_CLEANUP -eq 1 ]]; then
    echo "[cleanup] skipped (--no-cleanup)" | tee -a "$TEST_LOG"
    return
  fi
  echo "[cleanup] Stopping processes..." | tee -a "$TEST_LOG"
  for pid in "${PIDS_TO_KILL[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  sleep 1
  for pid in "${PIDS_TO_KILL[@]}"; do
    kill -9 "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null || true
  echo "[cleanup] Done" | tee -a "$TEST_LOG"
}
trap cleanup EXIT

# ── Kill stale processes ──────────────────────────────────────────────────────
echo "[1/6] Cleaning stale processes..." | tee "$TEST_LOG"
pkill -f "ggml-rpc-server.*${P_RPC_PORT}" 2>/dev/null || true
pkill -f "llama-server.*${P_SERVER_PORT}" 2>/dev/null || true
sleep 1

# ── Start RPC server (if rpc_port != 0) ──────────────────────────────────────
if [[ "${P_RPC_PORT}" != "0" && -n "$RPC_BIN" ]]; then
  echo "[2/6] Starting rpc-server on :${P_RPC_PORT} (CUDA=${P_RPC_CUDA_DEVICE})..." | tee -a "$TEST_LOG"
  CUDA_VISIBLE_DEVICES="${P_RPC_CUDA_DEVICE}" "$RPC_BIN" \
    -H 0.0.0.0 -p "${P_RPC_PORT}" \
    > "$RPC_LOG" 2>&1 &
  RPC_PID=$!
  PIDS_TO_KILL+=("$RPC_PID")
  sleep 3

  if ! ss -tlnp | grep -q ":${P_RPC_PORT}"; then
    echo "WARN: rpc-server not listening on :${P_RPC_PORT}" | tee -a "$TEST_LOG"
  else
    echo "  rpc-server PID=$RPC_PID listening on :${P_RPC_PORT}" | tee -a "$TEST_LOG"
  fi
else
  echo "[2/6] Skipping rpc-server (rpc_port=0 or binary not found)" | tee -a "$TEST_LOG"
  RPC_PID=""
fi

# ── Build llama-server command line from params ───────────────────────────────
echo "[3/6] Building llama-server command..." | tee -a "$TEST_LOG"

LLAMA_ARGS=()
LLAMA_ARGS+=(-m "$P_MODEL_PATH")
LLAMA_ARGS+=(--host 0.0.0.0 --port "$P_SERVER_PORT")
LLAMA_ARGS+=(-ngl "${P_N_GPU_LAYERS}")
LLAMA_ARGS+=(--ctx-size "$P_CTX")

if [[ "${P_FLASH_ATTN}" == "on" ]]; then
  LLAMA_ARGS+=(--flash-attn on)
elif [[ "${P_FLASH_ATTN}" == "off" ]]; then
  LLAMA_ARGS+=(--flash-attn off)
fi

LLAMA_ARGS+=(--cache-type-k "${P_CACHE_TYPE_K}")
LLAMA_ARGS+=(--cache-type-v "${P_CACHE_TYPE_V}")

# RPC connection (if rpc_port != 0)
if [[ "${P_RPC_PORT}" != "0" && -n "${P_RPC_ENDPOINT:-}" ]]; then
  LLAMA_ARGS+=(--rpc "${P_RPC_ENDPOINT}:${P_RPC_PORT}")
  LLAMA_ARGS+=(-dev "RPC0,CUDA0")
fi

# Tensor split (for pooled mode)
if [[ -n "${P_TENSOR_SPLIT:-}" ]]; then
  LLAMA_ARGS+=(--tensor-split "$P_TENSOR_SPLIT")
fi
if [[ -n "${P_SPLIT_MODE:-}" ]]; then
  LLAMA_ARGS+=(--split-mode "$P_SPLIT_MODE")
fi

# Override tensors
if [[ -n "${P_OVERRIDE_TENSORS}" ]]; then
  LLAMA_ARGS+=(--override-tensor "$P_OVERRIDE_TENSORS")
fi

# Explicit device order override (e.g. "CUDA0,RPC0" to test a different
# ordering than the RPC-split default below). RPC device names are the bare
# "RPC<n>" (n = index among RPC servers, NOT the endpoint string — see
# ggml/src/ggml-rpc/ggml-rpc.cpp:2035-2038, add_rpc_devices/dev_name). Note
# RPC-split mode already unconditionally emits `-dev "RPC0,CUDA0"` above when
# rpc_port != 0 — only set P_DEVICE to override that default with something
# else; a later --device/-dev flag wins over an earlier one.
if [[ -n "${P_DEVICE:-}" ]]; then
  LLAMA_ARGS+=(--device "$P_DEVICE")
fi

# Micro-batch size (prompt-processing / speculative-verification batch)
if [[ -n "${P_UBATCH:-}" ]]; then
  LLAMA_ARGS+=(--ubatch-size "$P_UBATCH")
fi

# YaRN rope-scaling (long-context configs)
if [[ -n "${P_ROPE_SCALING:-}" ]]; then
  LLAMA_ARGS+=(--rope-scaling "$P_ROPE_SCALING")
fi
if [[ -n "${P_ROPE_SCALE:-}" ]]; then
  LLAMA_ARGS+=(--rope-scale "$P_ROPE_SCALE")
fi
if [[ -n "${P_YARN_ORIG_CTX:-}" ]]; then
  LLAMA_ARGS+=(--yarn-orig-ctx "$P_YARN_ORIG_CTX")
fi

# Continuous batching / multi-slot
if [[ -n "${P_PARALLEL:-}" ]]; then
  LLAMA_ARGS+=(--parallel "$P_PARALLEL")
fi
if [[ "${P_CONT_BATCHING:-}" == "on" || "${P_CONT_BATCHING:-}" == "true" ]]; then
  LLAMA_ARGS+=(--cont-batching)
fi

# KV-unified buffer — defaults to enabled only when slots are "auto"; with an
# explicit --parallel N this must be requested explicitly or ctx gets split N-ways.
if [[ "${P_KV_UNIFIED:-}" == "on" || "${P_KV_UNIFIED:-}" == "true" ]]; then
  LLAMA_ARGS+=(--kv-unified)
elif [[ "${P_KV_UNIFIED:-}" == "off" || "${P_KV_UNIFIED:-}" == "false" ]]; then
  LLAMA_ARGS+=(--no-kv-unified)
fi

# Chat template
if [[ "${P_JINJA:-}" == "on" || "${P_JINJA:-}" == "true" ]]; then
  LLAMA_ARGS+=(--jinja)
fi
if [[ -n "${P_CHAT_TEMPLATE_FILE:-}" ]]; then
  LLAMA_ARGS+=(--chat-template-file "$P_CHAT_TEMPLATE_FILE")
fi

# Speculative decoding (draft-mtp)
if [[ -n "${P_SPEC_TYPE:-}" ]]; then
  LLAMA_ARGS+=(--spec-type "$P_SPEC_TYPE")
fi
if [[ -n "${P_SPEC_DRAFT_N_MAX:-}" ]]; then
  LLAMA_ARGS+=(--spec-draft-n-max "$P_SPEC_DRAFT_N_MAX")
fi
if [[ -n "${P_SPEC_DRAFT_P_MIN:-}" ]]; then
  LLAMA_ARGS+=(--spec-draft-p-min "$P_SPEC_DRAFT_P_MIN")
fi
# External draft model (DSpark / DFlash)
if [[ -n "${P_SPEC_DRAFT_MODEL:-}" ]]; then
  LLAMA_ARGS+=(--spec-draft-model "$P_SPEC_DRAFT_MODEL")
fi
if [[ -n "${P_SPEC_DRAFT_DEVICE:-}" ]]; then
  LLAMA_ARGS+=(--spec-draft-device "$P_SPEC_DRAFT_DEVICE")
fi
if [[ -n "${P_SPEC_DRAFT_NGL:-}" ]]; then
  LLAMA_ARGS+=(--spec-draft-ngl "$P_SPEC_DRAFT_NGL")
fi
if [[ -n "${P_SPEC_DRAFT_TYPE_K:-}" ]]; then
  LLAMA_ARGS+=(--spec-draft-type-k "$P_SPEC_DRAFT_TYPE_K")
fi
if [[ -n "${P_SPEC_DRAFT_TYPE_V:-}" ]]; then
  LLAMA_ARGS+=(--spec-draft-type-v "$P_SPEC_DRAFT_TYPE_V")
fi

# Production-parity params (infra/hydra-head/config/global.yaml) — added for
# arm 016 to close the baseline-harness-vs-production config gap.
if [[ "${P_CACHE_PROMPT:-}" == "on" || "${P_CACHE_PROMPT:-}" == "true" ]]; then
  LLAMA_ARGS+=(--cache-prompt)
elif [[ "${P_CACHE_PROMPT:-}" == "off" || "${P_CACHE_PROMPT:-}" == "false" ]]; then
  LLAMA_ARGS+=(--no-cache-prompt)
fi
if [[ -n "${P_CACHE_REUSE:-}" ]]; then
  LLAMA_ARGS+=(--cache-reuse "$P_CACHE_REUSE")
fi
if [[ "${P_MLOCK:-}" == "on" || "${P_MLOCK:-}" == "true" ]]; then
  LLAMA_ARGS+=(--mlock)
fi
if [[ -n "${P_CHECKPOINT_MIN_STEP:-}" ]]; then
  LLAMA_ARGS+=(--checkpoint-min-step "$P_CHECKPOINT_MIN_STEP")
fi
if [[ -n "${P_PRIO:-}" ]]; then
  LLAMA_ARGS+=(--prio "$P_PRIO")
fi
if [[ -n "${P_PRIO_BATCH:-}" ]]; then
  LLAMA_ARGS+=(--prio-batch "$P_PRIO_BATCH")
fi
if [[ "${P_CONTEXT_SHIFT:-}" == "on" || "${P_CONTEXT_SHIFT:-}" == "true" ]]; then
  LLAMA_ARGS+=(--context-shift)
elif [[ "${P_CONTEXT_SHIFT:-}" == "off" || "${P_CONTEXT_SHIFT:-}" == "false" ]]; then
  LLAMA_ARGS+=(--no-context-shift)
fi

# Cache idle slots (requires --cache-ram)
_cache_idle_lower=$(echo "${P_CACHE_IDLE_SLOTS:-}" | tr '[:upper:]' '[:lower:]')
if [[ "$_cache_idle_lower" == "on" || "$_cache_idle_lower" == "true" ]]; then
  LLAMA_ARGS+=(--cache-idle-slots)
elif [[ "$_cache_idle_lower" == "off" || "$_cache_idle_lower" == "false" ]]; then
  LLAMA_ARGS+=(--no-cache-idle-slots)
fi

# Cache RAM limit (MiB) — host-RAM prompt cache for idle-slot swap
if [[ -n "${P_CACHE_RAM_MIB:-}" ]]; then
  LLAMA_ARGS+=(--cache-ram "$P_CACHE_RAM_MIB")
fi

echo "  Args: ${LLAMA_ARGS[*]}" | tee -a "$TEST_LOG"

if [[ $DRY_RUN -eq 1 ]]; then
  echo "[DRY RUN] Would run: CUDA_VISIBLE_DEVICES=${P_SERVER_CUDA_DEVICE} $LLAMA_BIN ${LLAMA_ARGS[*]}" | tee -a "$TEST_LOG"
  echo "=== DRY RUN complete ==="
  exit 0
fi

# ── Start llama-server ────────────────────────────────────────────────────────
echo "[4/6] Starting llama-server on :${P_SERVER_PORT}..." | tee -a "$TEST_LOG"
CUDA_VISIBLE_DEVICES="${P_SERVER_CUDA_DEVICE}" "$LLAMA_BIN" "${LLAMA_ARGS[@]}" \
  > "$LLAMA_LOG" 2>&1 &
SERVER_PID=$!
PIDS_TO_KILL+=("$SERVER_PID")

# Wait for health
echo "  Waiting for server ready (timeout ${P_TIMEOUT}s)..." | tee -a "$TEST_LOG"
READY=0
for i in $(seq 1 "${P_TIMEOUT}"); do
  if curl -s "http://127.0.0.1:${P_SERVER_PORT}/health" 2>/dev/null | grep -q '"status":"ok"'; then
    READY=1
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "  FAILED: llama-server exited prematurely at ${i}s" | tee -a "$TEST_LOG"
    tail -20 "$LLAMA_LOG" >> "$TEST_LOG" 2>/dev/null || true
    exit 1
  fi
  sleep 1
done

if [[ "$READY" -eq 0 ]]; then
  echo "  FAILED: llama-server not ready in ${P_TIMEOUT}s" | tee -a "$TEST_LOG"
  tail -30 "$LLAMA_LOG" >> "$TEST_LOG" 2>/dev/null || true
  exit 1
fi
echo "  llama-server ready after ${i}s (PID=$SERVER_PID)" | tee -a "$TEST_LOG"

# ── Health check + curl loop ──────────────────────────────────────────────────
echo "[5/6] Running health check + curl loop (${P_REQUESTS} requests)..." | tee -a "$TEST_LOG"
HEALTH=$(curl -s "http://127.0.0.1:${P_SERVER_PORT}/health" 2>&1)
echo "  Health: $HEALTH" | tee -a "$TEST_LOG"

# Record dmesg timestamp before test
DMESG_BEFORE=$(dmesg 2>/dev/null | tail -1 | awk '{print $1}' | tr -d '[]' || echo "0")

# Build prompt
if [[ -f "${P_PROMPT_PATH}" ]]; then
  PROMPT_TEXT=$(head -c 2000 "${P_PROMPT_PATH}" 2>/dev/null)
else
  PROMPT_TEXT="Explain the concept of attention in transformers. Provide a detailed technical explanation."
  echo "  WARN: prompt file not found, using fallback" | tee -a "$TEST_LOG"
fi
PROMPT_JSON=$(echo "$PROMPT_TEXT" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')

BAD=0
for req in $(seq 1 "${P_REQUESTS}"); do
  echo "  Request $req/${P_REQUESTS}..." | tee -a "$TEST_LOG"

  RESP=$(curl -s --max-time 120 "http://127.0.0.1:${P_SERVER_PORT}/v1/chat/completions" \
    -H "Content-Type: application/json" \
    -d "{
      \"model\": \"local\",
      \"messages\": [{\"role\": \"user\", \"content\": ${PROMPT_JSON}}],
      \"max_tokens\": ${P_N_PREDICT},
      \"stream\": false
    }" 2>&1) || true

  # Check for crash indicators in server log (NOT response body —
  # reasoning_content contains model prose that can match "cuda.*error"
  # false-positive like "no CUDA errors?" — see #703 / arm 051).
  # A log-pattern match ALONE is not a crash: transient-then-recovered
  # compute-buffer reserve retries print "cudaMalloc failed"/"failed to
  # allocate ..." and then serve normally ("sched_reserve: ... retrying
  # without pipeline parallelism" → model loaded → listening) — this
  # false-positived arms 051/052/053 audits and 060 run1. Real crash =
  # server process gone OR /health failing (matching line kept as
  # evidence). NOTE: LLAMA_LOG matches are cumulative across requests;
  # genuine later deaths are still caught by the alive/health checks
  # below and the final Xid/journalctl sweep.
  if [[ -f "$LLAMA_LOG" ]] && grep -qiE "cudaMalloc failed|ggml.*abort|Xid|segfault|alloc.*failed|illegal memory" "$LLAMA_LOG" 2>/dev/null; then
    if ! kill -0 "$SERVER_PID" 2>/dev/null || ! curl -s "http://127.0.0.1:${P_SERVER_PORT}/health" 2>/dev/null | grep -q '"status":"ok"'; then
      echo "  CRASH detected in server log after request $req (server dead or unhealthy)" | tee -a "$TEST_LOG"
      grep -iE "cudaMalloc failed|ggml.*abort|Xid|segfault|alloc.*failed|illegal memory" "$LLAMA_LOG" | tail -5 >> "$TEST_LOG"
      BAD=1
      break
    else
      echo "  WARN: crash-pattern lines in server log after request $req, but server alive+healthy (recovered reserve/retry) — continuing" | tee -a "$TEST_LOG"
    fi
  fi

  # Check server alive
  if ! curl -s "http://127.0.0.1:${P_SERVER_PORT}/health" 2>/dev/null | grep -q '"status":"ok"'; then
    echo "  Server died after request $req" | tee -a "$TEST_LOG"
    BAD=1
    break
  fi

  # Check Xid in dmesg
  XID=$(dmesg 2>/dev/null | awk -v ts="$DMESG_BEFORE" '$0 >= ts' | grep -iE "Xid 13|Xid 43|NVRM.*error|Xid.*General" | head -5 || true)
  if [[ -n "$XID" ]]; then
    echo "  XID ERROR detected after request $req:" | tee -a "$TEST_LOG"
    echo "$XID" | tee -a "$TEST_LOG"
    echo "$XID" >> "$XID_LOG"
    BAD=1
    break
  fi

  echo "  Request $req OK" | tee -a "$TEST_LOG"
done

# ── Final Xid check via journalctl ────────────────────────────────────────────
echo "[6/6] Final Xid check..." | tee -a "$TEST_LOG"
XID_JOURNAL=$(journalctl -k --since "$(date -d '-5 minutes' '+%Y-%m-%d %H:%M:%S' 2>/dev/null || date '+%Y-%m-%d %H:%M:%S')" 2>/dev/null \
  | grep -iE 'Xid 13|Xid 43|NVRM.*error' | head -5 || true)
if [[ -n "$XID_JOURNAL" ]]; then
  echo "  XID in journalctl:" | tee -a "$TEST_LOG"
  echo "$XID_JOURNAL" | tee -a "$TEST_LOG"
  echo "$XID_JOURNAL" >> "$XID_LOG"
  BAD=1
fi

# Copy logs to results — snapshot crash-loop phase separately so multi-turn
# data (which accumulates in the same live log file when --no-cleanup is used)
# doesn't get lost.
cp "$LLAMA_LOG" "$CRASHLOOP_LOG" 2>/dev/null || true
cp "$LLAMA_LOG" "$RESULTS_DIR/llama-server-full.log" 2>/dev/null || true
if [[ -n "${RPC_PID:-}" ]]; then
  cp "$RPC_LOG" "$RESULTS_DIR/rpc-server-full.log" 2>/dev/null || true
fi

# ── Write result summary ──────────────────────────────────────────────────────
RESULT="good"
[[ $BAD -eq 1 ]] && RESULT="bad"

cat > "$RESULTS_DIR/summary.txt" << EOF
params: $PARAMS_NAME
sha: $SHA
date: $(date --iso-8601=seconds)
model: $P_MODEL_PATH
expect: ${P_EXPECT:-unknown}
actual: $RESULT
requests: ${P_REQUESTS}
server_port: ${P_SERVER_PORT}
rpc_port: ${P_RPC_PORT}
ctx: ${P_CTX}
flash_attn: ${P_FLASH_ATTN}
override_tensors: ${P_OVERRIDE_TENSORS:-none}
EOF

echo ""
echo "=== Result: $(echo $RESULT | tr a-z A-Z) (expect: ${P_EXPECT:-unknown}) ===" | tee -a "$TEST_LOG"
echo "Results dir: $RESULTS_DIR"
ls -lh "$RESULTS_DIR/" 2>/dev/null || true
echo ""

exit $BAD
