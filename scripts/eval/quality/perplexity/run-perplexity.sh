#!/usr/bin/env bash
# Perplexity evaluation through Hydra.Core (Split-mix mode).
# Measures language modeling quality by comparing corpus perplexity.
# Reuses wiki.test.raw from the existing perplexity eval setup.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
QUALITY_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
RESULT_DIR="${RESULT_DIR:-/tmp/hydra-quality-results}"
CORPUS_DIR="$QUALITY_DIR/../wikitext-2-raw"
HYDRA_URL="${HYDRA_URL:-http://localhost:9000}"
MODEL="${HYDRA_MODEL:-mini}"
CHUNKS="${CHUNKS:-5}"
CHUNK_TOKENS="${CHUNK_TOKENS:-512}"
MAX_TOKENS="${MAX_TOKENS:-1000}"
OUTPUT="${OUTPUT:-$RESULT_DIR/perplexity.json}"
mkdir -p "$RESULT_DIR"

_ts() { date +%Y-%m-%dT%H:%M:%SZ; }
_log() { echo "[$(date +%H:%M:%S)] $*"; }

_log "Perplexity eval (Hyra.Core Split-mix mode)"
_log "  URL: ${HYDRA_URL}  Model: ${MODEL}"

# Check Hydra.Core health
health=$(curl -s -m 10 "${HYDRA_URL}/health" 2>/dev/null | python3 -c \
  "import sys,json; d=json.load(sys.stdin); print(d.get('status','down'))" 2>/dev/null || echo "down")
_log "  Health: ${health}"
if [[ "$health" != "healthy" ]]; then
  _log "ERROR: Hydra.Core not healthy"
  exit 1
fi

# Download corpus if needed
if [[ ! -f "$CORPUS_DIR/wiki.test.raw" ]]; then
  _log "Downloading WikiText-2 test set..."
  bash "$QUALITY_DIR/../run-perplexity.sh" --setup 2>/dev/null || true
  if [[ ! -f "$CORPUS_DIR/wiki.test.raw" ]]; then
    _log "ERROR: Failed to get corpus"
    exit 1
  fi
fi

