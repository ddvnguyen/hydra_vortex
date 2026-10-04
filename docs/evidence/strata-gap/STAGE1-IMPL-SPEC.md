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

---

## 2. Target design: per-layer union streaming, double-buffered device pool, dedicated copy stream

### 2.1 Shape (adapted from Strata's prefill regime)

Strata's prefill streams experts **routing-independently**: the copy engine moves a layer's
experts through a staging ring while earlier layers compute, with **one**
`cudaStreamSynchronize` per MoE layer per chunk for host grouping + ordering
(`src/prefill/prefill.cpp:1503-1512` [verified in source]) and per-entry
`cudaStreamWaitEvent` for streamed experts (`OPTION-C-DESIGN.md:110` cites
`prefill.cpp:1690-1694` [verified in that doc, not re-read here]).
Ring sizing: `ring_slots()` = **384** slots when pinned share >= 0.9, else **96**
(`prefill.cpp:77-88` [verified in source]); `STRATA_PREFILL_RING` overrides.
Experts **stay quantized**; compute is MMQ int8 tensor-core GEMM
(`OPTION-C-DESIGN.md:112` cites `src/prefill/moe_mmq.cu:125-151`).
Cold cache still streams every blob each chunk — yet 3x the fork.

The fork port keeps that shape but replaces Strata's pack/arena inputs with the fork's own
tensors:

1. **Per-layer union pass per ubatch.** After routing ids are known for the ubatch, compute
   the distinct-expert set per layer (host pass over the ids tensor — the same ids the
   sched already pulls to host at `ggml-backend.cpp:1836-1853`, so no new readback).
   Stream **those experts only (not all 512)** from host-resident weights to the device pool.
2. **Double-buffered device pool, layer-ahead.** Two pool slots alternate: while layer L
   computes its grouped GEMM from slot A, layer L+1's union streams into slot B on a
   **dedicated copy stream**. Flip on event landing (Strata's
   `pending`-applied-when-event-lands shape, `generate.cpp:4797-4824` per `OPTION-C-DESIGN.md:84`).
3. **GPU grouped GEMM on the pool.** The prefill `mul_mat_id` nodes execute against the
   resident pool (grouped path) instead of falling to `PREFILL_LEGACY` CPU compute.
4. **Must NOT require `--moe-expert-cache-size > 0`.** That flag costs **40% decode**
   (F1 9.28 vs F0 15.30 [measured]) and is a load-time buft override (`src/llama.cpp:329`
   [verified in source]) that cannot be enabled for prefill only. The pool is **phase-scoped**:
   allocated for prefill, sized as a double buffer only (~1.0-1.8 GiB, §3.3), freed or
   parked after prefill. Fallback if a new pool proves too large: reuse `moe-cache` staging
   with the pool sized for the double buffer only (2 x 292 x 1.7838 MiB ≈ 1.02 GiB
   [derived], `design-prefill-fastpath-DRAFT.md:4`).
5. **Decode untouched with the new flag off (default off).** Outcome gating keeps
   `DECODE_GROUPED`/`DECODE_LEGACY` selection byte-identical when the flag is unset;
   G-K1 requires 100% identical decode tokens flag on vs off (§4).

### 2.2 Why the union (not the bank, not per-miss)

- The draft's offline union table (64 route traces / 428 non-overlapping 512-token prefill
  windows): **k=10 union = 292.5/512 (sd 38.1)** = 57.1% of the bank
  (`design-prefill-fastpath-DRAFT.md:42-49`). Per-expert 1.7838 MiB -> **521.8 MiB/layer/ubatch**.
- CPU-only union analysis done for this spec (§7.5): decode-probe mean 307.6/512 (60.1%),
  Stage 0 measured 68-79% of declared per 2048-chunk. Three independent sources band
  **57-79%** — the union is a bit over half the bank at 512 tokens and well under it at 2048.
- Per-miss fetching is explicitly out: it reintroduces the in-step readback + decision cost
  that killed the decode-lookahead design (36.3 us/invocation break-even,
  `design-prefill-fastpath-DRAFT.md:152`). The unit here is a **whole-layer batch transfer
  amortised over the ubatch** — no predictor, no per-token decision, no readback beyond the
  ids the base path already pulls.

