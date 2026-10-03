# Nsight Systems per-kernel decode capture — RTX 3060 (t0011)

Date: 2026-10-03
Branch: `docs/811-nsys-decode` (off `docs/811-ranking-poc`)
Task: t0011 — split the **~32 ms/token UNATTRIBUTED bucket** left by
`PROFILE-DECODE.md` (fork vs Strata @4K: 65.4 vs 30.9 ms/tok, ~34.5 gap,
PROF fields explain only ~2 ms + 0 + 0), using one Nsight Systems
per-kernel decode-window capture (`nsys`, no `--profile-decode`).

## TL;DR / verdict

- **Capture succeeded (NSYS-2).** Collection window verified *inside* one
  steady decode (p4k rep1): 9.000 s at 11:39:49.13–11:39:58.13 (+07:00),
  **106 tokens**, wall **84.91 ms/tok** (traced; same-process untraced rep0
  just before = 72.5 ms/tok → nsys overhead ≈ **+12.4 ms/+17%**).
- **Wall splits cleanly in two** (sums exactly to the window):
  - **GPU-active 27.59 ms/tok (32.5%)** — kernels 24.87 + device memcpy 2.79
    (merged union; 7 ms overlap). Cross-checked: nvidia-smi util in-window
    30–39% (mean ≈33%).
  - **GPU-idle 57.32 ms/tok (67.5%)** — **owned by the CPU**: while the GPU
    sits idle, **~4.1 cores run the CPU-MoE expert GEMMs** (`ggml_vec_dot_*`
    under `GOMP_parallel`), 24.8 s core-time total.
- **CPU on-CPU attribution (39,452 symbolized IP samples):**
  **86.0% CPU-MoE**, 10.7% spin inside `cudaStreamSynchronize`, 1.9%
  launch/memcpy APIs, 1.5% other. Workers: 5 threads × ~4.15 s + main
  7.71 s = 28.82 s (3.20 cores avg).
- **Split-boundary sync is real but NOT wall-additive:** 40,280
  `cudaStreamSynchronize` = **exactly 380/token**, 28.65 ms/tok API time ≈
  on-CPU spin (29.1) ≈ GPU-active span (27.6) → the main thread spends the
  GPU-active span waiting for the GPU; the sync does not add wall beyond it.
- **VERDICT on the ~32 ms bucket (IMPL-GAP items):** it is **CPU-MoE expert
  compute on the critical path (item 1 — CONFIRMED dominant)**. Even granting
  the baseline the full traced GPU-active value, baseline CPU-side ≥ 65.4 −
  27.6 = **37.8 ms/tok** — cannot fit in anything but CPU-MoE. Item 2
  (split sync + copies, predicted 10–25 ms wall-additive) → **demoted:
  nested inside GPU-active** (consistent with PROF `t_sync_ms ≈ 0`).
  Item 3 (threads) unchanged at ~1.8 ms (PROFILE-DECODE).
- Harness health: `external_pressure=0`, H2D 6.12/6.13 GB/s, link good
  in-window; `bench_rc=1`/`parse_rc=2`/`status=link_invalid` are explained
  end-to-end below (nsys SIGINT at window end + teardown artifact) — not
  anomalies.

## 1. Method

### Rig / guards (t0011 rules)

- RTX 3060 only (`CUDA_VISIBLE_DEVICES=1 CUDA_DEVICE_ORDER=PCI_BUS_ID`),
  port **8093**, one GPU one task, runs strictly sequential; exact-pid kills;
  `TMPDIR=$WT/.local/tmp` (never `/tmp`); no builds, no sudo.
- Pre-arm gate: `MemAvailable 75.7 GiB >= 67` (owner ruling 2026-10-03: the
  67 GiB gate applies **pre-arm only**, as t0006/PROFILE-DECODE/P3 did).
  During-run sampler (5 s: MemAvailable, swap si/so, qemu RSS) kept as
  evidence; abort only on **external** pressure (≥3 consecutive samples with
  sustained swap or qemu RSS growth ≥1 GiB) — never triggered
  (`external_pressure=0`).
