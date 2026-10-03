# Option C — Stage 0 results: measure before building (zero code)

Date: 2026-10-03
Branch: `docs/811-stage0` (off `origin/epic/811-strata-gap-analysis` @ `2b0c8a64c`)
Spec: `OPTION-C-DESIGN.md` §"Stage 0" (line 176+), PR #821 · Task: t0006-derived measurement stage
Rig: RTX 3060 **GPU1 only**, port 8093, one GPU one task, runs strictly sequential.
Binary: `.local/q2g/src/build/bin/llama-server` (no builds, no podman, no `/tmp`).

## TL;DR / verdict

All **nine runs finished green** — the five pre-registered arm types (`F0C`×2, `F0P`, `T12`, `T16`,
`SPLIT`) plus three addendum OFF replicates (`T16U-1`, `T12U-1`, `T12U-2`) — every one with
`status=ok`, `bench_rc=0`, `parse_rc=0`, no crash, no `post_decode() failed`, no HTTP 500.
Every gate green except G-0b, which cannot run here.

| Gate | Result | Evidence |
|---|---|---|
| **G-0a** attribution of 65.4 ±10% ms/tok | **PASS (marginal)** — closes as 27.6 GPU-active + 0.93 boundary + ~36.9 CPU-side = 65.4; control re-measured **71.9 / 72.9** (+9.9 / +11.4%) → day-over-day drift disclosed | §3, §4, §10 |
| **G-0b** correctness floor (G-K2 denominator) | **NOT RUN** — needs CI `llama-perplexity` build; local builds barred this session | §7 |
| **G-0c** `-t16` ≥ +6% decode | **FIRES profiled (+6.1%), FAILS unprofiled (+3.87%)** ⇒ **do not ship `-t16`**. Adjacent zero-code finding: **`-t12` unprofiled = +6.62%** on the canonical 4K cell (n=2 arms, 0.6 pp margin; `p12k` +1.9%) — owner decision required | §6 |
| **G-L** PCIe validity | **PASS** — h2d 6.10–6.13 GB/s (band 6.00–6.30), `gen1 ∧ util≥20 = 0` samples on all 9 runs, `link_bad_samples=0` | §5 |
| **G-V** VRAM ≤ 11 000 | **PASS** — 6271 MiB on every run | §2 |
| **G-G** graphs | **PASS** — `graphs reused = 1581` on all eight 3-rep runs (= control), SPLIT-1 = 315 (1 rep × 128 tok); 0 `post_decode() failed`, 0 HTTP 500 | §2 |
| **G-R** no axis regresses >3% | **PASS** — cold prefill 200.74–203.09 tok/s (spread 1.2%), TTFT ≤0.4%, VRAM/h2d/graphs identical across all 9 runs | §2 |
| R7 profiler ON/OFF replicate | **SATISFIED** for F0 (F0C-1, F0C-2 vs F0P-1); ON-only legs T12/T16 get OFF replicates as addendum arms | §6 |

**Stage implications** (per `OPTION-C-DESIGN.md` §5.2):

- **Stage 2 — CANCELLED before any code.** Measured boundary share = **0.93 ms/token** of
  copy transfer (5.41 MiB ÷ 6.12 GB/s) + 0.0047 ms output-region sync, with the per-boundary
  `cudaStreamSynchronize` proven *nested, not wall-additive* by NSYS (380 syncs/tok, API time ≈
  spin ≈ GPU-active). **1.4–2.4 ms ≪ 6 ms kill criterion.** The 10–25 ms `[hypothesis]` in
  `IMPL-GAP.md`/`IMPL-GAP-FORK.md` item 2 is **refuted**.
- **Stage 1 — SUPPORTED by measurement, still blocked on G-K2.** Cold prefill pulls
  **≈31–36 GiB per 2048-token chunk (68–79% of the 45.67 GiB declared)** and the link runs at
  **~51% duty** (peak 6.689 GB/s, 34 s ≥5.5 GB/s) ⇒ **≈49–58% of cold-prefill wall is PCIe
  expert streaming**. G-P's 1.15× bar is a duty-cycle problem, not a bandwidth problem.
- **Stage 3 — unchanged**, gated on G-K2 (§7) + now has its attribution; R2 answered: recurrent/PLE
  state does **not** ride the boundary copies (§9).
- **Stage 4 — NO CHANGE.** The turn-2/3 fixed cost is **inside the slot** (`≈10000/731`, F-B row 1)
  and **pre-slot ≈ 0** (TTFT ≈ eval). The slow tail rate (73.1 / 64.7 tok/s vs 189–202 cold) is handed
  to Stage 1 as the same per-chunk streaming mechanism `[derived]` (§8).

---

## 1. Method

### 1.1 Arms (all knobs pre-existing; flags = t0006 F0 verbatim + only the stated instrumentation)

| arm | `-t` | profiler | sched-debug | reps | purpose |
|---|---:|---|---|---:|---|
| `F0C-1`, `F0C-2` | 6 | off | – | 3 | item 1: un-profiled F0 control (re-measure + R7 OFF leg) |
| `F0P-1` | 6 | `--profile-decode` | – | 3 | item 1: `t_wall/t_cpu/t_dev/t_sync` per 64-step window |
| `T12-1` | 12 | `--profile-decode` | – | 3 | item 3 config arm (spare recorded) |
| `T16-1` | 16 | `--profile-decode` | – | 3 | item 3 config arm (G-0c target) |
| `SPLIT-1` | 6 | – | `GGML_SCHED_DEBUG=1` + `-lv 5` | 1 × 128 tok | item 2: `## SPLIT` per token (settle 97 vs ~193) |
| **addendum** `T16U-1` | 16 | **off** | – | 3 | R7 OFF replicate for the G-0c ship decision (§6) |
| **addendum** `T12U-1`, `T12U-2` | 12 | **off** | – | 3 | R7 OFF replicate of T12 + a second arm (n=2, matching F0C) |

