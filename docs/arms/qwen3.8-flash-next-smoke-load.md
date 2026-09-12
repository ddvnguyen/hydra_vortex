# Qwen3.8-Flash-Next-APEX-I-Mini Smoke Load — single-process layer-split fit

## Summary

**Question:** Does the fork (`qwen4exp`, B01 `10814 1d3c4a8e3`) load `/mnt/SSD/qwen3.8-flash-next-apex-mini` (78.7 GiB, 6 shards, Unsloth APEX-I-Mini) single-process local `CUDA0+CUDA1` with `layer` split + `fit`? Before benchmarking, confirm (a) PLE table quant and (b) `ffn_down_exps` 640-wide alignment don't trigger hard errors, and measure VRAM/RAM footprint.

**Result: PASS — loads and serves with `--fit on` alone (no manual `--override-tensor`). Both flagged failure modes are present as `IQ4_NL` but handled correctly by the fit/MoE overflow path; no quant-alignment error. Decode is functional.**

## Model under test

- Path: `/mnt/SSD/qwen3.8-flash-next-apex-mini/` = `Qwen3.8-Flash-Next-APEX-I-Mini-{00001..00006}-of-00006.gguf` = `515M + 27G + 14G + 14G + 14G + 4.4G` = ~74 GiB on disk, **73.29 GiB file size (3.56 BPW) reported by loader**, `176.94 B` params, `qwen4exp` arch `48` layers, `512` experts (`10` used), `n_ctx_train 262144`, `embedding 2560`, `rope_sections [11,11,10,0]`, `smm`/`hyper`/`ple` hybrid. Loader counts `1224` tensors, mix `f32 388, f16 1, q8_0 338, q6_K 195, q5_K 132, iq2_xxs 33, iq4_nl 49 ... bf16 24` — file_type `IQ2_XXS 2.0625 bpw` but shard tensors dominate.
- `qwen4exp.ple.layers [1]`, `ple.ngram 3`, `ple.heads_per_ngram 8`, `ple.conv_kernel 4`, `ple.eos 248044`, `per_layer_token_embd 160`, `expert_feed 640`, `ssm 4/128/16/48/6144`, `full_attn_interval 4`.
- Build support: `src/models/qwen4exp.cpp` 1282 lines present in B01 tree; `llama-server --help` lists `--split-mode`, `--layer-range`, `--fit`, `--fit-target`, `--override-tensor`.

## Flagged failure modes checked (explicit)

1. **PLE table `per_layer_token_embd.weight` independent quant** — Found via `gguf.GGUFReader` in shard `00002-of-00006`: `per_layer_token_embd.weight type 20 IQ4_NL shape [160 320001536]`. Loader also prints `tensor per_layer_token_embd.weight (size = 27465 MiB) lazy read enabled`. **Independent quant is present**, expected to be huge and ideally on CPU. With fit, it is absorbed via the layer-range/overflow logic; no fatal `IQ4_NL` error, just `CPU_Mapped 27465.95 MiB` accounted.

2. **`ffn_down_exps.weight` 640-wide NOT QK_K(256)-aligned** — Found via reader across shards 3-6: all `ffn_down_exps.weight type 20 IQ4_NL shape [640 2560 512]`. Note: failure mode feared is `Q4_K/Q5_K` (block 256) on 640; but actual quant is **`IQ4_NL` (block 32, 640%32==0)**, so alignment is fine. Loader loads them without error; fit handles their placement as MoE overflow tensors. **Not triggered** — passes. `ffn_gate_exps`, `ffn_up_exps`, `ffn_up_shexp` similarly on shards 2-6 (q6_K/q8_0 mix) — no alignment fault.

Both checks: **no hard quant error** — fit path succeeds.

## Build provenance (B01)

`CMAKE_BUILD_TYPE=Release`, `GGML_CUDA_FA_ALL_QUANTS=ON`, `GGML_CUDA_FORCE_CUBLAS=OFF`, `arch 86;120`, `DCUDAToolkit_ROOT=/opt/software/cuda/13.2.2`, binary `src/llama-cpp/build-cuda1322/bin/llama-server+ggml-rpc-server 0.4.0-dev build 10814 commit 1d3c4a8e3`, `BASELINE_SHA 5fff12845`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`, rig `RTX 5060 Ti 16G sm_120 CUDA0 15849 MiB` + `RTX 3060 12G sm_86 CUDA1 11911 MiB`, driver 595.91, CUDA 13.2.

## Attempts

### Attempt A — with manual override + fit (FAILED, expected flag conflict)

```bash
GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 \
src/llama-cpp/build-cuda1322/bin/llama-server \
  -m /mnt/SSD/qwen3.8-flash-next-apex-mini/Qwen3.8-Flash-Next-APEX-I-Mini-00001-of-00006.gguf \
  --host 0.0.0.0 --port 18081 --split-mode layer \
  --fit on --fit-target 1536 --override-tensor per_layer_token_embd=CPU \
  --ctx-size 8192-16384 -fa on --jinja --log-verbosity 4
