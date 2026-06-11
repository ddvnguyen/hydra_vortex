#!/usr/bin/env bash
# Hydra Eval: Needle-in-a-Haystack with llama-server source-of-truth verification.
#
# Verifies P/D split using llama-server metrics (quantitative) and
# llama-server logs (qualitative) — NOT just Hydra's own logs.
#
# Usage:
#   bash scripts/eval/run-niah.sh -c 2000 -d 50              # single test
#   bash scripts/eval/run-niah.sh -c 2000,5000,8000 -d 50     # sweep
#
# Prerequisites:
#   - Both llama-servers running with --metrics --log-verbosity 4
#   - RTX in router mode: model loaded via /models/load?model=balanced
#   - P100 with model pre-loaded: /health returns {"status":"ok"}
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULT_DIR="${RESULT_DIR:-/tmp/hydra-eval-results}"
COORD_URL="${COORD_URL:-http://localhost:9000}"
MODEL="${HYDRA_MODEL:-balanced}"
RTX_URL="${RTX_URL:-http://localhost:8080}"
P100_URL="${P100_URL:-http://192.168.122.21:8086}"
P100_SSH="${P100_SSH:-hydra-p100}"
P100_LOG_PATH="${P100_LOG_PATH:-hydra-logs/llama-p100.log}"

CHARS_PER_TOKEN=3.0

# ---- helpers ----

_log()  { echo "[$(date +%H:%M:%S)] $*" >&2; }
_die()  { echo "FATAL: $*" >&2; exit 1; }

parse_metric_val() {
    # Extract a float value from Prometheus text for a given metric name
    echo "$1" | grep "^${2}[{ ]" 2>/dev/null | awk '{print $NF}' | head -1
}

parse_metric_val_fallback() {
    local val
    val=$(parse_metric_val "$1" "$2")
    echo "${val:-0}"
}