Shared flags (`.local/strata-research/arm-stage0.sh:131-138`): `--split-mode layer -fit off -ngl 99
--n-cpu-moe 99 --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0 -c 16384
--parallel 1 --flash-attn on --jinja --experimental-logs --load-mode none --ple-prefetch -b 2048
-ub 2048 --host 127.0.0.1 --port 8093 --alias gsq-rco-iq3_s --spec-type none`.

### 1.2 Guards

Pre-arm gates (all passed): both GPUs ≤5 MiB, 8091 not listening, 8093 free, no other
`llama-server`, MemAvailable ≥67 GiB (pre-arm only), spare = `nproc − threads` ≥ 2
(`leader-handoff-state.md:756-757`). H2D pinned probe pre **and** post every arm; 1 Hz link
sampler (`sample_link.sh`) covering load + bench. Exact-pid kills only; `TMPDIR` under
`.local/tmp`; ports 8086/8091 never touched; GPU0 (5060 Ti) never used.

### 1.3 Protocol

Identical to t0006 (`PRE-REGISTERED.md`, `prompts/sha256sums.txt`): warmup 96 tok
(`ad920c19…`) → `p4k` (4068 tok, `fa13b451…`) ×3 → `p12k` (12283 tok, `2e5eabdf…`) ×3,
`--max-tokens 256`.

PROF field semantics were read from source before interpreting (table in
`PROFILE-DECODE.md:74-86`): `t_wall` = one `llama_decode()` CLOCK_MONOTONIC delta over a 64-step
window (`llama-context.cpp:3559,4014`, window `:807-852`); `t_cpu` = aggregate process CPU
(**includes thread-pool spin**); `t_dev` = `cudaEventElapsedTime` device-timeline **span**
(**includes GPU-idle**, not GPU-busy); `t_sync` = output-extraction region only. `PROF summary`
= median over windows. **Steady-state = windows with `win>=2` (n=23 of 25)**; startup windows 0–1
and one ~64.8 s idle window are median-robust but distort the *mean*, so all cross-arm numbers
below are **medians**.

---

## 2. Results — bench and gates

### 2.1 Bench (`stage0/arms/*.bench.json`)

Decode tok/s per rep → median → ms/tok; cold prefill = rep0 `prompt_tokens / ttft`.

| arm | -t | prof | p4k decode r0/r1/r2 | **p4k med** | **ms/tok** | p12k decode r0/r1/r2 | **p12k med** | p4k cold pf | p12k cold pf | TTFT p4k / p12k |
|---|---:|:-:|---|---:|---:|---|---:|---:|---:|---|
| F0C-1 | 6 | – | 14.51 / 13.91 / 13.78 | **13.91** | **71.9** | 13.62 / 14.64 / 13.06 | 13.62 | 202.37 | 201.17 | 20.35 / 61.31 |
| F0C-2 | 6 | – | 13.04 / 14.72 / 13.72 | **13.72** | **72.9** | 14.53 / 13.32 / 14.07 | 14.07 | 202.43 | 200.80 | 20.35 / 61.42 |
| F0P-1 | 6 | on | 12.90 / 12.94 / 12.99 | **12.94** | **77.3** | 13.02 / 14.02 / 14.13 | 14.02 | 202.88 | 200.93 | 20.30 / 61.39 |
| T12-1 | 12 | on | 14.73 / 14.61 / 14.50 | **14.61** | **68.4** | 14.73 / 13.30 / 13.98 | 13.98 | 202.64 | 201.07 | 20.33 / 61.34 |
| T16-1 | 16 | on | 13.73 / 13.47 / 14.02 | **13.73** | **72.8** | 13.95 / 13.58 / 13.92 | 13.92 | 203.09 | 201.53 | 20.28 / 61.20 |
| SPLIT-1 | 6 | – | 13.66 (1 rep) | **13.66** | **73.2** | 14.32 (1 rep) | 14.32 | 202.44 | 200.74 | 20.35 / 61.44 |
| **T12U-1** (addendum) | 12 | – | 14.76 / 14.90 / 14.81 | **14.81** | **67.5** | 14.47 / 14.48 / 14.93 | 14.48 | 202.96 | 200.84 | 20.30 / 61.41 |
| **T12U-2** (addendum) | 12 | – | 14.65 / 14.70 / 14.39 | **14.65** | **68.3** | 13.74 / 13.72 / 13.96 | 13.74 | 202.97 | 201.14 | 20.29 / 61.32 |
| **T16U-1** (addendum) | 16 | – | 14.40 / 14.35 / 13.31 | **14.35** | **69.7** | 13.27 / 14.03 / 13.92 | 13.92 | 202.91 | 201.36 | 20.30 / 61.25 |

Unprofiled (ship-relevant) decode, p4k median of 3 reps per arm, vs the unprofiled control pair:

| | `-t 6` control | `-t 12` (n=2 arms) | `-t 16` (n=1) |
|---|---|---|---|
| p4k tok/s per arm | 13.91 (F0C-1), 13.72 (F0C-2) | **14.81** (T12U-1), **14.65** (T12U-2) | **14.35** (T16U-1) |
| p4k mean tok/s → ms/tok | **13.815 → 72.4** | **14.73 → 67.9** | **14.35 → 69.7** |
| **Δ vs control mean** | — | **+6.62%** (−4.5 ms) | **+3.87%** (−2.7 ms) |
| per-arm Δ vs F0C-1 / F0C-2 | — | +6.47/+7.94 (arm 1), **+5.32/+6.63** (arm 2) | +3.16 / +4.59 |
| p12k mean tok/s | 13.845 (13.62, 14.07) | 14.11 (14.48, 13.74) = **+1.9%** | 13.92 = **+0.5%** |

Note the `p12k` cell is far noisier (control pair spans 13.62–14.07, ±1.6%) and the first T12U arm
was its high outlier — **`p4k` is the canonical cell** (it is the cell the 65.4 ms/tok reference and
G-0c are defined against), `p12k` is reported as G-R context.

