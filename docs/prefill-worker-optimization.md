# Prefill-Only Worker Optimization

## The Trick

Move decode-only and input-only tensors to CPU — they're never needed on GPU during prefill. The freed VRAM lets you lower ncmoe (keeping more expert layers on GPU), dramatically boosting prefill throughput with no quality loss.

**Result on Mini Q3_K (13.28 GB):** ncmoe dropped from 16→2, prefill went from 709→1187 pp (+67%).

## Quick Config

```bash
llama-server -m model.gguf -ngl 99 -ncmoe <low-ncmoe> \
  -ot "token_embd\.weight=CPU" \
  -ot "output\.weight=CPU" \
  -ot "output_norm\.weight=CPU"
```

## Which Tensors Can Go to CPU

Use `gguf_dump.py` to inspect your model first:

```bash
python3 gguf-py/gguf/scripts/gguf_dump.py model.gguf > /tmp/model_layers.txt
```

Then grep for the key tensors:

```bash
# Always-on-GPU tensors (needed every token):
grep -E "blk\.0\.(attn|ssm|norm|shexp|gate_inp)" /tmp/model_layers.txt

# Expert tensors (heavy, per-block):
grep -E "blk\.0\.ffn_(down|gate|up)_exps" /tmp/model_layers.txt

# Embedding/output (candidates for CPU offload):
grep -E "token_embd|output\.weight|output_norm" /tmp/model_layers.txt

# Overall quant distribution:
tr -s ' ' < /tmp/model_layers.txt | grep "^  [0-9]*:" | awk '{print $NF}' | sort | uniq -c | sort -rn
```

## Tensor Roles in a Prefill-Only Worker

| Tensor | Size (Q3_K, 41-block model) | Needed for prefill? | CPU-safe? |
|--------|------|:---:|:---:|
| `token_embd.weight` | 509 MB | Only at input start | ✅ Safe — used once per prompt |
| `output.weight` | 509 MB | **Never** (decode only) | ✅ Safe — output prediction head |
| `output_norm.weight` | 2 KB | **Never** (decode only) | ✅ Safe |
| `blk.N.attn_*.weight` | ~35 MB/block | Every token | ❌ Must stay GPU |
| `blk.N.ssm_*.weight` | ~10 MB/block | Every token | ❌ Must stay GPU |
| `blk.N.ffn_down_exps.weight` | ~107 MB/block | Every token | ⚠️ GPU if ncmoe allows |
| `blk.N.ffn_gate_exps.weight` | ~107 MB/block | Every token | ⚠️ GPU if ncmoe allows |
| `blk.N.ffn_up_exps.weight` | ~107 MB/block | Every token | ⚠️ GPU if ncmoe allows |
| `blk.N.ffn_*_shexp.weight` | ~3 MB/block | Every token | ❌ Must stay GPU |
| `blk.N.gate_inp.weight` | Small | Every token (expert routing) | ❌ Must stay GPU |

For a 41-block Qwen3.6 model, moving `token_embd` + `output` to CPU frees **~1 GB VRAM**. That's 2-3 more expert blocks on GPU — or equivalently, ncmoe drops by ~2-3.

## Methodology

### Step 1: Dump model layers

```bash
python3 gguf-py/gguf/scripts/gguf_dump.py model.gguf | grep "ffn_down_exps"
```

Look at the quant type (Q3_K, Q4_K, etc.) and raw byte size per tensor.

### Step 2: Calculate per-block VRAM cost

Expert tensors per block:
```
(ffn_down_exps + ffn_gate_exps + ffn_up_exps) in GB
```
Example: Q3_K experts = 3 × 107 MB = 321 MB/block

### Step 3: Calculate ncmoe effect

```
VRAM_per_block × (41 - ncmoe) = expert VRAM on GPU
VRAM_per_block × ncmoe = expert VRAM on CPU
```

### Step 4: VRAM budget

| Component | VRAM |
|-----------|------|
| Expert tensors on GPU | (41 - ncmoe) × per-block-size |
| Always-active (attn/SSM/norms/shexp) | ~2 GB |
| KV cache (8K, q8_0, 4 slots) | ~0.5 GB |
| CUDA context + scratch | ~1-2 GB |
| Output (if on GPU) | 0.5 GB |
| Token embeddings (if on GPU) | 0.5 GB |
| **Total** | sum must be < 15.8 GB (RTX 5060 Ti) |

### Step 5: Binary search for min ncmoe

```bash
for ncmoe in 0 4 8 12 16; do
  llama-bench -m model.gguf -ngl 99 -ncmoe $ncmoe \
    -ot "token_embd\.weight=CPU" \
    -ot "output\.weight=CPU" \
    -p 4096 -n 64 -fa 1 --cache-type-k q8_0 --cache-type-v q8_0
done
```

If it fails (OOM), ncmoe too low. If it works, try lower.

### Step 6: Verify VRAM used

```bash
llama-server -m model.gguf -ngl 99 -ncmoe <found> \
  -ot "token_embd\.weight=CPU" \
  -ot "output\.weight=CPU" \
  -c 8192 --host 127.0.0.1 --port 18080 &
sleep 5
nvidia-smi --query-gpu=memory.used --format=csv,noheader
kill %1
```

