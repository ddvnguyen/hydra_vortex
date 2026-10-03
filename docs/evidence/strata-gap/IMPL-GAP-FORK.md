# Epic #811 — Fork-side implementation gap (companion to IMPL-GAP.md)

Scope: READ-ONLY close-out of the fork-side questions F-A..F-D. Fork source: `.local/q2g/src`. No rig, no GPU, no commits, branch `docs/811-impl-gap`.
Tags: **[verified in source]** = cited file:line read directly; **[hypothesis]** = reasoned, not read; **[measured]** = from our own run logs (t0006 evidence).

---

## F-A. Fork decode code map for `--n-cpu-moe 99` [verified in source unless noted]

### A1. Routing: flag → CPU execution of `ffn_*_exps` MUL_MAT_ID
Six hops [verified in source]:
1. Flag: `--n-cpu-moe` → `llm_add_n_cpu_ffn_overrides(value, LLM_FFN_EXPS_REGEX, …)` `common/arg.cpp:2855-2863`; regex `\.ffn_(up|down|gate|gate_up)_(ch|)exps` `common/common.h:1158`, per-layer expansion `blk\.<i>` `common/common.h:1162-1173`.
2. Loader match, **first-match-wins + break**: `src/llama-model-loader.cpp:1290-1318`. MoE-cache override is deliberately **prepended** ahead of user overrides when slots>0 `src/llama.cpp:318-336` (comment "Prepending matters because the loader's match loop is first-match-wins").
3. **Surprise**: `select_weight_buft` `llama-model-loader.cpp:1060-1070` walks `make_cpu_buft_list` order (ACCEL → **host buft of first device** `llama-model.cpp:1055-1063` → extra/repack → plain CPU `:1082-1089`), gated only by `ggml_backend_dev_supports_op` (`llama-model-loader.cpp:1049-1051`, never `supports_buft`); CUDA *advertises* MUL_MAT_ID for IQ3_S (`ggml-cuda.cu:7841-7844`, `ggml-cuda/mmid.cu:35-36`). ⇒ **expert weights land in `CUDA0_Host` pinned memory**, and repack/extra bufts never get selected for them [High confidence; buffer name only at DEBUG `llama-model-loader.cpp:1311-1315`; `GGML_CUDA_NO_PINNED` demotes `ggml-cuda.cu:1334-1347`].
4. Node → backend via `ggml_backend_sched_backend_from_buffer` `ggml-backend.cpp:890-905`: CUDA rejects host buft on discrete GPU (`ggml-cuda.cu:8216-8226`), CPU accepts host buft (`ggml-cpu.cpp:515-518`). Pass-3 upgrade cannot rescue (`ggml-backend.cpp:1216-1300` requires same buft).
5. Execution: `ggml_compute_forward_mul_mat_id` `ggml-cpu.c:1542`.
6. `-ot per_layer_token_embd=CPU` uses the same branch (GET_ROWS) — embedding on host too.
⇒ Our F0 measured 15.3 tok/s **is genuinely CPU experts** (and `-moe-expert-cache-size 0` skips the whole MoE-cache override block `src/llama.cpp:329`, user overrides honoured verbatim).

### A2. Split count + sync points per token
- **≈96–97 backend splits per decode token** = 1 CPU run + 1 GPU run per MoE layer (48 layers) + graph-start CPU run (token embedding) + graph-end GPU output block; node order GPU(attn/resid/norm/router) → CPU(gate_up MMID, GLU, down MMID) → GPU(next) per layer (`src/models/qwen4exp.cpp:420,1062,1211`; build `src/llama-graph.cpp:2311-2400`); split starts on backend change `ggml-backend.cpp:1348` or weight-buft mismatch `:1331-1346`. **[hypothesis on the exact integer — derived from node order, needs `GGML_SCHED_DEBUG=1` to count; could double to ~193 if `exp_probs_b`/`ffn_norm_exps` sit inside the CPU run]**
- **Every boundary is host-blocking** [verified in source]: with `n_copies==1` event ptrs are NULL (`ggml-backend.cpp:2023-2026`), so boundary drain does `ggml_backend_synchronize(prev)` (`:1774-1781`); cross-backend input copy falls back to blocking `ggml_backend_tensor_copy` because CUDA `cpy_tensor_async` requires both sides CUDA (`ggml-cuda.cu:4257-4264`) and CPU's is NULL (`ggml-cpu.c:206`) (`ggml-backend.cpp:1902-1915`); `ggml_backend_synchronize(cuda)` = `cudaStreamSynchronize` (`ggml-cuda.cu:4312-4315`), CPU sync = no-op (`ggml-cpu.c:207`); `set_tensor/get_tensor` = `cudaMemcpyAsync` + `cudaStreamSynchronize(cudaStreamPerThread)` (`ggml-cuda.cu:841-858`). MoE weight branch also syncs (`ggml-backend.cpp:1817-1819,1846-1848`).
- **GPU waits for CPU only by host serialization**: CPU split runs synchronously, then host issues H2D + next CUDA dispatch (`ggml-backend.cpp:1922,1946`); the `cudaLaunchHostFunc` device-side wait is `#if 0`'d out (`ggml-cuda.cu:7179-7186`).
- **`pipeline_parallel` is force-disabled by our flags**: `bool pipeline_parallel = … && !model.has_tensor_overrides()` `src/llama-context.cpp:1289-1294`; overrides exist (from `-ncmoe`/`-ot`) ⇒ `n_copies = 1` (`ggml-backend.cpp:1998`) ⇒ no CUDA events ⇒ **zero cross-backend overlap** [verified in source].
- **`--op-offload` (default on) can't help decode**: needs batch ≥ 32 (`ggml-cuda.cu:8488`, env `GGML_OP_OFFLOAD_MIN_BATCH`; `get_op_batch_size(MUL_MAT_ID) = ne[2] = n_tokens` `:8243`) — decode has 1 token (`ggml-backend.cpp:970-977`, `ggml-cuda.cu:8249-8252`).