Reference: t0006 F0 = 15.30 tok/s = **65.4 ms/tok**. Every arm's decode sits 4–18% slower than that
reference — the reference did **not** reproduce exactly (see caveat C1), but all Stage-0 gates are
ratios against same-session controls.

### 2.2 Gates / health (`stage0/logs/*`)

| check | F0C-1 | F0C-2 | F0P-1 | T12-1 | T16-1 | SPLIT-1 |
|---|---|---|---|---|---|---|
| verdict (`*.summary.txt`) | `status=ok bench_rc=0 parse_rc=0` | same | same | same | same | same |
| h2d pre / post (GB/s) | 6.12 / 6.11 | 6.12 / 6.13 | 6.12 / 6.13 | 6.11 / 6.12 | 6.12 / 6.11 | 6.11 / 6.13 |
| `link_bad_samples` | 0 | 0 | 0 | 0 | 0 | 0 |
| `gen1 ∧ util≥20` (computed from `*.link.tsv`) | **0** | **0** | **0** | **0** | **0** | **0** |
| VRAM after load (MiB) | 6271 | 6271 | 6271 | 6271 | 6271 | 6271 |
| MemAvailable after (GiB) | 100.9 | 100.8 | 100.6 | 102.0 | 102.2 | 101.2 |
| `load1_max` during run | 6.9 | 4.96 | 9.18 | 6.67 | 7.05 | 3.85 |
| spare (`nproc − threads`) | 14 | 14 | 14 | **8** | **4** | 14 |
| `graphs reused` (final) | **1581** | **1581** | **1581** | **1581** | **1581** | 315 |
| `print_timing` lines | 62 | 62 | 64 | 62 | 62 | 24 |
| `post_decode() failed` / HTTP 500 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| arm status | ok | ok | ok | ok | ok | ok |

Addendum arms (same checks, `stage0/logs/T12U-*.`, `T16U-1.*`):

| check | T12U-1 | T12U-2 | T16U-1 |
|---|---|---|---|
| verdict | `status=ok bench_rc=0 parse_rc=0 align match=True` | same | same |
| h2d pre / post (GB/s) | 6.13 / 6.12 | 6.12 / 6.11 | 6.11 / 6.13 |
| `link_bad_samples` / `gen1 ∧ util≥20` | 0 / **0** | 0 / **0** | 0 / **0** |
| VRAM (MiB) / MemAvailable after (GiB) | 6271 / 100.2 | 6271 / 99.9 | 6271 / 100.4 |
| `load1_max` / spare | 6.61 / **8** | 8.21 / **8** | 7.26 / **4** |
| `graphs reused` / `print_timing` | **1581** / 62 | **1581** / 62 | **1581** / 62 |
| `post_decode() failed` / HTTP 500 | 0 / 0 | 0 / 0 | 0 / 0 |

