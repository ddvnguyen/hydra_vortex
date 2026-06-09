# Hydra P/D Split Eval Tests

Verification that RTX prefill → KV save → P100 decode → KV restore works correctly.
Three test tiers: NIAH (retrieval), Perplexity (distribution), Math (reasoning).

## Architecture Under Test

```
Client (curl) → Hydra Core :9000
  → Router → RTX prefill (1 token, stream=false)
  → SaveKv → GET /slots/{id}/state → /mnt/llm-ram/store/{session}.kv
  → PickDecode → P100
  → RestoreKv → PUT /slots/{id}/state?erase_existing=true
  → Decode → streaming/non-streaming response
```

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
journalctl --user -u hydra-core -f --no-pager | grep -E "routing|save_kv|restore_kv|request_timeline|cold_concurrency|prefill"

# RTX llama-server
docker logs -f llama-cpp 2>&1 | grep -E "hydra|slot|state"

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
echo "=== RTX slots ===" && curl -s http://localhost:8080/slots | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d),\"slots\")" 2>/dev/null
echo "=== P100 slots ===" && curl -s http://192.168.122.21:8086/slots | python3 -c "import sys,json; d=json.load(sys.stdin); s=d[0] if d else {}; print(f\"{len(d)} slots n_past={s.get(\"n_past\",0)}\")" 2>/dev/null
echo "=== KV files ===" && ls -lh /mnt/llm-ram/store/*.kv 2>/dev/null || echo "none"
'
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
