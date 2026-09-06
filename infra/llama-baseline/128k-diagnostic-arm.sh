#!/usr/bin/env bash
# 128k-diagnostic-arm.sh — Instrumented 128K multi-turn diagnostic for issue #703.
#
# Root-causes the turn-8-10 timeout by isolating:
#   (a) server compute collapse, (b) KV-eviction storm, (c) coherence collapse,
#   (d) client/harness timeout, and (e) tensor-boundary effects.
#
# Usage:
#   bash infra/llama-baseline/128k-diagnostic-arm.sh <params.yml> <arm-id>
#
# Example:
#   bash infra/llama-baseline/128k-diagnostic-arm.sh infra/llama-baseline/params/045-diagnostic-128k-27-38.yml 045
#
# Outputs:
#   /tmp/rpc-test/results/<arm-id>-<sha>/   — all logs, slot snapshots, curl responses
#   stdout — structured summary with pass/fail per turn, timing, evictions
#
# Requirements: python3 + pyyaml, curl, jq (optional), bash 4+
set -uo pipefail

# ── Argument parsing ──────────────────────────────────────────────────────────
PARAMS_FILE="${1:?Usage: $0 <params.yml> <arm-id>}"
ARM_ID="${2:?Usage: $0 <params.yml> <arm-id>}"
TURN_TIMEOUT=120         # seconds per turn — prevents unbounded hangs
TOTAL_TURNS=10           # growing-context multi-turn
FIRST_TURN_TOKENS=5000   # target ~5K tokens for first turn
GROWTH_TOKENS=2000       # add ~2K tokens per subsequent turn

# ── Parse YAML to shell vars via python3 ──────────────────────────────────────
eval "$(python3 - "$PARAMS_FILE" <<'PYEOF'
import sys, yaml

with open(sys.argv[1]) as f:
    p = yaml.safe_load(f)

def q(v):
    if v is None:
        return '""'
    s = str(v)
    s = s.replace("'", "'\\''")
    return f"'{s}'"

for key in ('name', 'model_path', 'cuda_home', 'rpc_port',
            'rpc_cuda_device', 'server_port', 'server_cuda_device', 'ctx',
            'flash_attn', 'cache_type_k', 'cache_type_v', 'n_gpu_layers',
            'rpc_endpoint', 'n_predict', 'timeout',
            'tensor_split', 'rope_scaling', 'rope_scale', 'yarn_orig_ctx',
            'parallel', 'cont_batching', 'jinja',
            'spec_type', 'kv_unified', 'ubatch',
            'cache_reuse', 'cache_prompt', 'prio_batch', 'context_shift'):
    val = p.get(key)
    if val is not None:
        print(f"P_{key.upper()}={q(val)}")
PYEOF
)"

PARAMS_NAME="${P_NAME:-$ARM_ID}"
LLAMA_CPP="${LLAMA_CPP:-/mnt/WorkDisk/workspace/worktree/1q3ry0vb/majestic-toad/src/llama-cpp}"
SHA=$(git -C "$LLAMA_CPP" rev-parse --short HEAD 2>/dev/null || echo "unknown")
RESULTS_DIR="/tmp/rpc-test/results/${ARM_ID}-${SHA}"

mkdir -p "$RESULTS_DIR"
cp "$PARAMS_FILE" "$RESULTS_DIR/params.yml"

# ── Find binaries ─────────────────────────────────────────────────────────────
find_binary() {
  local name="$1"
  local cuda_tag=""
  if [[ -n "$P_CUDA_HOME" ]]; then
    cuda_tag=$(basename "$P_CUDA_HOME" | tr -d '.')
  fi
  for dir in "$LLAMA_CPP/build-cuda${cuda_tag}/bin" "$LLAMA_CPP/build-cuda1322/bin" "$LLAMA_CPP/build/bin" "$LLAMA_CPP/build"; do
    if [[ -x "$dir/$name" ]]; then echo "$dir/$name"; return; fi
  done
  local found
  found=$(find "$LLAMA_CPP" -name "$name" -type f -executable 2>/dev/null | head -1)
  if [[ -n "$found" ]]; then echo "$found"; return; fi
  echo ""
}