SPLIT-1's `graphs reused = 315` and 24 `print_timing` lines are consistent with 1 rep × 128 tok
(≈1/5 of a 3-rep arm's request count) — not a graph regression. Its 3 `invalid` hits are the benign
`erased invalidated context checkpoint (… n_swa = 0, size = 112.571 MiB)` slot messages, not
`post_decode()` failures.

---

## 3. Item 1 — profile windows (`F0P-1`, `T12-1`, `T16-1`)

Steady-state = `win>=2`, n=23 of 25 windows per arm (`stage0/arms/*.prof.txt`).

| metric | F0P-1 (`-t 6`) | T12-1 (`-t 12`) | T16-1 (`-t 16`) |
|---|---:|---:|---:|
| window tps (median) | 13.58 | 14.61 | 14.02 |
| **wall ms/step** | **73.64** | **68.46** | **71.31** |
| cpu ms/step (aggregate, includes spin) | 253.97 | 393.35 | 468.70 |
| cpu/wall = busy core-equivalents | **3.45 / 6 (57.5%)** | **5.75 / 12 (47.9%)** | **6.57 / 16 (41.1%)** |
| dev span ms/step | 73.51 | 68.16 | 71.01 |
| dev span / wall | 99.8% | 99.6% | 99.6% |
| **sync region ms/step** | **0.0047** | **0.0047** | **0.0047** |
| `rx_mbs` / `bytes_per_step_mib` (NVML) | 0 / 0.0 in every window | 0 / 0.0 | 0 / 0.0 |
| best / worst steady window | 65.36 / 1012.76 ms | 65.42 / 1011.05 | 66.06 / 1012.05 |
| mean steady wall (distorted by one idle window) | 114.54 | 109.98 | 112.21 |
| `PROF prefill` line | n=2029, 207.73 tok/s | n=2029, 207.30 | n=2029, 207.60 |

Readings:

1. **The device-timeline span covers ≥99.6% of wall, but that is a span, not GPU-busy.** NSYS
   (`NSYS-DECODE.md`) splits the same window 27.59 ms GPU-active / 57.32 ms GPU-idle — the idle
   lives *inside* that span, hidden from PROF. Never read `t_dev/wall` as utilisation (R7).
2. **The output/sampling sync region is a zero factor** (0.0047 ms/tok) — reproduces
   `PROFILE-DECODE.md` exactly.
3. **The NVML PCIe counters inside `--profile-decode` are dead** (`rx_mbs=0`,
   `bytes_per_step_mib=0.0` in all 69 window lines). They were replaced for this stage by an
   external `nvidia-smi dmon` sampler (§5). `PROFILE-DECODE.md:155-156`'s "no per-step PCIe traffic
   during decode" reading of those counters is therefore **not** independent evidence — the
   external sampler *does* measure decode traffic (5.1–5.7 MiB/token, §5).
4. **Thread axis:** going `-t 6 → -t 12 → -t 16` raises aggregate CPU 254 → 393 → 469 ms/tok
   (+85%) while wall moves only 73.6 → 68.5 → 71.3 ms/tok. The extra aggregate is thread-pool spin,
   as `PROFILE-DECODE.md §3` concluded; wall is **not** thread-starved.
5. **Best steady window ≈ 65.4 ms** — the fastest 64-step window in each arm equals the t0006
   reference, i.e. the reference is reachable within-session; the *median* sits ~10% above it (C1).
6. **Replication check against the prior session** (`PROFILE-DECODE.md`): their T6 = 73.06,
   T12 = 71.27 ms/tok profiled; this session F0P = 73.64 (+0.8%), T12 = 68.46 (−3.9%). Same
   direction, same magnitude class → the instrument is stable.

---

## 4. Item 2 — split count: **97 vs ~193 is settled**

`SPLIT-1` logged **2916 `## SPLIT` lines in 24 blocks** (12 decode-shape + 12 prefill-shape;
parsed to `stage0/split-parse.json`):

| | decode shape (n_tokens=1) | prefill shape (ubatch) |
|---|---|---|
| blocks | 12 | 12 |
| **splits per block** | **98** (hist `{"98": 12}`) | **145** (hist `{"145": 12}`) |
| backends | **49 CPU + 49 CUDA0, strictly alternating** | 143 CUDA0 + 2 CPU |
| **boundaries** | **97** | 144 |
| splits carrying inputs | 97 of 98 | 144 of 145 |
| input entries (nonzero) | 158 (103) | 158 (154) |
| **declared bytes** | **5.41 MiB/token** (unique per block: 5.18 … 20.94, ubatch-dependent) | **46765.37 MiB = 45.67 GiB** |
| └ weights | 0 | **46629.0 MiB = 45.54 GiB** — 141 tensors `blk.{0..46}.ffn_{gate,up,down}_exps.weight`, 47 layers |
| └ activations | 5.41 MiB | 136.37 MiB |
| recurrent/PLE `state_cache_entries` | **0** | **0** |
| top families (decode) | `ffn_moe_down-N` 48× = 4.688 MiB, `hc_mixed-*` ≈ 0.48 MiB, mask/embed ≈ 0.11 | — |

**Verdict: 97 non-empty / 98 total split points per decode step — the `~193` estimate is refuted**
(2× too high, because it counted each boundary twice). Cross-check: NSYS saw exactly
**380 `cudaStreamSynchronize`/token** = 3.9 syncs per boundary × 97 → consistent.

Decode traffic per step therefore has a hard floor: **5.41 MiB must cross CPU→GPU every token**
(`ffn_moe_down` activations 4.688 MiB + `hc_mixed` 0.469 + leaves/mask/embed ≈ 0.25).

---

## 5. Supplementary — PCIe streaming measurement (`nvidia-smi dmon`, `F0C-2`)

Why: the PROF NVML counters read 0 (§3.3), and prefill's Stage-1 premise had no byte measurement.
One `nvidia-smi dmon -i 1 -s t -d 1` sampler ran alongside `F0C-2` (313 samples @1 Hz, epoch-anchored
in `logs/F0C-2.dmon.epoch`).

| quantity | value |
|---|---|
| **session total RX** | **344.1 GiB** (TX 44.80 GiB) |
| peak | **6.689 GB/s** |
| time ≥5.5 GB/s / 1.0–5.5 / 0.1–1.0 / <0.1 GB/s | **34 s / 29 s / 18 s / 232 s** of 313 s |
| load+init (117.7 s window) | 29.85 GiB |
| warmup cell (96 prompt + 64 tok, 8.9 s) | 17.16 GiB |
| `p4k` rep0 **cold** (4077 new tok, TTFT 20.35) | **63.42 GiB** over 39.9 s |
| `p4k` rep1 **cache-hit** decode | **1.38 GiB** / 18 s = 79 MB/s |
| `p4k` rep2 window | 34.37 GiB — *drift artifact: the first seconds of `p12k` rep0 (see C4)* |
| `p12k` rep0 **cold** (12292 new tok, TTFT 61.42) | **196.09 GiB** over 79.0 s |
| `p12k` rep1 / rep2 cache-hit | 1.56 / 0.32 GiB |

**Derived (each shown with its assumption):**

1. **Decode cross-check ✓.** Predicted cache-hit decode traffic = 5.41 MiB × 256 = **1.35 GiB**;
   measured window **1.38 GiB (79 MB/s at 14.72 tok/s ⇒ 5.1 MiB/tok)**. `p12k` rep1: 80 MB/s at
   13.32 tok/s ⇒ 5.7 MiB/tok. **Independent confirmation of the split-declared 5.41 MiB/token
   within ±6%.**
2. **Per-chunk prefill stream ≈31–36 GiB / 2048 tokens = 68–79% of declared 45.67 GiB**
   `[measured, ±C4 drift]`:
   - `p4k` rep0: (63.42 − 1.35) / 2 chunks = **31.0 GiB/chunk**
   - `p12k` rep0: (196.09 − 1.35) / 6 chunks = **32.5 GiB/chunk**
   - whole-session residual: 344.1 − 17.16 (warmup) − 29.85 (load) − 3.26 (cache-hit decode) −
     6.4 (decode inside cold cells) = 287.4 GiB over 8 cold chunks = **35.9 GiB/chunk**
   The partial-vs-full filter is real: the declared 45.67 GiB is the *used-expert* union, and
   `ggml-backend.cpp:1805` ("copy only the experts that are used", grouping `:1875-1890`) plus the
   device-reuse split (`:1330-1342`) are what make the actual stream smaller than declared.
3. **Link duty during cold prefill ≈ 51%.** (63.42 + 196.09 − 2.7) GiB over (20.35 + 61.42) s =
   3.14 GiB/s against a 6.15 GiB/s link floor (measured h2d 6.12 GB/s) ⇒ **~50% of the link's
   capacity is idle while prefill computes.**
4. **PCIe is ≈49–58% of cold-prefill wall.** 31–36 GiB ÷ 6.15 GiB/s = 5.0–5.9 s of the
   measured 10.2 s/chunk (20.35 s / 2, and 61.42 s / 6) — the two cells agree on 10.2 s/chunk to
   <1%.
5. **Even tiny prompts stream.** The 96-token warmup moved **17.16 GiB** (38% of declared) —
   there is no cheap-chunk regime; streaming is per-graph, not amortised over the prompt.

---

## 6. Item 3 — config arms and G-0c

| comparison | Δ decode (p4k median) | verdict |
|---|---:|---|
| **T12-1 vs F0P-1** (both profiled, pre-registered) | 14.61 vs 12.94 = **+12.9%** | **fires** |
| **T16-1 vs F0P-1** (both profiled, pre-registered) | 13.73 vs 12.94 = **+6.1%** | **fires G-0c's ≥+6% bar** |
| T12-1 vs F0C-1 (profiled vs unprofiled) | 68.4 vs 71.9 = +5.0% | not a like-for-like gate |
| T16-1 vs F0C-1 | 72.8 vs 71.9 = **−1.3%** | profiled T16 does **not** beat the unprofiled control |
| T16-1 vs F0C-2 | 72.8 vs 72.9 = +0.1% | ditto |
| p12k decode, T16 vs F0P | 13.92 vs 14.02 = −0.5% | profiled p12k unchanged (G-R-safe) |

`p12k` cold prefill and TTFT are flat across every `-t` (200.80–202.96 tok/s cold p4k,
200.80–201.53 cold p12k, TTFT 20.28–20.35 / 61.20–61.44 s) ⇒ **the config arm does not touch the
prefill axis**, and spare stays ≥2 on every arm (14 / 8 / 4).

### 6.1 G-0c: profiled comparison fires, unprofiled comparison does not

The gate says *ship it as a config change*, and a shipped default is exercised **unprofiled**,
where the profiler's tax is absent. Risk R7 explicitly rejects profiler-on legs without an OFF
replicate. Three **addendum runs** were therefore added to the harness (case entries only; zero
product code, disclosed as post-hoc because they are OFF replicates of already-run arms, not new
knobs) and run immediately after the pre-registered five:

| comparison (p4k decode median of 3 reps) | `-t 6` | `-t 12` | `-t 16` |
|---|---:|---:|---:|
| **profiled** (pre-registered) | 12.94 (F0P-1) | 14.61 (T12-1) | 13.73 (T16-1) |
| **unprofiled** (OFF replicates) | 13.91 / 13.72 (F0C-1/2) | **14.81 / 14.65** (T12U-1/2) | **14.35** (T16U-1) |
| Δ **profiled** vs its control | — | **+12.9%** | **+6.1%** |
| Δ **unprofiled** vs its control (arm means) | — | **+6.62%** | **+3.87%** |
| profiler tax (profiled − unprofiled, same `-t`) | **+6.8%** (77.3 vs 72.4 ms) | **+0.8%** (68.4 vs 67.9) | **+4.5%** (72.8 vs 69.7) |

Two things follow:

1. **The profiler does not transfer cleanly between thread counts.** Its tax ranges **+0.8% to
   +6.8%** across the three configs, so a profiled-vs-profiled delta does not survive the OFF
   replicate: T16 goes **+6.1% → +3.87%**, T12 **+12.9% → +6.62%**. This is exactly what R7 was
   written for — **profiled-only evidence is not sufficient to ship.**
2. **G-0c as written (`-t 16` ≥ +6%) does not hold in the shipping configuration: +3.87%**
   (and +0.5% on `p12k`). ⇒ **do not ship `-t 16`, and do not drop later CPU-engine (Stage 3) work
   on this evidence.**

### 6.2 What the OFF replicates *do* show

**`-t 12` unprofiled = 14.73 tok/s mean (14.81 / 14.65) = 67.9 ms/tok, +6.62% vs the unprofiled
control pair on `p4k`** — clears the +6% bar, with **0.6 pp of margin** (per-arm: +6.47% and
+5.32% against F0C-1; +7.94% and +6.63% against F0C-2). On `p12k` it is only **+1.9%**, inside the
cell's own ±1.6% noise. All gates green on both arms (h2d 6.12–6.13, `link_bad=0`,
`gen1 ∧ util≥20 = 0`, VRAM 6271, `graphs reused = 1581` = control, 0 `post_decode() failed`,
0 HTTP 500, spare 8, `status=ok`).

