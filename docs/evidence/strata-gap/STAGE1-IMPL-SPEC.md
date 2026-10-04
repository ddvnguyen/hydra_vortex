# Option C Stage 1 — implementable spec: prefill routing-independent GPU expert streaming

**Epic:** #811 · **Branch:** `docs/811-stage1-spec` (off `epic/811-strata-gap-analysis`)
**Date:** 2026-10-04 · **Status:** READ-ONLY SPEC (no code changed, no build, no rig, no GPU)
**Spec parent:** `OPTION-C-DESIGN.md` Stage 1 (§ "Stage 1", gates G-P/G-D/G-K1/G-K2/G-V/G-L/G-G/G-R)
**Measurement parent:** `STAGE0-RESULTS.md` (PR #822) + `stage0b/STAGE0B-RESULTS.md` (merged, DO NOT SHIP `-t12`)
**Prefill-design parent:** `docs/design-prefill-fastpath-DRAFT.md` Mechanism B (routing-independent streaming)

**Claim tags.** `[verified in source]` = the cited `file:line` was read directly for this spec
(fork = `.local/q2g/src`, export of fork commit `7a03f921d`, tree-verified in
`stage0/PERPLEXITY-CI.md:8-38`; Strata = `/mnt/WorkDisk/strata/src-v0.1.29`).
`[measured]` = from our own runs (`stage0/`, `stage0b/`, t0006).
`[hypothesis]` = reasoned, not yet measured or not yet read. **No number is invented:**
quantitative figures are `[measured]`, `[verified in source]`, pre-registered thresholds,
or explicitly derived with the derivation shown.

**What this spec is for.** A dev agent implements the fork patch from §2-§3, checks it against §4,
measures it with §5, and ships the binary through §6. §7 lists what can still kill it.
Stage 2 is **cancelled** (`STAGE0-RESULTS.md:29-33`); Stage 1 is **blocked on G-K2**
(`STAGE0-RESULTS.md:19,34-37,344-354`) — this spec does not unblock it, it makes the blocked
work implementable the day the floor exists.

---

## 0. Stage 1 in one paragraph

Cold prefill on the RTX 3060 pulls **31-36 GiB per 2048-token chunk** over PCIe
(`STAGE0-RESULTS.md:34-37,256-262` [measured]) while the link idles **~49% of the wall**
(`:265-270` [measured, derived]) and the expert GEMMs run as plain CPU `vec_dot`.
Stage 1 streams the **per-layer union of routed experts for the ubatch** (not all 512)
to a **phase-scoped, double-buffered device pool on a dedicated copy stream while the
previous layer computes**, and runs the expert GEMMs on the GPU (grouped MMQ int8).
It must **not** require `--moe-expert-cache-size > 0`, leaves decode bit-identical with the
new flag off (default off), and clears **G-P: cold-12K prefill >= 1.15x of 201.0 tok/s**
(>= 231 tok/s vs the same-session control).

---

## 1. The exact current prefill path (pure-prefill ubatch, `--n-cpu-moe 99`, host-resident experts)

### 1.1 Graph construction: `build_moe_ffn` -> `mul_mat_id` nodes

| Step | Location [verified in source] | What happens |
|---|---|---|
| MoE FFN node builder | `.local/q2g/src/src/llama-graph.cpp:2076-2118` (thin overload) -> `:2120-2195+` (full builder) | builds router logits (`build_lora_mm(gate_inp, cur)`, `:2152`), gating (`:2167-2188`), expert selection; flags router tensor `GGML_TENSOR_FLAG_MOE_ROUTER` (`:2160`) |
| qwen4exp call site | `.local/q2g/src/src/models/qwen4exp.cpp:1207-1225` (`build_layer_ffn` -> `build_moe_ffn(cur, ffn_gate_inp, ffn_up_exps, ffn_gate_exps, ffn_down_exps, nullptr, n_expert, n_expert_used, LLM_FFN_SILU, ...)`) | passes the three expert banks + optional fused `ffn_gate_up_exps`; shared-expert path follows (`:1227-1234`) and is **not** part of the streamed union (dense, stays where placed) |
| Expert weight placement | `--n-cpu-moe 99` -> `common/arg.cpp:2854-2863` (`-ncmoe/--n-cpu-moe` -> `llm_add_n_cpu_ffn_overrides(value, LLM_FFN_EXPS_REGEX, ...)`) | first 99 layers' `*exps` tensors pinned to CPU buffers; with `--moe-expert-cache-size 0` (`arg.cpp:2875-2886`, default 0) no cache override exists, so **all expert tensors are host tensors** |
| Cache override (why cache>0 is all-or-nothing) | `.local/q2g/src/src/llama.cpp:318-367` | `moe_expert_cache_slots > 0` **prepends** pattern `\.ffn_(up\|down\|gate\|gate_up)_(ch\|)exps` (`:350-351`) so it wins first-match over any `-ncmoe` override; warning `:361-364`. Hence no per-layer hybrid is expressible today (`OPTION-C-DESIGN.md:131` row 2) |

### 1.2 Scheduler: backend assignment, splits, and why the GPU never sees prefill expert work

| Step | Location [verified in source] | What happens |
|---|---|---|
| Split on backend change | `ggml/src/ggml-backend.cpp:1348-1366` (`node_backend_id != cur_backend_id \|\| need_new_split` cuts a new split) | expert `mul_mat_id` nodes are CPU-assigned (host weights + offload rule below); surroundingոrms/attn stay CUDA0. Stage 0 counted **98 splits / 97 boundaries per decode step**, strictly alternating 49 CPU + 49 CUDA0 (`STAGE0-RESULTS.md:208-224` [measured]) |
| GPU offload rule never fires for MoE | `ggml/src/ggml-cuda/ggml-cuda.cu:8240-8243` (`MUL_MAT_ID` batch key = `op->ne[2]`) + `:8249-8252` (`offload iff batch >= op_offload_min_batch_size`) + `:8488` (default `GGML_OP_OFFLOAD_MIN_BATCH=32`, env-overridable) | for `mul_mat_id`, `ne[2]` = **k = 10** < 32, so the node stays on CPU **regardless of token count** (`OPTION-C-DESIGN.md:139` row 10). This is the single line that strands prefill expert FLOPs on the CPU |
| Boundary drain is host-blocking | `ggml-backend.cpp:1774-1780` (`events NULL` -> `ggml_backend_synchronize(prev)`) | every boundary pays a host-side drain; Stage 0 proved it **nested, not wall-additive** (380 syncs/tok inside GPU-active; share 0.93 ms transfer + ~0.5-1.5 ms bounded host overhead, Stage 2 kill at 1.4-2.4 ms << 6 ms) (`STAGE0-RESULTS.md:29-33,410-432`) |
| Cross-backend copy, sync-or-async | `ggml-backend.cpp:1900-1916` (`cpy_tensor_async`, else blocking `ggml_backend_tensor_copy` after `ggml_backend_synchronize`) | activations cross CPU->GPU here; weights take the expert-slice path below |

### 1.3 Where the 31-36 GiB/chunk PCIe pull originates (who copies, which stream, which sync)

The prefill weight stream is the scheduler's **used-expert slice copy**, not the moe-cache copy
engine (which is decode-gated, §1.4):

1. **Used-expert detection (host readback, per split).** `ggml-backend.cpp:1804-1853`:
   `ggml_backend_synchronize(input_backend)` (`:1816`), then `ggml_backend_tensor_get_async` +
   `ggml_backend_synchronize(ids_backend)` to pull the ids tensor to host (`:1836-1839`),
   then a host bitset pass over `ids[ne1 x ne0]` marking `used_ids` (`:1841-1853`).
   Cost: one device round-trip + one full ids scan per MoE weight input per prefill split.
2. **Grouped range copy (consecutive-run grouping).** `:1855-1892` (`copy_experts(first,last)`):
   consecutive used experts are copied as one span via `ggml_backend_tensor_set_async`
   (`:1862-1867`, with a `min(expert_size,512)` pad for CUDA MMQ, `:1859-1866`);
   gaps between runs are **not** copied (this grouping + device-reuse is why actual <
   declared, `STAGE0-RESULTS.md:262-264`).
3. **Stream/sync.** The copy is issued by the sched dispatch on the split (CUDA) backend;
   when `cpy_tensor_async` is unavailable the fallback is a **blocking host-waited copy**
   (`:1902-1916`: `ggml_backend_synchronize(input_backend)`, event-or-backend sync,
   then `ggml_backend_tensor_copy`). There is **no dedicated copy stream and no
   layer-ahead overlap**: each layer's weights arrive just-in-time, host-waited, before that
   layer's (CPU) GEMMs. [hypothesis on the exact CUDA stream handle: the split backend's
   compute stream, not a separate copy stream — the source shows no second stream here;
   the separate `copy_stream` infra lives in moe-cache and is decode-gated.]