### 2.3 Flag name candidates (fork's own flag family)

Owner rule: upstream params untouched; new behaviour behind fork-owned flags
(`OPTION-C-DESIGN.md:52` §0.2; §21a pending surface `--moe-expert-home`,
`--moe-expert-pins`, `--moe-expert-pin-count`). Candidates, in preference order:

1. `--moe-prefill-stream` (OPTION-C placeholder, `OPTION-C-DESIGN.md:250`) — matches the
   `--moe-expert-cache-*` family (`arg.cpp:2875-2895` [verified in source]), reads as the
   prefill counterpart of the decode cache. **Recommended.**
2. `--moe-prefill-gpu-stream` — more explicit about where compute runs; longer.
3. `--prefill-moe-stream` — breaks the `--moe-*` prefix convention; not recommended.

Env form follows the family pattern (`LLAMA_ARG_MOE_EXPERT_CACHE_SIZE` precedent,
`arg.cpp:2886`): `LLAMA_ARG_MOE_PREFILL_STREAM`. Type: boolean (default off). No `N`
size knob in v1 — the pool sizes itself from the measured union (§3.3); a size knob is a
v2 tuning escape hatch, not a v1 requirement.

---

## 3. Diff-style patch sketch per file (NOT applied — design only)

### 3.1 `common/arg.cpp` — new flag (~15 lines)

```diff
+    add_opt(common_arg(
+        {"--moe-prefill-stream"},
+        "Stream prefill MoE expert unions to GPU on a dedicated copy stream with a\n"
+        "phase-scoped double buffer; decode untouched. 0 disables (default). "
+        "Does not require --moe-expert-cache-size.",
+        [](common_params & params) {
+            params.moe_prefill_stream = true;
+        }
+    ).set_env("LLAMA_ARG_MOE_PREFILL_STREAM"));
```

plus the `bool moe_prefill_stream = false;` field on `common_params` (beside
`n_moe_expert_cache_slots`, `arg.cpp:2875-2886` region) and pass-through into
`llama_cparams`/`llama_model_params` as the codebase's existing MoE knobs do.
[hypothesis on the exact params struct plumbing: mirror `n_moe_expert_cache_slots`;
re-verify at patch time.]

### 3.2 `ggml/src/ggml-cuda/moe-cache.cuh` — new outcome + pool descriptor (~30 lines)

```diff
 enum ggml_cuda_moe_graph_outcome : uint32_t {
     GGML_CUDA_MOE_GRAPH_OUTCOME_PREFILL_LEGACY = 0,
     GGML_CUDA_MOE_GRAPH_OUTCOME_DECODE_GROUPED,
     GGML_CUDA_MOE_GRAPH_OUTCOME_DECODE_LEGACY,
     GGML_CUDA_MOE_GRAPH_OUTCOME_ERROR,
+    GGML_CUDA_MOE_GRAPH_OUTCOME_PREFILL_STREAMED,  // Stage 1: union in device pool, grouped GPU GEMM
 };
```

plus a phase-scoped pool descriptor (device pointer x2, per-slot layer tag + ready event,
union list per layer, pool byte size; see §3.4). The outcome keeps `PREFILL_LEGACY = 0`
so every existing comparison keeps its meaning when the flag is off.

### 3.3 `ggml/src/ggml-cuda/moe-cache.cu` — plan, copy engine, union pass (~250-400 lines, the bulk)

```diff
-    const bool pure_prefill = cached_prefill && !cached_decode;
-    if (pure_prefill) {
-        plan->outcome_ = GGML_CUDA_MOE_GRAPH_OUTCOME_PREFILL_LEGACY;
+    const bool pure_prefill = cached_prefill && !cached_decode;
+    if (pure_prefill && !prefill_stream_enabled) {          // flag off: byte-identical legacy path
+        plan->outcome_ = GGML_CUDA_MOE_GRAPH_OUTCOME_PREFILL_LEGACY;
```

at `:9768-9770` (and mirror at `:9883`, `:10360-10362` — all three decision points must
agree or the plan/execution/certified-inventory checks at `ggml-cuda.cu:6905-6911` will
refuse the capture [verified in source]).

