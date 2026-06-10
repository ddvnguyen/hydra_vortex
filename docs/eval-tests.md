# Hydra P/D Split Eval Tests

Verification that RTX prefill → KV save → P100 decode → KV restore works correctly.
Three test tiers: NIAH (retrieval), Perplexity (distribution), Math (reasoning).

## Architecture Under Test

```
Client (curl) → Hydra Core :9000
  → Router → RTX prefill (n_predict=0, stream=false)
  → SaveKv → RPC StateGet(llama :9503) → RPC Put(Store :9500)
  → PickDecode → P100
  → RestoreKv → RPC Get(Store :9500) → RPC StatePut(llama :9502)
  → Decode → streaming/non-streaming response
```

All KV state operations use binary Hydra RPC (ports 9500/9502/9503), not HTTP.
The Store persists KV blobs at `HYDRA_STORE_DIR` (/mnt/llm-ram/store in the container).

## Tier 1: Needle-in-a-Haystack (NIAH)

Buries a secret passkey in a long prompt. After KV migration, verifies the model
can still retrieve it. A correct retrieval proves the KV cache was transferred
bit-identical.

### Run

```bash
# Single test at 2K tokens
bash scripts/eval/run-niah.sh -c 2000 -d 50

# Sweep: 2K, 5K, 8K context at 50% needle depth
bash scripts/eval/run-niah.sh -c 2000,5000,8000 -d 50

# Background via tmux
bash scripts/eval/run-niah.sh -c 2000,5000,8000 -d 50 --bg
```

### Verification

- HTTP 200 + finish_reason=stop
- Response contains the exact passkey string
- P100 llama metrics: `prompt_tokens_seconds` ≈ 0 (no re-prefill)
- Hydra Core logs: `event=request_timeline ... save_kv_ms=... restore_kv_ms=...`

## Tier 2: Perplexity Comparison

Compares model perplexity on the same corpus through direct RTX vs P/D split path.
Identical scores prove KV cache didn't alter the output distribution.

### Prerequisites

```bash
# Download WikiText-2 test set (~2.5 MB)
wget -q -O /tmp/wikitext-test.txt \
  https://huggingface.co/datasets/mindchain/wikitext2/resolve/main/wikitext-test.txt
# Strip Wiki markup headers
sed -i '/^ = /d' /tmp/wikitext-test.txt
```

### Run

```bash
bash scripts/eval/run-perplexity.sh --baseline          # RTX direct
bash scripts/eval/run-perplexity.sh --via-coordinator    # Through Hydra P/D split
bash scripts/eval/run-perplexity.sh --compare            # Diff the two
```

### Verification

- Perplexity scores must be identical (no divergence)
- P100 prompt_ms < 5000 (cached path, not full re-prefill)
- Token-level logits unchanged

## Tier 3: Math Reasoning (lm-evaluation-harness)

Runs GSM8K math problems through Hydra's P/D split. Multi-step reasoning chains
will break if KV cache is corrupted.

### Install

```bash
pip install lm_eval[api]
```

### Run

```bash
lm_eval --model local-chat-completions \
  --model_args model=qwen35moe,base_url=http://localhost:9000/v1/chat/completions \
  --tasks gsm8k --batch_size 1 --limit 20
```

### Verification

- Accuracy matches or exceeds direct llama-server baseline
- No garbled / nonsensical completions

## Monitoring During Tests

### Grafana (recommended)

```
http://localhost:3000 → Hydra Timeline dashboard
```
Shows per-request phase durations (prefill_ms, save_kv_ms, restore_kv_ms, decode_ms).

### Live Logs

```bash
# Hydra Core scheduler events (P/D split phases)
podman logs -f hydra-core 2>&1 | grep -E "routing|save_kv|restore_kv|request_timeline|prefill|decode|state_trans"

# RTX llama-server
podman logs -f llama-cpp 2>&1 | grep -E "hydra|slot|state|STATE|n_past"

# P100 llama-server
ssh hydra-p100 'journalctl --user -u llama-p100 -f --no-pager | grep -E "hydra|slot|state"'
```

### Metrics Endpoints

```bash
# Hydra Core metrics
curl -s http://localhost:9000/metrics | grep -E "hydra_save_kv|hydra_restore_kv|hydra_prefill|hydra_decode|hydra_mix_precision"

# RTX llama
curl -s http://localhost:8080/metrics | grep -E "tokens|requests"

# P100 llama
curl -s http://192.168.122.21:8086/metrics | grep -E "tokens|requests"
```

