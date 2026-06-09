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

### 2K Token NIAH — P/D Split Confirmed

```
Trace: niah-2k-1781023182
Session: sess_9ab1e3871c58764503a97979
```

**P/D Split Phase Timeline (from Hydra Core logs):**

| Phase | Time (ms) | Node | Detail |
|---|---|---|---|
| Route | 0 | — | `cold_concurrency`, RTX picked |
| Prefill | 3,392 | RTX | 920 tokens at ~271 tok/s |
| Save KV | 3,543 | RTX → tmpfs | 151ms for ~800 MB state |
| Restore KV | 3,750 | P100 ← tmpfs | 207ms restore |
| Decode | 5,947 | P100 | 28 tok/s decode |

**KV Cache Integrity:**

| Metric | Value | Meaning |
|---|---|---|
| `cached_tokens` | 918/919 | 99.9% cache hit — KV properly restored |
| `prompt_ms` | 70ms | No re-prefill (full prefill was 3.4s) |
| `prompt_n` | 1 | Only 1 new token processed |
| `cache_n` | 918 | 918 tokens from restored cache |
| `prompt_per_second` | 13.1K tok/s | Cache-hit speed, not GPU prefill |

**Log Events:**
```
event=cold_route Route=cold_concurrency Pw=rtx Free=true Healthy=true Est=908
event=prefill_done Node=rtx Slot=0 NPastFromLLama=920
event=save_kv_start Node=rtx Slot=0 NPast=920
event=restore_kv_start Node=p100 Slot=0
event=request_timeline prefill_ms=3392 save_kv_ms=3543 restore_kv_ms=3750 decode_ms=5947
```

**Model Behavior Note:** The Qwopus 35B model outputs reasoning content in Chinese,
which can consume the token budget before reaching the content field. For NIAH
passkey verification, increase `max_tokens` to 200+ so reasoning + content both fit.

## Interpreting Results

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