```diff
 // resolve_streams / has_*_grouped_* predicates, e.g. :4589, :4618, :4626, :4638
-        if (plan_->outcome_ != GGML_CUDA_MOE_GRAPH_OUTCOME_DECODE_GROUPED) {
+        if (plan_->outcome_ != GGML_CUDA_MOE_GRAPH_OUTCOME_DECODE_GROUPED &&
+            plan_->outcome_ != GGML_CUDA_MOE_GRAPH_OUTCOME_PREFILL_STREAMED) {
```

un-gating the copy engine for the new outcome only. New code (one function each):

- `prefill_union_pass(ids_host, T, K, n_expert) -> bitset + count + ordered list` — host pass
  shaped exactly like the sched's existing `used_ids` bitset loop
  (`ggml-backend.cpp:1841-1853` [verified in source]); runs once per MoE layer per chunk
  during the single per-layer host sync (Strata's `kPfHostGroup` shape,
  `prefill.cpp:1503-1512`).
- `prefill_stream_issue(layer, union, pool_slot, copy_stream)` — `cudaMemcpyAsync` runs of
  consecutive experts (same consecutive-run grouping as `copy_experts`,
  `ggml-backend.cpp:1855-1892`) onto the dedicated copy stream, `cudaEventRecord` per slot.
- `prefill_stream_wait(pool_slot, compute_stream)` — `cudaStreamWaitEvent` before the
  layer's grouped GEMM (Strata's per-entry wait shape, `prefill.cpp:1690-1694` per
  `OPTION-C-DESIGN.md:110`).

Authority/certification hazard (must-satisfy, not optional): the `GROUP_REASON_PREFILL`
records (`:9791-9799`) + `certified_inventory` + `has_certified_complete_mmid_inventory`
checks (`ggml-cuda.cu:6905-6917`) must accept the new outcome, or graph capture refuses.
The sketch keeps prefill group records as-is and adds the streamed outcome as a
certification-accepting variant; the decode-era *authority invariant refusing pools*
(`design-prefill-fastpath-DRAFT.md:154`) is the known hazard — §4's numerics test, not an
assumption, decides whether the prefill group path inherits it.

### 3.4 Data structures

```cpp
// Phase-scoped (prefill only). Freed/parked when prefill ends; never touched by decode.
struct moe_prefill_stream_pool {
    void*      slot[2];          // device buffers, pool_bytes each
    size_t     pool_bytes;       // sized at first prefill chunk (§3.5)
    int        resident_layer[2];// which layer's union is in each slot, -1 = empty
    cudaEvent_t ready[2];        // recorded on copy_stream when the slot's union lands
    uint32_t   front;            // slot the current layer computes from
};
struct moe_prefill_union {
    uint32_t ids[512];           // ordered distinct experts, count <= 512
    uint32_t count;
};
```

No LRU, no frequency, no eviction: the pool is a **two-slot flip-flop**, not a cache.
Admission policy = the union; replacement policy = alternation. This is why Stage 1 needs
no residency machinery (§1.1/§1.2 of `OPTION-C-DESIGN.md` stay Stage 3 scope).

### 3.5 Buffer sizing for 12 GB (RTX 3060: F0 uses 6271 MiB; gate G-V <= 11000 MiB)

Inputs: F0 VRAM after load **6271 MiB** [measured, `STAGE0-RESULTS.md:134`];
per-expert **1.7838 MiB** (`design-prefill-fastpath-DRAFT.md:34`, GGUF table);
union@512tok **292.5** (sd 38.1) (same); measured per-2048-chunk stream **31-36 GiB**
(`STAGE0-RESULTS.md:256-262`).

| Pool variant | Arithmetic | Bytes |
|---|---|---:|
| Union-sized (512-tok union) | 2 x 292.5 x 1.7838 MiB | **1043.5 MiB (~1.02 GiB)** |
| Union-sized +3sd margin | 2 x (292.5+3x38.1=406.8) x 1.7838 | **1451 MiB (~1.42 GiB)** |
| Whole-bank (saturation-proof) | 2 x 512 x 1.7838 MiB | **1826.6 MiB (~1.78 GiB)** |
| 2048-chunk union [hypothesis §7.5] | 2 x ~400 x 1.7838 MiB | **~1427 MiB (~1.39 GiB)** |