Read it conservatively: **a real, zero-code ~6% on the canonical 4K cell, thin on the second cell,
and `-t 12` is not the arm G-0c names.** It is reported as a finding for the owner, not as a
fired gate.

**Open decision for the owner** (recorded, not taken here): document `-t 12` as the default
(+6.6% p4k, spare 8) or stay at `-t 6`. No in-repo config file sets `-t` for this harness — the
default lives in `.local/strata-research/arm-stage0.sh` (gitignored) and in the operator's launch
command, so a "config change" here is a documented default, not a code diff. The only in-repo `-t`
reference found is `docs/evidence/strata-3060-ab/harness/arm-fork.sh:82` (`-t 6`).

**Even if `-t 12` ships, Stage 3 is not superseded:** +6.6% is a thread-count effect on a budget
whose CPU-side share is ~37 ms/tok (§10) — the 5.75 core-equivalents at `-t 12` are 47.9% of the
pool, i.e. the CPU-MoE work is still the dominant term. G-0c's "drop any later CPU-engine work"
intended a ≥+6% *cheap* win that removes the *need* for the cache; it does not remove the measured
attribution.

---

## 7. Item 4 — G-0b correctness floor: **NOT RUN**

`llama-perplexity` is not built and local builds are barred this session (`.local/q2g` is a pinned
checkout; no `cmake`, no CI artefacts pulled). Consequences, stated plainly:

- **No G-K2 floor exists** ⇒ `G-0b: NOT RUN — needs CI llama-perplexity build`.
- **G-K2 is a blocking gate for Stage 1 and Stage 3** (`OPTION-C-DESIGN.md` Stage-3 gates, risk R6)
  ⇒ **Stages 1 and 3 remain blocked** even though Stage 0's attribution gate passed.