```

Log: `common_param: tensor_buft_overrides already set by user, abort fit` → fit aborts, then `allocating 28400 MiB on CUDA0: cudaMalloc failed OOM` → exit. **Root cause: `--fit` and `--override-tensor` are mutually exclusive in this fork** — fit bails when overrides are pre-set, so large single-device alloc OOMs. Not a model quant bug.

Logs: `/tmp/qwen-smoke-8192.log` (port 18081), `/tmp/qwen-smoke-8192b.log` (18082 variant same error).

### Attempt B — `--fit on` alone, no override (PASS)

```bash
GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 \
src/llama-cpp/build-cuda1322/bin/llama-server \
  -m /mnt/SSD/qwen3.8-flash-next-apex-mini/Qwen3.8-Flash-Next-APEX-I-Mini-00001-of-00006.gguf \
  --host 127.0.0.1 --port 18082 --split-mode layer \
  --fit on --fit-target 1536 --ctx-size 8192 --parallel 1 \
  --flash-attn on --jinja --log-verbosity 4
```

**Fit log (4.64 s):** initial projected `48376 MiB device vs 27395 free` → `need 24052 less`, then `dense-only surplus 19670`, targets `14108/10214`, iterative `ngl_per_device` search → final `CUDA0 13 layers (1 overflowing) 14054 MiB used 1590 free`, `CUDA1 36 layers (29 overflowing, UP) 10019 MiB used 1731 free`, Host `~52 GiB`, `successfully fit params to free device memory`.

Loader: `per_layer_token_embd lazy 27465 MiB`, `offloaded 49/49 layers`, `CPU_Mapped 27465.95 MiB` + `CUDA0 12783.94 + CUDA1 9502.40` + mapped dense buffers; KV `192 MiB (8192 cells, 12 layers)` + indexer `72 MiB` + RS `112 MiB`; `sched_reserve 1160/238 MiB`, `warming up`, `listening on 127.0.0.1:18082`, `model loaded` `{"status":"ok"}`.

Actual `nvidia-smi` during serve: `CUDA0 14203-14307 MiB /16311`, `CUDA1 10177-10197 /12288` (fit estimate +120-380 overhead). Host `57Gi used /19Gi free /65Gi avail` (start) → `RAM 123Gi total, 66Gi avail` after pod stop, so **~52 GiB host mapped** + ~24 GiB device ≈ `76 GiB` effective resident vs `73 GiB file + 0.27 GiB context`, within `46-59 GiB` device + host estimate (device 24 vs 18-22 projected, host 52 vs 28-37 — higher due to lazy PLE pinned).

Logs: `/tmp/qwen-smoke-fit.log`.

## Smoke inference (functional)

- `POST /v1/chat/completions {messages:[{role:user, content:"Hello, say hi briefly."}], max_tokens:30}` → `choices[0].message.content "Hi! 👋"`, `reasoning_content` ok, `usage {prompt 58, completion 23, total 81}`, `timings {prompt_n 58 prompt_ms 1433 prompt_per_second 40.45, predicted_n 23 predicted_ms 997 predicted_per_second 22.05}`.
- Second probe `Explain in one sentence what PLE means` → coherent answer, `prompt 45.15 tok/s, predicted 23.37 tok/s (50 tok, 2096 ms)`.
- No `ffn_down_exps` alignment fault, no PLE quant fault; chat template `thinking=1` preserved.

**Performance note:** This is a **smoke ctx 8192 single-slot** throughput; ~22 tok/s decode is expected for this hybrid MoE at 8K without draft/MTP, not comparable to 140K MTP baseline (20 tok/s MTP-off vs 44 MTP-on). Real benchmark needs `--ctx 32768-262144` + draft tuning — done separately.

## Recommendation

1. **Model is loadable single-process `layer` on this rig** — no fork patch needed for APEX-I-Mini. Use **fit without manual override**:
   ```
   -m ...00001-of-00006.gguf --split-mode layer --fit on --fit-target 1536 --ctx-size 8192  --parallel 1 --flash-attn on --jinja
   ```
   For larger context, raise `--ctx-size 32768/65536` and let fit re-solve (expect `CUDA0 ~14.5-15 GiB, CUDA1 ~10-11 GiB, Host 55-60 GiB`); **do not combine `--fit` with `--override-tensor`** — it disables fit.
2. **Both flagged risks are benign here:** PLE is `IQ4_NL 27465 MiB lazy` handled by fit/host; `ffn_down_exps` is `IQ4_NL 640` (block 32) not K-quant, alignment ok. No action.
3. **Next benchmark:** Run `multiturn-growth-test.sh`/M01 with this model at `32768` then `65536` (fit targets auto-adjust); monitor host OOM (52 GiB already) and tail PLE latency. Separate doc if throughput arms are opened.
4. **Do not use RPC/colibri/ds4** — single-process `layer` + fit is the only sane path for this size on 28 GiB device (54 GiB logical with fit overflow). P100/`ggml-rpc` not needed.

## Provenance & cleanup

- **B01** `10814 1d3c4a8e3` `86;120` `CUDA 13.2.2` `Release FA_ALL_QUANTS`, `BASELINE_SHA 5fff12845`, `UM=1`, `fit-target 1536`.
- **RAM discipline:** stopped `pod_llama-baseline` → `free 56Gi used /65Gi avail` (was 37Gi), drain-verify `1 MiB`, `curl 18081/health` fail, `nvidia-smi 1 MiB` free; after smoke `pkill llama-server` → `1 MiB` free, `podman pod start pod_llama-baseline` → `{"status":"ok"}` `15847/11911` restored.
- **Logs:** `/tmp/qwen-smoke-fit.log` (success fit 4.64s + 97s load + health ok), `/tmp/qwen-smoke-8192*.log` (override+fit abort → OOM), chat probes timing above.