### Quick Health Check While Running

```bash
watch -n 2 '
echo "=== Core ===" && curl -s http://localhost:9000/health 2>/dev/null | python3 -c "
import sys,json; d=json.load(sys.stdin)
n=d[\"nodes\"]
print(f\"rtx healthy={n[\"rtx\"][\"healthy\"]} idle={n[\"rtx\"][\"slots_idle\"]}\")
print(f\"p100 healthy={n[\"p100\"][\"healthy\"]} idle={n[\"p100\"][\"slots_idle\"]}\")
" 2>/dev/null
echo "=== RTX slots ===" && curl -s http://localhost:8080/slots 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); print(f\"{len(d)} slots, n_past={[s.get(\"n_past\",0) for s in d]}\")" 2>/dev/null
echo "=== P100 slots ===" && curl -s http://192.168.122.21:8086/slots 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); print(f\"{len(d)} slots, n_past={[s.get(\"n_past\",0) for s in d]}\")" 2>/dev/null
echo "=== Token deltas ===" && echo -n "RTX: " && curl -s http://localhost:8080/metrics 2>/dev/null | grep "^llamacpp:prompt_tokens_total\|^llamacpp:tokens_predicted_total" | tr "\n" " " && echo "" && echo -n "P100: " && curl -s http://192.168.122.21:8086/metrics 2>/dev/null | grep "^llamacpp:prompt_tokens_total\|^llamacpp:tokens_predicted_total" | tr "\n" " " && echo ""
echo "=== Last test report ===" && ls -lt /tmp/hydra-eval-results/*report* 2>/dev/null | head -1
'
```

## P100 Recovery

If P100 `/v1/chat/completions` hangs (returns empty after timeout) or RPC StatePut
times out, the P100 GPU is likely in a CUDA kernel hang. The systemd service was
SIGTERM-killed mid-inference, leaving the GPU in an unrecoverable state.

### Symptoms
- `curl http://192.168.122.21:8086/health` → `{"status":"ok"}` (health fine)
- `curl http://192.168.122.21:8086/slots` → slots exist with valid n_past
- `curl http://192.168.122.21:8086/v1/chat/completions` → **hangs** (no response)
- `ssh hydra-p100 'journalctl --user -u llama-p100'` → `Failed with result 'timeout'`
- P100 GPU-Util = 0%, but model VRAM still allocated (~12GB)
- Core logs: `restore_kv_start` followed by no `state_restored` for 60+ seconds

### Recovery procedure
```bash
# 1. Stop P100 (may take 30s due to stuck CUDA kernel)
ssh hydra-p100 'systemctl --user stop llama-p100'

# 2. Wait for GPU cleanup (GPU memory must drop to near 0)
sleep 10
ssh hydra-p100 nvidia-smi | grep "MiB"

# 3. Start P100 + wait for model (~90s)
ssh hydra-p100 'systemctl --user start llama-p100'
# Monitor: curl -s http://192.168.122.21:8086/health

# 4. Verify completions work
curl -s -m15 http://192.168.122.21:8086/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"balanced","messages":[{"role":"user","content":"2+2"}],"max_tokens":2}'
# Should return JSON with choices[0].message.content

# 5. Restart Core to clear stuck classifier
podman restart hydra-core
```

## Direct llama-server Testing (bypassing Core)

When the P/D split infrastructure is unstable or you want to isolate content quality
from routing issues, test directly against llama-server:

### Single-node RTX test
```bash
RKEY="NIAH-$(printf '%04X' $RANDOM)"
echo "Passkey: $RKEY"

# Build prompt
python3 -c "
paras = ['Software engineering...','Database indexing...','Container orchestration...']
target = 2000; depth = 50; pk = '$RKEY'
hs = ' '.join(paras * (target * 3 // sum(len(p) for p in paras)))
ip = len(hs) * depth // 100
prompt = f'Code: {pk}\n\n{hs[:ip]} IMPORTANT: code is {pk}. {hs[ip:]}\n\nWhat is the code?'
import json; open('/tmp/niah-body.json','w').write(json.dumps({'model':'balanced','messages':[{'role':'user','content':prompt}],'max_tokens':100,'temperature':0}))
"

# Send to RTX directly
curl -s http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" -d @/tmp/niah-body.json | python3 -c "
import sys,json
d=json.load(sys.stdin)
msg=d['choices'][0]['message']; u=d['usage']
cont=msg.get('content',''); reas=msg.get('reasoning_content','')
print(f'prompt={u[\"prompt_tokens\"]} comp={u[\"completion_tokens\"]} cached={u.get(\"prompt_tokens_details\",{}).get(\"cached_tokens\",0)}')
print(f'content ({len(cont)}c): [{cont}]')
print(f'reasoning ({len(reas)}c): [{reas[:500]}]')
print(f'PASSKEY: {\"✓ FOUND\" if \"$RKEY\" in (cont+reas) else \"✗ NOT FOUND\"}')
"
```