- The workload holds **~48 GB shmem-dirty** for its whole lifetime (83.6 GB
  GGUF mmap; MemAvailable dips 75.7 → ~26.3 GiB at bench, min 24.78 GiB at
  teardown) — measured workload property per the owner ruling, not a leak;
  recovered to 75.7 GiB at close-out.

### Binary / cmdline

F0-exact t0006 cmdline (PROFILE-DECODE, **minus `--profile-decode`**),
`-t 6`:

```
-m /mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
--split-mode layer -fit off -ngl 99 --n-cpu-moe 99
--override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0
-c 16384 --parallel 1 --flash-attn on --jinja -t 6
--experimental-logs --load-mode none --ple-prefetch -b 2048 -ub 2048
--host 127.0.0.1 --port 8093 --alias gsq-rco-iq3_s --spec-type none
```

Binary `.local/q2g/src/build/bin/llama-server`,
`LD_LIBRARY_PATH=build/bin:/opt/software/cuda/13.2.1/lib64`.

### nsys invocation (harness `.local/strata-research/arm-nsys.sh`, not committed)

```
nsys profile --trace=cuda,nvtx,osrt --sample=process-tree \
  --cuda-graph-trace=node --delay=166 --duration=9 \
  --force-overwrite=true -o <rep> -- llama-server <F0 flags>
```

`--sample=process-tree` collects CPU IP/backtrace samples (symbolized against
the local build). No `--profile-decode` (owner ruling: capture via
`--delay/--duration`, per original spec).

### Bench protocol (t0006-identical, `bench3060.py`)

warmup (sha `ad920c19…`, 64 tok, 14.18 tok/s) → p4k (sha `fa13b451…`, 4068
tok) × 3 reps, `--max-tokens 256`. Prompts byte-identical to t0006.

## 2. Capture-window verification

`--delay` does **not** give an absolute start: measured session-start offset
vs T0 (echoed as line 1 of the engine log) was **+6.004 s on NSYS-1
(delay=170)** and **+0.978 s on NSYS-2 (delay=166)** — nondeterministic
nsys/CUPTI init time. Window position is therefore always verified
post-hoc from `ANALYSIS_DETAILS`/`TARGET_INFO_SESSION_START_TIME`:

| anchor | value |
|---|---|
| T0 (arm) | `1791002222.154` = 11:37:02.154 |
| server clock origin | 11:37:02.809 (**Δ = +0.655 s**; derived twice: rep0 request 11:39:08.271 − 125.462 s of task-67 timing, and rep1 slot launch 11:39:47.240 − 164.431 s — identical) |
| session start (= delay expiry) | `1791002389.133` = 11:39:49.133 → **T0+166.978** (delay 166 + 0.978) |
| duration | 9.00015 s → window **11:39:49.133 → 11:39:58.133** |

Decode bracket (engine `print_timing`, task 326 = p4k rep1, decode start
≈11:39:47.4):

| event | server clock | epoch | in window? |
|---|---|---|---|
| slot launch (task 326) | 164.431 | 11:39:47.240 | before |
| `n_gen = 100` (tg 11.90) | 173.012 | 11:39:55.821 | **inside** |
| `n_gen = 138` (tg 12.05) | 176.062 | 11:39:58.871 | after (by 0.74 s) |
| SIGINT #1 "cleaning up before exit" | 178.411 | 11:40:01.220 | after (+3.1 s) |
| SIGINT #2 "terminating immediately" (arm cancel) | 208.413 | 11:40:31.222 | after (+30 s) |

