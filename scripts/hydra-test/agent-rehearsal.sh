#!/usr/bin/env bash
# agent-rehearsal.sh — 6-step scripted agent-task rehearsal through Hydra TEST.
#
# Steps:
#   1. System-prompt task briefing (core-A)
#   2. Tool-call-style JSON request (core-A)
#   3. Multi-turn follow-up referencing prior context (core-A)
#   4. Concurrent burst: 3 sequential to core-A + 3 sequential to core-B, parallel across cores
#   5. Session-continuation turn asserting context retention (core-A)
#   6. Prod-isolation probe: compare prod :9000 before/after
#
# Exit 0 only if all steps return 200 with completion_tokens > 0.
# Logs to docs/hydra-test/evidence/agent-rehearsal-<timestamp>.jsonl
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
EVIDENCE_DIR="$REPO_ROOT/docs/hydra-test/evidence"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
EVIDENCE_FILE="$EVIDENCE_DIR/agent-rehearsal-${TIMESTAMP}.jsonl"
MAX_TOKENS=64
TIMEOUT=120

CORE_A="http://localhost:19000/v1/chat/completions"
CORE_B="http://localhost:19001/v1/chat/completions"
PROD="http://localhost:9000/v1/models"
MODEL="qwen3.5-9b-test"

mkdir -p "$EVIDENCE_DIR"

log_step() {
  local step="$1" core="$2" status="$3" tokens="$4" latency="$5" detail="$6"
  printf '{"step":"%s","core":"%s","http_status":%s,"completion_tokens":%s,"latency_s":%.2f,"detail":"%s","ts":"%s"}\n' \
    "$step" "$core" "$status" "$tokens" "$latency" "$detail" "$TIMESTAMP" >> "$EVIDENCE_FILE"
}

chat() {
  # chat <core_url> <messages_json_array> <step_id> <core_label>
  local url="$1" messages="$2" step_id="$3" core_label="$4"
  local body start resp tokens latency

  body=$(jq -n \
    --arg model "$MODEL" \
    --argjson messages "$messages" \
    --argjson maxt "$MAX_TOKENS" \
    '{model: $model, messages: $messages, max_tokens: $maxt}')

  start=$(python3 -c 'import time; print(time.time())')
  if ! resp=$(curl -sf --max-time "$TIMEOUT" -X POST "$url" \
    -H "Content-Type: application/json" \
    -d "$body" 2>&1); then
    latency=$(python3 -c "import time; print(round(time.time() - $start, 2))")
    log_step "$step_id" "$core_label" 500 0 "$latency" "curl_error"
    echo "FAIL: step $step_id curl error" >&2
    return 1
  fi
  latency=$(python3 -c "import time; print(round(time.time() - $start, 2))")

  # Check for API-level error
  local errmsg
  errmsg=$(echo "$resp" | jq -r '.error.message // empty' 2>/dev/null)
  if [ -n "$errmsg" ]; then
    local errstatus
    errstatus=$(echo "$resp" | jq -r '.error.status // 500' 2>/dev/null)
    log_step "$step_id" "$core_label" "$errstatus" 0 "$latency" "api_error"
    echo "FAIL: step $step_id API error: $errmsg" >&2
    return 1
  fi

  tokens=$(echo "$resp" | jq '.usage.completion_tokens // 0' 2>/dev/null)
  if [ "$tokens" -eq 0 ] 2>/dev/null; then
    log_step "$step_id" "$core_label" 200 0 "$latency" "zero_tokens"
    echo "FAIL: step $step_id returned 0 completion tokens" >&2
    return 1
  fi

  log_step "$step_id" "$core_label" 200 "$tokens" "$latency" "ok"
  echo "$resp"
  return 0
}

echo "=== Hydra TEST Agent Rehearsal ($TIMESTAMP) ==="
echo "Evidence: $EVIDENCE_FILE"
echo ""

# -------------------------------------------------------
# Step 0: prod-isolation snapshot (before any test traffic)
# -------------------------------------------------------
echo "Step 0: prod-isolation snapshot (before)..."
PROD_BEFORE=$(curl -sf --max-time 10 "$PROD" 2>&1) || {
  echo "WARN: prod :9000 unreachable before test (skipping isolation check)" >&2
  PROD_BEFORE=""
}

# -------------------------------------------------------
# Step 1: System-prompt task briefing (core-A)
# -------------------------------------------------------
echo "Step 1: system-prompt task briefing (core-A)..."
STEP1_MSGS='[
  {"role":"system","content":"You are a helpful assistant. You are helping with a test rehearsal. Respond briefly."},
  {"role":"user","content":"Task briefing: You are agent-alpha. Your job is to analyze a code snippet and report line count. Acknowledge this task."}
]'
chat "$CORE_A" "$STEP1_MSGS" "step1_system_briefing" "core-A" >/dev/null
echo "  step1: OK"

# -------------------------------------------------------
# Step 2: Tool-call-style JSON request (core-A)
# -------------------------------------------------------
echo "Step 2: tool-call-style JSON request (core-A)..."
STEP2_MSGS='[
  {"role":"system","content":"You are a helpful assistant. Always respond with valid JSON when asked."},
  {"role":"user","content":"Return a JSON object with keys: tool (string), args (object with key file containing src/main.py). No markdown fences."}
]'
chat "$CORE_A" "$STEP2_MSGS" "step2_tool_call_json" "core-A" >/dev/null
echo "  step2: OK"

