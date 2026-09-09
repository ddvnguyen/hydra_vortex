#!/usr/bin/env bash
# test-suite.sh — MAIN test suite for the llama-baseline harness.
#
# Gates whatever arm/param file is currently deployed (docker-compose
# LLAMACPP_PARAMS_FILE) against three checks:
#
#   1. health       — server up, model loaded (fast, ~1s)
#   2. single        — 1 session, N_TURNS turns, verifies context depth
#                       grows correctly and every turn completes
#   3. concurrency-2 — 2 sessions, N_TURNS turns, verifies context depth
#                       AND genuine concurrent decode overlap
#
# All arm param configs (params/*.yml), concurrent-decode-test.sh (single-
# shot ~1K-deep concurrency probe), bench-baseline.sh, 128k-diagnostic-arm.sh
# and the spike-*/747.*/12x-series exploratory boots are SUB-TESTS: tooling
# used to investigate specific regressions or characterize one arm, not part
# of this gating suite. Run them directly when diagnosing something specific.
#
# Usage: bash test-suite.sh <server_port> [n_turns=12]
# Requires an already-running llama-server (e.g. via run-with-params.sh).
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PORT="${1:?usage: $0 <server_port> [n_turns=12]}"
N_TURNS="${2:-12}"
NEW_TOKENS_PER_TURN=8000
OUTPUT_TOKENS_PER_TURN=750

FAIL=0
section() { echo; echo "=== $1 ==="; }

# ---------------------------------------------------------------------------
# 1/3 health check
# ---------------------------------------------------------------------------
section "1/3 health check"

HEALTH_CODE=$(curl -s -m 10 -o /tmp/ts_health.json -w '%{http_code}' "http://127.0.0.1:${PORT}/health")
echo "  GET /health -> $HEALTH_CODE $(cat /tmp/ts_health.json 2>/dev/null)"
if [[ "$HEALTH_CODE" != "200" ]]; then
  echo "  FAIL: server not healthy"
  FAIL=1
else
  echo "  PASS"
fi

PROPS_CODE=$(curl -s -m 10 -o /tmp/ts_props.json -w '%{http_code}' "http://127.0.0.1:${PORT}/props")
if [[ "$PROPS_CODE" != "200" ]]; then
  echo "  FAIL: /props not reachable"
  FAIL=1
else
  MODEL=$(python3 -c 'import json; print(json.load(open("/tmp/ts_props.json")).get("model_alias","?"))' 2>/dev/null || echo "?")
  echo "  model loaded: $MODEL"
fi
rm -f /tmp/ts_health.json /tmp/ts_props.json

if [[ "$FAIL" -ne 0 ]]; then
  echo
  echo "=== result ==="
  echo "MAIN TEST SUITE FAILED (server not healthy, skipping depth tests)"
  exit 1
fi

# ---------------------------------------------------------------------------
# 2/3 single-request depth
# ---------------------------------------------------------------------------
section "2/3 single-request depth (1 session, $N_TURNS turns)"
OUT1=$(bash "$SCRIPT_DIR/multiturn-growth-test.sh" "$PORT" 1 "$N_TURNS" "$NEW_TOKENS_PER_TURN" "$OUTPUT_TOKENS_PER_TURN" 2>&1)
echo "$OUT1"
if echo "$OUT1" | grep -q "FAILED:"; then
  echo "  FAIL: one or more turns failed"
  FAIL=1
elif ! echo "$OUT1" | grep -q "summary:"; then
  echo "  FAIL: no summary produced"
  FAIL=1
else
  echo "  PASS"
fi

# ---------------------------------------------------------------------------
# 3/3 concurrency-2 depth
# ---------------------------------------------------------------------------
section "3/3 concurrency-2 depth (2 sessions, $N_TURNS turns)"
OUT2=$(bash "$SCRIPT_DIR/multiturn-growth-test.sh" "$PORT" 2 "$N_TURNS" "$NEW_TOKENS_PER_TURN" "$OUTPUT_TOKENS_PER_TURN" 2>&1)
echo "$OUT2"
if echo "$OUT2" | grep -q "FAILED:"; then
  echo "  FAIL: one or more turns failed"
  FAIL=1
fi
if ! echo "$OUT2" | grep -q "concurrency check: PASS"; then
  echo "  FAIL: sessions did not overlap (or check missing)"
  FAIL=1
else
  echo "  PASS (depth + concurrency verified)"
fi

# ---------------------------------------------------------------------------
section "result"
if [[ "$FAIL" -eq 0 ]]; then
  echo "ALL MAIN TESTS PASSED"
  exit 0
else
  echo "MAIN TEST SUITE FAILED"
  exit 1
fi
