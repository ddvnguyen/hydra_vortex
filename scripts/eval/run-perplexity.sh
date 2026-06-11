#!/usr/bin/env bash
# Hydra Eval: Perplexity comparison — RTX direct vs Hydra P/D split.
#
# Requires: llama-perplexity binary (built from llama.cpp)
#           WikiText-2 corpus at /tmp/wikitext-test.txt
#
# Usage:
#   bash scripts/eval/run-perplexity.sh --setup          # download corpus
#   bash scripts/eval/run-perplexity.sh --baseline        # RTX direct perplexity
#   bash scripts/eval/run-perplexity.sh --via-coordinator # through Hydra
#   bash scripts/eval/run-perplexity.sh --compare         # diff the two
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULT_DIR="/tmp/hydra-eval-results"
COORD_URL="${COORD_URL:-http://localhost:9000}"
RTX_LLAMA="${RTX_LLAMA_URL:-http://localhost:8080}"
P100_LLAMA="${P100_LLAMA_URL:-http://192.168.122.21:8086}"

MODEL="/mnt/SSD/Qwopus3.6-35B-A3B-v1-APEX-MTP-I-Balanced.gguf"
PERP="/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp/build_sm120/bin/llama-perplexity"
CORPUS="/tmp/wikitext-test.txt"

_log()  { echo "[$(date +%H:%M:%S)] $*"; }

# ---- setup corpus ----

setup_corpus() {
    if [[ -f "$CORPUS" ]]; then
        _log "Corpus already exists: $CORPUS ($(wc -c < "$CORPUS") bytes)"
        return
    fi
    _log "Downloading WikiText-2 test set..."
    curl -sL -o /tmp/wikitext-test-raw.txt \
        "https://huggingface.co/datasets/mindchain/wikitext2/resolve/main/wikitext-test.txt"
    # Strip Wiki markup headers (lines starting with " = ")
    grep -v '^ = ' /tmp/wikitext-test-raw.txt > "$CORPUS"
    rm -f /tmp/wikitext-test-raw.txt
    _log "Corpus ready: $CORPUS ($(wc -c < "$CORPUS") bytes, $(wc -l < "$CORPUS") lines)"
}

# ---- baseline: direct RTX perplexity ----

run_baseline() {
    _log "Running baseline perplexity on RTX (direct llama-perplexity)..."
    local out="${RESULT_DIR}/perplexity-rtx-direct.txt"

    if [[ ! -x "$PERP" ]]; then
        echo "FAIL: llama-perplexity not found at $PERP"
        echo "Build with: cmake --build build_sm120 --target llama-perplexity -j\$(nproc)"
        exit 1
    fi

    "$PERP" -m "$MODEL" -ngl 99 -f "$CORPUS" -c 4096 -b 512 \
        --ppl-stride 256 --chunks 5 2>&1 | tee "$out"

    _log "Baseline saved: $out"
}

# ---- via Hydra Coordinator (P/D split) ----