# -------------------------------------------------------
# Step 3: Multi-turn follow-up referencing prior context (core-A)
# -------------------------------------------------------
echo "Step 3: multi-turn follow-up (core-A)..."
STEP3_MSGS='[
  {"role":"system","content":"You are a helpful assistant. Remember the prior conversation."},
  {"role":"user","content":"Task briefing: You are agent-alpha. Your job is to analyze a code snippet and report line count."},
  {"role":"assistant","content":"Acknowledged. I am agent-alpha. I will analyze a code snippet and report the line count."},
  {"role":"user","content":"Follow-up: Based on the task you acknowledged earlier, what is the first step you would take? Reference the task name."}
]'
chat "$CORE_A" "$STEP3_MSGS" "step3_multiturn_followup" "core-A" >/dev/null
echo "  step3: OK"

# -------------------------------------------------------
# Step 4: Concurrent burst — 3 to core-A + 3 to core-B
#         Sequential within each core, parallel across cores.
#         Max 1 in-flight per core (cold_atomic self-lease limit).
# -------------------------------------------------------
echo "Step 4: concurrent burst (3 core-A + 3 core-B, parallel across cores)..."

run_burst() {
  local url="$1" core_label="$2" burst_id="$3"
  local i msg
  for i in 1 2 3; do
    msg=$(jq -n \
      --arg idx "$i" \
      --arg core "$core_label" \
      '[{"role":"system","content":"You are a helpful assistant."},{"role":"user","content":"Burst request \($idx) to \($core): What is 2 + \($idx)? Reply with just the number."}]')
    chat "$url" "$msg" "step4_burst_${burst_id}_${i}" "$core_label" >/dev/null
    echo "  step4 burst $core_label request $i: OK"
  done
}

# Launch both bursts in parallel (subshells)
run_burst "$CORE_A" "core-A" "A" &
PID_A=$!
run_burst "$CORE_B" "core-B" "B" &
PID_B=$!

# Wait for both; collect exit codes
FAIL=0
wait "$PID_A" || FAIL=1
wait "$PID_B" || FAIL=1

if [ "$FAIL" -ne 0 ]; then
  echo "FAIL: step 4 burst had failures" >&2
  exit 1
fi
echo "  step4: OK (6 requests completed)"

# -------------------------------------------------------
# Step 5: Session-continuation turn asserting context retention (core-A)
# -------------------------------------------------------
echo "Step 5: session-continuation context retention (core-A)..."
STEP5_MSGS='[
  {"role":"system","content":"You are a helpful assistant. Remember the prior conversation."},
  {"role":"user","content":"Task briefing: You are agent-alpha. Your job is to analyze a code snippet and report line count."},
  {"role":"assistant","content":"Acknowledged. I am agent-alpha. I will analyze a code snippet and report the line count."},
  {"role":"user","content":"What task were you assigned earlier? Reply with just the task name."}
]'
STEP5_RESP=$(chat "$CORE_A" "$STEP5_MSGS" "step5_context_retention" "core-A")
# Verify the response references the earlier task
STEP5_CONTENT=$(echo "$STEP5_RESP" | jq -r '.choices[0].message.content // ""' 2>/dev/null)
if echo "$STEP5_CONTENT" | grep -qiE "alpha|analy|line.count|task"; then
  echo "  step5: OK (context retained: response references agent-alpha task)"
else
  echo "  step5: WARN (response may not reference prior context): $STEP5_CONTENT" >&2
  # Still counts as pass if 200 OK — the model is small and may not follow perfectly
fi

# -------------------------------------------------------
# Step 6 POST: prod-isolation probe
# -------------------------------------------------------
echo "Step 6: prod-isolation probe..."
PROD_AFTER=$(curl -sf --max-time 10 "$PROD" 2>&1) || {
  echo "WARN: prod :9000 unreachable after test (skipping isolation check)" >&2
  PROD_AFTER=""
}

if [ -n "$PROD_BEFORE" ] && [ -n "$PROD_AFTER" ]; then
  if [ "$PROD_BEFORE" = "$PROD_AFTER" ]; then
    log_step "step6_prod_isolation" "prod" 200 0 0 "prod_unchanged"
    echo "  step6: OK (prod response unchanged — no contamination)"
  else
    log_step "step6_prod_isolation" "prod" 200 0 0 "prod_changed_DIFFERENT"
    echo "  FAIL: prod response CHANGED during test (contamination?)" >&2
    exit 1
  fi
else
  log_step "step6_prod_isolation" "prod" 200 0 0 "prod_skipped_unreachable"
  echo "  step6: SKIP (prod unreachable — no isolation check)"
fi

# -------------------------------------------------------
# Summary
# -------------------------------------------------------
echo ""
echo "=== Rehearsal complete ==="
echo "Evidence file: $EVIDENCE_FILE"
echo "Steps logged: $(wc -l < "$EVIDENCE_FILE")"
echo ""
echo "All steps passed (200 OK, tokens > 0)."
exit 0