**Token count N = 106** (window is fully inside decode, prefill long done):
rate derivation gives 105.1–106.4 (untraced pre-window 13.79 tok/s × 1.6 s
+ traced segment to `n_gen=100` + 12.46 tok/s × 2.31 s), and the CUDA-API
count locks it: **40,280 `cudaStreamSynchronize` ÷ 106 = exactly 380/token**
(no other divisor in 100–115 fits). Per-token rate during capture =
106 / 9.00015 s = 11.78 tok/s = **84.91 ms/tok**.

Link sampler in-window: every row `gen=4 width=4`, **util 30–39%** (mean
≈33% ≈ the 32.5% GPU-active computed from kernels — independent
cross-check), mem 6345 MiB. The single `link_bad` row (11:40:31,
gen=1/util=100/mem=4643) is the SIGINT-#2 teardown instant (partial weight
unmap), outside the window; harness `status=link_invalid` reflects only that
row.

### Expected non-clean statuses (all explained)

- **`bench_rc=1`, `parse_rc=2`**: nsys interrupts the app ≈3 s after window
  end (observed behavior on both runs). rep1's stream then hangs until the
  bench client's own timeout (11:40:31.046, `err=None`, `decode=None`) and
  rep2 gets `Connection refused` (port closing) → partial JSON. Window data
  unaffected (window ended 0.7 s before `n_gen=138`, 33 s before the abort).
- **NSYS-1** (`delay=170`, offset +6.004 s): harness aborted early
  ("llama-server child not found under nsys" — pid-discovery bug, fixed
  before NSYS-2), so its window fired over an **idle** app. Kept purely as
  the delay-offset calibration (rep 276 KB); no decode data.
- **DRY-1 `status=anomaly_low_tps_dry`**: harness parser false positive
  (`decode_tps_med=0` while reps were 14.28/14.28/14.01, `bench_rc=0`);
  parser fixed (reads `cells[].reps[].decode_tps`) before DRY-2/NSYS-2.

## 3. Wall decomposition (sums to the window)

Window 9000.15 ms = 106 tok = **84.91 ms/tok**:

| # | bucket | window ms | ms/tok | % wall |
|---|---|---|---|---|
| 1 | GPU-active (merged kernel ∪ memcpy ∪ memset) | 2924.3 | **27.59** | 32.5% |
| 1a | — kernel busy | 2635.9 | 24.87 | 29.3% |
| 1b | — device memcpy (net of 7 ms overlap) | 288.4 | 2.72 | 3.2% |
| 2 | **GPU-idle → CPU-owned** | 6075.9 | **57.32** | 67.5% |
| | **total** | **9000.1** | **84.91** | 100% |

GPU-idle internals (kernel-gap basis, before removing memcpy):

| gap class | window ms | ms/tok |
|---|---|---|
| gaps ≥ 1 ms (4037 gaps, max 3.701 ms) | 5072.8 | 47.86 |
| micro gaps (<1 ms, 325,649) | 1290.1 | 12.17 |
| total between kernels | 6362.9 | 60.04 |

Main-thread CUDA API (parallel to bucket 1, NOT additive to wall):

| API | calls | calls/tok | total ms | ms/tok |
|---|---|---|---|---|
| `cudaStreamSynchronize` | 40,280 | **380.0** | 3037.3 | 28.65 |
| `cudaGraphLaunch` | 5,276 | 49.8 | 455.9 | 4.30 |
| `cudaMemcpyAsync` | 17,016 | 160.5 | 59.7 | 0.56 |

`cudaStreamSynchronize`: med 3.9 µs (most split boundaries return
immediately), avg 75.4 µs, max 2.04 ms — the 28.65 ms/tok is the tail.
Its on-CPU spin counterpart (§5) is 29.1 ms/tok, and the GPU-active span is
27.6 ms/tok: all three within 4% → **sync = waiting for GPU work, nested
inside bucket 1**.

## 4. GPU details

Top kernels (of 2635.9 ms busy; top 13 = 83.7%; full data in
`nsys/stats/NSYS-2_cuda_gpu_kern_sum.csv`):