### Checking model output quality
The Qwen35MoE model with `--reasoning on` produces `reasoning_content` (thinking)
separately from `content` (final answer). With insufficient `max_tokens`, the model
exhausts all tokens in reasoning and produces empty content. For NIAH tests:

| Context | Recommended max_tokens | Why |
|---------|----------------------|-----|
| 1K tokens | 80 | Reasoning + answer fits |
| 2K tokens | 100-120 | Reasoning takes ~70 tokens, leaves room for content |
| 5K tokens | 200 | Longer context → longer reasoning chain |
| 8K+ tokens | 300+ | Substantial reasoning needed |

Always check BOTH `content` AND `reasoning_content` for the passkey.
An empty `content` with the passkey in `reasoning_content` is a PASS —
the model recalled correctly; it just needs more tokens to finalize the answer.

### Verifying P/D split independently
```bash
# Snapshot metrics before test
curl -s http://localhost:8080/metrics | grep "^llamacpp:prompt_tokens_total " > /tmp/pre-rtx-ppt.txt
curl -s http://192.168.122.21:8086/metrics | grep "^llamacpp:tokens_predicted_total " > /tmp/pre-p100-tpt.txt

# Run test via Core
curl -s http://localhost:9000/v1/chat/completions -d @/tmp/niah-body.json

# Check deltas — RTX ppt should be >100, P100 tpt should be >=3
echo "RTX prefill: +$(($(curl -s http://localhost:8080/metrics|grep '^llamacpp:prompt_tokens_total '|awk '{print int($NF)}') - $(cat /tmp/pre-rtx-ppt.txt|awk '{print int($NF)}')))"
echo "P100 decode: +$(($(curl -s http://192.168.122.21:8086/metrics|grep '^llamacpp:tokens_predicted_total '|awk '{print int($NF)}') - $(cat /tmp/pre-p100-tpt.txt|awk '{print int($NF)}')))"
```

## Test Data

Test prompts are generated from `_SEED_PARAS` (55 technical paragraphs covering
distributed systems, ML, databases, etc.) at ~3 chars/token. Passkeys are random
8-character hex strings.

## Verified Test Run (2026-06-09)

### 2K Token NIAH — P/D Split **PASS** via llama-server Metrics

Full report: `tests/results/niah-2k-verified-2026-06-09.md`

**llama-server Metrics (Ground Truth):**

| Metric | RTX Δ | P100 Δ | Meaning |
|--------|-------|--------|---------|
| `prompt_tokens_total` | **+958** | **+1** | RTX prefilled 958 tokens; P100 processed 1 (cache hit) |
| `tokens_predicted_total` | **+1** | **+200** | RTX generated 1 token (prefill completion only); P100 decoded 200 |

**llama-server Logs — RTX:**
- `slot launch_slot_: id 1 | task 81 | n_tokens = 958`
- `prompt eval time = 3530.34 ms / 958 tokens (271.36 tok/s)`
- `eval time = 0.00 ms / 1 tokens` — only the single prefill token

**llama-server Logs — P100 (definitive KV migration proof):**
- `PUT /slots/0/state 192.168.122.1 200` — KV state upload received
- `hydra: STATE_PUT slot=0 restored=76306884 B n_past=958 n_prompt_tok=958` — **76 MB state restored with 958 prompt tokens!**
- `restored context checkpoint (n_tokens = 958, size = 62.813 MiB)` — checkpoint reconstructed
- `cached n_tokens = 957, memory_seq_rm [957, end)` — 957 tokens served from cache
- `prompt eval time = 63.06 ms / 1 tokens` — only 1 new token processed (vs 3.5s full prefill)
- `eval time = 7217.02 ms / 200 tokens (27.71 tok/s)` — decode at 28 tok/s
- `graphs reused = 395` — CUDA graphs reused from restore

**Hydra Core Timeline:**
```
prefill_ms=3555 save_kv_ms=3710 restore_kv_ms=3913 decode_ms=11218 total_ms=11218
```

