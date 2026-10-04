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

---

## 4. Correctness plan

### 4.1 G-K1: decode identical tokens, flag on vs off (blocking, must be 100%)

Construction claim: Stage 1 touches prefill only; with `--moe-prefill-stream` on, decode
takes the same outcomes, same splits, same kernels as flag off. Test, not assumption:

- temp 0, 256 new tokens, cells p1k/p4k/p12k (same prompts as Stage 0/0b,
  `stage0/prompts/` sha-pinned): token sequence flag-ON vs flag-OFF (same binary) must be
  **100% identical**. Any difference = bug, stop (same rule as Stage 2's scheduling-only
  gate, `OPTION-C-DESIGN.md:278`).
- Same-binary control (not same-provenance two-binary): A/B differ by runtime flag only,
  so provenance divergence is excluded by construction.

### 4.2 G-K2: KL <= 2x floor (blocking; floor placeholder — never invent a number)

- Prefill numerics **may legitimately differ** (CPU `vec_dot` -> GPU MMQ int8), so G-K1
  does not apply to prefill tokens; G-K2 is the gate
  (`OPTION-C-DESIGN.md:239`, Stage 1 gates).
- Corpus + tool: teacher-forced KLD over `scripts/eval/wikitext-2-raw/wiki.test.raw` via
  the `kl-gate.sh` harness, stage binary vs control (same-provenance binary, §6 rule).
- Floor status: **G-0b NOT RUN** (`STAGE0-RESULTS.md:344-354`) — the CI `llama-perplexity`
  artifact now exists (`stage0/PERPLEXITY-CI.md`; artifact
  `llama-perplexity-sm86-sm120-3808d4e`, sha256 `92594986…`, §6) but the KL floor run
  itself has not happened. **Floor value: [PLACEHOLDER — to be filled by the G-0b run;
  this spec invents no number.]** Stage 1 measurement is authorised to start when the
  floor exists; until then §5's KL leg records raw divergences without a pass/fail.
- Temp-0 nondeterminism caveat: the record shows model-level temp-0 nondeterminism
  (acceptance 0.43 vs 0.80, `OPTION-C-DESIGN.md:379` R6) — the KL gate uses teacher-forced
  distributions, not sampled tokens, precisely to stay out of that trap.

### 4.3 Numerics checklist (pre-registered; each is a check, not an assumption)

| # | Check | Why | Instrument |
|---|---|---|---|
| N1 | CPU `vec_dot` (F32 accumulate [hypothesis — verify `ggml-cpu.c` vec_dot type at patch time]) vs GPU MMQ int8 quant error per expert GEMM | the expected prefill-logit delta source; bounds G-K2 | `--dump-logits/-residual` fork dumps + KL leg; compare flag-ON prefill logits vs flag-OFF on a fixed 512-tok ubatch |
| N2 | Router-census perturbation hazard: the decode-era cached path carries a recorded *router-census perturbation that changes logits* (`design-prefill-fastpath-DRAFT.md:154`) | if the union pass or grouped dispatch perturbs ids/weights seen by the router, prefill routing itself shifts | fixed-ubatch ids dump ON vs OFF must be bit-identical; any ids diff = stop |
| N3 | Authority-invariant refusal: decode-era *authority invariant refusing pools* (same source) | `GROUP_REASON_PREFILL` records + certification must accept `PREFILL_STREAMED` (§3.3) or capture silently falls back | `GGML_SCHED_DEBUG` + `graphs reused` counter: streamed outcome present in plan, no silent `PREFILL_LEGACY` fallback (log the outcome per chunk) |
| N4 | Padding semantics: sched copies `min(expert_size,512)` extra bytes for CUDA MMQ (`ggml-backend.cpp:1859-1867`) | the streamed pool must carry the same padding or MMQ reads NaNs | unit-check pool fill vs sched fill byte-compare on one layer before first GEMM |
| N5 | Shared-expert + norm path unchanged | `qwen4exp.cpp:1227-1234` shared path is outside the union; norm scales (`expert_weights_scale`) must reach the GPU kernel unchanged | prefill-logit spot check with shared-expert-only prompt slice [hypothesis on instrument — record at patch time] |

---