| kernel | ms | % busy | inst | inst/tok |
|---|---|---|---|---|
| `mul_mat_vec_q<ggml_type 14>` | 676.5 | 25.7% | 12,602 | 119 |
| `mul_mat_vec_f<bf16, n=160>` | 510.0 | 19.3% | 10,444 | 99 |
| `mul_mat_vec_f<bf16, n=256>` | 358.0 | 13.6% | 41,668 | 393 |
| `mul_mat_vec_q<ggml_type 13>` | 114.5 | 4.3% | 3,340 | 32 |
| `k_bin_bcast<op_add>` | 98.0 | 3.7% | 61,588 | 581 |
| `mul_mat_vec_q<ggml_type 12>` | 76.2 | 2.9% | 3,983 | 38 |
| `k_get_rows_float` (paged KV) | 69.9 | 2.7% | 5,600 | 53 |
| `gated_delta_net_cuda<128>` | 65.7 | 2.5% | 3,876 | 37 |
| `scale_f32` | 64.6 | 2.4% | 50,712 | 478 |
| `k_get_rows_float_vec` | 64.4 | 2.4% | 3,876 | 37 |
| `mul_mat_vec_q<ggml_type 23>` | 58.7 | 2.2% | 3,873 | 37 |
| `flash_attn_ext_f16<256>` | 48.0 | 1.8% | 1,292 | 12 |
| `quantize_q8_1` | 42.5 | 1.6% | 30,687 | 289 |

All `mul_mat_vec_*` combined = **1841.7 ms = 69.9% of GPU busy** (bf16
matvecs + quantized matvecs for attention/router/logits — the GPU does *not*
run expert GEMMs; those are `--n-cpu-moe`). ~3823 kernels/token total.

Device memcpy (`cuda_gpu_mem_time_sum`):

| kind | ms | count | bytes |
|---|---|---|---|
| D2D | 170.9 | 33,164 | 16.27 GB (≈95 GB/s on-device) |
| H2D | 92.3 | 6,572 | 538 MB (≈5.8 GB/s ≈ PCIe, matches the 6.12 GB/s probe) |
| D2H | 32.1 | 10,444 | 160 MB |
| memset | 1.4 | 1,292 | — |

**Discrepancy flag (unresolved):** PROFILE-DECODE's PROF windows reported
`rx_mbs=tx_mbs=0` during decode, yet nsys sees 538 MB H2D + 160 MB D2H in
this window (5.9 MB/tok PCIe). Either the NVML per-window delta missed
them or the PROF field rounds per-step bytes to 0.0 MiB/step (5 MB/tok
rounds to 0.0). Not re-derivable here; flagging honestly as `[open]`.

OSRT (blocked-time sums across threads, overlap wall — parking, not on-CPU):
`poll` 25.18 s/1027 calls, `pthread_cond_timedwait` 12.50 s/18,
`pthread_cond_wait` 8.93 s/323, `pthread_cond_clockwait` 8.92 s/107.

## 5. CPU attribution (nsys SAMPLING)

39,452 CPU IP samples (4384/s across threads; stacks ↔
`COMPOSITE_EVENTS` 1:1, `stackDepth=0` rows = full stacks) classified by
symbol over the whole window:

| class | samples | share | core-ms/tok | note |
|---|---|---|---|---|
| **CPU-MoE** (`ggml_vec_dot_*` ← `ggml_graph_compute_thread` ← `GOMP_parallel`) | 33,912 | **86.0%** | **233.9** | expert GEMMs, OMP worker threads |
| spin/block in `cudaStreamSynchronize` | 4,206 | 10.7% | 29.1 | ≈ sync API 28.65 ✓ |
| CUDA launch/memcpy APIs | 750 | 1.9% | 5.2 | |
| other / server-IO / allocator | 584 | 1.5% | 4.1 | incl. nsys/CUPTI helper threads (0.35 s on-CPU total) |

Leaf symbols of the CPU-MoE share (core-ms/tok = share × 271.9 total
on-CPU ms/tok):