**P/D Split Confirmed** — all 4 criteria met on independent llama-server metrics.

### Content Quality Verdict (Dual-Verdict Format)

Every test run produces TWO independent verdicts. Content quality is equally important
as P/D split correctness.

#### Current Status (2026-06-09)

| Check | Result | Detail |
|-------|--------|--------|
| **P/D Split** | ✓ PASS | RTX prefill +954, P100 decode +278, KV cache hit |
| **Content** | ✗ FAIL | 0 chars content — model `--reasoning on` issue |

**Root cause:** The \`--reasoning on\` server flag forces Qwen-style thinking mode.
With large haystack context, the model generates Chinese Spring Boot code instead of
answering the prompt. This affects BOTH direct llama-server calls and Hydra P/D split
calls identically. Simple prompts ("What is 2+2?") work correctly with sufficient
max_tokens. Remediation: increase max_tokens or disable reasoning mode.

**Dual verdict format:**

| Symbol | Meaning |
|--------|---------|
| ✓✓ COMPLETE PASS | Both P/D split and content quality pass |
| ⚠ PARTIAL | One passed, one failed |
| ✗✗ COMPLETE FAIL | Both failed |

## Test Output Format

Each test produces a Markdown report at `tests/results/{test-name}-report.md`
with these sections:

### 1. Header — What was tested
```markdown
## Eval Test: NIAH-2000 (2026-06-09T16:39:00Z)

| Field | Value |
|-------|-------|
| Prompt size | 2000 tokens (~6000 chars) |
| Needle depth | 50% |
| Passkey | `NIAH-1A2B` |
| Expected | RTX prefill → KV save → P100 KV restore → P100 decode |
```

### 2. llama-server Metrics (Ground Truth)

The `llamacpp:prompt_tokens_total` and `llamacpp:tokens_predicted_total`
counters on BOTH nodes, compared pre/post test. Deltas prove which GPU did
which work:
- RTX `prompt_tokens_total` ↑↑ = prefill happened on RTX
- RTX `tokens_predicted_total` ↑1 = only 1-token prefill completion, NOT decode
- P100 `prompt_tokens_total` ↑0 = KV cache hit, NO re-prefill
- P100 `tokens_predicted_total` ↑↑ = decode happened on P100

### 3. llama-server Slot State

Snapshot of `/slots` on both nodes before and after.

### 4. llama-server Logs — Both Nodes

Relevant log lines from both llama-servers captured during the test window:
- `srv update_slots:` / `slot N launch_slot_` — slot lifecycle
- `POST /v1/chat/completions` / `GET /slots/N/state` — HTTP requests
- `perf:` — timing data

### 5. Hydra Core Timeline

The `request_timeline` event showing phase durations.

### 6. Response Quality

Checks: HTTP code, finish_reason, cached_tokens, prompt_ms, reasoning content, passkey recall.

### 7. Overall: PASS / FAIL

Based on the 4 llama-server metric delta criteria above.

## P/D Split Pass/Fail Criteria

A test PASSES only when ALL four conditions are met on llama-server metrics:

| # | Condition | Source | Threshold |
|---|-----------|--------|-----------|
| 1 | RTX processed prompt tokens | `llamacpp:prompt_tokens_total` delta | > 100 |
| 2 | RTX did NOT decode | `llamacpp:tokens_predicted_total` delta | ≤ 5 |
| 3 | P100 did NOT re-prefill | `llamacpp:prompt_tokens_total` delta | ≤ 5 |
| 4 | P100 DID decode | `llamacpp:tokens_predicted_total` delta | ≥ 3 |

## Example Output

See the verified test run section below for a real report from 2026-06-09.

| Signal | Good | Bad |
|--------|------|-----|
| NIAH passkey recall | Exact match | Wrong or missing |
| P100 `prompt_tokens_seconds` | ≈ 0 | > 0 (re-prefill happening) |
| `prompt_ms` in response | < 5000 | > 10000 (full re-prefill) |
| Perplexity diff RTX vs Hydra | 0% | > 0.1% |
| `save_kv_ms` in timeline | 500-2000 | > 5000 or error |
| `restore_kv_ms` in timeline | 500-2000 | > 5000 or error |
| RTX slot after save | idle (free) | stuck (leak) |
| P100 decode latency | normal (~35 ms/tok) | slow or hanging |
| Reasoning/thinking content | Present in response | Missing |
