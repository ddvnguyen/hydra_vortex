#!/usr/bin/env bash
set -euo pipefail
# bench-baseline.sh — drive vanilla llama-server :8080 and capture baseline metrics
# Usage: bash infra/llama-baseline/bench-baseline.sh [--base-url http://localhost:8080/v1] [--out tests/bench/baselines/rtx2-baseline-$(date +%Y%m%d-%H%M).json]
BASE_URL="${1:-http://localhost:8080/v1}"
OUT="${2:-tests/bench/baselines/rtx2-baseline-$(date +%Y%m%d-%H%M%S).json}"
if [[ "$1" == --* ]]; then BASE_URL="${2:-http://localhost:8080/v1}"; OUT="${3:-tests/bench/baselines/rtx2-baseline-$(date +%Y%m%d-%H%M%S).json}"; fi
# Support --base-url and --out flags
while [[ $# -gt 0 ]]; do case "$1" in --base-url) BASE_URL="$2"; shift 2;; --out) OUT="$2"; shift 2;; *) shift;; esac; done

mkdir -p "$(dirname "$OUT")"
echo "==> Baseline harness against $BASE_URL → $OUT"
curl -s "$BASE_URL/../health" 2>&1 | head -c 500 || true; echo
curl -s "$BASE_URL/models" 2>&1 | head -c 1000 || true; echo

# Prefer harness.py if present, else minimal curl loop
if [[ -f tests/bench/harness.py ]]; then
  echo "==> Using tests/bench/harness.py"
  python3 -m tests.bench.harness --base-url "$BASE_URL" --out "$OUT" 2>&1 | tee -a "$OUT.log" || true
else
  echo "==> harness.py not found — running minimal curl probes (512/4096/8192)"
  TMP=$(mktemp)
  for TOKS in 512 4096 8192; do
    echo "--- Probe $TOKS tokens ---"
    PROMPT=$(python3 -c "print('hello ' * ($TOKS//2))")
    START=$(date +%s.%N)
    curl -s -N "$BASE_URL/chat/completions" -H 'Content-Type: application/json' \
      -d "{\"model\":\"Qwopus3.6-27B\",\"messages\":[{\"role\":\"user\",\"content\":\"$PROMPT\"}],\"max_tokens\":32,\"stream\":true}" \
      -o "$TMP" -w "\n%{time_total}\n" 2>&1 | tail -n 5 || true
    echo "TTFT/decode logged to $TMP"
  done
  nvidia-smi --query-gpu=index,name,memory.total,memory.used,utilization.gpu --format=csv 2>&1 | tee -a "$OUT.log" || true
  cat "$TMP" > "$OUT" 2>&1 || echo '{"note":"minimal probe, see .log"}' > "$OUT"
fi
echo "==> Done: $OUT"
ls -lh "$OUT" "$OUT.log" 2>&1 || true