## 5. Measurement protocol (reuses the Stage 0 harness, interleaved paired design)

### 5.1 Harness (no new infrastructure)

- Driver: `.local/wt-stage0/.local/strata-research/arm-stage0.sh` (flags
  `stage0` §1.1 = t0006 F0 verbatim: `--split-mode layer -fit off -ngl 99 --n-cpu-moe 99
  --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0 -c 16384
  --parallel 1 --flash-attn on --jinja --experimental-logs --load-mode none --ple-prefetch
  -b 2048 -ub 2048 --spec-type none`, port 8093, GPU1-only) + parsers
  (`bench3060.py`, `parse_arm_logs.py`) + 1 Hz link sampler (`sample_link.sh`) +
  pinned H2D probe pre/post every arm. `TMPDIR` under `.local/tmp`, never `/tmp`.
- Add one dimension only: `--moe-prefill-stream` ON vs OFF. Everything else identical,
  including `--load-mode none` (so pinning is not a confound, §3.7).
- PCIe probe: `nvidia-smi dmon` alongside cold-prefill arms (Stage 0 §5 method) to re-measure
  per-chunk GiB and link duty ON vs OFF — the mechanism's direct evidence.

### 5.2 Interleaved paired design (as in `stage0b/`, merged)

`stage0b/PRE-REGISTERED.md:32-48` + `STAGE0B-RESULTS.md:1-57`: 8 arms, strictly interleaved
A B A B …, one session, fresh `llama-server` per arm, same-provenance binary (§6).
Per-arm value V = median of 3 reps' `decode_tps` (prefill: median of rep0 cold `prompt_tokens/TTFT`);
paired `delta_k = 100*(V(Tx-k)-V(C-k))/V(C-k)`, report mean/min/max + 95% CI exactly as
Stage 0b did (`STAGE0B-RESULTS.md:22-25`). Stage 0b's lesson is binding: the non-interleaved
+6.62% halved to +3.35% under pairing — **no unpaired prefill claim ships**.

### 5.3 Pre-registered thresholds (frozen before the first arm)

| Gate | Threshold | Basis |
|---|---|---|
| **G-P primary** | cold-12K prefill **>= 1.15x** the same-session OFF control (>= ~231 tok/s against the 201.0 reference; gate computed on the session control, not the reference) | `OPTION-C-DESIGN.md:230-234`; kill below 1.15x. Stretch (recorded, not gating): >= 300 tok/s (1.5x); Strata 605.9 is ceiling reference only |
| G-D / G-R | decode and every non-prefill axis regress **<= 3%** vs same-session control, flag ON and OFF | `OPTION-C-DESIGN.md:171` (3% > ~1-2% session noise [measured]) |
| G-K1 | decode tokens 100% identical ON vs OFF (§4.1) | blocking |
| G-K2 | prefill KL **<= 2x floor** (§4.2; floor placeholder until G-0b) | blocking for ship; raw divergences recorded meanwhile |
| G-V | VRAM after load **<= 11000 MiB** | §3.5 arithmetic says ~7700-8100 |
| G-L | pinned H2D probe **6.00-6.30 GB/s** pre+post; `gen1 AND util>=20 = 0` samples | `STAGE0-RESULTS.md:21` verbatim |
| G-G | `graphs reused` >= control, zero `post_decode() failed`, zero HTTP 500 | `OPTION-C-DESIGN.md:374` R1 (D3 crash precedent) |
| Mechanism check | per-chunk GiB ON <= OFF; link duty ON > OFF (dmon) | proves the overlap mechanism, not just the wall |

Tail-prefill first measurement (Stage 0 §8 handoff): same dmon method on a 731-token
turn-2-shaped prefill — resolves union-saturation vs per-token-scaling for the tail
(C7 `[derived]`); record, not gate.

---

## 6. CI path: fork branch builds the rig artifact without local builds

### 6.1 What PR #160 already built (do not re-do)

