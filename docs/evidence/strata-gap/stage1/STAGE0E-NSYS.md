# Stage 0e — nsys measurement of `f`: **f = 0.31–0.56, HIGHER than the inferred 0.20–0.23**; and the flag-ON root cause

**Epic:** #811 · **Branch:** `docs/811-stage0e` (off `origin/epic/811-strata-gap-analysis`)
**Date:** 2026-10-04 · **Rig:** RTX 3060 = GPU1 only; GPU0 never touched (1 MiB throughout)
Follows `STAGE0D-NSYS.md` (PR #831, merged) and `STAGE1B-RESULTS.md` (PR #830, merged).
No fork change, no CI cycle. Raw artefacts in `stage0e-nsys/`.

## Verdict

1. **`f` is now MEASURED, and it is higher than inferred**: **0.314** (lower bound) to **0.559**
   (upper bound), against the inferred **0.20–0.23** from `STAGE1B-RESULTS.md` §6.
2. Therefore **#830's conclusion that "G-P was never reachable" is wrong**, and this doc
   withdraws it. At the measured `f`, the Stage 1b-R 2-of-3-copies ceiling is **≈1.18–1.38×**, so
   **G-P's 1.15× is plausible — conditional on staging actually engaging.**
3. Staging does **not** engage, and the reason is now pinned exactly:
   **`moe-stage: bank=0 staged=0 declined=141`** — all 141 MoE-weight splits decline because
   Stage 1a's plan hook never runs, so the flag the gate reads is never set.

| item | value | source |
|---|---|---|
| `f` (expert-GEMM / non-transfer) | **0.314 – 0.559** | §2, §3 |
| inferred `f` (#830) | 0.20–0.23 | superseded |
| GPU busy | **8.717 s of a 31.298 s span → 72.1% idle** | §2 |
| H2D | **96 001 MB / 40 089 copies / 15.751 s → 5.95 GiB/s** | §2 |
| link duty (H2D ÷ wall) | **63.3%** | §4 |
| expert GEMM kernels | **zero MMQ / `mul_mat_q`**; dequant → cuBLAS | §2 |
| host `cudaStreamSynchronize` | **n = 3 937, 6.257 s = 25.2% of wall** | §3 |
| flag-ON engagement | **none** — `declined=141` | §5 |

---

## 1. Capture provenance

| | |
|---|---|
| artifact | `.local/stage1b-ci/34068ff9/bin/llama-server` |
| sha256 | `0076d1ef3b14a00b29e3240ee1550e4520fdd87fca921ff8ab32cf19e55910a6` |
| `source_commit` | `34068ff959505962669288463f4debbbc21c05d7` (CI run `37203862664`, green) |
| arm | **flag OFF** (legacy path), VRAM after load **6271 MiB** — verified in `STAGE0D-NSYS.md` §1 |
| nsys | `~/nsys-local/…/2025.6.3/target-linux-x64/nsys`, Nsight Systems 2025.6.3.541-256337736014v0 |
| report | `S1OFF34068ff9-prefill.nsys-rep`, **52 636 229 bytes** |
| trace flags | `--trace=cuda,nvtx,osrt,cublas --sample=none --cpuctxsw=none --gpu-metrics-devices=none` |
| request | p4k, `max_tokens=1` ⇒ pure prefill, **4119 prompt tokens**, `cache_prompt` off |
| measured wall | **24.873 s**, **165.60 tok/s** |

Method per `STAGE0D-NSYS.md` §3, and it worked: **no `--delay`**, `--duration=900`, request issued
after `/health` + warmup, server `SIGINT`ed, `nsys stats` gated on a **non-empty** kernel CSV.

**p4k at `-ub 2048` is 3 ubatches: 2048 + 2048 + 23.** Kernel-burst segmentation
(gap > 0.4 s) yielded **658 bursts** and did **not** cleanly resolve individual ubatches, so all
figures below are **session totals across the 3 ubatches of the measured request** (the separate
1-token warmup contributes negligibly). Per-ubatch values in §6 are **derived by division and
labelled as such**, not measured.

### The two failures on the way here (both mine, both exact)

1. **§3's method, first execution: `SRV_PID` empty.** `pgrep -P "$NSYS_PID" -x llama-server`
   returned nothing, so `kill -INT ""` was a no-op, nsys never finalised, and the gate correctly
   refused to analyse. Fixed by resolving the pid through several fallbacks.
2. **`kill -KILL` destroyed the report.** With the pid fixed, the server exited cleanly and nsys
   printed `Collecting data...`, but the script's `kill -KILL "$NSYS_PID"` fired 180 s later,
   mid-finalisation, and **no `.nsys-rep` was written at all**. Fixed by blocking in `wait` and
   never signalling nsys after the server is gone.
3. **The gate's filename was wrong**, which is why it *false-alarmed* on attempt 2:
   `nsys stats --output X` writes `X_cuda_gpu_kern_sum.csv`, but the gate checked `X.csv`, so a
   healthy capture looked empty. The capture had in fact succeeded.

## 2. GPU timeline — prefill is not GPU-bound

From `CUPTI_ACTIVITY_KIND_KERNEL` (744 242 rows), union of busy intervals:

```
kernel span (first→last kernel) = 31.298 s
union busy                      =  8.717 s
idle within span                = 22.581 s   (72.1%)
```

**The GPU is idle 72% of the span.** This is the single most important number in the document and
it was invisible before: no amount of kernel tuning addresses 72% idle.

### Kernel breakdown (union busy = 8.717 s)

| class | time | % of GPU busy | % of wall | launches |
|---|---:|---:|---:|---:|
| **DEQUANT** (`dequantize_block_iq4_nl` / `iq2_s` / `iq3_xxs`, `convert_unary<float,__half>`) | **2.860 s** | **32.8%** | **11.5%** | 307 264 |
| **GEMM** (`ampere_h1688gemm`, `ampere_h16816gemm`, `cutlass_80_wmma…`) | **2.240 s** | **25.7%** | **9.0%** | 103 665 |
| OTHER (`rms_norm_f32`, `unary_gated_op_kernel`, `splitKreduce_kernel`, `flash_attn_ext_f16`) | 1.335 s | 15.3% | 5.4% | 126 118 |
| **ATTENTION** `gated_delta_net_cuda<128,0,0>` | **0.748 s** | 8.6% | **3.0%** | 252 |
| TOPK (router) `cub::DeviceTopKKernel` | 0.608 s | 7.0% | 2.4% | 198 336 |
| ELEMENTWISE (`k_bin_bcast`, concat) | 0.514 s | 5.9% | 2.1% | 6 952 |
| GET_ROWS (`k_get_rows_float_vec`, the `ids`) | 0.412 s | 4.7% | 1.7% | 1 655 |

Top individual kernels, verbatim names:

```
0.748 s  n=  252  void gated_delta_net_cuda<(int)128, (bool)0, (bool)0>(const float *, ...)
0.729 s  n=9354  ampere_h1688gemm_128x128_ldg8_stages_32x1_tn
0.574 s  n=36287 void dequantize_block_iq4_nl<__half>(const void *, T1 *)
0.509 s  n=23616 void dequantize_block_iq2_s<__half>(const void *, T1 *)
0.486 s  n=40391 ampere_h16816gemm_128x64_ldg8_stages_64x3_tn
0.426 s  n=101659 void convert_unary<float, __half>(const void *, T2 *, long, ...)
0.416 s  n=20566 void dequantize_block_iq3_xxs<__half>(const void *, T1 *)
0.393 s  n= 1134 void k_get_rows_float_vec<float>(const T1 *, const int *, T1 *, ...)
0.346 s  n=99168 void cub::_V_300200_SM_860_1200::detail::topk::DeviceTopKKernel<...>
0.308 s  n=101659 void convert_unary<__half, float>(const void *, T2 *, long, ...)
0.256 s  n= 3968 void k_bin_bcast<&op_add, float, float, float, ...>
0.250 s  n=24448 void cutlass::Kernel2<cutlass_80_wmma_tensorop_h161616gemm_16x16_128x2_tn_align8>
```

### The expert path is dequant → cuBLAS, **not MMQ**

```
MMQ / mul_mat_q kernels found: 0
```

**Zero.** `ggml_cuda_should_use_mmq` should select MMQ for any NVIDIA arch ≥ Turing
(`mmq.cu:347-349`), and Stage 0 recorded the prefill expert MMIDs as CUDA0 MMQ work — but no MMQ
kernel is present. What is present is **per-block dequantization of the staged expert weights
(307 264 launches, 2.860 s)** feeding **cuBLAS/cutlass GEMM**. So on this rig the expert "GEMM"
cost is **dequant-dominated, and roughly half of it is dequantization, not arithmetic.**

This also explains why DEQUANT is the largest GPU class: it is proportional to the *staged union
bytes*, which is exactly the quantity Stage 1b-R was trying to overlap.

## 3. `f` — measured

`STAGE1B-RESULTS.md` §6 defined `f = expert-GEMM time / non-transfer time`, with
`non-transfer = wall − H2D`. That definition is kept verbatim so the numbers are comparable.

```
wall (measured p4k prefill)   = 24.873 s
H2D (transfer)                = 15.751 s
non-transfer                  =  9.122 s

expert DEQUANT                =  2.860 s
all GEMM (expert + dense)     =  2.240 s
```

The expert/dense split **cannot be separated** from this trace, hence a range:

| | numerator | `f` |
|---|---:|---:|
| **lower** (DEQUANT only — unambiguously expert weights) | 2.860 | **0.314** |
| **upper** (DEQUANT + all GEMM) | 5.100 | **0.559** |

**Measured `f` = 0.314 – 0.559, versus the inferred 0.20 – 0.23.**

**This overturns #830 §6.** The inference put `f` in the 1.08× row of the pre-registered ceiling
table; the measurement puts it in the **1.18–1.38×** rows:

```
per full ubatch (2048 tok) ≈ 12.3 s, 47 layers
per layer:  transfer c = 37.3 ms/tensor ;  expert GEMM = 20.3 ms (deq) … 36.2 ms (deq+gemm)
saving/layer = 2 x min(g, c)  =  40.6 ms … 72.4 ms
saving/ubatch = x47           =  1.91 s  … 3.40 s
ceiling       = 12.3 / (12.3 - saving) = 1.18x … 1.38x
```

So **G-P ≥ 1.15× is arithmetically reachable on this rig**, and #830's "never reachable" reading
was an artefact of under-estimating `f`. That claim is withdrawn.

**Why the inference was low.** It derived `f` from G-P itself (`min(g,c)` implied by the observed
speedup), so it could never exceed what the unimplemented overlap delivered — it was circular.
Measuring the kernel timeline breaks the circle.

## 4. Transfers and link duty

| operation | bytes | count | time | note |
|---|---:|---:|---:|---|
| **Host→Device** | **96 001 MB (93.75 GiB)** | **40 089** | **15.751 s** | the expert-weight staging |
| Device→Host | 2 307 MB | 1 520 | 0.354 s | includes the per-layer `ids` D2H |
| Device→Device | 5 688 MB | 1 980 | 0.035 s | |
| memset | 2 731 MB | 74 472 | 0.072 s | |

**Link duty = H2D ÷ wall = 15.751 / 24.873 = 63.3%.**
**Achieved H2D bandwidth = 96 001 MB / 15.751 s = 5.95 GiB/s (6.10 GB/s)**, against the pinned
probe's **6.10–6.12 GB/s** measured in `#830` §4. **So the link runs at ~100% of achievable
whenever it is being used** — the headroom is not bandwidth, it is *duty*.

`nvidia-smi` `pcie.rx.util` is **not a valid field on this driver** (the 1 Hz sampler wrote an
error string per sample), so duty is derived from the nsys memcpy totals rather than sampled. That
is a *better* measure (exact bytes and exact engine time) but it is a different method from Stage
0's dmon duty, so the two duty figures are not directly comparable: Stage 0 reported **51%** by
dmon, this reports **63.3%** by memcpy-time.

## 5. Flag-ON root cause — pinned exactly

`34068ff9`, flag ON, `-lv 5`, one warm request. Verbatim, the only `moe`-matching line in the whole
log:

```
2.01.152.108 I ggml_backend_sched_compute_splits: moe-stage: bank=0 staged=0 declined=141
```

- `bank=0` — the bank was **never allocated** (the runtime-derived size is zero).
- `staged=0` — nothing staged.
- **`declined=141` — every one of the 141 MoE-weight splits declined.** That is exactly the 141
  CUDA0 MoE weight splits Stage 0 documented (`split-parse.json` → `prefill.weight_layers = 47`,
  `weight_tensors = 141`), so the gate is firing per split and declining for one reason.

VRAM after the request was **6335 MiB** — no 900 MiB bank.

**The gate reads Stage 1a's *plan* flag, and Stage 1a's plan note never fires.** Both of its log
strings are present in `libggml-cuda.so`:

```
moe-cache: prefill expert streaming plan ACTIVE (PREFILL_STREAMED); v1 executes the legacy path, grouped dispatch follows in Stage 1b
moe-cache: --moe-prefill-stream is on but a prefill plan fell back to legacy (cache-backed experts, group records, or non-prefill graph); decode and numerics unchanged
```

together with one-shot guards `_ZL31moe_prefill_stream_note_emittedvE6logged` and
`_ZL32moe_prefill_stream_note_fallbackvE6logged`. **Neither line appears in the log at all**
(`grep -icE 'moe'` over the run returns exactly 1 — the `moe-stage` line above).

⇒ It is **not** that `moe_prefill_stream_plan_viable` declined (that would have emitted the
"fell back to legacy" line). **The plan-compile hook never ran on this path**, so
`ggml_backend_cuda_moe_get_prefill_stream()` stays at its default `false`, so
`moe_stage_enabled()` returns false, so no bank is allocated and all 141 splits decline.

This is a **more precise diagnosis than `STAGE0D-NSYS.md` §2 offered**, and it corrects it: the
problem is not an over-strict whitelist, it is that Stage 1a never compiles a plan here. The
one-line fix is therefore most likely on the Stage 1a side (make the plan hook reachable for a
`--moe-expert-cache-size 0` prefill graph), not on the Stage 1b-R scheduler side. **Not
implemented — fork changes are out of scope for this task.**

## 6. Per-ubatch figures — derived, not measured

Segmentation into individual ubatches **failed** (§1). Dividing the session totals over the 3
ubatches of the measured request (2 × 2048 + 23), so the two full ubatches dominate:

| quantity | per ubatch (derived) |
|---|---:|
| wall | ≈ 12.3 s |
| H2D bytes / time | ≈ 31 000 MB / 5.1 s |
| H2D duty | ≈ 63% |
| DEQUANT (expert) | ≈ 0.94 s |
| GEMM (all) | ≈ 0.74 s |
| attention | ≈ 0.25 s |
| TOPK (router) | ≈ 0.20 s |
| GET_ROWS (`ids`) | ≈ 0.14 s |
| **GPU busy total** | **≈ 2.86 s of 12.3 s (23%)** |

## 7. Host-side cost

| API | calls | host time | % of wall |
|---|---:|---:|---:|
| `cudaMemcpyAsync` | 42 305 | 14.919 s | 60.0% |
| **`cudaStreamSynchronize`** | **3 937** | **6.257 s** | **25.2%** |
| `cudaLaunchKernel` | 680 805 | 2.267 s | 9.1% |
| `cudaMallocHost` (one-off, load) | 7 | 8.906 s | — |

**`cudaStreamSynchronize` = 6.257 s, 25.2% of the wall, over 3 937 calls.** The scheduler's
predicted per-ubatch barriers were 141 split syncs + 47 `ids` syncs = **188**; the measured call
count is **3 937, about 21× more**, so the synchronisation cost is broader than the model
predicted. Note this is *host-blocking* time and therefore overlaps GPU work — it is not additive
with the 72% GPU idle figure.

## 8. What the data supports as the next lever

Ranks strictly by measured share of the 24.873 s wall. No recommendation beyond what the numbers show.

| lever | measured basis | share of wall |
|---|---|---:|
| **(a) host-sync removal** | `cudaStreamSynchronize` 6.257 s, n=3 937 | **25.2%** |
| **(b) attention + norms** | `gated_delta_net_cuda` 0.748 s + `rms_norm` ≈ 0.20 s + `unary_gated` 0.165 s | **≈ 4.5%** |
| **H2D duty** (not a listed lever, but the largest single block) | 15.751 s at 63.3% duty, 5.95 GiB/s of a 6.10 GB/s ceiling | **63.3%** |
| **(c) smaller ubatch** | **not measured** — no ubatch sweep in this task | — |
| **(d) nothing worth doing** | **not supported**: 72% GPU idle, 63% H2D duty and 25% host-blocking all leave headroom | — |

**By measured share the ordering is (a) ≫ (b).** But (a) is only *actionable* once the mechanism
engages, and it does not: §5 shows the flag-on path declines all 141 splits. **The binding
constraint is neither (a) nor (b) — it is that Stage 1b-R currently runs inert.** Two facts make
the case for fixing that first:

- `f` measured **0.314–0.559**, above the inferred 0.20–0.23, so the pre-registered 2-of-3-copies
  ceiling is **1.18–1.38×** — above G-P's 1.15×.
- The overlap has never actually been measured with a *clean* control: #830's flag-off arm carried
  a wasted 900 MiB bank and an extra sync per split, which biased the ratio **against** the flag.

(c) is untested but is the one lever whose arithmetic improves with smaller ubatch, since the
per-layer `ids` sync amortises over fewer tokens.

## 9. Could not measure / known-open

1. **The 524a7659 flag-on nsys capture was NOT taken** (task said "if you can also…"). The
   concurrency question — *did any H2D overlap a GEMM on the one build where flag-on differed from
   flag-off* — is therefore **still unanswered**.
2. **Per-ubatch segmentation failed**; §6 is derived by division, not measured. A capture of a
   single ubatch (e.g. 2 048-token prompt) would resolve it.
3. **Expert vs dense GEMM split is not separable** from this trace, hence the `f` range.
4. **Host-sync attribution** is a single aggregate (3 937 calls); which call sites are the 141-split
   and 47-`ids` barriers versus the other ~3 700 is not resolved.
5. **Link duty by dmon** not taken (`pcie.rx.util` unsupported on this driver); §4 duty is
   memcpy-derived and not directly comparable to Stage 0's 51%.
6. **G-K2 not run**, per prior instruction.
7. `f` measured on the **flag-OFF legacy** path. It is the right quantity for the staged design
   (the same compute is what hides copies) but it was not re-measured on a build where staging
   engages, because none exists yet.

## Leader review note (2026-10-04) — the 1.18–1.38x ceiling is overstated; corrected to ~1.08–1.16x

**What stands:** `f` is measured (DEQUANT 2.860 s lower bound; with all GEMM 5.100 s upper bound), the #830
inference of `f ≈ 0.2` was circular and is withdrawn, the 72% GPU idle / 63% link duty / 25% host-sync
figures are real, and the `bank=0 staged=0 declined=141` root cause (Stage 1a plan hook never runs) is
good evidence.

**What does not stand: the §3 ceiling arithmetic.** The saving and the wall use different bases. The
`2·min(g,c)` saving per layer is built from `g` = session DEQUANT ÷ (47×3) (session total, all 3 ubatches),
so "saving/ubatch = 1.91–3.40 s" is in fact the **session-total** saving, but it is divided by a
**single-ubatch** wall (12.3 s). §6 repeats the mix: kernels divided by 3, wall divided by 2.
On a consistent session basis, with hideable GEMM = 2/3 of the expert GEMM time (copy(up) behind GEMM(gate),
copy(down) behind GEMM(up); copy(gate) cannot hide because ids(L+1) depend on layer L's output):

```
saving = 2/3 x (2.860 .. 5.100) = 1.91 .. 3.40 s   of the 24.873 s session wall
ceiling = 24.873 / (24.873 - saving) = 1.083x .. 1.158x
```

So G-P 1.15x is reachable **only at the very top of the upper bound** (all GEMM counted as expert GEMM,
which §3 says cannot be separated). The honest reading: ceiling 1.08–1.16x, G-P **marginal, not
"arithmetically reachable"**, and #830's "never reachable" is **softened, not overturned**.
The unconstrained bound (all compute hidden) is max(15.75, 9.12)/24.873 -> 1.58x, but dependencies forbid it.

**§8 lever (c) is backwards.** H2D happens once per ubatch (about 31 GB each, nearly the whole expert
set), so a **larger** ubatch amortises transfers and the ids syncs; a smaller one multiplies them. At
`-ub 4096` p4k would restage once instead of twice, and `STAGE0C`/t0002 already saw ub 512->8192 give 6.93x
prefill on the 35B. This is the cheapest lever by far (config only, no fork change) and is untested here.
Follow-up t0026 sweeps `-ub`.

## 10. Rig state

GPU1 only. Both GPUs returned to **1 MiB / 0%**; ports 8091/8086/8093 free; no `llama-server`, no
`nsys`. Pre-arm `MemAvailable` 81 GiB (≥ 67). All output under `.local/`, `TMPDIR` in `.local/tmp`,
**nothing under `/tmp`**. No sudo, no local binaries, no CI cycle.

## Change summary

**Changed:** this document plus `stage0e-nsys/` raw artefacts, on branch `docs/811-stage0e`. No
product, harness, export or config file touched. **Fork untouched** (`feat/moe-prefill-stream`
still at `34068ff9`, DRAFT PR #161 open, not merged), per instruction.

**Assumptions:** the flag-OFF trace is representative of the staged path's compute content (same
kernels, different overlap); `f` keeps #830 §6's definition verbatim for comparability; the three
measured ubatches (2048+2048+23) dominate session totals.

**Risks:** §3 **withdraws** #830's central claim that G-P was unreachable — the leader should treat
#830's "null is explained" as superseded. §5 localises the remaining blocker to Stage 1a's plan
hook, which is outside this task's authority to fix.