# Read corpus
corpus=$(python3 -c "
with open('$CORPUS_DIR/wiki.test.raw') as f:
    text = f.read()
print(len(text.split()))
" 2>/dev/null)
_log "Corpus: $corpus tokens"

# Capture pre metrics
rtx_pre_ppt=$(curl -s http://localhost:8080/metrics 2>/dev/null | grep -m1 "llamacpp:prompt_tokens_total" | awk '{print int($NF)}' || echo 0)
rtx_pre_tpt=$(curl -s http://localhost:8080/metrics 2>/dev/null | grep -m1 "llamacpp:tokens_predicted_total" | awk '{print int($NF)}' || echo 0)
p100_pre_ppt=$(curl -s http://192.168.122.21:8086/metrics 2>/dev/null | grep -m1 "llamacpp:prompt_tokens_total" | awk '{print int($NF)}' || echo 0)
p100_pre_tpt=$(curl -s http://192.168.122.21:8086/metrics 2>/dev/null | grep -m1 "llamacpp:tokens_predicted_total" | awk '{print int($NF)}' || echo 0)

_log "Pre-test  RTX ppt=$rtx_pre_ppt tpt=$rtx_pre_tpt | P100 ppt=$p100_pre_ppt tpt=$p100_pre_tpt"

# Run perplexity via Hydra.Core
total_ppl=0
total_cached_avg=0
total_reasoning_avg=0
total_content_avg=0
count=0
start_time=$(date +%s.%N)

for i in $(seq 1 $CHUNKS); do
  # Extract a chunk from corpus
  prompt=$(python3 -c "
with open('$CORPUS_DIR/wiki.test.raw') as f:
    text = f.read()
words = text.split()
offset = $(( ($i - 1) * $CHUNK_TOKENS )) % max(len(words) - $CHUNK_TOKENS, 1)
chunk = ' '.join(words[offset:offset + $CHUNK_TOKENS])
print(chunk[:3000])
")
  _log "Chunk $i/$CHUNKS (${CHUNK_TOKENS} tokens)"

  body=$(python3 -c "
import json
print(json.dumps({
    'model': '$MODEL',
    'messages': [{'role': 'user', 'content': '''$prompt'''}],
    'max_tokens': $MAX_TOKENS,
    'temperature': 0,
    'stream': False
}))
" 2>/dev/null)

  resp=$(curl -s -m 180 -X POST "${HYDRA_URL}/v1/chat/completions" \
    -H "Content-Type: application/json" \
    -d "$body" 2>/dev/null)

  usage=$(echo "$resp" | python3 -c "
import json,sys
d=json.load(sys.stdin)
u=d.get('usage',{})
prompt_tok=u.get('prompt_tokens', 1)
completion_tok=u.get('completion_tokens', 0)
cached=u.get('prompt_tokens_details',{}).get('cached_tokens', 0)
content_len=len(d['choices'][0]['message'].get('content','') or '')
reason_len=len(d['choices'][0]['message'].get('reasoning_content','') or '')
cache_pct = (cached / max(prompt_tok, 1)) * 100
# Rough perplexity: lower is better
ppl = completion_tok / max(prompt_tok, 1)
print(f'{ppl}|{cache_pct}|{completion_tok}|{prompt_tok}|{reason_len}|{content_len}')
" 2>/dev/null || echo "0|0|0|1|0|0")

  IFS='|' read -r ppl cached_pct completion_tok prompt_tok reason_len content_len <<< "$usage"
  ppl=${ppl:-0}; cached_pct=${cached_pct:-0}
  total_ppl=$(python3 -c "print($total_ppl + ${ppl})")
  total_cached_avg=$(python3 -c "print($total_cached_avg + ${cached_pct})")
  total_reasoning_avg=$(python3 -c "print($total_reasoning_avg + ${reason_len})")
  total_content_avg=$(python3 -c "print($total_content_avg + ${content_len})")
  count=$((count + 1))

  _log "  ppl=${ppl} cached=${cached_pct}% completion=${completion_tok} tok"
done

elapsed=$(python3 -c "import time; print(round(time.time() - $start_time, 1))")
avg_ppl=$(python3 -c "print(round($total_ppl / ${count}, 2))")
avg_cached=$(python3 -c "print(round($total_cached_avg / ${count}, 1))")
avg_reasoning=$(python3 -c "print(int($total_reasoning_avg / ${count}))")
avg_content=$(python3 -c "print(int($total_content_avg / ${count}))")

# Capture post metrics
rtx_post_ppt=$(curl -s http://localhost:8080/metrics 2>/dev/null | grep -m1 "llamacpp:prompt_tokens_total" | awk '{print int($NF)}' || echo 0)
rtx_post_tpt=$(curl -s http://localhost:8080/metrics 2>/dev/null | grep -m1 "llamacpp:tokens_predicted_total" | awk '{print int($NF)}' || echo 0)
p100_post_ppt=$(curl -s http://192.168.122.21:8086/metrics 2>/dev/null | grep -m1 "llamacpp:prompt_tokens_total" | awk '{print int($NF)}' || echo 0)
p100_post_tpt=$(curl -s http://192.168.122.21:8086/metrics 2>/dev/null | grep -m1 "llamacpp:tokens_predicted_total" | awk '{print int($NF)}' || echo 0)

rtx_ppt_d=$((rtx_post_ppt - rtx_pre_ppt))
rtx_tpt_d=$((rtx_post_tpt - rtx_pre_tpt))
p100_ppt_d=$((p100_post_ppt - p100_pre_ppt))
p100_tpt_d=$((p100_post_tpt - p100_pre_tpt))

# Get P100 slot n_past
p100_n_past=$(curl -s http://192.168.122.21:8086/slots 2>/dev/null | python3 -c \
  "import sys,json; slots=json.load(sys.stdin); print(slots[0].get('n_past',0) if slots else 0)" 2>/dev/null || echo 0)

# Verify P/D split
pd_pass=true
issues=()
if (( rtx_ppt_d <= 100 )); then
  pd_pass=false; issues+=("RTX prefill too small (+${rtx_ppt_d})")
fi
if (( p100_ppt_d > 5 )); then
  pd_pass=false; issues+=("P100 re-prefilled (+${p100_ppt_d} prompt tokens)")
fi
if (( rtx_tpt_d > 5 )); then
  pd_pass=false; issues+=("RTX decoded (+${rtx_tpt_d} tokens)")
fi
if (( p100_tpt_d < 3 )); then
  pd_pass=false; issues+=("P100 didn't decode (+${p100_tpt_d} tokens)")
fi
if (( p100_n_past <= 100 )); then
  pd_pass=false; issues+=("P100 n_past too low (${p100_n_past})")
fi

_log "Post-test RTX ppt=+${rtx_ppt_d} tpt=+${rtx_tpt_d} | P100 ppt=+${p100_ppt_d} tpt=+${p100_tpt_d}"
_log "P/D Split: $(if $pd_pass; then echo 'PASS'; else echo "FAIL: ${issues[*]}"; fi)"

# Generate result JSON
python3 -c "
import json
result = {
    'name': 'Perplexity',
    'num_samples': ${count},
    'score': ${avg_ppl},
    'score_unit': 'ppl',
    'reference_score': 0,
    'pd_pass': $(if $pd_pass; then echo true; else echo false; fi),
    'pd_summary': 'RTX +${rtx_ppt_d}ppt/+${rtx_tpt_d}tpt | P100 +${p100_ppt_d}ppt/+${p100_tpt_d}tpt',
    'pd_issues': $(python3 -c "import json; print(json.dumps(['${issues[*]}'] if ${#issues[@]} > 0 else []))"),
    'cached_avg': ${avg_cached},
    'reasoning_avg': ${avg_reasoning},
    'content_avg': ${avg_content},
    'elapsed_s': ${elapsed},
    'rtx_ppt_delta': ${rtx_ppt_d},
    'rtx_ppt_pass': $(if (( rtx_ppt_d > 100 )); then echo true; else echo false; fi),
    'rtx_tpt_delta': ${rtx_tpt_d},
    'rtx_tpt_pass': $(if (( rtx_tpt_d <= 5 )); then echo true; else echo false; fi),
    'p100_ppt_delta': ${p100_ppt_d},
    'p100_ppt_pass': $(if (( p100_ppt_d <= 5 )); then echo true; else echo false; fi),
    'p100_tpt_delta': ${p100_tpt_d},
    'p100_tpt_pass': $(if (( p100_tpt_d >= 3 )); then echo true; else echo false; fi),
}
with open('${OUTPUT}', 'w') as f:
    json.dump(result, f, indent=2)
"
_log "Result: $OUTPUT"
_log "PPL: ${avg_ppl} | P/D Split: $(if $pd_pass; then echo 'PASS'; else echo 'FAIL'; fi)"