Fork PR `ddvnguyen/llama.cpp#160` (`ci/perplexity-artifact`, base `hydra-fork`, **not to be
merged**) added an **artifact mode** to the existing `hydra-build.yml` + `build-combo.sh`
(`stage0/PERPLEXITY-CI.md:85-164`): new `workflow_dispatch` input `build_llama_perplexity`
emits one combo `{arch sm86-sm120, binary llama-perplexity, cuda 13.2, arch 86;120, mode
artifact}`; ghcr login / build-push / deploy-reminder steps are skipped for `mode=artifact`
and replaced by `actions/upload-artifact` (binary + `*.so*` + `ldd.txt` + `build-info.txt`);
`build-combo.sh` takes an optional 10th arg `MODE` (`image` default = unchanged behaviour;
`artifact` = stage + exit before registry/podman, binary never executed). Configure flags
are byte-identical to the `llama-server` combo (`CMAKE_CUDA_ARCHITECTURES=86;120`, Release,
`GGML_CUDA=ON`, `GGML_CUDA_FORCE_CUBLAS=ON`, `GGML_RPC=ON`, `GGML_CUDA_FA(_ALL_QUANTS)=ON`,
`GGML_CUDA_GRAPHS=ON`, `GGML_CUDA_NCCL=ON`, `BUILD_SHARED_LIBS=ON`, RPATH `$ORIGIN`).
It produced `llama-perplexity-sm86-sm120-3808d4e` (660 MB, `cuobjdump`: 380 cubins
alternating sm_86/sm_120a) from source commit `7a03f921d` (+2 CI-only workflow commits).

### 6.2 Minimal extension for Stage 1: `llama-server` in artifact mode

The artifact-mode plumbing is binary-agnostic except for two allowlist points
[hypothesis on exact lines — re-verify against `hydra-build.yml` at patch time, the
`PERPLEXITY-CI.md:96-151` diff is the map]:

1. `hydra-build.yml` `resolve`: accept `build_llama_server=true` together with
   `mode=artifact` (today only `build_llama_perplexity` routes to the artifact combo;
   `gh pr diff 160` could not render the full 100-file PR body here — HTTP 406
   diff-too-large — so the dev agent re-reads the two workflow files, not the PR page).
   Proposed combo, mirroring the perplexity one exactly:
   `{arch sm86-sm120, binary llama-server, cuda_version 13.2, cuda_arch 86;120, mode artifact}`.
2. `build-combo.sh`: no change needed if `MODE` is already positional-arg-10 and the
   binary name is parameterised (it is: `cp $BUILD_DIR/bin/$BINARY`, `PERPLEXITY-CI.md:138-151`).
   If a per-binary build-target case exists, add the `llama-server` target beside it.

Dispatch mirrors the approved one (owner-approved pattern, artifact only, cloud runner —
cloud because the rig host cannot take a CUDA build mid-session, `PERPLEXITY-CI.md:70-83`):

```bash
gh workflow run hydra-build.yml --repo ddvnguyen/llama.cpp --ref <stage1-ci-branch> \
  -f build_llama_engine=false -f build_llama_server=true -f build_llama_perplexity=false \
  -f arch_sm86_sm120=true -f arch_sm60=false \
  -f runner_target=cloud -f execution_mode=matrix -f runner=cloud
```

