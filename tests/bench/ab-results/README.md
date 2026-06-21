# A/B test results — engine vs upstream

This directory holds raw results from the engine-vs-upstream A/B test
that surfaced issue **#316** (engine prompt cache broken) and confirmed
the null hypothesis at cold prefill.

## Files

- **`engine-vs-official-cold-prefill.json`** — single cold prefill on
  Qwen3.6-35B-A3B-APEX-I-Balanced.gguf, unique nonce in the prompt so
  no prompt cache can match. Engine is 2-4% faster than upstream
  (within noise). Fork's 3-endpoint patch adds no per-request overhead.
- **`engine-vs-official-prompt-cache.json`** — three calls in a row
  with the same prompt and same `session_id`. Engine achieves 0%
  cache hit (cold prefill every time). Upstream achieves 97% cache hit
  (call 2-3: ~64 ms vs ~1242 ms). See #316.

## Method

Both binaries launched as standalone llama-server processes on the
RTX 5060 Ti (single-model mode, not router mode), with the same args
on each side:

```
--model /mnt/SSD/Qwen3.6-35B-A3B-APEX-I-Balanced.gguf
--port 8080 --host 127.0.0.1
--ctx-size 32000 --parallel 2 --n-gpu-layers 99
--n-cpu-moe 30 --override-tensor 'token_embd\.weight=CPU'
--chat-template-file /mnt/SSD/qwen3.6_merged_template.jinja
--jinja --cont-batching --slots --metrics
--rope-scale 5 --rope-scaling yarn --yarn-orig-ctx 32768
--log-verbosity 1 --timeout 1800
```

| Binary | Version | Source |
|---|---|---|
| engine   | 9541 (c357ad25b) | `podman cp hydra-head-rtx:/llama/bin/llama-server` (hydra fork HEAD) |
| official | 200 (c576070)    | `git clone upstream master` + `cmake -DGGML_CUDA=ON` (built locally) |

The fork's only server.cpp change is the 3 KV-streaming endpoints
(`/slots/{id}/state` GET/PUT + meta), all of which are idle in this
single-model test. Everything else — CUDA kernels, sampler, tokenizer
— is identical to upstream.

## Reproducing

```bash
# Engine
setsid /tmp/hydra-ab/start_engine.sh > /tmp/eng.log 2>&1 &

# Official
setsid /tmp/hydra-ab/start_official.sh > /tmp/off.log 2>&1 &

# Wait for /health=ok on each, then run cold_ab.py / cache_ab.py
python3 /tmp/hydra-ab/cold_ab.py
python3 /tmp/hydra-ab/cache_ab.py
```

The launcher scripts and the A/B harness live in `/tmp/hydra-ab/` on
the test box. They are not checked into the repo because they're
throwaway; the JSON results are the durable artifact.