## Real Results (Qwopus 35B-A3B)

### Model layer breakdown

| Tensor class | Nano (10.87 GB) | Mini (13.28 GB) |
|-------------|------|------|
| token_embd | Q2_K | Q3_K |
| output | Q5_K | Q6_K |
| ffn_*_exps (×41×3) | **Q3_K** | **Q3_K** |
| attn_* | Q4_K/Q3_K | Q4_K/Q3_K |
| ssm_* | Q4_K/F32 | Q4_K/F32 |

**Key insight:** Both models have identical Q3_K experts. The 2.41 GB difference is only in embeddings/output — exactly the tensors we move to CPU.

### Prefill-only results (output+embd→CPU)

| Model | ncmoe | pp4096 | tg64 | vs default best ncmoe | Deploy |
|-------|:---:|--------|------|-----------------------|--------|
| **Nano IQ2_XXS** | 0 | 1341 | 132 | baseline (already fits) | Primary |
| **Mini Q3_K** | 2 | **1187** | **115** | +67% pp vs ncmoe=16 | Prefill worker |
| Mini Q3_K | 4 | 1001 | 95 | |
| Balanced Q5_K | 20 | 462 | 47 | baseline | Decode worker |

### Why Mini at ncmoe=2 works when ncmoe=0 fails

| Component | ncmoe=0 (all GPU) | ncmoe=2 + CPU offload |
|-----------|:---:|:---:|
| Experts on GPU | 41 × 321 MB = 13.2 GB | 39 × 321 MB = 12.5 GB |
| Always-active | 2.0 GB | 2.0 GB |
| KV cache | 0.5 GB | 0.5 GB |
| Output+embd | 1.0 GB | **0 GB (CPU)** |
| CUDA overhead | 2.0 GB | 1.5 GB |
| **Total** | **18.7 GB ❌** | **16.0 GB ⚠️** |

## Decode-Phase Optimization

During decode, only **one** tensor can be safely moved to CPU:

```bash
-ot "token_embd\.weight=CPU"
```

| Tensor | Needed for decode? | CPU-safe? |
|--------|:---:|:---:|
| `token_embd.weight` | Yes (each output token) | ✅ Safe — just an 8KB row lookup |
| `output.weight` | **Yes (logits every token)** | ❌ Must stay GPU |
| `output_norm.weight` | **Yes** | ❌ |
| All attention/SSM/experts | **Yes** | ❌ |

The `token_embd` lookup is tiny: 2048 floats (8KB) per token from a 509MB matrix. Moving it to CPU saves 509 MB VRAM with negligible performance cost — **but only helps when VRAM is tight**.

### Decode results (Balanced Q5_K)

| Config | pp4096 | tg64 | Δ decode |
|--------|--------|------|:---:|
| ncmoe=20 default | 379 | 44 | baseline |
| ncmoe=20 + token_embd→CPU | 453 | 47 | +8% |
| **ncmoe=18 + token_embd→CPU** | **465** | **50** | **+12%** |
| ncmoe=16 even with CPU offload | OOM | | |

Freed VRAM lets ncmoe drop 20→18 (+2 expert blocks on GPU). Nano at ncmoe=0 gets zero gain — VRAM isn't tight with 4.3 GB free.

### Why decode gains are smaller than prefill

- **Prefill**: compute-bound (all 256 experts per token × all prompt tokens), benefits from every expert on GPU
- **Decode**: memory-bound (KV cache access), only 8/256 experts per token, expert placement less impactful
- Most decode time is spent in attention over KV cache rows, not expert matmuls

## Production Configs

For the P/D split architecture (#161):

### Prefill worker (Mini Q3_K + CPU offload)

```bash
llama-server -m Mini-Q3_K.gguf \
  -ngl 99 -ncmoe 2 -c 32768 -fa 1 \
  --cache-type-k q8_0 --cache-type-v q8_0 \
  -ot "token_embd\.weight=CPU" \
  -ot "output\.weight=CPU" \
  -ot "output_norm\.weight=CPU"
```
→ **1187 pp t/s** (3.5s for 4K tokens)

### Decode worker (Balanced Q5_K + CPU offload)

```bash
llama-server -m Balanced-Q5_K.gguf \
  -ngl 99 -ncmoe 18 -c 32768 -fa 1 \
  --cache-type-k q8_0 --cache-type-v q8_0 \
  -ot "token_embd\.weight=CPU"
```
→ **50 tg/s** (4.0s for 200 tokens)

### Full P/D pipeline

```
Prefill: Mini Q3_K, ncmoe=2, output/embd→CPU  →  1187 pp  →  3.5s
KV xfer: 400 MB via Store @ 540 MB/s          →             →  0.7s
Decode:  Balanced Q5_K, ncmoe=18, embd→CPU   →   50 tg   →  4.0s
────────────────────────────────────────────────────────────────
Total (4K+200):                                               8.2s
```

**2.1x faster than production (17.4s)** with Q3_K prefill quality + Q5_K decode quality.
