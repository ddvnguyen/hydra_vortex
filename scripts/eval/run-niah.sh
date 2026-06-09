#!/usr/bin/env bash
# Hydra Eval: Needle-in-a-Haystack (NIAH) test for P/D split verification.
#
# Usage:
#   bash scripts/eval/run-niah.sh -c 2000 -d 50           # single test
#   bash scripts/eval/run-niah.sh -c 2000,5000,8000 -d 50  # sweep
#   bash scripts/eval/run-niah.sh -c 2000 -d 50 --bg       # tmux background
#
# Verification:
#   - Passkey appears in response (KV cache intact)
#   - P100 prompt_tokens_seconds ≈ 0 (no re-prefill)
#   - Hydra timeline shows save_kv_ms + restore_kv_ms
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULT_DIR="/tmp/hydra-eval-results"
COORD_URL="${COORD_URL:-http://localhost:9000}"
MODEL="${HYDRA_MODEL:-balanced}"

# ---- helpers ----

_log()  { echo "[$(date +%H:%M:%S)] $*"; }
_ok()   { echo "  ✓ $*"; }
_fail() { echo "  ✗ $*"; }

# ---- prompt generator ----

_CHARS_PER_TOKEN=3.0

_SEED_PARAS=(
    "Software engineering encompasses requirements gathering, system design, implementation, testing, deployment, and maintenance."
    "Database indexing strategies significantly impact query performance. B-tree indexes excel at range queries, while hash indexes optimize point lookups."
    "Container orchestration platforms automate deployment, scaling, and management of containerized applications."
    "Distributed systems require careful handling of consistency, availability, and partition tolerance."
    "API design best practices include consistent naming conventions, proper versioning strategies, and comprehensive error handling."
    "Test-driven development writes tests before production code, ensuring requirements are clearly understood."
    "Network protocols like TCP provide reliable, ordered delivery of data between applications."
    "Microservices architecture decomposes applications into independently deployable services."
    "Caching strategies improve application performance by storing frequently accessed data in fast storage layers."
    "Authentication and authorization are fundamental security concerns in modern applications."
    "Monitoring and observability are critical for production systems, providing visibility into system behavior."
    "Machine learning pipelines transform raw data into trained models through stages of collection and preprocessing."
    "Concurrency control mechanisms prevent race conditions in multi-threaded applications."
    "Cloud infrastructure patterns include lift-and-shift migration and cloud-native architecture."
    "Code review practices improve code quality through peer examination of correctness and maintainability."
    "Load balancing distributes incoming traffic across multiple servers to ensure reliability."
    "Data serialization formats like Protocol Buffers provide efficient binary encoding with schema evolution."
    "Dead letter queues handle messages that cannot be processed successfully in distributed systems."
    "Circuit breaker patterns prevent cascading failures by detecting unhealthy downstream services."
    "Infrastructure as code manages cloud resources through declarative configuration files."
    "Reactive programming models handle asynchronous data streams and propagate changes."
    "Feature flags enable safe deployment of new functionality by toggling features without code changes."
    "Rate limiting protects APIs from abuse by restricting the number of requests per time window."
    "Message queues decouple producers and consumers, enabling asynchronous communication."
    "Search indexing builds inverted data structures for fast full-text retrieval."
    "Pipeline automation reduces manual effort in software delivery through continuous integration."
    "Data partitioning strategies distribute large datasets across multiple nodes for scalability."
    "Service mesh implementations provide observability and traffic management for microservices."
    "WebAssembly enables high-performance code execution in web browsers."
    "Chaos engineering proactively tests system resilience by introducing controlled failures."
    "Event sourcing stores state changes as an append-only event log for audit trails."
    "Configuration management tools automate server provisioning and application deployment."
    "Connection pooling reuses database connections to reduce connection establishment overhead."
    "GraphQL provides a flexible query language for APIs, allowing clients to request exact data."
    "Time-series databases optimize storage and querying for timestamped data points."
    "Edge computing moves computation closer to data sources, reducing latency for IoT applications."
    "Zero-trust security models require verification for every access request regardless of network location."
    "Observability-driven development uses telemetry data to guide implementation decisions."
    "Blue-green deployment strategies reduce downtime by running two identical production environments."
    "Data lakes provide centralized repositories for storing structured and unstructured data at scale."
    "Consensus algorithms like Raft and Paxos ensure agreement across distributed system nodes."
    "Immutable infrastructure treats servers as disposable, replacing rather than modifying them."
    "Polyglot persistence uses different data storage technologies for different data requirements."
    "Backpressure mechanisms prevent system overload by signaling producers to slow down."
    "Canary releases gradually roll out changes to a small subset of users before full deployment."
    "Sharding partitions databases horizontally across multiple servers for write scalability."
    "Idempotency keys ensure exactly-once processing semantics in distributed message systems."
    "Content delivery networks cache static assets at edge locations for global low-latency access."
    "Vector databases enable similarity search for embeddings used in recommendation and semantic search."
    "Stream processing frameworks handle real-time data with exactly-once semantics and windowed operations."
    "Federated learning trains machine learning models across decentralized data without centralizing it."
    "Quantum-resistant cryptography prepares for post-quantum computing threats to current encryption."
    "Data mesh architectures treat data as a product with domain-oriented ownership and governance."
    "Platform engineering builds internal developer platforms to improve developer experience."
    "Confidential computing protects data in use by performing computation in hardware-based trusted environments."
)