| leaf | share | core-ms/tok |
|---|---|---|
| `ggml_vec_dot_iq4_nl_q8_0` | 19.8% | 53.8 |
| `ggml_vec_dot_iq2_s_q8_K` | 19.6% | 53.3 |
| `ggml_vec_dot_iq3_xxs_q8_K` | 18.4% | 50.0 |
| `ggml_vec_dot_iq3_s_q8_K` | 15.5% | 42.1 |
| `ggml_vec_dot_q2_0_q8_0` | 8.6% | 23.4 |
| other ggml frames (`iq4_xs`, `quantize_row_q8_K_ref`, compute_thread) | 4.1% | 11.1 |

On-CPU totals (SCHED_EVENTS, `isSchedIn` pairs): **28.820 s = 3.20 cores
avg** — main `llama-server` tid 7.707 s (85.6% busy: ~4.0 s MoE master +
3.08 s sync-spin + 0.56 s launch APIs) + **5 workers × 4.12–4.17 s**
(≈46% each). Closed accounting: 0.86 × 28.82 = 24.79 s CPU-MoE ≈
5×4.15 + 4.0 ✓.

Critical-path closure: CPU-MoE 24.79 s ÷ GPU-idle wall 6.076 s = **4.08
cores sustained exactly while the GPU is idle** — CPU-MoE runs *between*
CUDA graph segments (ggml scheduler is serial per token), so it is
wall-visible, not hidden under GPU work. Workers parked during GPU-active
(their on-CPU 46% ≈ the 67.5% idle fraction × ~0.68 utilization during
idle).

Main-thread sync stack: `cudaStreamSynchronize` ←
`ggml_backend_cuda_synchronize` ← `ggml_backend_sched_graph_compute_async_ext`
← `llama_context::graph_compute` ← `server_queue::start_loop`.
CPU stack: `ggml_vec_dot_*` ← `ggml_graph_compute_thread` ← `GOMP_parallel`
← `ggml_graph_compute` ← `ggml_backend_cpu_graph_compute`.

## 6. Verdict vs the ~32 ms bucket (IMPL-GAP items)

| # | bucket | predicted | measured here | verdict |
|---|---|---|---|---|
| 1 | CPU-MoE on critical path | decode CPU ~25–40 `[arithmetic]` | **57.32 ms/tok GPU-idle, 86% of on-CPU, 4.08 cores sustained** | **CONFIRMED — owns the bucket** |
| 2 | split-boundary sync + copies (96–97/step) | 10–25 `[hypothesis]` | 380 syncs/tok, 28.65 ms API ≈ spin ≈ GPU-active (27.6) | **demoted — real but nested inside GPU-active, not wall-additive** (agreeing with PROF `t_sync_ms ≈ 0`) |
| 3 | thread count `-t 6`→`-t 12` | 5–15 | 1.8 (PROFILE-DECODE) | minor (unchanged) |
| 6 | GPU-busy vs GPU-idle split | needed nsys | **27.59 / 57.32 ms/tok** | **measured** |

Baseline closure (un-profiled fork t0006 F0 = 65.4 ms/tok): GPU-active
cannot exceed its traced value (tracing only inflates kernels), so baseline
CPU-side ≥ 65.4 − 27.6 = **37.8 ms/tok** — the ~32 ms PROF-unattributed
bucket plus PROF's arithmetic attributions ≈ CPU-MoE + spin/launch margin.
The **Strata-side 30.9 ms/tok was not re-measured** (one GPU one task; same
caveat as PROFILE-DECODE §5.5).

## 7. Baselines and overhead

| run | state | decode ms/tok |
|---|---|---|
| t0006 F0 (historical) | untraced | 65.4 |
| DRY-1 (today, cold) | untraced | 70.0 (14.28/14.28/14.01) |
| DRY-2 (today, warm) | untraced | 71.6 (median 13.97) |
| NSYS-2 rep0 (same process, pre-window) | nsys loaded, not collecting | 72.5 (13.79) |
| PROFILE-DECODE T6 | `--profile-decode` | 73.1 |
| **NSYS-2 window** | **nsys collecting** | **84.91** |