### A3. CUDA graphs under mixed backends
Compiled in, default ON (`build/ggml/ggml-config.cmake:72`, `ggml-cuda/CMakeLists.txt:125-127`) [verified in source], but **CUDA never sees CPU nodes**: sched splits first (`ggml-backend.cpp:1301-1428`) so each capture is a pure-GPU subgraph. Compat veto is narrow (MMID fallback-sync or MoE-cached, `ggml-cuda.cu:4404-4440`). ⇒ graphs wrap the GPU splits (cheap replay for attn/norms/router) and contribute nothing to the CPU cost nor hide the ~97 boundaries. [verified in source]

### A4. CPU expert kernel + threads (`-t 6`)
`ggml_compute_forward_mul_mat_id` `ggml-cpu.c:1542`, `n_tasks = n_threads` `:2357-2360` [verified in source]:
- Single-thread pre-pass builds per-expert routing map `:1641-1655`; outer loop over **all 512 experts, `continue` if `matrix_row_counts==0`** `:1665-1671` ⇒ only routed experts touched; reads **every output row of that expert's slice** (`src0_cur = src0->data + cur_a*nb02`, `nr0 = ne01` `:1681-1686`).
- Chunking: `chunk_size=16` (64 if `nr0==1||nr1==1`), fallback if `< nth*4` `:1684-1707`; work-stealing atomic per expert `:1705-1734` — threads balance within an expert, lockstep across experts.
- **IQP/AVX-512 multi-token panel path skipped at decode**: gated `ggml_cpu_iqp_mul_mat_id_min_batch(cne1)` with `GGML_IQP_MIN_BATCH_ID = 8` (`ggml-cpu/iqp.cpp:20-26`); decode routes ≤1 token per expert ⇒ plain path; prefill with ≥8 tok/expert ⇒ IQP taken (`iqp.cpp:499` IQ3_S eligible) [verified in source; per-expert counts in our runs = hypothesis].
- **No repack for IQ3_S**: `ggml_repack_get_optimal_repack_type` lists q4_0/q4_K/q5_K/q6_K/q2_K/iq4_nl/mxfp4/q8_0 only (`ggml-cpu/repack.cpp:4528-4565`); IQ3_S dispatches `ggml_vec_dot_iq3_s_q8_K` (`ggml-cpu.c:357-361`) → arch kernel `ggml-cpu/arch/x86/quants.c:3384`.
- Threads: `-t` `common/arg.cpp:1519-1526` → `n_threads` set per graph_compute, decode uses `n_threads` not `n_threads_batch` (`llama-context.cpp:4507-4524`). ⇒ **6 threads, no affinity pinning (unlike Strata's pinned physical-core pool), no repack, plain vec_dot at T=1**.

### A5. The "route through the GPU LRU cache regardless of --cpu-moe" warning
Exact source, sole occurrence: `src/llama.cpp:361-364` `LLAMA_LOG_WARN("--moe-expert-cache-size is set; expert tensors route through the GPU LRU cache regardless of --cpu-moe / --n-cpu-moe.\n")`, emitted only inside `if (params.moe_expert_cache_slots > 0)` `src/llama.cpp:329`, after the MoE-cache override prepend `:318-336,347-355` (first-match-wins beats `-ncmoe`). ⇒ at `--moe-expert-cache-size 0` (our F0/F2) **this warning cannot fire**; if we saw it, provenance = env `LLAMA_ARG_MOE_EXPERT_CACHE_SIZE` (`common/arg.cpp:2885`) or an F1 (cache>0) run, or a neighbouring warning (`llama-model-loader.cpp:1299-1304` mmap-override). [verified in source; observed-instance = open provenance]

### A6. In-source telemetry (for future rig use, not now)
- **`--profile-decode`** (server-only): `common/arg.cpp:1977-1982` → `prof_step` emits `PROF … t_wall_ms t_cpu_ms t_dev_ms t_sync_ms` per 64-step window `llama-context.cpp:726,847-853`, `dev_ms` from CUDA profiling `:4012`. ⇒ would directly split our 65.4 ms into GPU/CPU/sync. [verified in source]
- `GGML_SCHED_DEBUG` prints `## SPLIT #n` per split (`ggml-backend.cpp:1988-1989,1004-1031`) → would count the ~97. `GGML_CUDA_GRAPH_PROFILE` (`ggml-cuda.cu:6421`). No default per-op timing.

---

## F-B. Fork prefix-cache decision path — PARTIAL (full report + log pass interrupted) [partial]

What survived the verification pass (all re-cited lines confirmed by direct read) [verified in source]:
- **Discriminating log lines**: per-chunk `slot.print_timings_pp()` at top of every batch-fill iteration `server-context.cpp:3952-3954`, prints `prompt processing, n_tokens = %6d, progress = …, t = %6.2f s / %.2f tokens per second` `:746-747` gated `t_prompt_ms ≥ 3000` `:739-741`; successive lines cumulative (advanced in `post_decode`). Final `prompt eval time / N tokens` — `n_prompt_processed` zeroed `:3927`, incremented only per `is_prompt` row actually submitted `:5140-5152` ⇒ **N = true evaluated token count**. `graphs reused` per decode `src/llama-context.cpp:6501` (counter reset `:5454`). Live cache counts `/slots`: `n_prompt_tokens{,_processed,_cache}` `server-context.cpp:824-826`.
- **KV checkpoint/restore machinery exists on the prompt path** (candidate for turn-2 fixed cost): `checkpoint_offsets` def `:4082`, loop `:4085-4095`, user-msg break `:4067-4071`; workspace re-reserve `llama-context.cpp:1652,1656`; slot LRU line `:1865`; `n_batch` retry `:4539`; NO_UPDATE guard `llama-kv-cache.cpp:2975-2976`.
- `kv_unified` env `common/arg.cpp:1732` (`LLAMA_ARG_KV_UNIFIED`).
- **Decision table (to apply to logs)**: `prompt eval time ≈ 10000 ms / 731` ⇒ cost inside slot; `≈ 6000 ms / 731` ⇒ ~4 s pre-slot; `N ≠ 731` ⇒ LCP not honored; low `graphs reused` ⇒ rebuild; 2+ `prompt processing` lines ⇒ extra chunks.

**OPEN**: the full Q1 (template-vs-LCP ordering, exact `cached_tokens` semantics), Q2, Q3, Q5 narrative and the F0-2/F0-3 log application were lost when the subagent's resumed session was interrupted. Also **`docs/evidence/strata-3060-ab/` does not exist in this worktree** (see F-C) — the logs themselves must be located first.

---

## F-C. Strata prefill chunk + profile hints — SOURCE DONE, LOGS MISSING

- **Chunk print sites** [verified in source]: serve: `strata serve: prompt chunk auto: %lld tokens` `src/program/generate.cpp:3105`; generate: `strata generate: prompt chunk auto: …` `:4399`. Auto rule picks 2048–8192 from cache-slot budget `generate.cpp:3062-3085,1181-1185`.
- **`STRATA_VERIFY_PROFILE` exists** [verified in source]: `src/core/verify.cpp:270` `prof_on_ = std::getenv("STRATA_VERIFY_PROFILE") != nullptr;` → cudaMalloc stamp buffer `:271-273`, per-layer GPU stage stamps `:340` (dense group), `:437` (`fused_gr_read_multi`), window stamps read back `:1054-1059`, report emitted `:796` (`if (!prof_on_ || prof_windows_ == 0) return ""`). ⇒ can split GPU-busy into stages — **requires a GPU run, deferred** (not allowed this task).
- **LOG GAP [measured-negative]**: `docs/evidence/strata-3060-ab/` is **not present in this worktree** (ls fails; `find /mnt/WorkDisk … -name S0-2-engine.log` empty; `git ls-files` has only `docs/evidence/strata-gap/IMPL-GAP.md`). ⇒ cannot grep `prompt chunk auto` / re-extract decode lines here. The actual chunk used in S0-2/S1 runs **remains unknown** → Open (leader: point at the log location — likely the main checkout — or confirm the evidence lives elsewhere).

---

## F-D. Strata verify = GPU-wait + host: additive or pipelined? → **ADDITIVE (strict per-layer ping-pong)** [verified in source + measured]

Read `src/core/verify.cpp:950-1059` end-to-end:
- One `Verifier::run` per window: host staging `ms_host` `:960-989`; **single `cudaGraphLaunch`** `:991`; then **one sequential loop `for k in (le_-lb_)*G`** `:999-1048` — per layer-group, **spin-wait for GPU ring `while (*seq < want)` `:1007-1021` → `ms_wait += b-a` `:1046`, THEN `pool(user, …)` CPU experts `:1028-1030` → `ms_pool += ms_since(b)` `:1047`, then flag publish `:1033-1045`** — strictly wait-then-pool, no interleaving. Final `cudaStreamSynchronize` `:1050-1053`.
- ⇒ `verify = Σ wait_k + Σ pool_k + stage` is an **exact partition of host wall inside the loop**, and the ping-pong is causal per layer (attention of l+1 depends on `resid_l` which needs `pool_l`), so GPU cannot run meaningfully ahead during `pool`. **Confirmed by arithmetic [measured]**: 15.76 + 10.52 + 0.29 ≈ 26.6 ≈ verify 28.18 (residual ≈ flag/spin granularity) — i.e. **sum ≈ wall ⇒ no cross-layer overlap credit**. The "publish-first" benefit (`expert_source.cpp:816-820`) is limited to **starting expert-miss prefetch (PCIe) ahead of the pool**, not overlapping pool with GPU compute.
- Corollary for IMPL-GAP.md gaps #2/#5: Strata and the fork are **both** ping-pong designs; Strata's ping-pong is one graph launch + mapped-memory spin per layer with tiny per-layer overhead, the fork's is ~97 backend-sched boundaries each paying `cudaStreamSynchronize` + blocking activation copies. The gap is **per-boundary overhead and CPU/GPU share**, not the existence of overlap.

---

## Updated ranked estimates (fork-side facts folded in)

| # | Gap (from IMPL-GAP.md) | Updated estimate | Change reason |
|---|------------------------|------------------|---------------|
| **1** | Expert GEMM placement (GPU vs CPU) | **Prefill: confirmed dominant, ~3× fully explained** [verified]: fork computes every expert row on CPU with plain `vec_dot` (no IQ3_S repack, A4), pinned `CUDA0_Host` weights (A1); IQP multi-token only when ≥8 tok/expert (prefill probably yes, decode no). **Decode: fork CPU share ≈ 25–40 ms of 65.4** [hypothesis: 10/10 routed experts × 48 layers on `-t 6` vs Strata 10.5 ms for 3.8 CPU experts + 6.1 GPU entries on full-core pool] | now code-backed instead of hypothesized |
| **2** | Per-layer split/sync | **Mechanism confirmed; ≈96–97 boundaries/token, each with `cudaStreamSynchronize` + blocking H2D of CPU outputs; `pipeline_parallel` force-off by our own flags ⇒ zero overlap. Estimate 10–25 ms/token of the Δ** [hypothesis on magnitude: 97 × 0.1–0.25 ms boundary + serialized set/get_tensor copies; exact count needs `GGML_SCHED_DEBUG`] | A2/A3 code map; note `--op-offload` and CUDA-graphs cannot mitigate |
| **3** | CPU engine quality (pool/threads) | **Estimate 5–15 ms/token** [hypothesis]: 6 unpinned threads vs Strata pinned all-cores with 3× thread tasks; no repack. Portable levers: raise `-t`/pinning; IQP already covers prefill. | A4 |

Residual arithmetic: 65.4 ≈ GPU 16 (Strata-class floor) + CPU experts 25–40 + boundaries 10–25 ⇒ brackets observed 65.4. [hypothesis — component split is arithmetic, not measured; `--profile-decode` (A6) would make it measured in a future allowed run.]

---

## Open / follow-ups
1. **F-B rest**: full prefix-path report + F0-2/F0-3 log application (decision table above) — subagent's resumed session interrupted; re-dispatch with fresh context. Logs must be located first (item 3).
2. **F-A A2 integer**: count splits via `GGML_SCHED_DEBUG=1` (future run) to confirm 97 vs ~193.
3. **t0006 evidence location**: `docs/evidence/strata-3060-ab/` absent from this worktree — locate main-checkout copy before any further log greps (blocks F-C log value + F-B log pass + any `prompt chunk auto` answer).
4. **F-C runtime half**: grep `prompt chunk auto` in S0-2/S1 logs once (3); `STRATA_VERIFY_PROFILE` run = future rig use (GPU, not now).
5. **F-A A5 provenance**: which run/env actually printed the LRU warning (slots>0 precondition).
6. Strata expert-buft name (`CUDA0_Host` vs plain CPU) observable only at DEBUG / `GGML_SCHED_DEBUG` — confirm in a future run if it matters.