run_via_coordinator() {
    _log "Running perplexity through Hydra P/D split..."
    _log "This sends each chunk as a completion to Hydra, which routes prefill→RTX, save→P100, decode."

    local out="${RESULT_DIR}/perplexity-via-hydra.txt"
    local chunk_size=4096
    local stride=256
    local n_chunks=5

    # Split corpus into chunks
    local total_chars=$((chunk_size * 3))
    local corpus_text
    corpus_text=$(head -c $((total_chars * n_chunks)) "$CORPUS" 2>/dev/null)

    echo "=== Perplexity via Hydra P/D Split ===" > "$out"
    echo "chunks=$n_chunks chunk_size=$chunk_size stride=$stride" >> "$out"
    echo "" >> "$out"

    for i in $(seq 1 $n_chunks); do
        local start=$(( (i-1) * stride * 3 ))
        local len=$(( chunk_size * 3 ))
        local chunk="${corpus_text:$start:$len}"

        _log "Chunk $i/$n_chunks: ${#chunk} chars..."

        local trace_id="ppl-chunk${i}-$(date +%s)"
        local resp_file="${RESULT_DIR}/perplexity-chunk${i}.json"

        local http_code
        http_code=$(curl -s -w '%{http_code}' -o "$resp_file" \
            --max-time 120 \
            -X POST "${COORD_URL}/v1/chat/completions" \
            -H "Content-Type: application/json" \
            -H "X-Hydra-Trace-Id: ${trace_id}" \
            -d "$(python3 -c "
import json
text = open('/dev/stdin').read().strip()
print(json.dumps({
    'model': 'balanced',
    'messages': [{'role': 'user', 'content': f'Continue the following text exactly as written, word for word, without any additional commentary:\n\n{text}'}],
    'max_tokens': 1000,
    'temperature': 0,
    'stream': False
}))
" <<< "$chunk")" 2>/dev/null)

        echo "chunk=$i http=$http_code" >> "$out"

        # Log timing from response
        python3 -c "
import json
try:
    d=json.load(open('$resp_file'))
    u=d.get('usage',{})
    print(f'chunk=$i prompt_tokens={u.get(\"prompt_tokens\",\"?\")} completion_tokens={u.get(\"completion_tokens\",\"?\")} total_tokens={u.get(\"total_tokens\",\"?\")}', file=open('$out','a'))
except: print(f'chunk=$i parse_error', file=open('$out','a'))
" 2>/dev/null

        sleep 1
    done

    _log "Coordinator results: $out"
}

# ---- compare ----

run_compare() {
    _log "Comparing RTX baseline vs Hydra P/D split results..."

    if [[ ! -f "${RESULT_DIR}/perplexity-rtx-direct.txt" ]]; then
        _log "No baseline found. Run --baseline first."
        return 1
    fi
    if [[ ! -f "${RESULT_DIR}/perplexity-via-hydra.txt" ]]; then
        _log "No coordinator results. Run --via-coordinator first."
        return 1
    fi

    echo ""
    echo "=== RTX Direct (baseline) ==="
    grep -E "^\[" "${RESULT_DIR}/perplexity-rtx-direct.txt" | head -5

    echo ""
    echo "=== Hydra P/D Split ==="
    cat "${RESULT_DIR}/perplexity-via-hydra.txt"

    echo ""
    _log "For full perplexity comparison, use llama-perplexity directly:"
    echo "  $PERP -m $MODEL -ngl 99 -f $CORPUS -c 4096 -b 512 --ppl-stride 256 --chunks 5"
    echo ""
    _log "Then compare the [N]X.XXXX scores. They must be identical."
}

# ---- monitor during test ----

monitor_bg() {
    local out="${RESULT_DIR}/perplexity-monitor.log"
    _log "Background monitor → $out"
    {
        echo "=== Monitor $(date) ==="
        for i in $(seq 1 60); do
            echo "--- $(date +%H:%M:%S) ---"
            echo "RTX:"; curl -s -m 2 "$RTX_LLAMA/slots" 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d),'slots')" 2>/dev/null
            echo "P100:"; curl -s -m 2 "$P100_LLAMA/slots" 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'  n_past={s.get(\"n_past\",0)}') for s in d]" 2>/dev/null
            sleep 5
        done
    } > "$out" 2>&1 &
    echo $!
}

# ---- main ----

main() {
    mkdir -p "$RESULT_DIR"

    case "${1:-}" in
        --setup)
            setup_corpus
            ;;
        --baseline)
            setup_corpus
            run_baseline
            ;;
        --via-coordinator)
            setup_corpus
            local mon_pid
            mon_pid=$(monitor_bg)
            run_via_coordinator
            kill "$mon_pid" 2>/dev/null || true
            ;;
        --compare)
            run_compare
            ;;
        *)
            echo "Usage: $0 [--setup|--baseline|--via-coordinator|--compare]"
            echo ""
            echo "  --setup            Download WikiText-2 corpus"
            echo "  --baseline         Run perplexity on RTX directly"
            echo "  --via-coordinator  Run through Hydra P/D split"
            echo "  --compare          Compare both results"
            exit 1
            ;;
    esac
}

main "$@"
