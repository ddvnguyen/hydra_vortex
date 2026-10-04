# Stage 1b-R — pre-registration + corrected design: **staging overlap**, not FLOP migration

**Epic:** #811 · **Branch:** `docs/811-stage1b-redesign` (off `origin/epic/811-strata-gap-analysis` @ `03d9e73f6`)
**Date:** 2026-10-04 · **Status:** DESIGN + PRE-REGISTRATION (no code changed, no build, no rig, no GPU)
**Supersedes:** the mechanism of `STAGE1-IMPL-SPEC.md` §2-§3 (the spec text stays; §7 ERRATUM below records why)
**Leader decision:** recorded `turn_report` turn 1858 (`d-1c77ef6548`) re-scoping Stage 1b to 1b-R
**Fork PR:** `ddvnguyen/llama.cpp#161` (DRAFT, **never merge**), branch `feat/moe-prefill-stream`

**Claim tags.** `[verified in source]` = cited `file:line` read directly in
`.local/fork-ci` @ `f8ec04f1`. `[measured]` = from our own rig runs (`stage0/`, `stage0c/`).
`[hypothesis]` = reasoned, not measured. **No number is invented** — every quantity below is
`[measured]`, `[verified in source]`, a pre-registered threshold, or derived with the
arithmetic shown.

---

## 0. TL;DR

The Stage 1 spec's mechanism is **void** (§1). Prefill expert GEMMs **already run on the
GPU**; there is no CPU→GPU FLOP migration to win. What remains — and what Stage 1b-R does —
is the *other* half of the same measured fact: the sched's per-split expert-weight staging is
**host-serialized against its own compute**, leaving the PCIe link **~49% idle** through
prefill `[measured]`.

| | |
|---|---|
| **Mechanism** | stage the used-expert union on a **dedicated copy stream** into a **double-buffered** device staging bank, ordered by **events**, so layer *L+1*'s H2D overlaps layer *L*'s MMQ GEMM |
| **Expert weights** | stay **CPU-resident exactly as today**; the moe-cache buft route is **forbidden** (would move decode MMID to CUDA) |
| **Prefill graph capture** | **IS active** and **does not block it** — the handoff is a stream wait-event *outside* the captured graph (§3) |
| **ggml-backend.cpp budget** | **~105 lines projected**, hard stop 300 (§5) |
| **VRAM delta** | **+450 MiB** → 6721 MiB vs G-V ≤ 11000 (§6) |
| **Upper bound** | 10.2 s → **~5.9 s** per 2048-token chunk = **1.73x** if overlap were perfect; gate is 1.15x (§7) |
| **Gates** | **UNCHANGED** from `STAGE1-IMPL-SPEC.md` §5.3 / `OPTION-C-DESIGN.md` Stage 1 (§8) |
| **Stop rules** | §9 |

---

## 1. ERRATUM — `STAGE1-IMPL-SPEC.md` §1.2 row "GPU offload rule never fires for MoE"

### 1.1 What the spec asserted

> `STAGE1-IMPL-SPEC.md:55` — "for `mul_mat_id`, `ne[2]` = **k = 10** < 32, so the node stays on
> CPU **regardless of token count** … **This is the single line that strands prefill expert
> FLOPs on the CPU**" `[verified in source]` at the time.

### 1.2 Why it is wrong

`ne[2]` is **n_tokens**, not k.

```
// ggml/src/ggml.c:3407-3408   ggml_mul_mat_id
const int64_t ne[4] = { as->ne[1], ids->ne[0], b->ne[2], 1 };
struct ggml_tensor * result = ggml_new_tensor(ctx, GGML_TYPE_F32, 4, ne);
```

`ids->ne[0]` (= `n_expert_used` = 10) lands in **`ne[1]`**. `b->ne[2]` lands in **`ne[2]`**, and
the activation fed to the expert MMIDs is `ggml_reshape_3d(ctx0, cur, n_embd, 1, n_tokens)`
(`src/llama-graph.cpp:2299`), so `b->ne[2] == n_tokens`.

The batch key is that same `ne[2]`:

```
// ggml/src/ggml-cuda/ggml-cuda.cu:8234-8247   get_op_batch_size
case GGML_OP_MUL_MAT_ID:
case GGML_OP_ROPE:
case GGML_OP_ROPE_BACK:
    return op->ne[2];
```

and the offload vote is a plain `>=`:

```
// ggml-cuda.cu:8249-8253   ggml_backend_cuda_device_offload_op
return get_op_batch_size(op) >= dev_ctx->op_offload_min_batch_size;
```

default `min_batch_size = 32` (`ggml-cuda.cu:8491`, env `GGML_OP_OFFLOAD_MIN_BATCH`).

The vote is consulted only when the weight's buffer is the CPU one and the op is supported:

```
// ggml-backend.cpp:969-982   ggml_backend_sched_backend_id_from_cur
if (src->buffer != NULL && src->buffer->usage == GGML_BACKEND_BUFFER_USAGE_WEIGHTS) {
    int src_backend_id = ggml_backend_sched_backend_from_buffer(sched, src, tensor);
    if (sched->op_offload && src_backend_id == sched->n_backends - 1 && ggml_backend_buffer_is_host(src->buffer)) {
        for (int b = 0; b < src_backend_id; b++) {
            if (ggml_backend_supports_op(sched->backends[b], tensor) && ggml_backend_offload_op(sched->backends[b], tensor)) {
                SET_CAUSE(tensor, "1.off");
                return b;                       // <-- CUDA0
            }
        }
    }
    SET_CAUSE(tensor, "1.wgt%d", i);
    return src_backend_id;                      // <-- CPU
}
```

IQ3_S is `GGML_CUDA_MMID_SOURCE_ADVERTISED`, so `supports_op` passes
(`ggml-cuda.cu:7842-7843`) and the vote is reached.

⇒ **at ubatch 2048 the prefill expert MMID is assigned to CUDA0; at decode (`ne[2] = 1`) it
is assigned to CPU.** The spec had the direction exactly inverted.

### 1.3 Independent confirmation from our own measured log (no code reasoning)

`stage0/logs/SPLIT-1-engine.log`, block reserved at
`n_tokens = 2048` (line 3540), lines 3542-3831:

| quantity | value | source |
|---|---|---|
| splits in the block | **145** (143 CUDA0 + 2 CPU) | `stage0/split-parse.json` → `block_kinds.prefill` |
| `blk.{0..46}.ffn_{gate,up,down}_exps.weight` tensors appearing as **CUDA0** split inputs | **141 / 141** | `stage0/logs/SPLIT-1-engine.log:3542-3831`, counted |
| example | `## SPLIT #2: CUDA0 # 1 inputs … [blk.0.ffn_gate_exps.weight (306M)]` | line 3544 |
| example | `## SPLIT #4: CUDA0 # 2 inputs … [blk.0.ffn_down_exps.weight (450M)] [ple_embd (20M)]` | line 3546 |

and the decode block reserved at `n_tokens = 1` (line 2946):

| quantity | value |
|---|---|
| splits | **98**, strictly alternating **49 CPU + 49 CUDA0** (`split-parse.json` → `block_kinds.decode`, `strict_alternating: true`) |

**Consequence for Stage 1.** Goals (c) "run the grouped GPU GEMM from the pool **instead of** the
CPU/sched used-expert slice-copy path" is void: the sched slice-copy path is precisely what
*feeds* the GPU GEMM that already runs. Removing it removes prefill. Goals **(a)** union gather,
**(b)** copy-stream fill with layer-ahead overlap, and **(d)** decode untouched remain exactly
right — and they are the whole prize. The leader's re-scope (turn 1858) adopts this.

**The forbidden alternative, stated once.** Giving the expert weights the fork's moe-cache
buffer type (`ggml_backend_cuda_moe_cached_buffer_type`, `moe-cache.cu:14826-14840`, with
`is_host()` deliberately returning **false** at `:14820-14823` so CUDA claims the buffer, and
`ggml-cuda.cu:8228-8230` letting any CUDA device claim it) **would move the decode expert MMID
to CUDA** — the measured −40% decode of `--moe-expert-cache-size > 0`
(`OPTION-C-DESIGN.md` Stage 1) — and would break G-K1 and G-D. Not used.