RPC_BIN=$(find_binary "ggml-rpc-server")
LLAMA_BIN=$(find_binary "llama-server")

echo "=== 128K diagnostic arm: ${ARM_ID} (${PARAMS_NAME}) ==="
echo "Date: $(date --iso-8601=seconds)"
echo "Results: ${RESULTS_DIR}"
echo "RPC binary: ${RPC_BIN:-NOT FOUND}"
echo "Llama binary: ${LLAMA_BIN:-NOT FOUND}"
echo "Model: $P_MODEL_PATH"
echo "Tensor split: ${P_TENSOR_SPLIT:-none}"
echo ""

if [[ -z "$LLAMA_BIN" ]]; then
  echo "ERROR: llama-server not found. Build first." | tee "$RESULTS_DIR/error.log"
  exit 1
fi

# ── Log files ─────────────────────────────────────────────────────────────────
RPC_LOG="$RESULTS_DIR/rpc-server.log"
LLAMA_LOG="$RESULTS_DIR/llama-server.log"
TEST_LOG="$RESULTS_DIR/test.log"

# ── Cleanup ───────────────────────────────────────────────────────────────────
PIDS_TO_KILL=()
cleanup() {
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
echo "[1/7] Cleaning stale processes..." | tee "$TEST_LOG"
pkill -f "ggml-rpc-server.*${P_RPC_PORT}" 2>/dev/null || true
pkill -f "llama-server.*${P_SERVER_PORT}" 2>/dev/null || true
sleep 1

# ── Start RPC server ─────────────────────────────────────────────────────────
if [[ "${P_RPC_PORT}" != "0" && -n "$RPC_BIN" ]]; then
  echo "[2/7] Starting rpc-server on :${P_RPC_PORT} (CUDA=${P_RPC_CUDA_DEVICE})..." | tee -a "$TEST_LOG"
  CUDA_VISIBLE_DEVICES="${P_RPC_CUDA_DEVICE}" "$RPC_BIN" \
    -H 0.0.0.0 -p "${P_RPC_PORT}" \
    > "$RPC_LOG" 2>&1 &
  RPC_PID=$!
  PIDS_TO_KILL+=("$RPC_PID")
  sleep 3
  if ss -tlnp | grep -q ":${P_RPC_PORT}"; then
    echo "  rpc-server PID=$RPC_PID listening on :${P_RPC_PORT}" | tee -a "$TEST_LOG"
  else
    echo "  WARN: rpc-server not listening on :${P_RPC_PORT}" | tee -a "$TEST_LOG"
  fi
else
  echo "[2/7] Skipping rpc-server" | tee -a "$TEST_LOG"
fi

# ── Build llama-server command ────────────────────────────────────────────────
echo "[3/7] Building llama-server command..." | tee -a "$TEST_LOG"

LLAMA_ARGS=()
LLAMA_ARGS+=(-m "$P_MODEL_PATH")
LLAMA_ARGS+=(--host 0.0.0.0 --port "$P_SERVER_PORT")
LLAMA_ARGS+=(-ngl "${P_N_GPU_LAYERS}")
LLAMA_ARGS+=(--ctx-size "$P_CTX")

if [[ "${P_FLASH_ATTN}" == "on" ]]; then
  LLAMA_ARGS+=(--flash-attn on)
fi

LLAMA_ARGS+=(--cache-type-k "${P_CACHE_TYPE_K}")
LLAMA_ARGS+=(--cache-type-v "${P_CACHE_TYPE_V}")

if [[ "${P_RPC_PORT}" != "0" && -n "${P_RPC_ENDPOINT:-}" ]]; then
  LLAMA_ARGS+=(--rpc "${P_RPC_ENDPOINT}:${P_RPC_PORT}")
  LLAMA_ARGS+=(-dev "RPC0,CUDA0")
fi

if [[ -n "${P_TENSOR_SPLIT:-}" ]]; then
  LLAMA_ARGS+=(--tensor-split "$P_TENSOR_SPLIT")
fi

if [[ -n "${P_UBATCH:-}" ]]; then
  LLAMA_ARGS+=(--ubatch-size "$P_UBATCH")
fi
if [[ -n "${P_ROPE_SCALING:-}" ]]; then
  LLAMA_ARGS+=(--rope-scaling "$P_ROPE_SCALING")
fi
if [[ -n "${P_ROPE_SCALE:-}" ]]; then
  LLAMA_ARGS+=(--rope-scale "$P_ROPE_SCALE")
fi
if [[ -n "${P_YARN_ORIG_CTX:-}" ]]; then
  LLAMA_ARGS+=(--yarn-orig-ctx "$P_YARN_ORIG_CTX")
fi
if [[ -n "${P_PARALLEL:-}" ]]; then
  LLAMA_ARGS+=(--parallel "$P_PARALLEL")
fi
if [[ "${P_CONT_BATCHING:-}" == "on" ]]; then
  LLAMA_ARGS+=(--cont-batching)
fi
if [[ "${P_KV_UNIFIED:-}" == "on" ]]; then
  LLAMA_ARGS+=(--kv-unified)
fi
if [[ "${P_JINJA:-}" == "on" ]]; then
  LLAMA_ARGS+=(--jinja)
fi
if [[ -n "${P_SPEC_TYPE:-}" ]]; then
  LLAMA_ARGS+=(--spec-type "$P_SPEC_TYPE")
fi
if [[ "${P_CACHE_PROMPT:-}" == "on" ]]; then
  LLAMA_ARGS+=(--cache-prompt)
fi
if [[ -n "${P_CACHE_REUSE:-}" ]]; then
  LLAMA_ARGS+=(--cache-reuse "$P_CACHE_REUSE")
fi
if [[ -n "${P_PRIO_BATCH:-}" ]]; then
  LLAMA_ARGS+=(--prio-batch "$P_PRIO_BATCH")
fi
if [[ "${P_CONTEXT_SHIFT:-}" == "on" ]]; then
  LLAMA_ARGS+=(--context-shift)
fi
# Cap n-predict at 2048 per spec (prevents runaway generation)
LLAMA_ARGS+=(--n-predict 2048)

echo "  Args: ${LLAMA_ARGS[*]}" | tee -a "$TEST_LOG"

# ── Start llama-server ────────────────────────────────────────────────────────
echo "[4/7] Starting llama-server on :${P_SERVER_PORT}..." | tee -a "$TEST_LOG"
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

# ── Helper: get slot info ────────────────────────────────────────────────────
get_slots() {
  curl -s "http://127.0.0.1:${P_SERVER_PORT}/slots" 2>/dev/null
}

get_slot_summary() {
  curl -s "http://127.0.0.1:${P_SERVER_PORT}/slots" 2>/dev/null | python3 -c "
import json, sys
try:
    slots = json.load(sys.stdin)
    for s in slots:
        total = s.get('n_prompt_tokens', 0)
        cached = s.get('n_prompt_tokens_cache', 0)
        processed = s.get('n_prompt_tokens_processed', 0)
        n_past = s.get('n_past', 0)
        ctx = s.get('n_ctx', 0)
        cache_pct = (cached / total * 100) if total > 0 else 0
        print(f'n_past={n_past} total={total} cached={cached} processed={processed} ctx={ctx} cache_pct={cache_pct:.1f}%')
        break
    else:
        print('no_active_slots')
except:
    print('parse_error')
" 2>/dev/null || echo "slots_unavailable"
}

# ── Helper: generate filler text to a file ────────────────────────────────────
generate_filler() {
  local target_tokens="$1"
  local turn_num="$2"
  local output_file="$3"
  python3 -c "
import sys
target = int(sys.argv[1])
turn = int(sys.argv[2])
output = sys.argv[3]
base = 'The transformer architecture uses self-attention mechanisms to process sequential data. Multi-head attention allows the model to jointly attend to information from different representation subspaces. Layer normalization stabilizes the hidden state distributions. Residual connections enable gradient flow through deep networks. The feed-forward network applies two linear transformations with a non-linearity in between. '
filler = ''
while len(filler) < target:
    filler += base
filler = filler[:target]
with open(output, 'w') as f:
    f.write(filler)
" "$target_tokens" "$turn_num" "$output_file"
}

# ── Helper: build messages JSON for a turn ────────────────────────────────────
# Writes to a file (avoids ARG_MAX on large contexts)
build_messages_json() {
  local turn="$1"
  local history_file="$2"  # path to accumulated history JSON array
  local filler_file="$3"  # path to filler text file
  local output_file="$4"  # where to write the JSON

  python3 - "$turn" "$history_file" "$filler_file" "$output_file" <<'PYEOF'
import json, sys

turn = int(sys.argv[1])
history_file = sys.argv[2]
filler_file = sys.argv[3]
output_file = sys.argv[4]

with open(filler_file) as f:
    filler = f.read()

task_prompt = (
    f"Write a Python function called 'process_turn_{turn}' "
    f"that takes a list of integers and returns their sum multiplied "
    f"by {turn}. Save it to /tmp/turn_{turn}.py and run it with input "
    f"[1,2,3,4,5]. Print the result. "
    f"Here is some context to process: {filler}"
)

msgs = [{"role": "user", "content": task_prompt}]

# Load prior history if exists
try:
    with open(history_file) as f:
        history = json.load(f)
    msgs = history + msgs
except (FileNotFoundError, json.JSONDecodeError):
    pass

with open(output_file, 'w') as f:
    json.dump(msgs, f)
PYEOF
}

# ── Multi-turn curl probe ─────────────────────────────────────────────────────
echo "[5/7] Running ${TOTAL_TURNS}-turn diagnostic curl probe..." | tee -a "$TEST_LOG"
echo "  First-turn target: ${FIRST_TURN_TOKENS} tok, growth: ${GROWTH_TOKENS} tok/turn" | tee -a "$TEST_LOG"
echo "  Per-turn timeout: ${TURN_TIMEOUT}s" | tee -a "$TEST_LOG"
echo "" | tee -a "$TEST_LOG"

HISTORY_FILE="$RESULTS_DIR/history.json"
> "$HISTORY_FILE"  # empty initially
echo "[]" > "$HISTORY_FILE"

SERVER_URL="http://127.0.0.1:${P_SERVER_PORT}"

# Arrays to collect per-turn data
declare -a TURN_RESULTS=()
declare -a TURN_TIMING=()
declare -a TURN_SLOTS_BEFORE=()
declare -a TURN_SLOTS_AFTER=()
declare -a TURN_STATUS=()
FIRST_FAIL_TURN=""

for turn in $(seq 1 "$TOTAL_TURNS"); do
  TARGET_TOK=$((FIRST_TURN_TOKENS + (turn - 1) * GROWTH_TOKENS))

  # Generate filler text to a file (avoids shell expansion limits)
  FILLER_FILE="$RESULTS_DIR/turn_${turn}_filler.txt"
  generate_filler "$TARGET_TOK" "$turn" "$FILLER_FILE"

  # Build messages JSON to a file (avoids ARG_MAX on large contexts)
  MSGS_FILE="$RESULTS_DIR/turn_${turn}_msgs.json"
  build_messages_json "$turn" "$HISTORY_FILE" "$FILLER_FILE" "$MSGS_FILE"

  echo "  Turn ${turn}/${TOTAL_TURNS} (target ~${TARGET_TOK} tok)..." | tee -a "$TEST_LOG"

  # Capture /slots BEFORE
  SLOTS_BEFORE=$(get_slot_summary)
  echo "    /slots before: ${SLOTS_BEFORE}" | tee -a "$TEST_LOG"
  TURN_SLOTS_BEFORE+=("$SLOTS_BEFORE")

  # Build curl request JSON to a file (avoid shell expansion limits)
  CURL_REQUEST="$RESULTS_DIR/turn_${turn}_request.json"
  python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    msgs = json.load(f)
with open(sys.argv[2], 'w') as f:
    json.dump({
        'model': 'x',
        'messages': msgs,
        'max_tokens': 2048,
        'temperature': 0
    }, f)
" "$MSGS_FILE" "$CURL_REQUEST"

  # Execute curl with timing — read request body from file
  TURN_START_EPOCH=$(date +%s)
  CURL_OUTPUT="$RESULTS_DIR/turn_${turn}.json"

  TURN_WALL_START=$(date +%s%N)
  timeout "$TURN_TIMEOUT" curl -s -X POST \
    "${SERVER_URL}/v1/chat/completions" \
    -H 'Content-Type: application/json' \
    -d @"$CURL_REQUEST" \
    -o "$CURL_OUTPUT" 2>"$RESULTS_DIR/turn_${turn}_curl_stderr.txt"
  CURL_EXIT=$?
  TURN_WALL_END=$(date +%s%N)
  TURN_WALL_MS=$(( (TURN_WALL_END - TURN_WALL_START) / 1000000 ))
  TURN_WALL_S=$(echo "scale=2; $TURN_WALL_MS / 1000" | bc 2>/dev/null || echo "$((TURN_WALL_MS / 1000))")

  # Capture /slots AFTER
  SLOTS_AFTER=$(get_slot_summary)
  echo "    /slots after:  ${SLOTS_AFTER}" | tee -a "$TEST_LOG"
  TURN_SLOTS_AFTER+=("$SLOTS_AFTER")

  # Parse response
  if [[ $CURL_EXIT -eq 124 ]]; then
    STATUS="TIMEOUT"
    echo "    Turn ${turn} TIMEOUT (${TURN_WALL_S}s wall-clock)" | tee -a "$TEST_LOG"
  elif [[ $CURL_EXIT -ne 0 ]]; then
    STATUS="CURL_ERROR"
    echo "    Turn ${turn} CURL_ERROR exit=$CURL_EXIT (${TURN_WALL_S}s)" | tee -a "$TEST_LOG"
  else
    # Check for server error in response
    ERROR_CHECK=$(python3 -c "
import json, sys
try:
    with open(sys.argv[1]) as f:
        data = json.load(f)
    if 'error' in data:
        print(f'SERVER_ERROR: {data[\"error\"]}')
    elif 'choices' in data and len(data['choices']) > 0:
        choice = data['choices'][0]
        text = choice.get('message', {}).get('content', '')
        finish = choice.get('finish_reason', 'unknown')
        tokens = data.get('usage', {}).get('completion_tokens', 0)
        # Check for repetition (coherence collapse)
        words = text.split()
        unique_ratio = len(set(words)) / max(len(words), 1)
        coherence = 'repetitive' if unique_ratio < 0.3 and len(words) > 20 else 'ok'
        print(f'OK finish={finish} tokens={tokens} coherence={coherence} unique_ratio={unique_ratio:.2f}')
        # Save text for inspection
        with open(sys.argv[1].replace('.json', '_text.txt'), 'w') as tf:
            tf.write(text)
    else:
        print(f'UNKNOWN: {json.dumps(data)[:200]}')
except Exception as e:
    print(f'PARSE_ERROR: {e}')
" "$CURL_OUTPUT")
    STATUS=$(echo "$ERROR_CHECK" | cut -d' ' -f1)
    echo "    Turn ${turn} ${STATUS} (${TURN_WALL_S}s wall-clock) — ${ERROR_CHECK}" | tee -a "$TEST_LOG"
  fi

  TURN_STATUS+=("$STATUS")
  TURN_TIMING+=("${TURN_WALL_MS}")

  # Save per-turn slots snapshots
  echo "$SLOTS_BEFORE" > "$RESULTS_DIR/slots_before_${turn}.txt"
  echo "$SLOTS_AFTER" > "$RESULTS_DIR/slots_after_${turn}.txt"
  # Also save raw JSON
  get_slots > "$RESULTS_DIR/slots_before_${turn}.json" 2>/dev/null || true
  get_slots > "$RESULTS_DIR/slots_after_${turn}.json" 2>/dev/null || true

  # Update history for next turn: append the NEW user message + assistant response
  # (msgs = history + new_user_msg, so only take the tail beyond current history)
  if [[ "$STATUS" == "OK" ]]; then
    python3 - "$HISTORY_FILE" "$MSGS_FILE" "$CURL_OUTPUT" <<'HISTEOF'
import json, sys

history_file = sys.argv[1]
msgs_file = sys.argv[2]
response_file = sys.argv[3]

with open(history_file) as f:
    history = json.load(f)

with open(msgs_file) as f:
    msgs = json.load(f)

with open(response_file) as f:
    resp = json.load(f)

assistant_text = resp['choices'][0]['message']['content']

# msgs = old_history + new_user_msg — only append the NEW user message, not the
# entire msgs list which would re-include old history (causing doubling).
new_user_msg = msgs[len(history):]
history.extend(new_user_msg)
history.append({"role": "assistant", "content": assistant_text})

with open(history_file, 'w') as f:
    json.dump(history, f)
HISTEOF
  else
    # On failure, still append the user message so context keeps growing
    python3 - "$HISTORY_FILE" "$MSGS_FILE" <<'HISTEOF'
import json, sys

history_file = sys.argv[1]
msgs_file = sys.argv[2]

with open(history_file) as f:
    history = json.load(f)

with open(msgs_file) as f:
    msgs = json.load(f)

# Only append the NEW user message (tail beyond current history length)
new_user_msg = msgs[len(history):]
history.extend(new_user_msg)
history.append({"role": "assistant", "content": "[turn failed - no response]"})

with open(history_file, 'w') as f:
    json.dump(history, f)
HISTEOF
  fi

  # Track first failure
  if [[ "$STATUS" != "OK" && -z "$FIRST_FAIL_TURN" ]]; then
    FIRST_FAIL_TURN="$turn"
  fi

  # Check server health between turns
  if ! curl -s "${SERVER_URL}/health" 2>/dev/null | grep -q '"status":"ok"'; then
    echo "    Server died after turn ${turn} — aborting" | tee -a "$TEST_LOG"
    TURN_STATUS+=("SERVER_DIED")
    break
  fi

  echo "" | tee -a "$TEST_LOG"
done

# ── Eviction count ────────────────────────────────────────────────────────────
echo "[6/7] Counting KV evictions..." | tee -a "$TEST_LOG"
EVICTION_COUNT=$(grep -c "making room for prompt cache entry" "$LLAMA_LOG" 2>/dev/null || echo "0")
echo "  Total evictions: ${EVICTION_COUNT}" | tee -a "$TEST_LOG"

# Per-turn eviction timestamps (correlate with turn timing)
grep "making room for prompt cache entry" "$LLAMA_LOG" 2>/dev/null > "$RESULTS_DIR/evictions.log" || true

# ── Server log tail ───────────────────────────────────────────────────────────
cp "$LLAMA_LOG" "$RESULTS_DIR/llama-server-full.log" 2>/dev/null || true

# ── Summary ───────────────────────────────────────────────────────────────────
echo "[7/7] Diagnostic summary" | tee -a "$TEST_LOG"
echo "" | tee -a "$TEST_LOG"

PASS_COUNT=0
FAIL_COUNT=0
TIMEOUT_COUNT=0
for s in "${TURN_STATUS[@]}"; do
  if [[ "$s" == "OK" ]]; then
    PASS_COUNT=$((PASS_COUNT + 1))
  elif [[ "$s" == "TIMEOUT" ]]; then
    TIMEOUT_COUNT=$((TIMEOUT_COUNT + 1))
    FAIL_COUNT=$((FAIL_COUNT + 1))
  else
    FAIL_COUNT=$((FAIL_COUNT + 1))
  fi
done

echo "=== DIAGNOSTIC ARM ${ARM_ID} RESULTS ==="
echo "Arm:       ${ARM_ID} (${PARAMS_NAME})"
echo "Model:     $(basename "$P_MODEL_PATH")"
echo "Split:     ${P_TENSOR_SPLIT:-none}"
echo "Date:      $(date --iso-8601=seconds)"
echo "Turns:     ${PASS_COUNT}/${TOTAL_TURNS} OK, ${TIMEOUT_COUNT} timeouts, $((FAIL_COUNT - TIMEOUT_COUNT)) other failures"
echo "Evictions: ${EVICTION_COUNT}"
echo "First fail: ${FIRST_FAIL_TURN:-none}"
echo ""

# Per-turn timing table
echo "Turn | Status     | Wall-clock (s) | Slots Before           | Slots After"
echo "-----|------------|----------------|------------------------|------------------------"
for i in $(seq 0 $((TOTAL_TURNS - 1))); do
  t=$((i + 1))
  s="${TURN_STATUS[$i]:-N/A}"
  w="${TURN_TIMING[$i]:-0}"
  w_s=$(echo "scale=2; $w / 1000" | bc 2>/dev/null || echo "$((w / 1000))")
  sb="${TURN_SLOTS_BEFORE[$i]:-N/A}"
  sa="${TURN_SLOTS_AFTER[$i]:-N/A}"
  printf "%4d | %-10s | %14s | %-22s | %-22s\n" "$t" "$s" "${w_s}s" "${sb:0:22}" "${sa:0:22}"
done
echo ""

# Decision criteria assessment
echo "=== DECISION CRITERIA ==="
if [[ -z "$FIRST_FAIL_TURN" ]]; then
  echo "All turns passed — no timeout observed in this run"
elif [[ "$FIRST_FAIL_TURN" -ge 8 ]] 2>/dev/null; then
  echo "First failure at turn ${FIRST_FAIL_TURN} (turn-8-10 range)"
  # Check if curl was slow at failing turn
  FAIL_TURN_MS="${TURN_TIMING[$((FIRST_FAIL_TURN - 1))]:-0}"
  FAIL_TURN_S=$(echo "scale=2; $FAIL_TURN_MS / 1000" | bc 2>/dev/null || echo "$((FAIL_TURN_MS / 1000))")
  if [[ "$FAIL_TURN_MS" -gt 90000 ]]; then
    echo "  curl slow at fail turn (${FAIL_TURN_S}s > 90s) → SERVER COMPUTE COLLAPSE"
  elif [[ "${TURN_STATUS[$((FIRST_FAIL_TURN - 1))]}" == "TIMEOUT" ]]; then
    echo "  curl timed out at turn ${FIRST_FAIL_TURN} → needs server-side log correlation"
  else
    echo "  curl returned in ${FAIL_TURN_S}s → check server-side timing"
  fi
else
  echo "First failure at turn ${FIRST_FAIL_TURN} (before turn-8 range — different failure mode)"
fi

# Coherence check for last successful turn
LAST_OK_TURN=""
for i in $(seq $((TOTAL_TURNS - 1)) -1 0); do
  if [[ "${TURN_STATUS[$i]}" == "OK" ]]; then
    LAST_OK_TURN=$((i + 1))
    break
  fi
done
if [[ -n "$LAST_OK_TURN" && -f "$RESULTS_DIR/turn_${LAST_OK_TURN}_text.txt" ]]; then
  TEXT_LEN=$(wc -c < "$RESULTS_DIR/turn_${LAST_OK_TURN}_text.txt")
  UNIQUE_WORDS=$(python3 -c "
text = open('$RESULTS_DIR/turn_${LAST_OK_TURN}_text.txt').read()
words = text.split()
unique = len(set(words))
total = len(words)
ratio = unique / max(total, 1)
print(f'total_words={total} unique_words={unique} ratio={ratio:.3f}')
" 2>/dev/null || echo "parse_error")
  echo "Last OK turn (${LAST_OK_TURN}) text: ${TEXT_LEN} bytes, ${UNIQUE_WORDS}"
fi

echo ""
echo "Results dir: ${RESULTS_DIR}"
echo "=== DONE ==="

exit 0