- Nothing else in Stage 0 depends on it: items 1/2/3/5/6 are timing, structure and log reads.

---

## 8. Item 5 — gap #7: F-B decision table applied to F0-2 / F0-3

Inputs: `docs/evidence/strata-3060-ab/logs/F0-2-engine.log`, `F0-3-engine.log`;
table at `IMPL-GAP-FORK.md:49-56`. One `server-context` persists across turns, so
`prompt eval time` is cumulative-incremental and `prompt_n` = that turn's **new** tokens.

| turn | task | F0-2 `prompt eval` / N → tok/s | F0-3 | `graphs reused` | `prompt processing` lines | F-B row hit |
|---:|---|---|---|---|---|---|
| 1 | 2390 | 48190.15 ms / **9121** = **189.27** | 48177.87 / 9121 = 189.32 | 2593 | 1 | cold long prompt (202 tok/s in this session's cold cell) |
| 2 | 2652 | **10004.05 ms / 731 = 73.07** | 9911.99 / **730** = 73.65 | 2593 → **2846** | 2 (218 → 727) | **≈10000/731 ⇒ cost inside slot**; N matches ⇒ LCP honoured; graphs +253 ⇒ **no rebuild**; 2 lines ⇒ **extra chunks** |
| 3 | 2911 | **11241.28 ms / 727 = 64.67** | 11230.01 / **724** = 64.47 | 2846 → **3099** | 2 (234 → 723) | cost inside slot; N matches; no rebuild; extra chunks |

**Not hit:** `≈6000/731` (pre-slot ~4 s) and `N ≠ 731` (LCP miss) and low `graphs reused`
(rebuild). **Pre-slot ≈ 0**: turn-2 TTFT 9.97 s ≈ `prompt eval` 10.00 s (from `IMPL-GAP.md:74-76`).

Chunk arithmetic (cumulative per `server-context.cpp:746-747`, which prints a line when a
`prompt processing` block exceeds 3 s → the last short chunk never prints):

- turn 2 (F0-2): **218 + 509 + 4 = 731** ✓ (F0-3: 217 + 509 + 4 = 730)
- turn 3 (F0-2): **234 + 489 + 4 = 727** ✓ (F0-3: 231 + 489 + 4 = 724)
- progress numerator = 9117 cached + cumulative new; denominator = full prompt (F-B row "N = 731").

**What it means:** the tail prefill is **2.6–2.9× slower than cold long-prompt prefill**
(73.1 / 64.7 vs 189.3 tok/s) *inside* the slot, and §5's warmup measurement shows even a 96-token
prompt streams 17 GiB. For a ≤2048-token batch the graph declares the same 45.54 GiB expert input;
whether it streams ~11 GiB (per-token-scaled 15.5 MiB/tok) or ~45 GiB (routing union saturated) is
**not measured — no `dmon` ran on the t0006 conversation** `[derived]`, so Stage 1 should take this
as its first measurement target rather than as a settled number.

**Stage 4 verdict: NO CHANGE** — cost inside slot, pre-slot ≈ 0; there is nothing for a
prefix/LCP fix to recover. The slow tail rate is a Stage 1 (streaming) symptom.

---

## 9. Item 6 — recurrent / PLE state (risk R2)

| question | answer | evidence |
|---|---|---|
| Does a recurrent RS buffer exist, and where? | **Yes, on CUDA0, 112.57 MiB** | `llama_memory_recurrent: CUDA0 RS buffer size = 112.57 MiB` (`SPLIT-1-engine.log:2884`); checkpoint size 112.571 MiB with `n_swa=0` |
| What are the tensors? | `cache_r_l%d`, `cache_s_l%d` (DeltaNet R/S), `cache_ple_r_l%d` (PLE conv history) | `llama-memory-recurrent.cpp:109,110,117` |
| Who consumes them? | `qwen4exp.cpp:1123` `conv_states_all = mctx_cur->get_r_l(il)`; `build_conv_state_at` `:1132` (def `:1350`); PLE `:1481` via `get_p_l` `:1516` | source |
| **Do they ride the cross-backend copies?** | **NO — `state_cache_entries = 0` in all 24 blocks; 0 occurrences of any `cache_*` name anywhere in the 1.8 MB SPLIT-1 log** (the only `cache_` hits are the client's `cached_tokens`/`cache_n` JSON fields) | `split-parse.json`, `logs/SPLIT-1-engine.log` |
| What *does* cross the boundary? | decode: **5.41 MiB/token of activations**; prefill: **45.54 GiB weights + 136 MiB activations** (`ple_embd`, `attn_inp_kq_mask`, `hc_mixed-*`, …) | §4 |

**Implication for Stages 2/3:** today no recurrent/PLE state is copied at a boundary, so no stage
is currently at risk of splitting conv history. Caveat: the names never print (they are only
allocated, never logged), so the conclusion rests on *absence of crossing* rather than an explicit
backend assignment line; the one hard fact is the buffer being pinned to **CUDA0**. Any
re-partition that moves a layer's MoE compute to CPU while its `cache_*` tensors stay on CUDA0
would introduce a **new** 112.57 MiB-per-state-set copy source at that boundary — flag for Stage 3
design review (`OPTION-C-DESIGN.md` R2).

---

## 10. G-0a closure arithmetic

Budget to explain: **65.4 ms/token** (t0006 F0, un-profiled, same prompt). Band ±10% = 58.9–71.9.

| component | ms/tok | source | kind |
|---|---:|---|---|
| GPU-active (kernels + device memcpy, union) | **27.59** | `NSYS-DECODE.md` per-kernel capture | measured |
| boundary copy transfer | **0.93** | 5.41 MiB ÷ 6.12 GB/s (§4, §5) | measured |
| boundary output/sync region `t_sync` | **0.005** | PROF (§3) | measured |
| host-side per-boundary sync/copy-launch | **≈0.5–1.5** | 97 boundaries × 5–15 µs bounded by NSYS "nested" finding (380 syncs/tok, API time ≈ spin ≈ GPU-active ⇒ not wall-additive) | **bounded, not timed** |
| CPU-MoE / host compute on critical path | **≈36.9** | residual: 65.4 − 27.6 − 0.93; cross-checked by NSYS (86% of on-CPU samples, 4.08 cores sustained, GPU-idle 57.32) and by PROF (3.45 core-equivalents at `-t 6`) | derived + measured |
| **sum** | **≈65.4** | | |

The three pre-registered sources each land:

1. **Profile windows** → wall is one device-timeline span (99.8%) with 3.45 busy core-equivalents
   behind it and a 0.0047 ms sync region; nothing in the sync/PCIe columns can carry tens of ms.
2. **Split count** → 97 boundaries × (0.93 ms transfer + nested sync) ≈ **0.9–2.4 ms total**, not
   the 10–25 ms the gap list predicted.
3. **Config arms** → **2.7–4.5 ms of wall is thread-tunable** (unprofiled: 72.4 → 69.7 at `-t 16`,
   → 67.9 at `-t 12`; profiled: 73.6 → 68.5 at `-t 12`); the remaining **~33–37 ms is CPU-MoE
   compute**, irreducible by threads — adding 6–10 threads moved wall by only −2.7…−5.1 ms while
   aggregate CPU moved +140…+215 ms/tok (3.45 → 5.75 core-equivalents).

⇒ **G-0a PASS (marginal).** The band is met structurally; the marginality is the *control drift*
(caveat C1), not an unexplained residual.

---

## 11. Gate-by-gate summary

| gate | threshold | measured | verdict |
|---|---|---|---|
| G-0a | three sources explain 65.4 ±10% | 27.6 + 0.93 + ~36.9 = 65.4; control 71.9 / 72.9 | **PASS (marginal)** — C1 disclosed |
| G-0b | G-K2 floor exists | not runnable here | **NOT RUN** → Stages 1/3 stay blocked |
| G-0c | `-t16` ≥ +6% decode, G-R/V/L pass | profiled **+6.1%**; **unprofiled +3.87%** (OFF replicate) | **FIRES profiled / FAILS shipping config → do not ship `-t16`**; `-t12` unprofiled **+6.62%** p4k reported as a finding (not the named arm, 0.6 pp margin) |
| G-L | h2d 6.00–6.30; `gen1 ∧ util≥20 = 0` | 6.10–6.13 all 9 runs; 0 such samples all 9 runs | **PASS** |
| G-V | VRAM ≤ 11 000 | 6271 every run | **PASS** |
| G-G | `graphs reused` ≥ control, 0 failures/500 | 1581 = control ×8 (3-rep runs); 0 / 0 | **PASS** |
| G-R | no axis regresses >3% | cold pf spread 1.2%, TTFT ≤0.4%, VRAM/h2d flat | **PASS** |
| spare rule | ≥2 spare logical CPUs | 14 / 14 / 14 / 8 / 4 / 14 / 8 / 8 / 4 | **PASS** |
| Stage-2 kill | boundary share < 6 ms ⇒ cancel | **1.4–2.4 ms** (0.93 measured + nested) | **Stage 2 CANCELLED before code** |

---

## 12. Stage implications (§5.2)

**Config default (Stage 0's only shippable output).** `-t 16` does **not** clear the +6% bar in
the unprofiled configuration (+3.87%) ⇒ **nothing ships from G-0c as written.** `-t 12` unprofiled
clears it on the canonical 4K cell (+6.62%, n=2 arms, 0.6 pp margin; `p12k` only +1.9%, all gates
green, spare 8) and is recorded for an owner decision (§6.2). No code and no default changed in
this stage either way.

**Stage 1 (prefill GPU expert streaming) — premise measured, gate still blocked.**
Cold prefill is ≈50% link / ≈50% compute by duty cycle, streaming 68–79% of the declared expert set
per chunk while the link peaks at 6.689 GB/s and idles half the time. Raising link *duty* (overlap,
not bandwidth) is the lever that makes G-P's ≥1.15× reachable; nothing here can produce 1.15× by
bandwidth alone. **Blocked on G-K2 (§7).** Note `--moe-expert-cache-size > 0` remains barred
(−40% decode), so the design must stream, not cache — which the measurement supports.

**Stage 2 (decode boundary) — cancelled by its own pre-registered kill criterion.**
Share measured at 1.4–2.4 ms/tok against a 6 ms threshold; NSYS independently shows the 97 boundary
syncs are nested inside GPU-active. The `--decode-overlap`-class fixes (events, async copy,
device-side wait) would buy at most the measured transfer, i.e. **≤1.5% decode** — below the stage's
own G-D ≥+10% bar before any code is written. **Do not build it.**

**Stage 3 (adaptive expert cache / hot-cold slab) — unchanged.** Attribution now available:
CPU-MoE ≈36.9 ms/tok of the 65.4 budget, 4.08 cores sustained, GPU-idle 57.32 ms — that is the
prize the slab split attacks. G-K2 blocking (§7); N ≤ 42 VRAM ceiling unchanged; R2 answered (§9).

**Stage 4 (turn-2/3 fixed cost) — no change.** Cost inside the slot, pre-slot ≈ 0 (§8).

---

## 13. Caveats

- **C1 — control drift.** Un-profiled F0 re-measured **71.9** (F0C-1) and **72.9** (F0C-2) ms/tok
  vs the 65.4 reference = **+9.9% / +11.4%** (median of the two arms 72.4, +10.4%). F0C-1 is inside
  the G-0a band, F0C-2 is 1.4% outside. Host load during the arms was `load1_max` 4.96–9.18
  (nproc=20) and MemAvailable 100.6–102.2 GiB. All stage conclusions are **ratios against
  same-session controls**, so structure is unaffected; absolute numbers should not be compared to
  t0006 without this note. The *best* 64-step window in each profiled arm was 65.4–66.1 ms, i.e. the
  reference value is still reachable in-session. For scale: the unprofiled `-t 12` arm landed at
  67.5 ms, only +3.2% above the reference — but that is a different config, it does **not** explain
  the `-t 6` drift.
- **C2 — the profiler tax is not constant.** Measured as (profiled − unprofiled) on the same
  thread count: **+6.8% at `-t 6`** (77.3 vs 72.4 ms), **+0.8% at `-t 12`** (68.4 vs 67.9),
  **+4.5% at `-t 16`** (72.8 vs 69.7). The `-t 6` figure matches `PROFILE-DECODE.md`'s +7.7.
  Consequence: a profiled-vs-profiled delta does not transfer to the unprofiled world (T16 +6.1% →
  +3.87%; T12 +12.9% → +6.62%), which is precisely what R7's OFF-replicate requirement exists to
  catch (§6).
- **C3 — `t_dev` is a span, not GPU-busy**; NVML-in-PROF PCIe counters are dead (read 0). Both were
  replaced by NSYS and `dmon` respectively.
- **C4 — `dmon` window drift.** Window boundaries vs bench timestamps drift up to ~30 s (visible as
  34.37 GiB landing in the `p4k` rep2 window). Session totals, peaks, time-bucket counts and the
  clean 1.38 GiB cache-hit window are drift-immune; per-chunk prefill estimates carry an
  uncertainty of a few GiB (§5.2 shows three independent estimates agreeing at 31.0 / 32.5 / 35.9).
- **C5 — SPLIT-1 is 1 rep × 128 tok** → its decode rate, `graphs reused` (315) and `print_timing`
  count (24) are not comparable to the 3-rep/256-tok arms. Its structural counts (24 blocks, 98/145
  splits, 97 boundaries, 5.41 MiB / 45.67 GiB) are per-shape and unaffected by rep count.
- **C6 — per-boundary host overhead is bounded, not timed** (0.5–1.5 ms estimate, §10).
- **C7 — tail-prefill streaming volume is `[derived]`**, not measured (no `dmon` on the t0006
  conversation logs) — see §8.
- **C8 — the load-window `VALIDITY WARN` is expected-by-design noise, not a gate.**
  `arm-stage0.sh:162` emits `VALIDITY WARN: no util>=20 sample during load` to harness stdout when
  no `util≥20` sample exists in the TSV at that moment. The load phase does no GPU work (the GGUF is
  memory-mapped; the first compute is the warmup request, which runs *after* the check) — observed
  on `T16U` (transcript captured under `.local/strata-research/out/`). Harness stdout was not
  persisted for the pre-registered arms. It is **not** part of G-L: the G-L condition
  (`gen1 ∧ util≥20 = 0`) is computed from the persisted `*.link.tsv` and is 0 on **all seven** arms.
- **C9 — replicate counts.** F0C ×2, T12U ×2, F0P / T12 / T16 / T16U / SPLIT ×1 each. Within-arm
  3-rep medians are tight (spread ≤13% except F0C-2's 1.68 tok/s and T16U's 1.09), but arm-to-arm
  variance of ±3% should be assumed for single-run arms — which matters for T16U's +3.87% (§6).

---

## 14. Follow-ups (no code in this stage)

1. **G-0b / G-K2 floor** — needs the CI `llama-perplexity` build (owner: artefact or a CI run).
   Until then Stages 1 and 3 are blocked.
2. **G-0c ship decision** — measuring is done (§6): `-t16` fails the bar unprofiled (+3.87%),
   `-t12` clears it on p4k (+6.62%, n=2, 0.6 pp margin) but only +1.9% on p12k. **Owner decision:**
   document `-t 12` as the default (no in-repo file holds `-t` today), or keep `-t 6`. Nothing in
   this stage changed a default.
3. **Stage 1 first measurement** — stream volume for a 731-token (turn-2) prefill, same `dmon`
   method (C7); resolves whether expert-union saturation or per-token scaling governs the tail.
4. **Control replicate** — a third un-profiled F0 arm would pin C1 (71.9 vs 72.9 vs reference 65.4)
   before any absolute-ms claim is used as a gate elsewhere.
5. **Strata 30.9 ms/tok baseline was not re-run** (one GPU one task, fork only) — gap ratios vs
   Strata in this document inherit that from `PROFILE-DECODE.md`.

---

## 15. Evidence files and repro

```
docs/evidence/strata-gap/
  STAGE0-RESULTS.md          this file
  OPTION-C-DESIGN.md         spec + gates (PR #821)
  stage0/
    PRE-REGISTERED.md        arms/gates/prompts fixed before the first run
    split-parse.json         item 2 parsed structure (24 blocks, 98/145 splits, 45.67 GiB)
    prompts/                 p4k, p12k, warmup + sha256sums.txt (byte-identical to t0006)
    arms/                    <ARM>.bench.json, .logparse.json, .prof.txt
    logs/                    <ARM>-engine.log, .link.tsv, .h2d.txt, .summary.txt,
                             .spare.txt, .vram_after_load.txt,
                             F0C-2.dmon.tsv + F0C-2.dmon.epoch   (PCIe probe)
  ../strata-3060-ab/logs/F0-2-engine.log, F0-3-engine.log   (item 5 inputs)
```

Repro (from the worktree root; harness is gitignored, under `.local/`):

```bash
cd .local/wt-stage0
bash .local/strata-research/arm-stage0.sh F0C     # …also F0P | T12 | T16 | SPLIT | T16U | T12U
nvidia-smi dmon -i 1 -s t -d 1 > stage0/logs/F0C-2.dmon.tsv &   # PCIe probe (arm 2 only)
```

Rig state at close-out: no `llama-server`, GPU0 = 1 MiB, GPU1 = 1 MiB, ports 8091/8093 free,
MemAvailable ≈ 101 GiB.