---

## 2. What actually costs the wall (the corrected mechanism map)

Per **2048-token ubatch**, prefill, `--n-cpu-moe 99 --moe-expert-cache-size 0`
(`ggml-backend.cpp:1724` `ggml_backend_sched_compute_splits`):

| # | step | `file:line` | cost class |
|---|---|---|---|
| 1 | new split per MoE weight (`need_new_split`, WEIGHTS buffer the backend cannot hold) | `ggml-backend.cpp:1338-1348` | — |
| 2 | **host block**: wait for the destination backend before overwriting the staging buffer | `ggml-backend.cpp:1797-1802` | **host stall ×141** |
| 3 | `ggml_backend_synchronize(input_backend)` (CPU → no-op) | `:1816` | — |
| 4 | ids D2H + **host block** on the ids backend (= CUDA0) | `:1836-1839` | **host stall ×47** |
| 5 | host bitset pass → `used_ids` | `:1841-1853` | host CPU |
| 6 | consecutive-run grouping; `ggml_backend_tensor_set_async` per run, with the `min(expert_size,512)` MMQ pad | `:1855-1892` (`:1859-1867`) | H2D |
| 7 | the H2D lands on the **compute stream**: `cudaMemcpyAsync(..., cuda_ctx->stream())` | `ggml-backend.cpp:265-278` → `ggml-cuda.cu:4213-4220` | **serialized with the GEMM** |
| 8 | per-split submit: `ggml_backend_sched_dispatch_split` | `:1707`, called `:1922` | async |
| 9 | split event recorded — **only if events exist** | `:1958-1960` | — |

**The two serialization facts that are the whole opportunity:**

* **(S1)** Step 7 puts the H2D on `cuda_ctx->stream()` — the same stream that runs the expert
  GEMM. Copy and compute for a layer cannot overlap even in principle, and with 141 MoE weight
  splits the stream is a strict alternation `copy(L) → GEMM(L) → copy(L+1) → …`.
* **(S2)** Step 2/9: `sched->events[b][c]` is allocated **only when `n_copies > 1`**
  (`ggml-backend.cpp:2030-2034`) and `--n-cpu-moe` sets a tensor override, which forces
  `pipeline_parallel = false` (`src/llama-context.cpp:1691-1693` receiving
  `has_tensor_overrides()` from `src/llama-model.cpp:1190`, defined `:2252-2253`) ⇒
  `n_copies == 1` (`ggml-backend.cpp:1997`) ⇒ **every event site degrades to a full host
  `ggml_backend_synchronize`** (`:1778`, `:1793`, `:1801`, `:1907`). 188 host stalls per
  ubatch. This is `OPTION-C-DESIGN.md` Stage 2 item 1, reused here **prefill-only and
  flag-gated**.

The CUDA event machinery the fix needs already exists and is wired:
`ggml_backend_cuda_event_record` (`ggml-cuda.cu:7171-7175`),
`ggml_backend_cuda_event_wait` (`:7177-7182`), declared at `ggml-backend.h:126-128`,
registered `ggml-cuda.cu:7479-7480`.

### 2.1 Staging-reuse hazard (why a second bank is mandatory)

The 141 staging destinations are **not** 141 buffers. `need_new_split` exists precisely so
ggml-alloc recycles one region — the code says so:

```
// ggml-backend.cpp:1336-1337
// check if a weight is on a different and incompatible backend
// by starting a new split, the memory of the previously offloaded weights can be reused
```

**Proof by arithmetic** `[derived]`: declared prefill weight input is **46 629.0 MiB = 45.54 GiB**
across 141 tensors (`split-parse.json` → `prefill_declared_MiB_unique` /
`declared_weight_MiB`, `STAGE0-RESULTS.md:217`), largest single tensor 450 MiB
(`SPLIT-1-engine.log:3546`). 141 separate banks would be 45.5 GiB — impossible on a 16 GB card
whose measured post-load VRAM is **6271 MiB** `[measured, STAGE0-RESULTS.md:134]`. Hence one
shared region of `bank_bytes = max over expert weights of n_expert * nb[2]` ≈ **450 MiB**.