generate_prompt() {
    local target_tokens=$1
    local needle_depth_percent=${2:-50}
    local needle="${3:-}"
    local target_chars=$((target_tokens * _CHARS_PER_TOKEN))
    local haystack=""
    local total=0
    while (( total < target_chars )); do
        for p in "${_SEED_PARAS[@]}"; do
            (( total >= target_chars )) && break
            haystack+="$p "
            total=$((total + ${#p} + 1))
        done
    done
    haystack="${haystack:0:$target_chars}"

    local passkey
    if [[ -n "$needle" ]]; then
        passkey="$needle"
    else
        passkey="SECRET-$(openssl rand -hex 4 | tr '[:lower:]' '[:upper:]')"
    fi

    # Insert needle at specified depth percentage
    local insert_pos=$(( ${#haystack} * needle_depth_percent / 100 ))
    local before="${haystack:0:$insert_pos}"
    local after="${haystack:$insert_pos}"
    local needle_block=" IMPORTANT: The secret verification code is ${passkey}. You must remember this code exactly."

    cat <<EOF
${before}${needle_block}${after}

Based on the reference material above, what is the secret verification code? Answer with ONLY the code, nothing else.
EOF
}

# ---- main test ----

run_niah_test() {
    local context_tokens=$1
    local needle_depth=$2
    local test_name=$3
    local passkey="PASS-$(printf '%04x' $RANDOM | tr '[:lower:]' '[:upper:]')"

    _log "NIAH test: ctx=${context_tokens} depth=${needle_depth}% key=${passkey}"

    local prompt
    prompt=$(generate_prompt "$context_tokens" "$needle_depth" "$passkey")

    local trace_id="niah-${test_name}-$(date +%s)"
    local result_file="${RESULT_DIR}/${test_name}-response.json"
    local timing_file="${RESULT_DIR}/${test_name}-timing.txt"

    mkdir -p "$RESULT_DIR"

    # Save prompt for debugging
    echo "$passkey" > "${RESULT_DIR}/${test_name}-expected.txt"

    # Send request
    local http_code timing
    http_code=$(curl -s -w '%{http_code}' -o "$result_file" \
        --max-time 300 \
        -X POST "${COORD_URL}/v1/chat/completions" \
        -H "Content-Type: application/json" \
        -H "X-Hydra-Trace-Id: ${trace_id}" \
        -d "$(python3 -c "
import json, sys
prompt = sys.stdin.read()
msg = {'role': 'user', 'content': prompt}
print(json.dumps({'model':'${MODEL}','messages':[msg],'max_tokens':50,'temperature':0,'stream':False}))
" <<< "$prompt")" 2>"$timing_file")

    local elapsed
    elapsed=$(grep -oP 'time_total:\s*\K[\d.]+' "$timing_file" 2>/dev/null || echo "?")

    # Verify
    local passed=true
    local issues=()

    if [[ "$http_code" != "200" ]]; then
        _fail "HTTP $http_code"
        passed=false
        issues+=("HTTP status: $http_code")
    fi

    # Check for passkey in response
    local content
    content=$(python3 -c "
import json, sys
try:
    d = json.load(open('$result_file'))
    c = d['choices'][0]['message'].get('content','')
    r = d['choices'][0]['message'].get('reasoning_content','')
    print(c)
except: print('PARSE_ERROR')
" 2>/dev/null)

    if echo "$content" | grep -qi "$passkey"; then
        _ok "Passkey FOUND: '$passkey' in response"
    else
        _fail "Passkey '$passkey' NOT found in response"
        _fail "Response: ${content:0:200}"
        passed=false
        issues+=("passkey not in response")
    fi

    # Check finish reason
    local finish
    finish=$(python3 -c "import json; d=json.load(open('$result_file')); print(d['choices'][0].get('finish_reason','?'))" 2>/dev/null)
    _log "finish_reason=$finish time=$elapsed"

    # Check thinking content
    local has_thinking=false
    python3 -c "
import json
d=json.load(open('$result_file'))
r=d['choices'][0]['message'].get('reasoning_content','')
exit(0 if len(r)>=20 else 1)" 2>/dev/null && has_thinking=true

    if $has_thinking; then
        _ok "Thinking/reasoning content present"
    else
        _fail "No thinking content (reasoning_content missing or too short)"
        issues+=("no thinking content")
    fi

    # Check usage stats
    python3 -c "
import json
d=json.load(open('$result_file'))
u=d.get('usage',{})
print(f\"usage: prompt={u.get('prompt_tokens','?')} completion={u.get('completion_tokens','?')} total={u.get('total_tokens','?')}\")" 2>/dev/null

    # Write summary
    cat > "${RESULT_DIR}/${test_name}-summary.txt" <<EOF
test:      ${test_name}
context:   ${context_tokens} tokens
depth:     ${needle_depth}%
passkey:   ${passkey}
trace_id:  ${trace_id}
http:      ${http_code}
passed:    ${passed}
time:      ${elapsed}s
finish:    ${finish}
issues:    ${issues[*]:-none}
EOF

    if $passed; then
        _ok "PASS: ${test_name}"
    else
        _fail "FAIL: ${test_name}"
    fi
    echo ""

    $passed
}

# ---- monitor (background) ----

start_monitor() {
    local test_name=$1
    local out="${RESULT_DIR}/${test_name}-monitor.log"
    _log "Starting monitor → $out"
    {
        echo "=== Monitor start $(date) ==="
        while true; do
            echo "--- $(date +%H:%M:%S) ---"
            echo "RTX slots:"; curl -s -m 2 http://localhost:8080/slots 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d),'slots')" 2>/dev/null || echo "error"
            echo "P100 slots:"; curl -s -m 2 http://192.168.122.21:8086/slots 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'  slot {s[\"id\"]}: n_past={s.get(\"n_past\",0)} processing={s.get(\"is_processing\",False)}') for s in d]" 2>/dev/null || echo "error"
            echo "KV files:"; ls -lh /mnt/llm-ram/store/*.kv 2>/dev/null | wc -l | xargs -I{} echo "{} files" || echo "0 files"
            sleep 5
            # stop after 5 minutes
            [[ $(find "$out" -mmin +5 2>/dev/null) ]] && break
        done
    } > "$out" 2>&1 &
    echo $!
}

# ---- main ----

main() {
    local contexts="2000"
    local depth=50
    local bg=false

    while [[ $# -gt 0 ]]; do
        case $1 in
            -c|--context) contexts="$2"; shift 2 ;;
            -d|--depth)   depth="$2"; shift 2 ;;
            --bg)         bg=true; shift ;;
            *) _log "Unknown: $1"; exit 1 ;;
        esac
    done

    _log "Hydra NIAH Eval — P/D Split Verification"
    _log "Coordinator: ${COORD_URL}  Model: ${MODEL}"
    _log "Context sizes: ${contexts}  Needle depth: ${depth}%"

    # Verify Hydra is healthy
    local health
    health=$(curl -s -m 5 "${COORD_URL}/health" 2>/dev/null || echo '{"status":"down"}')
    if ! echo "$health" | python3 -c "import sys,json; d=json.load(sys.stdin); exit(0 if d.get('status')=='healthy' else 1)" 2>/dev/null; then
        _fail "Hydra Core not healthy: $health"
        exit 1
    fi
    _ok "Hydra Core healthy"

    local total=0 passed=0
    IFS=',' read -ra CTX_ARR <<< "$contexts"

    for ctx in "${CTX_ARR[@]}"; do
        ctx=$(echo "$ctx" | xargs)  # trim
        local name="niah-c${ctx}-d${depth}"
        ((total++))

        if $bg; then
            tmux new-session -d -s "hydra-eval-${name}" \
                "bash '${SCRIPT_DIR}/run-niah.sh' -c ${ctx} -d ${depth} 2>&1 | tee '${RESULT_DIR}/${name}-full.log'"
            _log "Launched tmux: hydra-eval-${name}"
        else
            local mon_pid
            mon_pid=$(start_monitor "$name")
            if run_niah_test "$ctx" "$depth" "$name"; then
                ((passed++))
            fi
            kill "$mon_pid" 2>/dev/null || true
            sleep 2
        fi
    done

    if ! $bg; then
        _log "Results: ${passed}/${total} passed"
        if (( passed == total )); then
            _ok "ALL TESTS PASSED"
        else
            _fail "SOME TESTS FAILED"
        fi
        _log "Results: ${RESULT_DIR}/"
        ls -la "$RESULT_DIR"/
    fi
}

main "$@"
