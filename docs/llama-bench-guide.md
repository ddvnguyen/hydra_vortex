# llama-bench Testing Guide

## Quick Start

```bash
MODEL="/mnt/SSD/Qwopus3.6-35B-A3B-v1-APEX-MTP-I-Nano.gguf"
BENCH="/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp/build_sm120/bin/llama-bench"

# Basic benchmark
$BENCH -m "$MODEL" -ngl 99 -ncmoe 0 -p 4096 -n 64 -fa 1 \
  --cache-type-k q8_0 --cache-type-v q8_0 -r 3
```

## Key Flags

| Flag | Description | Example |
|------|-------------|---------|
| `-m` | Model path | `/mnt/SSD/model.gguf` |
| `-ngl N` | GPU layers (`99` or `999` = all) | `-ngl 999` |
| `-ncmoe N` | CPU MoE layers (0 = all on GPU) | `-ncmoe 0` |
| `-p N,N,N` | Prompt token sizes | `-p 512,4096,32768` |
| `-n N,N` | Generation token counts | `-n 1,64,128` |
| `-r N` | Repeat each test N times | `-r 3` |
| `-fa 1` | Flash attention on | `-fa 1` |
| `--cache-type-k/v TYPE` | KV cache quantization | `q8_0`,`f16`,`q4_0` |
| `-t N` | CPU threads | `-t 10` |
| `--rpc HOST:PORT` | RPC backend for multi-GPU | `--rpc 192.168.122.21:50052` |
| `-ot "PATTERN=DEVICE"` | Tensor override (regex) | see below |
| `--no-mmap` | Disable memory-mapped model load | |
| `--mlock` | Lock model in RAM | |
| `-v` | Verbose (shows tensor placement) | |

## Interpreting Output

```
| model | size | params | backend | ngl | test | t/s |
| qwen35moe | 10.87 GiB | 35.51 B | CUDA | 99 | pp4096 | 1350 |
| qwen35moe | 10.87 GiB | 35.51 B | CUDA | 99 | tg64 | 132 |
```

- **pp4096** = prompt processing (prefill) at 4096 tokens → 1350 tokens/second
- **tg64** = token generation (decode) of 64 tokens → 132 tokens/second
- Higher t/s = better

## Test Patterns

### 1. Single-GPU ncmoe sweep
Finds minimum CPU offload that fits VRAM:
```bash
for ncmoe in 0 8 16 20 24 28 32 40 48 56; do
  echo "ncmoe=$ncmoe:"
  $BENCH -m "$MODEL" -ngl 99 -ncmoe $ncmoe \
    -p 4096 -n 64 -fa 1 --cache-type-k q8_0 --cache-type-v q8_0 -r 1
done
```

### 2. Context size sweep
Checks max context that fits:
```bash
for ctx in 4096 8192 16384 32768 65536 131072; do
  echo "ctx=$ctx:"
  $BENCH -m "$MODEL" -ngl 99 -ncmoe 0 \
    -p $ctx -n 64 -fa 1 --cache-type-k q8_0 --cache-type-v q8_0 -r 1
done
```

### 3. Multi-GPU RPC with override-tensor
```bash
# Start rpc-server on remote GPU first:
ssh hydra-p100 "nohup /opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/rpc-server \
  -H 0.0.0.0 -p 50052 -t 4 -d CUDA0 > /tmp/rpc.log 2>&1 &"

# Run bench with tensor overrides:
$BENCH -m "$MODEL" -ngl 999 --rpc 192.168.122.21:50052 \
  -ot "blk\.(2|3|4|5|6|7|8|9|1[0-9]|2[0-2])\.ffn_(down_exps|gate_exps|up_exps)\.weight=RPC0[192.168.122.21:50052]" \
  -ot "blk\.(0|1|2[3-9]|3[0-9])\.ffn_(down_exps|gate_exps|up_exps)\.weight=CUDA0" \
  -ot "blk\..*\.(attn|ssm|norm|shexp|gate_inp)\.weight=CUDA0" \
  -p 4096 -n 64 -fa 1 --cache-type-k q8_0 --cache-type-v q8_0 -r 1

# Stop rpc-server after:
ssh hydra-p100 "pkill rpc-server"
```

**Override-tensor regex patterns:**
- `blk\.N\.` → specific block N
- `blk\.(2|3|4)\.` → blocks 2,3,4
- `blk\.([0-9]|1[0-9])\.` → blocks 0-19
- `ffn_(down_exps|gate_exps|up_exps)` → expert tensors only
- `(attn|ssm|norm|shexp|gate_inp)` → non-expert tensors
- Devices: `CUDA0` (local), `RPC0[host:port]` (remote)

### 4. MTP Speed Test (llama-server, not llama-bench)
llama-bench doesn't expose `--spec-type`. Use llama-server:
```bash
$BIN/llama-server -m "$MODEL" -ngl 99 -ncmoe 0 -c 4096 -fa 1 \
  --cache-type-k q8_0 --cache-type-v q8_0 \
  --spec-type draft-mtp --spec-draft-n-max 3 \
  --host 127.0.0.1 --port 18080 &

# Test via curl:
time curl -s http://127.0.0.1:18080/completion \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Hello world","n_predict":128,"temperature":0,"stream":false}' \
  | python3 -c "import json,sys; d=json.load(sys.stdin); t=d['timings']; \
     print(f'pp={t[\"prompt_n\"]} pp_t/s={t[\"prompt_per_second\"]:.0f} \
     tg={t[\"predicted_n\"]} tg_t/s={t[\"predicted_per_second\"]:.0f}')"

kill %1
```

### 5. Perplexity Test
```bash
PERP="$BIN/llama-perplexity"
CORPUS="/tmp/perplexity_corpus.txt"

$PERP -m "$MODEL" -ngl 99 -ncmoe 0 \
  -f "$CORPUS" -c 4096 -b 512 --ppl-stride 256 --chunks 10
# Output: [1]15.99,[2]11.39,[3]8.02,... (lower = better)
```

**Creating a test corpus:**
```bash
cat docs/*.md PROJECT_PLAN.md > /tmp/corpus.txt
```

For standard benchmarks, download WikiText-2:
```bash
wget https://huggingface.co/datasets/mindchain/wikitext2/resolve/main/wikitext-test.txt
```

## Production Bench Commands (GPU must be free)

```bash
# Stop production
podman stop llama-cpp
ssh hydra-p100 "systemctl --user stop llama-p100"

# Run tests...

# Restart
ssh hydra-p100 "systemctl --user start llama-p100"
podman start llama-cpp
```

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| CUDA OOM | Increase `-ncmoe` or reduce `-p` |
| `error: unrecognized buffer type RPC0` | RPC server died; restart it |
| `failed to load model` | Model corrupted or wrong architecture |
| RPC hangs | Kill rpc-server on both sides, restart |
| CPU-only (no CUDA in output) | Check `-ngl` flag, CUDA not found |