**Hazard.** With one bank, step 2's host block is *load-bearing*: layer *L+1*'s copy would
otherwise overwrite the weights layer *L*'s MMQ is still reading. Two consequences:

1. Deleting step 2 without a second bank is a **silent wrong-answer race**, not a slowdown.
   Hard rule for review: *the bank flip and the removal of the host block land in the same
   commit, or neither.*
2. Union-sizing the bank (only `n_union` experts of 512) is **safe** — MMQ dereferences only
   experts named in `ids`, so unstaged slots are never read. But it buys little: `[measured]`
   actual staging is **31-36 GiB/chunk = 68-79% of declared** (`STAGE0-RESULTS.md:256-262`), so
   a union-sized bank saves ≤ ~21% of *bytes*, which is not the lever. **v1 sizes the bank at
   the full tensor** (identical to today's single bank) and spends the effort on overlap only.
   This is a deliberate simplification of `STAGE1-IMPL-SPEC.md` §3.5 and it *reduces* VRAM risk.

---

## 3. Prefill graphs ARE captured — and it does not block the handoff

Leader instruction 1 required this answer before implementation.

**Capture is active.** `ggml_cuda_graph_check_compability` (`ggml-cuda.cu:4404-4440`) turns
capture **off** only when `ggml_cuda_mul_mat_id_needs_sync(node, cc)`:

```
// ggml-cuda.cu:1944-1971
static bool ggml_cuda_mul_mat_id_needs_sync(const ggml_tensor * dst, const int cc) {
    ...
    if (dst->ne[2] <= MMVQ_MAX_BATCH_SIZE) { ... }          // prefill ne[2]=2048: skipped
    if (ggml_cuda_should_use_mmq(src0->type, cc, src1->ne[2], /*n_experts=*/src0->ne[2])) {
        return false;                                        // <-- taken
    }
```

and MMQ is chosen unconditionally for any NVIDIA arch ≥ Turing once the MMQ shared-memory
floor is met:

```
// ggml/src/ggml-cuda/mmq.cu:341-349
if (smpbo < 48 * 1024) return false;
if (turing_mma_available(cc)) return true;      // sm_86 qualifies

// ggml/src/ggml-cuda/common.cuh:358-360
static bool turing_mma_available(const int cc) {
    return GGML_CUDA_CC_IS_NVIDIA(cc) && ggml_cuda_highest_compiled_arch(cc) >= GGML_CUDA_CC_TURING;
}
```

⇒ `needs_sync == false` ⇒ `use_cuda_graph` stays **true** ⇒ prefill reaches
`cudaGraphLaunch(graph->instance, cuda_ctx->stream())` (`ggml-cuda.cu:6653`).

**Corroboration that `graphs reused = 1581` counts decode steps** `[measured]`:
the counter rises **+253** between two `print_timing` lines of one request
(`stage0/logs/F0C-1-engine.log:83` `1328` → `:94` `1581`), and 1581 < the 1600 decode steps
the Stage 0 protocol runs (64 + 3×256 + 3×256), i.e. it is a decode-step counter with prefill
contributing ≈ single digits. Prefill graphs are separately reserved
(`SPLIT-1-engine.log:3540`, `4028`: 2 × `n_tokens = 2048`) and reused ~8 times per 12K request.

**Why capture is not a blocker.** The staging H2D is issued by the **scheduler**, outside the
captured graph; only the compute kernels are in the graph. The sched's per-split order is:

```
[step 6: set_tensor_async → cudaMemcpyAsync(copy_stream)]      // outside the graph
[step 8: dispatch_split → cudaGraphLaunch(exec, cuda_ctx->stream())]   // the graph
```

so the dependency "compute(L) must not start before copy(L) landed" is a
`cudaStreamWaitEvent(cuda_ctx->stream(), copy_done_L)` **on the compute stream, between the
copy and the launch**. That is an ordinary stream operation, *not* a graph node, hence
**capture-legal** and correctly ordered against `cudaGraphLaunch`. Nothing inside
`cudaGraph_t` changes; **capture stays enabled, so G-G is untouched** (`graphs reused` must
stay ≥ control).

The fork's own alternative — a device-side spin flag inside the MMQ kernel
(`mmq.cuh:1048-1069`, `x_stage_ready`) — is reachable only through the moe-cache dispatch hook
and is therefore **not used** (§1.3).

---

## 4. Design (1b-R)

Flag: the already-plumbed `--moe-prefill-stream` / `LLAMA_ARG_MOE_PREFILL_STREAM`
(`common/arg.cpp`, `llama_model_params`, published to CUDA at model load via the MoE-cache
proc-address pattern; Stage 1a commits `9e837556` + `4843a8b6`). **Default off. Flag off must
be byte-identical** — every change below is inside `if (prefill_stream_enabled && <prefill
shape>)`, and the shape test is the *same* test Stage 1a already uses
(`moe_prefill_stream_plan_viable`, `moe-cache.cu:9710-9737`: flag on, no cache machinery, no
group records, and every MMID node in `PREFILL` phase). Decode graphs fail that shape test by
construction and stay on the legacy path ⇒ G-K1 and G-D are structural, not hoped-for.

Per split, in the flag-on prefill case only:

| step | today | 1b-R |
|---|---|---|
| guard | — | `prefill_stream_enabled() && split is a MoE-weight split && events exist && n_experts·nb[2] ≤ bank_bytes` |
| 2 (`:1797-1802`) | `ggml_backend_synchronize(split_backend)` | `ggml_backend_event_wait(split_backend, ev_bank_free[bank])` — a **stream** wait, no host block |
| 6 (`:1855-1892`) | `cudaMemcpyAsync(..., ctx->stream())` | `cudaMemcpyAsync(..., copy_stream)` + `cudaEventRecord(ev_copy[bank])`; destination offset shifted by `bank_bytes` |
| after 6 | — | `ggml_backend_event_wait(split_backend, ev_copy[bank])` before `dispatch_split` |
| 9 (`:1958-1960`) | no event | already records `ev_bank_free[bank]` after the split ⇒ the *next* user of that bank waits |

**Bank flip.** `bank = (++moe_stage_seq) & 1`, per MoE-weight split, in graph order. Layer *L*
uses `bank[L&1]`; layer *L+1* uses the other one, which layer *L-1* last read — so by the time
copy(*L+1*) is issued, `ev_bank_free[bank[L+1]&1]` (recorded after split *L-1*) has landed.
Layer *L*'s GEMM reads `bank[L&1]`, which nothing else touches. **That is the whole overlap
argument, and it is why both halves must ship together (§2.1).**

**ids D2H (`:1836-1839`).** `ggml_backend_tensor_get_async` + full
`ggml_backend_synchronize(ids_backend)` also blocks the host, once per layer. With split events
live, this becomes `ggml_backend_event_synchronize(ev_ids[split])` — the event recorded after
the split that produced `ids`. **Note the residual:** that event still waits for everything
enqueued before it on the compute stream, so this alone removes a *host* stall but not the
GPU-side ordering. The copy-stream overlap is what removes the GPU-side serialization. Recorded
as a partial fix, not the win.

**Decode.** Untouched: the shape test rejects decode graphs; `n_copies` stays 1 unless the
flag is on *and* the graph is prefill-shaped; the extra events/bank are allocated only then.
k stays 10; no `--override-kv`; no change to model/ctx/flags.

---

## 5. Line budget (hard stop: 300 in `ggml-backend.cpp`)

| file | change | est. lines |
|---|---|---|
| `ggml/src/ggml-backend.cpp` | allocate `ev_bank_free[2]`, `ev_copy[2]`, `ev_ids[]` + the 2-bank device tensor when the flag is on (guard at `:2030-2034`) | ~18 |
| | `:1797-1802` host sync → bank event wait (flag-gated) | ~14 |
| | `:1855-1892` `copy_experts` → bank offset + copy-stream set + two event records/waits | ~28 |
| | `:1836-1839` ids sync → event synchronize | ~8 |
| | bank bookkeeping (flip, free-event bookkeeping, teardown in `sched_free`) | ~25 |
| | **subtotal `ggml-backend.cpp`** | **~93** |
| `ggml/src/ggml-cuda/ggml-cuda.cu` | `copy_stream` + `ev` set in `ggml_backend_cuda_context`; `set_tensor_async` redirects expert-weight staging to it; new iface entry for "stage on copy stream" | ~55 |
| `ggml/src/ggml-backend-impl.h` | one optional iface slot + NULL default | ~8 |
| `ggml/src/ggml-cuda/moe-cache.cu` | expose the existing `ggml_cuda_moe_prefill_stream_enabled()` + shape predicate to the sched (or a tiny accessor) | ~12 |
| **total** | | **~168** |

**~93 of 300 in `ggml-backend.cpp`** — inside the budget with room to absorb a fix-up cycle.
If a fix-up pushes `ggml-backend.cpp` past 300, **stop and report** (leader gate, this doc §9).

---

## 6. VRAM (G-V ≤ 11000 MiB)

| item | MiB | source |
|---|---:|---|
| F0 post-load VRAM | **6271** | `[measured]` `STAGE0-RESULTS.md:134` |
| 2nd staging bank (largest expert weight) | **+450** | `[derived]` from `SPLIT-1-engine.log:3546` (`blk.0.ffn_down_exps.weight (450M)`) |
| pre-allocated events | ~0 (a few KiB) | `[derived]` |
| **total** | **6721** | |
| **headroom to G-V (11000)** | **4279** | |

If the allocator happens to hand out *distinct* addresses per expert weight (the §2.1 reuse is
an intent, and I verified it by arithmetic rather than by address logging), the bank is already
≥ 2× and the delta is **0**. The implementation must therefore **derive the bank count at
runtime**, not assume 1 ⇒ 2. G-V passes either way.

---

## 7. Upper-bound arithmetic (from `[measured]` Stage 0 quantities)

`STAGE0-RESULTS.md:256-270`, per **2048-token** ubatch:

| term | value |
|---|---|
| cold-prefill wall | **10.2 s/chunk** (p4k 20.35 s / 2; p12k 61.42 s / 6 — the two cells agree to <1%) |
| bytes actually staged | **31.0 / 32.5 / 35.9 GiB** (three independent estimates) |
| pinned H2D floor | **6.15 GiB/s** (measured 6.10-6.13 GB/s, G-L band 6.00-6.30) |
| ⇒ transfer time | **5.0-5.9 s** ⇒ **49-58% of the wall is PCIe** |
| link duty | **≈51%** (3.14 GiB/s mean, peak 6.689 GB/s) ⇒ **≈49% of the link is idle** |

**Perfect-overlap ceiling** `[derived]`: wall → `max(transfer, compute)` instead of
`transfer + non-transfer`:

```
transfer          = 5.9 s   (worst of 5.0-5.9)
non-transfer      = 10.2 - 5.9 = 4.3 s
ceiling           = max(5.9, 4.3) = 5.9 s
speedup           = 10.2 / 5.9 = 1.73x
```

⇒ **G-P's 1.15x sits inside the ceiling with ~50% margin.** Stretch 1.5x (300 tok/s) needs
duty ≥ ~70%, plausible but not promised. `STAGE1-IMPL-SPEC.md` §3.6 quoted 1.9-2.4x; that
used a 2-slot *union* pool and is superseded — 1.73x is the number this design is measured
against, and it is derived only from quantities Stage 0 already measured.

**The premise this rests on, stated as a hypothesis** (`STAGE1-IMPL-SPEC.md` §7.3, restated):
that cold prefill is *staging-bound*, i.e. the 4.3 s non-transfer term is real GPU work that a
copy engine can hide behind. **Kill condition in §9 tests exactly this.** If the non-transfer
term is itself mostly host stalls (188 of them) rather than GPU work, then removing the stalls
wins even more; if it is GPU work the copy engine contends for, overlap wins less. Either way
the measurement decides — that is what a falsified Stage 1 is.

---

## 8. Pre-registered gates — **UNCHANGED**

Frozen here, before any Stage 1b-R arm runs. Same-binary flag toggle only
(`--moe-prefill-stream` on/off), paired and interleaved, **n ≥ 4 pairs**, per-pair deltas +
mean + 95% CI reported. Harness reused verbatim: `.local/wt-stage0/.local/strata-research/`
(`arm-stage0.sh`, `bench3060.py`, `parse_arm_logs.py`, `sample_link.sh`); KL runner from
`.local/wt-stage0c/`. `k = 10`, no `--override-kv`, no model/ctx change, GPU1 only.

| gate | threshold | basis |
|---|---|---|
| **G-P** (primary) | cold-12K prefill **≥ 1.15×** the same-session flag-off control (paired mean) | `OPTION-C-DESIGN.md` Stage 1; kill below |
| **G-K1** | decode token sequence **100% identical** flag on vs off, greedy, same prompt/seed, cells p1k/p4k/p12k | blocking |
| **G-K2** | teacher-forced **Mean KLD ≤ 0.010814** at **K = 14**, `llama-perplexity` rebuilt from **this branch** | `STAGE0C-RESULTS.md:159` |
| **G-V** | VRAM after load **≤ 11000 MiB** | §6 projects 6721 |
| **G-L** | pinned H2D probe **6.00-6.30 GB/s** pre **and** post every arm; `gen1 ∧ util≥20 = 0` samples | `STAGE0-RESULTS.md:21` |
| **G-G** | `graphs reused ≥` control, `post_decode() failed` = 0, HTTP 500 = 0 | capture stays on (§3) |
| mechanism | `dmon` per-chunk GiB flag-on ≤ flag-off **and** link duty flag-on > flag-off | proves overlap, not just wall |

Expected: G-K2 should pass with large margin because **the GEMM kernel and its inputs are
unchanged** — 1b-R moves *where and when* the same bytes land, it does not change which
arithmetic runs. If G-K2 fails, that is a real bug (a bank-flip race), not a numerics trade.

---

## 9. Stop rules

1. `ggml-backend.cpp` change exceeds **300 lines** ⇒ stop, report.
2. Any need to modify `cudaGraph_t` / `cudaGraphExec_t` internals ⇒ stop, report (§3 says it
   should not be needed).
3. The moe-cache buft route, or any change that moves the **decode** expert MMID to CUDA ⇒
   forbidden, stop, report (breaks G-K1/G-D).
4. **G-K1 or G-K2 fails** ⇒ stop optimising, diagnose (a G-K2 failure is a bank race).
5. **G-P < 1.15×** ⇒ report the measured null. Explicitly a valid result: copy-engine
   contention, or the FLOP/staging-bound premise being wrong a second time, are all
   publishable outcomes.
6. A fix-up cycle would need > 6 CI cycles total ⇒ stop, report.

---

## 10. Untested / open

- **Nothing is runtime-verified.** Zero builds, zero rig minutes, zero GPU seconds so far. The
  fork branch still carries only Stage 1a (whose flag-on path executes legacy, per
  `stage1/IMPL-CI.md`), so **G-P cannot be measured on the existing artifact**.
- §2.1's staging-region reuse is proven by VRAM arithmetic, **not** by address logging. The
  implementation derives the bank count at runtime for exactly this reason.
- `smpbo >= 48 KiB` on sm_86 is from the CUDA programming model, not read from this rig's
  device; if it were false, MMQ would not be chosen, `needs_sync` would return true, and
  prefill would **not** be captured — which would only make §3 easier.
- The §7 ceiling assumes the 4.3 s non-transfer term overlaps cleanly. Untested.
- G-K2 needs a `llama-perplexity` artifact **from this branch**; none exists yet.

---

## Change summary

**Changed:** created this document only
(`docs/evidence/strata-gap/stage1/STAGE1B-REDESIGN.md`) on branch `docs/811-stage1b-redesign`.
No source, config, harness, or export file touched; no build, no server, no GPU/rig use;
`/mnt/WorkDisk/strata` and `.local/fork-ci` opened read-only; nothing written under `/tmp`.

**Assumptions:** `.local/fork-ci` @ `f8ec04f1` (Stage 1a tip) is the base for the patch;
Stage 0/0c numbers as reported in their results docs; sm_86 satisfies the MMQ
shared-memory floor.

**Risks / open questions:** §9 stop rules and §10 untested list. The load-bearing ones are the
bank-flip race (§2.1 — the two halves must ship together) and the §7 staging-bound premise.