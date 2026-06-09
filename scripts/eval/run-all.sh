#!/usr/bin/env bash
# Hydra Eval: Run all tiers sequentially with monitoring.
#
# Usage:
#   bash scripts/eval/run-all.sh --small      # 2K NIAH only (fast, ~2 min)
#   bash scripts/eval/run-all.sh --full       # 2K + 5K + 8K NIAH (~15 min)
#   bash scripts/eval/run-all.sh --perplexity # perplexity baseline comparison
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULT_DIR="/tmp/hydra-eval-results"
COORD_URL="${COORD_URL:-http://localhost:9000}"

_log()  { echo "[$(date +%H:%M:%S)] $*"; }

mkdir -p "$RESULT_DIR"

# ---- check health ----

check_health() {
    local health
    health=$(curl -s -m 5 "${COORD_URL}/health" 2>/dev/null)
    if ! echo "$health" | python3 -c "import sys,json; d=json.load(sys.stdin); exit(0 if d.get('status')=='healthy' else 1)" 2>/dev/null; then
        echo "FAIL: Hydra Core not healthy: $health"
        exit 1
    fi
    echo "Hydra Core: healthy"
    curl -s -m 3 http://localhost:8080/health | python3 -c "import sys,json; d=json.load(sys.stdin); print(f'llama RTX: {d[\"status\"]}')" 2>/dev/null || echo "llama RTX: DOWN"
    curl -s -m 3 http://192.168.122.21:8086/health | python3 -c "import sys,json; d=json.load(sys.stdin); print(f'llama P100: {d[\"status\"]}')" 2>/dev/null || echo "llama P100: DOWN"
}

# ---- snapshot pre-test state ----

snapshot_pre() {
    local out="$RESULT_DIR/pre-test-snapshot.txt"
    _log "Saving pre-test snapshot → $out"
    {
        echo "=== Health ==="
        curl -s -m 5 "${COORD_URL}/health" 2>/dev/null
        echo "=== RTX slots ==="
        curl -s -m 3 http://localhost:8080/slots 2>/dev/null
        echo "=== P100 slots ==="
        curl -s -m 3 http://192.168.122.21:8086/slots 2>/dev/null
        echo "=== RTX metrics (tokens) ==="
        curl -s -m 3 http://localhost:8080/metrics 2>/dev/null | grep -E "tokens|requests"
        echo "=== P100 metrics (tokens) ==="
        curl -s -m 3 http://192.168.122.21:8086/metrics 2>/dev/null | grep -E "tokens|requests"
        echo "=== Hydra metrics ==="
        curl -s -m 3 "${COORD_URL}/metrics" 2>/dev/null | grep -E "hydra_save_kv|hydra_restore_kv|hydra_prefill|hydra_decode"
    } > "$out" 2>&1
}

# ---- collect logs for trace_ids ----

collect_logs() {
    local pattern="${1:-niah-}"
    local out="$RESULT_DIR/post-test-logs.txt"
    _log "Collecting Hydra Core logs → $out"
    journalctl --user -u hydra-core --no-pager -n 2000 2>/dev/null | grep -E "$pattern|request_timeline|save_kv|restore_kv|cold_concurrency|prefill" > "$out" 2>/dev/null || true
    _log "  $(wc -l < "$out") log lines captured"
}

# ---- main ----

main() {
    local mode="${1:---small}"

    echo "============================================"
    echo " Hydra P/D Split Eval Suite"
    echo " $(date)"
    echo "============================================"
    echo ""

    check_health
    echo ""

    snapshot_pre
    echo ""

    case "$mode" in
        --small)
            _log "Running small batch: 2K NIAH only"
            bash "$SCRIPT_DIR/run-niah.sh" -c 2000 -d 50 2>&1
            ;;
        --full)
            _log "Running full sweep: 2K + 5K + 8K NIAH"
            bash "$SCRIPT_DIR/run-niah.sh" -c 2000,5000,8000 -d 50 2>&1
            ;;
        --perplexity)
            _log "Running perplexity comparison"
            bash "$SCRIPT_DIR/run-perplexity.sh" --compare 2>&1
            ;;
        *)
            _log "Unknown mode: $mode"
            echo "Usage: $0 [--small|--full|--perplexity]"
            exit 1
            ;;
    esac

    echo ""
    collect_logs

    echo ""
    echo "============================================"
    echo " Results: ${RESULT_DIR}/"
    echo "============================================"
    ls -la "$RESULT_DIR"/
    echo ""
    echo "To see detailed results:"
    echo "  cat ${RESULT_DIR}/*-summary.txt"
    echo "  cat ${RESULT_DIR}/post-test-logs.txt"
}

main "$@"