New branch per stage binary (e.g. `ci/stage1-prefill-stream`), created from the exact
stage commit, carrying only the workflow allowlist diff; **never merged** (same rule as #160).
The branch's only purpose is to let the workflow run from a non-default ref
(`PERPLEXITY-CI.md:89-92` rationale: `workflow_dispatch` only registers workflows that
exist on the default branch).

### 6.3 Same-provenance rule (binding for every A/B in §5)

- The G-P/G-D A/B uses **one artifact binary**; arms differ by runtime flag only
  (`--moe-prefill-stream` on/off). Provenance divergence is excluded by construction.
- The G-K2 floor uses the **perplexity artifact of the same source commit** (already built:
  `3808d4e` == `7a03f921d` + CI-only workflow commits, `PERPLEXITY-CI.md:188`).
  Never compare a rig-local build against a CI build numerically: the four recorded
  configure deltas (`GGML_NATIVE`, `GGML_CUDA_FORCE_CUBLAS`, `GGML_RPC`,
  `GGML_CUDA_FA_ALL_QUANTS`, `PERPLEXITY-CI.md:42-65`) forbid it. G-0b is build-vs-itself
  for exactly this reason.
- If a future stage binary is built with a different `runner_target`, the floor is
  re-measured with a binary of the same provenance (`PERPLEXITY-CI.md:200-202`).

---

## 7. Risks, estimate, riskiest assumption, GPU-free falsification

### 7.1 Risk list (inherits `OPTION-C-DESIGN.md:368-381` R1-R8; only Stage 1 deltas below)

| # | Risk | Why it is real | Mitigation / gate |
|---|---|---|---|
| R1' | CUDA-graph capture refuses the new outcome | compat veto is narrow (`ggml-cuda.cu:4404-4412` per OPTION-C); prefill capture checks at `ggml-cuda.cu:6905-6917` [verified in source] must accept `PREFILL_STREAMED` | G-G (§5.3) + N3 (§4.3): log the outcome per chunk; silent fallback to legacy = stop |
| R2' | Router-census / authority hazards inherited from the decode cached path | recorded blockers on that path (`design-prefill-fastpath-DRAFT.md:154`) | N2/N3 checks (§4.3), not assumptions |
| R3' | Union saturates to the bank at 2048 chunks (B-union -> B-bank) | §7.5: 68-79% of declared already at 2048; saturation only costs volume, and whole-bank still fits (§3.5) | sizing cap = whole-bank; mechanism check (§5.3) records actual |
| R4' | Pageable source halves H2D if pinning regresses | loader downgrade `llama-model-loader.cpp:1324-1332`; draft 4.6x->2.4x split (CUDA0 numbers, not a 3060 prediction) | all arms run `--load-mode none` (Stage 0 precedent); record source kind |
| R5' | Temp-0 nondeterminism contaminates correctness reads | acceptance 0.43 vs 0.80 in the record (OPTION-C R6) | G-K1 on token sequences is same-binary flag-flip (deterministic path); G-K2 teacher-forced, never sampled |
| R6' | Session drift swallows a ~15-30% prefill win (cf. Stage 0b halving +6.62% -> +3.35%) | C1 drift +10% day-over-day (`STAGE0-RESULTS.md:486-494`) | interleaved pairing (§5.2) + 95% CI; gate on session control, never the 201.0 reference |

Deliberately not re-litigated: k stays 10, no `--override-kv`, one GPU = one task, N park,
Option D default (`OPTION-C-DESIGN.md:44-53` §0.2; §3.5 exclusions).

### 7.2 Agent-day estimate (implementation + CI + measurement)

| Work | Agent-days | Notes |
|---|---|---|
| Implementation (§3: flag + outcome + union pass + copy-engine ungate + pool + grouped dispatch) | **5-7** | inherits OPTION-C Stage 1 band (`OPTION-C-DESIGN.md:208`); bulk is `moe-cache.cu` plan/authority certification |
| CI extension (§6.2: allowlist + branch + dispatch + artifact verify) | **1** | plumbing exists (#160); two-file diff, `file`/`ldd`/`cuobjdump` verify only, binary never executed |
| Measurement (§5: 8-arm paired session + dmon + KL leg + report) | **2** | one rig session + G-0b floor run dependency (floor binary exists; rig time is the cost) |
| **Total** | **8-10** | inside OPTION-C's 15-25 overall only as the Stage 1 slice; Stages 3/4 separately priced |

### 7.3 The single riskiest assumption

**That the grouped GPU path can consume a ~300-450-expert/layer union (at ~17 tokens per
expert, the draft's tiny-GEMM shape, `design-prefill-fastpath-DRAFT.md:52`) plus overlapped
H2D fast enough to clear 1.15x — i.e. that cold prefill is CPU-expert-FLOP-bound (the
D0-L2 premise, now barred from its `--override-kv` instrument by §0.2) AND that link duty
can rise 51% -> ~85%+.**
If prefill is instead dominated by something the union does not move (attention halves,
host grouping sync, MMQ shape inefficiency at 17 rows), the wall will not move 15% no
matter how well the streaming is built. The ceiling arithmetic (§3.6: 1.9-2.4x fully
overlapped) says the prize exists; only the rig can say it is reachable — which is why
G-P kills below 1.15x rather than tuning further.

### 7.4 Cheap GPU-free falsification test (done — CPU-only analysis, reported numbers)

Question: does the per-layer distinct-expert union for a 2048-token chunk fit a streamed
design (B-union viable), or does it saturate to the bank (B-bank fallback)? Three
in-repo/offline sources, no GPU, no build:

1. **Prefill windows (draft, offline route traces):** 428 non-overlapping 512-token
   windows, k=10: **union mean 292.5/512 = 57.1% (sd 38.1)** = 521.8 MiB/layer
   (`design-prefill-fastpath-DRAFT.md:42-49`).
2. **Decode probe ranks (in-repo `tools/atlas/expert-ranks-db4eab0cbde667d5.json`,
   computed for this spec):** 48 layers x 512 positions (2 runs x 256 gen, k=10):
   per-layer distinct **min 198 / max 411 / mean 307.6 / median 313.0** =
   **mean 60.1%, median 61.1%** = 549 MiB/layer mean, 25.72 GiB over 48 layers.
   (Lower bound for prefill chunks: decode-only positions, smaller sample.)
3. **Stage 0 measured (rig `dmon` + SPLIT-1):** per-2048-chunk actual **31.0 / 32.5 /
   35.9 GiB** = **68-79% of declared 45.67 GiB** (`STAGE0-RESULTS.md:256-262`);
   96-token warmup already streams 17.16 GiB (38% of declared, `:271-272`) — no
   cheap-chunk regime.

Reading: the 512-token union sits at **57-61%** in two independent sources, and the
2048-chunk stream at **68-79%** of declared — rising with chunk size as expected, but
**not saturated**: even the top (79%) leaves ~21% of declared unstreamed, and the
sizing cap (whole-bank 1.78 GiB double buffer, §3.5) fits G-V regardless. The earlier
"one turn touches ~54% of experts per layer" is consistent with this band (57-61% at
512 positions; the ranking `hit_pct` medians 54.85/56.3 are a different quantity — cache
hits, not union — in the same band; do not conflate them) [hypothesis on the exact
provenance of the ~54% figure — no single source line was found; the band agreement is
the checkable claim].

**Falsification verdict: NOT falsified.** B-union is viable; B-bank is a fitting fallback,
not a redesign. Real bytes to stream per 2048-chunk: **31-36 GiB measured** (the sched
slice copy already filters unused experts) — the design moves the same bytes overlapped
with GPU compute instead of host-waited ahead of CPU compute. If a future trace shows
union_2048 -> ~512/layer, only the buffer variant changes (1.02 -> 1.78 GiB), both under
G-V.

### 7.5 Open items for the dev agent (checklist, not blockers)

1. Re-verify `ggml-cpu.c:2357-2361` (`n_tasks`) + `vec_dot` accumulate type (N1) in the
   stage branch — export line numbers drift.
2. Re-verify the two non-re-read cites before relying on them in code:
   `prefill.cpp:1690-1694` (per-entry wait) and `moe_mmq.cu:125-151` (both via
   `OPTION-C-DESIGN.md:110,112`).
3. Confirm the `hydra-build.yml` allowlist lines for §6.2 (PR #160's full diff was
   unrenderable here — HTTP 406; read the two workflow files directly).
4. Fill the G-K2 floor placeholder the day the G-0b run lands; until then record raw KL
   without pass/fail (§4.2).

---

## Change summary

**Changed:** created this document only
(`docs/evidence/strata-gap/STAGE1-IMPL-SPEC.md`) on branch `docs/811-stage1-spec`, in
four incremental commits. No source, config, harness, or export file touched; no build,
no server, no GPU/rig use; `/mnt/WorkDisk/strata` and `.local/q2g/src` opened read-only;
nothing written under `/tmp`.

**Assumptions:** `.local/q2g/src` == fork commit `7a03f921d` (tree-verified,
`stage0/PERPLEXITY-CI.md:8-38`); Strata v0.1.29 paths as cited; Stage 0/0b numbers as
reported in their results docs; the ~54% turn-coverage figure's exact provenance is
unresolved (§7.4) and is not load-bearing.

**Risks / open questions:** §7.1-§7.5 — the load-bearing ones are R1' (graph capture),
R2' (router/authority hazards), and the §7.3 FLOP-bound assumption behind the 1.15x gate.



