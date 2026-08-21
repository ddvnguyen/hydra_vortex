# Baseline 2xRTX — vanilla llama.cpp (no Hydra)

Bare-bone upstream `ggml-org/llama.cpp` `llama-server` pooling both RTX cards (5060 Ti 16GB + 3060 12GB) to get **clean metrics before Hydra COMBINED logic**.

## Leader contract

Signed via **ADR 0002** (`docs/decisions/0002-leader-contract.md`, Option A: Markdown + `Signed-off-by`). Scope: baseline running authority (build, compose, harness). Standing until superseded. Hermes fleet `v2.1.1` stays superseded (`f8b322c73`). Merges still require explicit user confirmation (`CLAUDE.md §4`).

## Hardware / model

* GPUs: 5060 Ti sm_120 `01:00.0` + 3060 sm_86 `02:00.0`, driver 595.84 CUDA 13.2, toolkit `/opt/software/cuda/13.2`
* Model: `Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf` 19.5GB at `/mnt/SSD` + `/mnt/WorkDisk/LLM-Models` (does not fit solo — pooled 28GB needed)
* KV: `q8_0` + `flash_attn on`, yarn RoPE `scale 4 / orig 32768`

## Why pooling + layer split

Upstream single-process pooling (not Hydra `rpc_engine` / `combined-*` fork flags):

```
CUDA_VISIBLE_DEVICES=0,1  nvidia.com/gpu=all
--n-gpu-layers 65 --tensor-split 25,40 --split-mode layer
```

* `65` not `99` — reserves VRAM for KV (Hydra dense-27b-combined uses `65/[25,40]/layer` in `models.json:77`)
* `25,40` ≈ `16GB/12GB` ratio; `layer` = whole layers per GPU (dense-stable, same rationale Hydra uses)

## Build (host, 8 jobs)

```bash
export DCUDAToolkit_ROOT=/opt/software/cuda/13.2 PATH=/opt/software/cuda/13.2/bin:$PATH
cmake -S src/llama-cpp -B src/llama-cpp/build -DGGML_CUDA=1 -DLLAMA_CURL=ON -DCMAKE_BUILD_TYPE=Release
cmake --build src/llama-cpp/build --parallel 8
src/llama-cpp/build/bin/llama-server --version
```

Submodule must point to upstream: `https://github.com/ggml-org/llama.cpp` (see `BASELINE_SHA`). The `hydra-fork` (`ddvnguyen/llama.cpp hydra-fork`) is replaced for this baseline.

## Run

**96K attempt (default, may OOM):**
```bash
podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d --build
podman logs -f llama-baseline_llama_1  # watch "model loaded" or "out of memory"
curl -s http://localhost:8080/health | jq
curl -s http://localhost:8080/v1/models | jq
```

**If OOM, fallback 64K (Hydra cap):**
```bash
podman compose -f infra/llama-baseline/docker-compose.baseline.yml down
podman compose -f infra/llama-baseline/docker-compose.baseline-64k.yml up -d --build
```

**Port conflict:** `hydra-system` uses `:8080` (RTX) + `:8081` (3060). Either `podman compose -f infra/docker-compose.hydra.yml down` or remap baseline to `:18080` in the compose `port` + harness URL.

## Harness (dsh / pi)

Vanilla OAI, no Hydra RPC:

```bash
export OPENAI_BASE_URL=http://localhost:8080/v1 OPENAI_API_KEY=dummy
# dsh
dsh run --model Qwopus3.6-27B --dataset <...>
# pi / opencode harness
curl -N http://localhost:8080/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"Qwopus3.6-27B","messages":[{"role":"user","content":"hello"}],"stream":true}'

# Batch harness
bash infra/llama-baseline/bench-baseline.sh  # writes tests/bench/baselines/rtx2-baseline-*.json
```

## Metrics

`bench-baseline.sh` runs short/medium/long prompts (512/4096/8192) and records TTFT, TPOT, prefill tok/s, p50/p95 + `nvidia-smi` VRAM. Do not overwrite `tests/bench/baselines/main.json` until intentional.

## Files

* `Dockerfile.baseline` — CUDA 13.2 runtime, copies host-built `llama-server`
* `docker-compose.baseline.yml` — 96K pooled, layer split
* `docker-compose.baseline-64k.yml` — fallback
* `bench-baseline.sh` — harness wrapper
* `BASELINE_SHA` — pinned upstream SHA for reproducibility