# ---- prompt generator (same seed paras as before) ----

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
    local depth_pct=${2:-50}
    local passkey=${3:-}
    local target_chars=$((target_tokens * CHARS_PER_TOKEN / 1))
    local haystack="" total=0

    while (( total < target_chars )); do
        for p in "${_SEED_PARAS[@]}"; do
            (( total >= target_chars )) && break
            haystack+="$p "
            total=$((total + ${#p} + 1))
        done
    done
    haystack="${haystack:0:$target_chars}"

    if [[ -z "$passkey" ]]; then
        passkey="KEY-$(printf '%04X' $RANDOM)"
    fi

    local insert_pos=$(( ${#haystack} * depth_pct / 100 ))
    local before="${haystack:0:$insert_pos}"
    local after="${haystack:$insert_pos}"

    printf '%s\n\n' \
      "The secret access code for today is: ${passkey}" \
      "Below is reference documentation about software engineering topics. Read it carefully." \
      "${before}" \
      " IMPORTANT: The secret verification code is ${passkey}. You must remember this code exactly." \
      "${after}" \
      "" \
      "Question: Based on the information provided, what is the secret access code? Give ONLY the code."
}

# ---- capture: pre-test state ----

capture_pre_state() {
    _log "Capturing pre-test state..."

    # Metrics (parse immediately so they don't change)
    RTX_METRICS_PRE=$(curl -s -m 5 "${RTX_URL}/metrics" 2>/dev/null)
    P100_METRICS_PRE=$(curl -s -m 5 "${P100_URL}/metrics" 2>/dev/null)

    # Slots
    RTX_SLOTS_PRE=$(curl -s -m 5 "${RTX_URL}/slots" 2>/dev/null)
    P100_SLOTS_PRE=$(curl -s -m 5 "${P100_URL}/slots" 2>/dev/null)

    # Log position markers
    RTX_LOG_MARKER=$(date -u +"%Y-%m-%dT%H:%M:%S")
    P100_LOG_LINES_PRE=$(ssh -o ConnectTimeout=5 "${P100_SSH}" "wc -l < ${P100_LOG_PATH}" 2>/dev/null || echo "0")
    P100_LOG_LINES_PRE=${P100_LOG_LINES_PRE:-0}

    # Hydra Core log marker
    HYDRA_LOG_MARKER=$(date -u +"%Y-%m-%dT%H:%M:%S")
}

# ---- capture: poll logs during test ----

start_log_capture() {
    local test_name=$1
    _log "Starting background log poller..."

    # We'll poll logs every 2s during the test to catch slot transitions
    {
        local start=$(date +%s)
        local max_polls=150  # 5 min max
        local i=0
        while (( i < max_polls )); do
            # Only poll if the main test is still running
            if [[ ! -f "/tmp/hydra-test-${test_name}.running" ]]; then
                break
            fi
            local ts=$(date +%H:%M:%S)
            echo "--- $ts ---"

            # RTX slots + request log excerpt
            echo "RTX slots:"
            curl -s -m 2 "${RTX_URL}/slots" 2>/dev/null | python3 -c "
import sys,json
try:
    slots=json.load(sys.stdin)
    for s in slots:
        print(f'  slot {s[\"id\"]}: n_past={s.get(\"n_past\",0)} processing={s.get(\"is_processing\",False)}')
except: print('  parse_error')" 2>/dev/null

            # P100 slots
            echo "P100 slots:"
            curl -s -m 2 "${P100_URL}/slots" 2>/dev/null | python3 -c "
import sys,json
try:
    slots=json.load(sys.stdin)
    for s in slots:
        print(f'  slot {s[\"id\"]}: n_past={s.get(\"n_past\",0)} processing={s.get(\"is_processing\",False)}')
except: print('  parse_error')" 2>/dev/null

            echo ""
            sleep 2
            i=$((i+1))
        done
        echo "--- poller stopped ($i polls) ---"
    } > "${RESULT_DIR}/${test_name}-slot-timeline.txt" 2>&1 &
}

# ---- capture: post-test state + logs ----

capture_post_state() {
    _log "Capturing post-test state..."

    RTX_METRICS_POST=$(curl -s -m 5 "${RTX_URL}/metrics" 2>/dev/null)
    P100_METRICS_POST=$(curl -s -m 5 "${P100_URL}/metrics" 2>/dev/null)
    RTX_SLOTS_POST=$(curl -s -m 5 "${RTX_URL}/slots" 2>/dev/null)
    P100_SLOTS_POST=$(curl -s -m 5 "${P100_URL}/slots" 2>/dev/null)

    # Extract logs since test start
    _log "Extracting llama-server logs..."

    # RTX container logs since marker
    podman logs llama-cpp --since "${RTX_LOG_MARKER}" 2>&1 \
        | grep -iE "slot|state|request|hydra|perf|completion|prompt|decode|eval|launch|update_slots" \
        > "${RESULT_DIR}/${CURRENT_TEST}-rtx-llama-logs.txt" 2>/dev/null || true

    # P100 log file since marker
    ssh -o ConnectTimeout=5 "${P100_SSH}" \
        "tail -n +$((P100_LOG_LINES_PRE + 1)) ${P100_LOG_PATH} 2>/dev/null | grep -iE 'slot|state|request|hydra|perf|completion|prompt|decode|eval|launch|update_slots'" \
        > "${RESULT_DIR}/${CURRENT_TEST}-p100-llama-logs.txt" 2>/dev/null || true

    # Hydra Core logs since marker
    podman logs hydra-core --since "${HYDRA_LOG_MARKER}" 2>&1 \
        | grep -iE "routing|cold_route|prefill|save_kv|restore_kv|request_timeline|decode|bg_save|session_type" \
        > "${RESULT_DIR}/${CURRENT_TEST}-hydra-logs.txt" 2>/dev/null || true
}

# ---- verify: P/D split using llama-server metrics as source of truth ----

verify_pd_split() {
    local test_name=$1
    local expected_prompt_tokens=$2
    local issues=()

    # --- Parse pre metrics ---
    local rtx_ppt_pre rtx_tpt_pre p100_ppt_pre p100_tpt_pre
    rtx_ppt_pre=$(parse_metric_val_fallback "$RTX_METRICS_PRE" "llamacpp:prompt_tokens_total")
    rtx_tpt_pre=$(parse_metric_val_fallback "$RTX_METRICS_PRE" "llamacpp:tokens_predicted_total")
    p100_ppt_pre=$(parse_metric_val_fallback "$P100_METRICS_PRE" "llamacpp:prompt_tokens_total")
    p100_tpt_pre=$(parse_metric_val_fallback "$P100_METRICS_PRE" "llamacpp:tokens_predicted_total")

    # --- Parse post metrics ---
    local rtx_ppt_post rtx_tpt_post p100_ppt_post p100_tpt_post
    rtx_ppt_post=$(parse_metric_val_fallback "$RTX_METRICS_POST" "llamacpp:prompt_tokens_total")
    rtx_tpt_post=$(parse_metric_val_fallback "$RTX_METRICS_POST" "llamacpp:tokens_predicted_total")
    p100_ppt_post=$(parse_metric_val_fallback "$P100_METRICS_POST" "llamacpp:prompt_tokens_total")
    p100_tpt_post=$(parse_metric_val_fallback "$P100_METRICS_POST" "llamacpp:tokens_predicted_total")

    # --- Deltas ---
    local rtx_ppt_d=$((rtx_ppt_post - rtx_ppt_pre))
    local rtx_tpt_d=$((rtx_tpt_post - rtx_tpt_pre))
    local p100_ppt_d=$((p100_ppt_post - p100_ppt_pre))
    local p100_tpt_d=$((p100_tpt_post - p100_tpt_pre))

    # Round down floats
    rtx_ppt_d=${rtx_ppt_d%.*}
    rtx_tpt_d=${rtx_tpt_d%.*}
    p100_ppt_d=${p100_ppt_d%.*}
    p100_tpt_d=${p100_tpt_d%.*}

    _log "Metrics deltas: RTX ppt=+${rtx_ppt_d} tpt=+${rtx_tpt_d} | P100 ppt=+${p100_ppt_d} tpt=+${p100_tpt_d}"

    # --- Verification criteria ---

    # 1. RTX must have processed many prompt tokens (prefill happened)
    local ppt_verdict rtx_ppt_icon
    if (( rtx_ppt_d > 100 )); then
        ppt_verdict="RTX prefilled (Δ=+${rtx_ppt_d}), P100 had cache hit (Δ=+${p100_ppt_d})"
        rtx_ppt_icon="✓"
    else
        ppt_verdict="RTX prompt_tokens delta too small (Δ=+${rtx_ppt_d})"
        issues+=("RTX prompt_tokens delta too small (+${rtx_ppt_d}, expected >100)")
        rtx_ppt_icon="✗"
    fi

    # 2. P100 should NOT have processed many prompt tokens (no re-prefill, KV cache hit)
    local p100_ppt_icon
    if (( p100_ppt_d <= 5 )); then
        p100_ppt_icon="✓"
    else
        issues+=("P100 re-prefilled (+${p100_ppt_d} prompt tokens — KV cache NOT used)")
        p100_ppt_icon="✗"
    fi

    # 3. RTX should NOT have generated many tokens (only 1-token prefill, not decode)
    local rtx_tpt_icon
    if (( rtx_tpt_d <= 5 )); then
        rtx_tpt_icon="✓"
    else
        issues+=("RTX decoded (+${rtx_tpt_d} tokens — RTX did the decode, not P100)")
        rtx_tpt_icon="✗"
    fi

    # 4. P100 must have generated tokens (actual decode)
    local p100_tpt_icon
    if (( p100_tpt_d >= 3 )); then
        p100_tpt_icon="✓"
    else
        issues+=("P100 didn't decode (+${p100_tpt_d} tokens — no generation on P100)")
        p100_tpt_icon="✗"
    fi

    # --- Slot state verification ---
    local rtx_slots_pre=$(echo "$RTX_SLOTS_PRE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d))" 2>/dev/null || echo "?")
    local rtx_slots_post=$(echo "$RTX_SLOTS_POST" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d))" 2>/dev/null || echo "?")

    local p100_n_past_pre=$(echo "$P100_SLOTS_PRE" | python3 -c "import sys,json; d=json.load(sys.stdin); s=d[0] if d else {}; print(s.get('n_past',0))" 2>/dev/null || echo "0")
    local p100_n_past_post=$(echo "$P100_SLOTS_POST" | python3 -c "import sys,json; d=json.load(sys.stdin); s=d[0] if d else {}; print(s.get('n_past',0))" 2>/dev/null || echo "0")

    # Check that P100 n_past increased (KV was restored)
    local slot_icon
    if (( ${p100_n_past_post:-0} > 100 )); then
        slot_icon="✓"
    else
        slot_icon="⚠ n_past=${p100_n_past_post} (may not have restored)"
    fi

    # --- Result ---
    local pass=true
    if (( ${#issues[@]} > 0 )); then
        pass=false
    fi

    # Return results via global variables (bash doesn't support returning structs)
    RTX_PPT_PRE=$rtx_ppt_pre;  RTX_PPT_POST=$rtx_ppt_post;  RTX_PPT_D=$rtx_ppt_d
    RTX_TPT_PRE=$rtx_tpt_pre;  RTX_TPT_POST=$rtx_tpt_post;  RTX_TPT_D=$rtx_tpt_d
    P100_PPT_PRE=$p100_ppt_pre; P100_PPT_POST=$p100_ppt_post; P100_PPT_D=$p100_ppt_d
    P100_TPT_PRE=$p100_tpt_pre; P100_TPT_POST=$p100_tpt_post; P100_TPT_D=$p100_tpt_d
    PD_PASS=$pass
    PD_ISSUES="${issues[*]:-none}"
    PPT_VERDICT="$ppt_verdict"
    PPT_ICONS="${rtx_ppt_icon}/${p100_ppt_icon}"
    TPT_ICONS="${rtx_tpt_icon}/${p100_tpt_icon}"
    RTX_PPT_ICON="$rtx_ppt_icon";  P100_PPT_ICON="$p100_ppt_icon"
    RTX_TPT_ICON="$rtx_tpt_icon";  P100_TPT_ICON="$p100_tpt_icon"
    SLOT_VERDICT="$slot_icon"
    P100_NPAST_POST=$p100_n_past_post
}

# ---- generate MD report ----

generate_md_report() {
    local test_name=$1
    local context_tokens=$2
    local depth_pct=$3
    local passkey=$4
    local timestamp=$5
    local http_code=$6
    local elapsed=$7
    local resp_file=$8

    # Parse response
    local finish content reasoning prompt_tokens completion_tokens cached_tokens
    local prompt_ms cache_n id_slot model fingerprint
    local reasoning_len content_len reasoning_preview content_preview
    IFS='|' read -r finish content reasoning prompt_tokens completion_tokens cached_tokens prompt_ms cache_n id_slot model fingerprint reasoning_len content_len reasoning_preview content_preview <<< \
        "$(python3 -c "
import json
d=json.load(open('${resp_file}'))
c=d['choices'][0]
msg=c['message']
u=d.get('usage',{})
t=d.get('timings',{})
ptd=u.get('prompt_tokens_details',{})
fin=c.get('finish_reason','?')
cont=msg.get('content','') or ''
reas=msg.get('reasoning_content','') or ''
# Escape for shell: replace newlines/tabs
cont_esc=cont.replace(chr(10),' ').replace(chr(9),' ').replace('|','/')
reas_esc=reas.replace(chr(10),' ').replace(chr(9),' ').replace('|','/')
print(f'{fin}|{cont_esc[:200]}|{reas_esc[:200]}|{u.get(\"prompt_tokens\",\"?\")}|{u.get(\"completion_tokens\",\"?\")}|{ptd.get(\"cached_tokens\",\"?\")}|{t.get(\"prompt_ms\",\"?\")}|{t.get(\"cache_n\",\"?\")}|{d.get(\"id_slot\",\"?\")}|{d.get(\"model\",\"?\")[:50]}|{d.get(\"system_fingerprint\",\"?\")}|{len(reas)}|{len(cont)}|{reas_esc[:100]}|{cont_esc[:100]}')
" 2>/dev/null)"

    # Verify passkey in both content and reasoning
    local passkey_result
    local passkey_in_content="✗"
    local passkey_in_reasoning="✗"
    if echo "${content}" | grep -qi "$passkey"; then
        passkey_in_content="✓"
    fi
    if echo "${reasoning}" | grep -qi "$passkey"; then
        passkey_in_reasoning="✓"
    fi
    if [[ "$passkey_in_content" == "✓" || "$passkey_in_reasoning" == "✓" ]]; then
        passkey_result="✓ Found: \`${passkey}\` (content=${passkey_in_content}, reasoning=${passkey_in_reasoning})"
    else
        passkey_result="✗ Not found in content or reasoning"
    fi

    # Validate reasoning_content (thinking models must produce reasoning)
    local reasoning_verdict="✗ EMPTY"
    reasoning_len=${reasoning_len:-0}
    if (( reasoning_len > 50 )); then
        reasoning_verdict="✓ ${reasoning_len} chars (thinking model produced reasoning)"
    elif (( reasoning_len > 0 )); then
        reasoning_verdict="⚠ ${reasoning_len} chars (too short for thinking model)"
    fi

    # Validate content (must have actual response, not just reasoning)
    local content_verdict="✗ EMPTY"
    content_len=${content_len:-0}
    if (( content_len > 10 )); then
        content_verdict="✓ ${content_len} chars"
    elif (( content_len > 0 )); then
        content_verdict="⚠ ${content_len} chars (very short)"
    fi

    # Check if finish_reason is 'stop' (natural completion) vs 'length' (truncated)
    local finish_verdict="✗ truncated (max_tokens too low)"
    if [[ "$finish" == "stop" ]]; then
        finish_verdict="✓ natural completion"
    fi

    # Llama-server log excerpts
    local rtx_log_file="${RESULT_DIR}/${test_name}-rtx-llama-logs.txt"
    local p100_log_file="${RESULT_DIR}/${test_name}-p100-llama-logs.txt"
    local hydra_log_file="${RESULT_DIR}/${test_name}-hydra-logs.txt"

    local rtx_logs="(no logs captured)"
    [[ -f "$rtx_log_file" ]] && rtx_logs=$(head -25 "$rtx_log_file" 2>/dev/null)
    local p100_logs="(no logs captured)"
    [[ -f "$p100_log_file" ]] && p100_logs=$(head -25 "$p100_log_file" 2>/dev/null)

    # Hydra timeline
    local hydra_timeline=""
    [[ -f "$hydra_log_file" ]] && hydra_timeline=$(grep "request_timeline" "$hydra_log_file" 2>/dev/null | head -1)

    # PT/TT verdict — include content/reasoning validation
    local overall="✓ **PASS**"
    local issues_list=()
    if ! $PD_PASS; then
        issues_list+=("P/D split failed: ${PD_ISSUES}")
    fi
    if [[ "$reasoning_verdict" == *"EMPTY"* ]]; then
        issues_list+=("reasoning_content is empty (thinking model should produce reasoning)")
    fi
    if [[ "$content_verdict" == *"EMPTY"* ]]; then
        issues_list+=("content is empty (model should produce final answer)")
    fi
    if [[ "$finish" != "stop" ]]; then
        issues_list+=("finish_reason=${finish} (expected 'stop', got truncated)")
    fi
    if (( ${#issues_list[@]} > 0 )); then
        local issues_str=""
        for issue in "${issues_list[@]}"; do
            issues_str+="- ${issue}\n"
        done
        overall="✗ **FAIL**\n\n${issues_str}"
    fi

    # Generate MD
    cat <<MDEOF
## Eval Test: NIAH-${context_tokens} (${timestamp})

| Field | Value |
|-------|-------|
| **Prompt size** | ${context_tokens} tokens (~$((context_tokens * 3)) chars) |
| **Needle depth** | ${depth_pct}% |
| **Passkey** | \`${passkey}\` |
| **Model** | ${model} |
| **Fingerprint** | ${fingerprint} |
| **Expected** | RTX prefill → KV save → P100 KV restore → P100 decode |

---

### llama-server Metrics (Ground Truth)

| Metric | RTX Pre | RTX Post | RTX Δ | P100 Pre | P100 Post | P100 Δ | Check |
|--------|---------|----------|-------|----------|-----------|--------|-------|
| \`prompt_tokens_total\` | ${RTX_PPT_PRE} | ${RTX_PPT_POST} | **+${RTX_PPT_D}** | ${P100_PPT_PRE} | ${P100_PPT_POST} | **+${P100_PPT_D}** | ${PPT_ICONS} |
| \`tokens_predicted_total\` | ${RTX_TPT_PRE} | ${RTX_TPT_POST} | **+${RTX_TPT_D}** | ${P100_TPT_PRE} | ${P100_TPT_POST} | **+${P100_TPT_D}** | ${TPT_ICONS} |

**Interpretation:**
- RTX prompt_tokens +${RTX_PPT_D}: ${RTX_PPT_ICON} prefill happened on RTX
- P100 prompt_tokens +${P100_PPT_D}: ${P100_PPT_ICON} no re-prefill on P100 (KV cache hit)
- RTX tokens_predicted +${RTX_TPT_D}: ${RTX_TPT_ICON} RTX did NOT decode (only 1-token prefill completion)
- P100 tokens_predicted +${P100_TPT_D}: ${P100_TPT_ICON} P100 DID decode

---

### llama-server Slot State

| Node | Pre-test | Post-test |
|------|----------|-----------|
| RTX | $(echo "$RTX_SLOTS_PRE" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'slot {s[\"id\"]}: n_past={s.get(\"n_past\",0)} proc={s.get(\"is_processing\",False)}') for s in d]") | $(echo "$RTX_SLOTS_POST" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'slot {s[\"id\"]}: n_past={s.get(\"n_past\",0)} proc={s.get(\"is_processing\",False)}') for s in d]") |
| P100 | $(echo "$P100_SLOTS_PRE" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'slot {s[\"id\"]}: n_past={s.get(\"n_past\",0)} proc={s.get(\"is_processing\",False)}') for s in d]") | $(echo "$P100_SLOTS_POST" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'slot {s[\"id\"]}: n_past={s.get(\"n_past\",0)} proc={s.get(\"is_processing\",False)}') for s in d]") |

**Slot verdict:** ${SLOT_VERDICT} (P100 n_past=${P100_NPAST_POST})

---

### llama-server Logs — RTX \`:8080\`

\`\`\`
${rtx_logs}
\`\`\`

Full log: \`${rtx_log_file}\`

---

### llama-server Logs — P100 \`:8086\`

\`\`\`
${p100_logs}
\`\`\`

Full log: \`${p100_log_file}\`

---

### Hydra Core Timeline

\`\`\`
${hydra_timeline}
\`\`\`

Full log: \`${hydra_log_file}\`

---

### Response Quality

| Check | Result |
|-------|--------|
| HTTP | ${http_code} |
| Time | ${elapsed}s |
| finish_reason | ${finish} (${finish_verdict}) |
| prompt_tokens | ${prompt_tokens} |
| completion_tokens | ${completion_tokens} |
| cached_tokens | ${cached_tokens} |
| prompt_ms | ${prompt_ms}ms |
| cache_n | ${cache_n} |
| **reasoning_content** | ${reasoning_verdict} |
| **content** | ${content_verdict} |
| Passkey recall | ${passkey_result} |
| Content preview | ${content_preview} |
| Reasoning preview | ${reasoning_preview} |

---

### Overall: $(echo -e "$overall")

MDEOF
}

# ---- main test runner ----

run_single_test() {
    local context_tokens=$1
    local depth_pct=$2
    local test_name=$3
    local timestamp
    timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

    # Generate passkey and prompt
    local passkey="NIAH-$(printf '%04X' $((RANDOM % 65536)))"

    _log "=== Test: ${test_name} ==="
    _log "Context: ${context_tokens} tokens, Depth: ${depth_pct}%, Passkey: ${passkey}"

    CURRENT_TEST="$test_name"
    mkdir -p "$RESULT_DIR"

    # Generate prompt via Python for proper JSON escaping
    python3 -c "
paras = [$(printf '"%s",' "${_SEED_PARAS[@]}" | sed 's/,$//')]
target = ${context_tokens}
depth = ${depth_pct}
pk = '${passkey}'
tc = target * int(${CHARS_PER_TOKEN})
hs = ''
while len(hs) < tc:
    for p in paras:
        if len(hs) >= tc: break
        hs += p + ' '
hs = hs[:tc]
ip = len(hs) * depth // 100
needle = f' IMPORTANT: The secret verification code is {pk}. You must remember this code exactly.'
prompt = f'The secret access code for today is: {pk}\n\nBelow is reference documentation about software engineering topics. Read it carefully.\n\n{hs[:ip]}{needle}{hs[ip:]}\n\nQuestion: Based on the information provided, what is the secret access code? Give ONLY the code.'
import json
with open('${RESULT_DIR}/${test_name}-body.json','w') as f:
    json.dump({'model':'${MODEL}','messages':[{'role':'user','content':prompt}],'max_tokens':1000,'temperature':0,'stream':False}, f)
with open('${RESULT_DIR}/${test_name}-passkey.txt','w') as f:
    f.write(pk)
print(f'prompt_chars={len(prompt)}')
" 2>/dev/null

    # ========== PRE-TEST CAPTURE ==========
    capture_pre_state

    # ========== START LOG POLLER ==========
    touch "/tmp/hydra-test-${test_name}.running"
    start_log_capture "$test_name"

    # ========== SEND REQUEST ==========
    _log "Sending completion to Hydra Core..."
    local trace_id="niah-${test_name}-$(date +%s)"
    local http_code elapsed

    http_code=$(curl -s -w '%{http_code}' -o "${RESULT_DIR}/${test_name}-response.json" \
        --max-time 300 \
        -X POST "${COORD_URL}/v1/chat/completions" \
        -H "Content-Type: application/json" \
        -H "X-Hydra-Trace-Id: ${trace_id}" \
        -d "@${RESULT_DIR}/${test_name}-body.json" \
        -w '\n%{time_total}' 2>/dev/null | tail -1)

    elapsed=${http_code##*$'\n'}
    http_code=${http_code%%$'\n'*}
    elapsed="${elapsed:-?}"

    _log "Response: HTTP ${http_code} in ${elapsed}s"

    # ========== POST-TEST CAPTURE ==========
    rm -f "/tmp/hydra-test-${test_name}.running"
    sleep 1  # Let poller finish

    capture_post_state

    # ========== VERIFY ==========
    verify_pd_split "$test_name" "$context_tokens"

    # ========== GENERATE REPORT ==========
    local md_file="${RESULT_DIR}/${test_name}-report.md"
    local md_content
    md_content=$(generate_md_report "$test_name" "$context_tokens" "$depth_pct" "$passkey" "$timestamp" "$http_code" "$elapsed" "${RESULT_DIR}/${test_name}-response.json")

    echo "$md_content" > "$md_file"
    echo "$md_content"

    _log "Report: $md_file"
}

# ---- main ----

main() {
    local contexts="2000"
    local depth=50

    while [[ $# -gt 0 ]]; do
        case $1 in
            -c|--context) contexts="$2"; shift 2 ;;
            -d|--depth)   depth="$2"; shift 2 ;;
            *) _log "Unknown flag: $1"; exit 1 ;;
        esac
    done

    _log "Hydra NIAH Eval — llama-server Source-of-Truth Verification"
    _log "  RTX: ${RTX_URL}  P100: ${P100_URL}  Coordinator: ${COORD_URL}"

    # Verify all services up
    for url in "${RTX_URL}/health" "${P100_URL}/health" "${COORD_URL}/health"; do
        local status
        status=$(curl -s -m 5 "$url" 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('status',d.get('error','down')))" 2>/dev/null || echo "down")
        _log "  ${url}: ${status}"
    done

    # Ensure RTX model is loaded
    local rtx_slots
    rtx_slots=$(curl -s "${RTX_URL}/slots" 2>/dev/null | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "0")
    if [[ "$rtx_slots" == "0" ]]; then
        _log "RTX has 0 slots — loading model..."
        curl -s -X POST "${RTX_URL}/models/load" -H "Content-Type: application/json" -d '{"model":"balanced"}' > /dev/null
        sleep 5
    fi

    local total=0 passed=0
    IFS=',' read -ra CTX_ARR <<< "$contexts"

    for ctx in "${CTX_ARR[@]}"; do
        ctx=$(echo "$ctx" | xargs)
        local name="niah-c${ctx}-d${depth}"
        total=$((total+1))

        run_single_test "$ctx" "$depth" "$name"
        if $PD_PASS; then passed=$((passed+1)); fi
        sleep 3  # Cool-down between tests
    done

    _log "============================================"
    _log "Results: ${passed}/${total} passed"
    _log "Reports: ${RESULT_DIR}/*-report.md"
    _log "============================================"
}

main "$@"