nsys collection overhead ≈ **+12.4 ms/+17% vs same-run rep0** (kernel
tracing + IP sampling + record-keeping). All §3–§5 numbers are *at traced
rates*; they are used for **split shares and ownership**, not for
re-deriving the 65.4 baseline.

**Unattributed residue:** the post-window segment of rep1
(server 175.3–176.1, collection stopped) still ran at 12.46 tok/s vs
13.8 tok/s pre-window. Residual CUPTI-loaded overhead or run-position
variance — **not attributable from this capture**; noted, not folded into
any bucket.

## 8. Caveats

1. **Window position is nondeterministic in absolute terms** (+6.00 s vs
   +0.98 s offsets on identical flags) — always verify from
   `ANALYSIS_DETAILS`, never from `--delay` alone.
2. **N = 106 (±1):** rate-derived 105.1–106.4, locked by the exact
   380-syncs/token divisor. All per-tok numbers inherit ≤1% quantization
   from this.
3. **Sample-share assumption:** uniform IP-sampling period → sample shares
   ≈ on-CPU time shares (standard; corroborated by the SCHED_EVENTS
   on-CPU totals closing to 86%/10.7% expectations within ~1%).
4. `libcuda` frames are stripped (unwind shows raw addresses) — classified
   via the symbolized callee above them; ggml/libgomp/libc fully symbolized
   from the local build. `[Max depth]` frames counted as-is.
5. NSys/CUPTI helper threads (~1.2% of on-CPU) sit in the "other" class;
   not excluded by hand.
6. Device-vs-host memcpy overlap (7 ms) removed in the merged union;
   kernel-gap total (60.04 ms/tok) differs from GPU-idle (57.32) only by
   the in-gap memcpy.
7. **Memory:** 48 GB shmem-dirty dip is a workload property (owner ruling);
   min 24.78 GiB at teardown, `external_pressure=0` throughout, 75.7 GiB
   after cleanup. Pre-arm gate was the only hard gate.
8. PCIe H2D/D2H in-window vs PROF `rx=tx=0`: unresolved discrepancy (§4).
9. Run-to-run variance: today's untraced runs (70.0–72.5) sit above the
   historical 65.4 — same class of variance PROFILE-DECODE flagged; all
   cross-arm comparisons here are within-run (rep0 vs window).

## 9. Evidence files

- `nsys/NSYS-2.nsys-rep` — capture (21.6 MB; sqlite extractable via
  `nsys export`)
- `nsys/NSYS-1.nsys-rep` — idle calibration capture (276 KB)
- `nsys/stats/NSYS-2_{cuda_gpu_kern_sum,cuda_api_sum,cuda_gpu_mem_time_sum,osrt_sum}.csv`
  — committed `nsys stats` exports backing §3–§4
- `logs/NSYS-2-{arm.out,engine.log,link.tsv,mem.tsv,h2d.txt,summary.txt,tps.txt,vram_after_load.txt}`
  — harness transcript (T0 on line 1 of engine log), full stderr, samplers, gates
- `logs/NSYS-1-*`, `logs/DRY-1-*`, `logs/DRY-2-*` — calibration + baseline runs
- `arms/{NSYS-2,DRY-1,DRY-2}.{bench.json,logparse.json}` — bench raw
  (window anchors in §2 derive from `NSYS-2.bench.json` + engine log)
- `prompts/` — t0006-identical p4k + warmup (sha256 in bench json)
- Repro harness: `.local/strata-research/arm-nsys.sh` (not committed, same
  convention as `arm-profile.sh`)

Rig state at close-out: no llama-server, no nsys, port 8093 free, GPU1
1 MiB, MemAvailable 75.7 GiB.