Totals vs gate: 6271 + 1451 = **7722 MiB** (recommended sizing); worst case
6271 + 1827 = **8098 MiB**. Headroom to G-V (11000 MiB): **~2900-3300 MiB**;
to the 12288 MiB ceiling: ~4.2-4.6 GiB. **G-V passes with margin in every variant,
including whole-bank saturation.** Size the pool at first prefill chunk as
`2 x max(union_measured, 292.5+3sd) x 1.7838 MiB`, capped at whole-bank; the cap is the
saturation-proof fallback, not a second knob.

### 3.6 Event/sync plan

| Point | Primitive | Source shape |
|---|---|---|
| One host sync per MoE layer per chunk (host grouping + ordering) | `cudaStreamSynchronize(compute_stream)` after ids D2H, before union pass | Strata `prefill.cpp:1512` [verified in source] |
| Union H2D on dedicated stream | `cudaMemcpyAsync` consecutive-expert runs + `cudaEventRecord(slot.ready)` on `copy_stream` | fork `copy_stream` infra, un-gated for the new outcome (§3.3) |
| Compute waits for stream, host does not | `cudaStreamWaitEvent(compute_stream, slot.ready)` before the layer's grouped GEMM; slot flip after | Strata per-entry wait `prefill.cpp:1690-1694` (via `OPTION-C-DESIGN.md:110`) |
| No decode-path sync change | decode keeps `DECODE_GROUPED`/`DECODE_LEGACY` outcomes; `ggml-cuda.cu:6927` overlap untouched | flag-off byte-identical (§2.1.5) |

Expected overlap: link duty 51% -> ~85-95% [hypothesis]; transfer 5.0-5.9 s/chunk hidden
behind GPU GEMM. Ceiling if fully overlapped: ~10.2 s/chunk -> ~4.3-5.2 s/chunk ≈
**1.9-2.4x** [hypothesis, ceiling — the gate is only 1.15x].

### 3.7 Pinned memory vs `--load-mode none` / mmap'd shards

Loader downgrade: `src/llama-model-loader.cpp:1324-1332` [verified in source] rewrites any
host buffer type to the plain CPU buffer when `use_mmap` is set. Pinned expert source
needs `--load-mode none` (restores pinning); measured decode cost **-4.9% = nil**
(`OPTION-C-DESIGN.md:227` [measured, cited]). Stage 0 ran `--load-mode none` on **every**
arm (`STAGE0-RESULTS.md:60-63`), so the G-P control already pays that cost — no new
confound. Strata's own source shape is a mapped-pinned arena + device alias
(`expert_source.cpp:505-537` [verified in source]: `cudaHostAllocMapped|Portable` +
`cudaHostGetDevicePointer`). The fork does **not** need to replicate the alias trick in
v1: pageable source halves prefill H2D (draft's 4.6x vs 2.4x split on CUDA0), but on the
3060 the link floor is 6.1 GB/s pinned-measured and the gate (1.15x) survives pageable;
record the source kind in the measurement report and revisit only if G-P fails.

### 3.8 Hybrid DeltaNet / PLE state unaffected (Stage 0 R2)

Stage 0 answered R2: recurrent/PLE state does **not** ride boundary copies —
`state_cache_entries = 0` in all 24 split blocks, zero `cache_*` names in the 1.8 MB
SPLIT-1 log; RS buffer pinned to CUDA0, 112.57 MiB (`STAGE0-RESULTS.md:390-406`).
Stage 1 moves only `ffn_*_exps` weights and grouped GEMM compute; it does not re-partition
any layer's execution across backends and introduces no new copy source for
`cache_r/s_l*` / `cache_ple_r_l*`. The one hard rule for review: any later change that
moves a layer's MoE compute to CPU while its `cache_*` tensors stay on CUDA0 would
introduce a new 112.57 MiB-per-state-set copy source (`:404-406`) — Stage 1 does not do
this by construction (compute moves CPU->GPU, state stays GPU).

(PART 2/4 committed — §2 target design + §3 patch sketch. Next: §4 correctness + §5 measurement.)

