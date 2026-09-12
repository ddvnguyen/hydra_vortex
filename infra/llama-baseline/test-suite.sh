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
# ── --smoke fast-fail gate ──────────────────────────────────────────────────
# Before committing to a full arm run, run `--smoke`. It does the health/props
# check + a minimal 1 session × 2 turn shallow probe (1024 new tok/turn, 64
# output) instead of the full N-turn depth test. A broken boot (OOM, fit-solver
# skip, wrong model path, template failure) shows up in seconds rather than
# after ~700s of context growth. It exits non-zero with a clear "SMOKE GATE
# FAILED" banner, so it chains with `&&`:
#
#   bash test-suite.sh --smoke 18081 && bash test-suite.sh 18081 12
#
# After smoke passes, either run the full suite above OR replay a saved deep
# context with checkpoint-replay.sh (see docs/arm-testing-fast-iteration.md).
#
# All arm param configs (params/*.yml), concurrent-decode-test.sh (single-
# shot ~1K-deep concurrency probe), bench-baseline.sh, 128k-diagnostic-arm.sh
# and the spike-*/747.*/12x-series exploratory boots are SUB-TESTS: tooling
# used to investigate specific regressions or characterize one arm, not part
# of this gating suite. Run them directly when diagnosing something specific.
#
# Usage:
#   bash test-suite.sh [--smoke] <server_port> [n_turns=12]
#
# Requires an already-running llama-server (e.g. via run-with-params.sh).
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

SMOKE=0
PORT=""
N_TURNS="12"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --smoke|-s) SMOKE=1; shift ;;
    --help|-h)
      echo "usage: $0 [--smoke] <server_port> [n_turns=12]"
      echo "  --smoke  health/props + 2-turn shallow probe only (fast-fail)"
      exit 0 ;;
    -*) echo "Unknown flag: $1" >&2; exit 1 ;;
    *)
      if [[ -z "$PORT" ]]; then PORT="$1"; else N_TURNS="$1"; fi
      shift ;;
  esac
done
if [[ -z "$PORT" ]]; then
  echo "usage: $0 [--smoke] <server_port> [n_turns=12]" >&2
  exit 1
fi

NEW_TOKENS_PER_TURN=8000
OUTPUT_TOKENS_PER_TURN=750

# Snappy smoke probe: 2 shallow turns, tiny output budget. Just enough to
# prove the model loads, the template renders and tokens flow — not depth.
SMOKE_TURNS=2
SMOKE_NEW_TOKENS_PER_TURN=512
SMOKE_OUTPUT_TOKENS_PER_TURN=16

FAIL=0
section() { echo; echo "=== $1 ==="; }

# ---------------------------------------------------------------------------
# health + props check (shared by smoke and full modes)
# ---------------------------------------------------------------------------
health_check() {
  local health_code props_code
  HEALTH_JSON=$(mktemp)
  PROPS_JSON=$(mktemp)

  health_code=$(curl -s -m 10 -o "$HEALTH_JSON" -w '%{http_code}' "http://127.0.0.1:${PORT}/health")
  echo "  GET /health -> $health_code $(cat "$HEALTH_JSON" 2>/dev/null)"
  if [[ "$health_code" != "200" ]]; then
    echo "  FAIL: server not healthy"
    FAIL=1
  else
    echo "  PASS"
  fi

  props_code=$(curl -s -m 10 -o "$PROPS_JSON" -w '%{http_code}' "http://127.0.0.1:${PORT}/props")
  if [[ "$props_code" != "200" ]]; then
    echo "  FAIL: /props not reachable"
    FAIL=1
  else
    # Model-loaded / fit-solver sanity: a skipped or failed load leaves an
    # empty alias, a sleeping server, or missing build info.
    read -r MODEL FTYPE BUILD SLEEPING < <(python3 - "$PROPS_JSON" <<'PYEOF'
import json, sys
d = json.load(open(sys.argv[1]))
print(d.get("model_alias") or "-",
      d.get("model_ftype") or "-",
      d.get("build_info") or "-",
      d.get("is_sleeping"))
PYEOF
)
    echo "  model loaded: $MODEL"
    echo "  quantization: $FTYPE"
    echo "  build:        $BUILD"
    if [[ "$MODEL" == "-" || -z "$MODEL" ]]; then
      echo "  FAIL: /props reports no model_alias (load skipped or failed)"
      FAIL=1
    fi
    if [[ "$BUILD" == "-" || -z "$BUILD" ]]; then
      echo "  FAIL: /props reports no build_info"
      FAIL=1
    fi
    if [[ "$SLEEPING" == "True" || "$SLEEPING" == "true" ]]; then
      echo "  FAIL: server reports is_sleeping=true (model not resident)"
      FAIL=1
    fi
  fi

  rm -f "$HEALTH_JSON" "$PROPS_JSON"
}

# ---------------------------------------------------------------------------
# smoke mode: health/props + minimal shallow probe, fail fast
# ---------------------------------------------------------------------------
if [[ "$SMOKE" -eq 1 ]]; then
  section "SMOKE GATE (health/props + ${SMOKE_TURNS}-turn shallow probe)"
  SMOKE_T0=$(date +%s%N)
  health_check
  if [[ "$FAIL" -ne 0 ]]; then
    echo
    echo "SMOKE GATE FAILED (server not healthy — skipping probe)"
    echo "  Do NOT commit to a full arm run; fix the boot first."
    exit 1
  fi

  echo
  echo "--- shallow probe: 1 session × $SMOKE_TURNS turns, ~$SMOKE_NEW_TOKENS_PER_TURN new tok, $SMOKE_OUTPUT_TOKENS_PER_TURN output ---"
  PROBE=$(bash "$SCRIPT_DIR/multiturn-growth-test.sh" "$PORT" 1 "$SMOKE_TURNS" \
                "$SMOKE_NEW_TOKENS_PER_TURN" "$SMOKE_OUTPUT_TOKENS_PER_TURN" 2>&1)
  PROBE_RC=$?
  echo "$PROBE"
  if [[ "$PROBE_RC" -ne 0 ]] || echo "$PROBE" | grep -q "FAILED:"; then
    echo "  FAIL: shallow probe turn failed"
    FAIL=1
  elif ! echo "$PROBE" | grep -q "summary:"; then
    echo "  FAIL: shallow probe produced no summary"
    FAIL=1
  else
    echo "  PASS (context grows, completions succeed)"
  fi

  SMOKE_T1=$(date +%s%N)
  SMOKE_MS=$(( (SMOKE_T1 - SMOKE_T0) / 1000000 ))
  echo
  echo "=== result ==="
  if [[ "$FAIL" -eq 0 ]]; then
    echo "SMOKE GATE PASSED (${SMOKE_MS} ms)"
    echo "  Safe to proceed: full 'test-suite.sh $PORT $N_TURNS' or a checkpoint replay."
    exit 0
  else
    echo "SMOKE GATE FAILED (${SMOKE_MS} ms)"
    echo "  Do NOT commit to a full arm run; fix the boot first."
    exit 1
  fi
fi

# ---------------------------------------------------------------------------
# full mode: 1/3 health
# ---------------------------------------------------------------------------
section "1/3 health check"
health_check

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
