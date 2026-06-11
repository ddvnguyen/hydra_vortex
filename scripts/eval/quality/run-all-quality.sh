#!/usr/bin/env bash
# Hydra Quality Test Suite — Orchestrator
# Runs all 5 benchmarks against Hydra.Core in Split-mix mode
# and generates a consolidated quality report with P/D split verification.
#
# Usage:
#   bash scripts/eval/quality/run-all-quality.sh [--skip-download]
#
# Env vars:
#   HYDRA_URL       Hydra.Core URL (default http://localhost:9000)
#   HYDRA_MODEL     Model name for requests (default mini)
#   QUALITY_LIMIT   Max samples per benchmark (comma-separated, default 150,150,100,30,5)
#   RESULT_DIR      Output directory (default /tmp/hydra-quality-results)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPORT_FILE="${REPORT_FILE:-/tmp/hydra-quality-results/report.md}"

_ts() { date +%Y-%m-%dT%H:%M:%SZ; }
_log() { echo "[$(date +%H:%M:%S)] $*"; }
_sep() { echo "============================================"; }

# ---- Args ----
SKIP_DOWNLOAD=false
while [[ $# -gt 0 ]]; do
  case $1 in
    --skip-download) SKIP_DOWNLOAD=true; shift ;;
    *) _log "Unknown: $1"; exit 1 ;;
  esac
done

RESULT_DIR="${RESULT_DIR:-/tmp/hydra-quality-results}"
HYDRA_URL="${HYDRA_URL:-http://localhost:9000}"
HYDRA_MODEL="${HYDRA_MODEL:-mini}"

mkdir -p "$RESULT_DIR"

# ---- Pre-flight checks ----
_sep
_log "Hydra Quality Test Suite"
_log "  Mode: Split-mix (RTX Mini -> P100 Balanced)"
_log "  Hydra.Core: ${HYDRA_URL}"
_log "  Model: ${HYDRA_MODEL}"
_log "  Results: ${RESULT_DIR}"
_sep

# Check Hydra.Core health
_log "Checking Hydra.Core health..."
health=$(curl -s -m 10 "${HYDRA_URL}/health" 2>/dev/null | python3 -c \
  "import sys,json; d=json.load(sys.stdin); print(d.get('status','down'))" 2>/dev/null || echo "down")
_log "  Health: ${health}"
if [[ "$health" != "healthy" ]]; then
  _log "ERROR: Hydra.Core not healthy at ${HYDRA_URL}"
  _log "  Start with: cd infra && podman-compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up -d"
  exit 1
fi

# Check both nodes
_log "Checking llama-server nodes..."
rtx_health=$(curl -s -m 5 http://localhost:8080/health 2>/dev/null | python3 -c \
  "import sys,json; print(json.load(sys.stdin).get('status','down'))" 2>/dev/null || echo "down")
p100_health=$(curl -s -m 5 http://192.168.122.21:8086/health 2>/dev/null | python3 -c \
  "import sys,json; print(json.load(sys.stdin).get('status','down'))" 2>/dev/null || echo "down")
_log "  RTX: ${rtx_health}  P100: ${p100_health}"

if [[ "$rtx_health" != "ok" ]]; then
  _log "ERROR: RTX llama-server not healthy"
  exit 1
fi
if [[ "$p100_health" != "ok" ]]; then
  _log "ERROR: P100 llama-server not healthy"
  exit 1
fi

# Ensure RTX has Mini model loaded (router mode)
_log "Ensuring Mini model is loaded on RTX..."
load_resp=$(curl -s -X POST http://localhost:8080/models/load \
  -H "Content-Type: application/json" -d '{"model":"mini"}' 2>/dev/null)
_log "  /models/load: ${load_resp}"

# ---- Download datasets ----
if ! $SKIP_DOWNLOAD; then
  _sep
  _log "Downloading datasets (one-time)..."
  python3 -c "
from datasets import load_dataset
import sys

datasets = [
    ('MMLU-Pro', 'TIGER-Lab/MMLU-Pro'),
    ('AIME 2026', 'MathArena/aime_2026'),
    ('LiveCodeBench', 'livecodebench/code_generation'),
]
for name, ds_id in datasets:
    try:
        print(f'  {name}...', end='', flush=True)
        load_dataset(ds_id, trust_remote_code=True)
        print(' OK')
    except Exception as e:
        print(f' SKIP ({e})')
        pass

# GPQA is gated — try but don't fail
try:
    load_dataset('idavidrein/gpqa', 'gpqa_main', trust_remote_code=True)
    print('  GPQA... OK')
except Exception as e:
    print(f'  GPQA... SKIP (gated dataset, needs huggingface-cli login)')
" 2>/dev/null
fi

# ---- Run benchmarks ----
_sep
total=0
passed=0
results=()

run_bench() {
  local name=$1; shift
  _log ""
  _log "=== ${name} ==="
  _log ""
  "$@" 2>&1 | while IFS= read -r line; do
    echo "  ${line}"
  done
  local rc=${PIPESTATUS[0]}
  if [[ $rc -eq 0 ]]; then
    _log "${name}: DONE"
    return 0
  else
    _log "${name}: FAILED (exit code ${rc})"
    return 1
  fi
}

# 1. MMLU-Pro
(( total++ ))
_log "[${total}/5] Running MMLU-Pro (150 samples)..."
if python3 "${SCRIPT_DIR}/mmlu-pro/evaluate.py" --limit 150 \
    --output "${RESULT_DIR}/mmlu-pro.json"; then
  results+=("mmlu-pro")
  (( passed++ ))
fi

# 2. GPQA
(( total++ ))
_log "[${total}/5] Running GPQA (150 samples)..."
if python3 "${SCRIPT_DIR}/gpqa/evaluate.py" --limit 150 \
    --output "${RESULT_DIR}/gpqa.json"; then
  results+=("gpqa")
  (( passed++ ))
else
  _log "  GPQA skipped (gated dataset — run huggingface-cli login first)"
fi

# 3. LiveCodeBench
(( total++ ))
_log "[${total}/5] Running LiveCodeBench (100 problems)..."
if python3 "${SCRIPT_DIR}/livecodebench/evaluate.py" --limit 100 \
    --output "${RESULT_DIR}/livecodebench.json"; then
  results+=("livecodebench")
  (( passed++ ))
else
  _log "  LiveCodeBench skipped (dataset may not be available)"
fi

# 4. AIME 2026
(( total++ ))
_log "[${total}/5] Running AIME 2026 (30 problems)..."
if python3 "${SCRIPT_DIR}/aime/evaluate.py" \
    --output "${RESULT_DIR}/aime.json"; then
  results+=("aime")
  (( passed++ ))
fi

# 5. Perplexity
(( total++ ))
_log "[${total}/5] Running Perplexity (5 chunks)..."
if bash "${SCRIPT_DIR}/perplexity/run-perplexity.sh"; then
  results+=("perplexity")
  (( passed++ ))
fi

# ---- Generate report ----
_sep
_log "Generating quality report..."
python3 "${SCRIPT_DIR}/common/report.py" \
  --results-dir "${RESULT_DIR}" \
  --output "${REPORT_FILE}" \
  --model "Split-mix (RTX Mini -> P100 Balanced)"

# ---- Summary ----
_sep
_log ""
_log "Results: ${passed}/5 benchmarks completed"
_log "Reports: ${RESULT_DIR}/*.json"
_log "Report:  ${REPORT_FILE}"
_log ""
_sep

# Print the report
if [[ -f "${REPORT_FILE}" ]]; then
  cat "${REPORT_FILE}"
fi