4. **Declared vs actual.** SPLIT-1 declares **45.67 GiB** per prefill-shaped block
   (weights **45.54 GiB** = 141 tensors `blk.{0..46}.ffn_{gate,up,down}_exps.weight` + 136 MiB
   activations) (`STAGE0-RESULTS.md:211-220` [measured]); `dmon` measures **31-36 GiB/chunk
   actual** (p4k rep0: 31.0, p12k rep0: 32.5, session-residual: 35.9 GiB/chunk)
   (`:256-262` [measured, ±C4 drift]). Ratio actual/declared = **68-79%**.
   Link duty during cold prefill ≈ **51%** (3.14 GiB/s vs 6.12-6.15 floor, peak 6.689 GB/s,
   34 s >= 5.5 GB/s) (`:265-267`); **49-58% of the 10.2 s/chunk wall is PCIe transfer**
   (31-36 GiB / 6.15 GiB/s = 5.0-5.9 s) (`:268-270`).

### 1.4 The moe-cache prefill outcome: planned groups, forced legacy compute

| Step | Location [verified in source] | What happens |
|---|---|---|
| Outcome enum | `ggml/src/ggml-cuda/moe-cache.cuh:317-322` (`PREFILL_LEGACY=0, DECODE_GROUPED, DECODE_LEGACY, ERROR`) | the vocabulary Stage 1 extends |
| `pure_prefill` forces legacy | `ggml/src/ggml-cuda/moe-cache.cu:9768-9770` (`pure_prefill = cached_prefill && !cached_decode` -> `outcome_ = PREFILL_LEGACY`) + `:9883`, `:10360-10362` (same at the other two decision points) | **expert GEMMs take the legacy (CPU) compute path** even though prefill group records (`GROUP_REASON_PREFILL`, `:9791-9799`) are planned right below (`:9772-9806`) |
| Copy engine decode-gated | `moe-cache.cu:4589` (`resolve_streams` skips non-`DECODE_GROUPED`), `:4618`, `:4626`, `:4638` (grouped-stream predicates all require `DECODE_GROUPED`) | the `copy_stream` + `cudaMemcpyBatchAsync` machinery exists but **never engages during prefill** (`OPTION-C-DESIGN.md:138` row 9) |
| Graph capture + prefill legacy | `ggml-cuda.cu:6905-6911` (`PREFILL_LEGACY` + certified inventory path) | prefill graphs are captured around the legacy outcome; decode-overlap (`decode_boundary_overlap`, `:6927`) only applies to `DECODE_GROUPED` |

### 1.5 The CPU kernel that pays the wall

`ggml_compute_forward_mul_mat_id`, `ggml/src/ggml-cpu/ggml-cpu.c:1542-1560+` [verified in
source]: single-thread ids pre-pass, then a work-sharing loop over experts
(`:1663-1694`: `continue` on unrouted `cne1==0`, `chunk_size=16`, NUMA-disables-chunking),
`n_tasks = n_threads` dispatch (`IMPL-GAP-FORK.md` cites `:2357-2361`; re-verify at patch
time [hypothesis on exact line in this export]), plain `vec_dot` IQ3_S, no repack, IQP
multi-token path skipped at decode (`OPTION-C-DESIGN.md:144` row 16). Stage 0 attribution:
**~36.9 ms/tok of the 65.4 budget is CPU-side** (`STAGE0-RESULTS.md:410-432`), and prefill
pays the same kernel once per routed expert per layer per ubatch with far taller
per-expert row counts than decode.

(PART 1/4 committed — §0 background + §1 current path. Next: §2 target design + §3 patch sketch.)
