# Option C — Stage 0b results: confirm the `-t 12` finding with n ≥ 4 interleaved pairs

Date: 2026-10-04
Branch: `docs/811-stage0b` (off `origin/epic/811-strata-gap-analysis` @ `9b2a8df54`)
Pre-registration: `stage0b/PRE-REGISTERED.md`, committed as `db6c99287` **before any run**
Spec: `STAGE0-RESULTS.md` §1/§2/§6/§10 (PR #822) · Task: t0006-derived confirmation stage
Rig: RTX 3060 **GPU1 only**, port 8093, one GPU one task, 8 arms strictly sequential
in one session. Binary: `.local/q2g/src/build/bin/llama-server` (no builds, no podman, no `/tmp`).

## TL;DR / verdict

**DO NOT SHIP `-t 12`.** All eight arms finished green and every gate passed, but the
pre-registered ship threshold was **not** met.

| pre-registered clause | threshold | measured | verdict |
|---|---|---|---|
| 1. mean paired `p4k` delta | **≥ +6.0%** | **+3.35%** | **FAIL** |
| 2. paired `p4k` delta positive | ≥ 3 of 4 pairs | **4 of 4** | PASS |
| 3. `p12k` mean paired delta | ≥ −1.5% | **+5.85%** | PASS |
| 4. G-L ∧ G-V ∧ G-G ∧ G-R | all hold | all hold | PASS |

**Measured delta with its spread (the statistic the pre-registration fixed):
`p4k` mean `+3.35%`, min `+2.01%`, max `+5.34%`** — per-pair `+5.34 / +2.01 / +2.18 / +3.87`,
all four positive. 95% CI on the mean **`[+0.85%, +5.85%]`** — even the upper bound of the
interval sits **below the +6% bar**, so the miss is not a sample-size artifact.

**The Stage 0 `+6.62%` did not reproduce.** Interleaving the arms inside one session, where
both configs are sampled at the same session positions, moves the estimate to **`+3.33%`
(unpaired arm means, the secondary statistic) — roughly half the Stage 0 number.**

| | Stage 0 (`n=2`, not interleaved) | **Stage 0b (`n=4` pairs, interleaved)** |
|---|---|---|
| `-t 6` control mean `p4k` | 13.815 tok/s = 72.39 ms/tok | **14.030 tok/s = 71.28 ms/tok** |
| `-t 12` treatment mean `p4k` | 14.730 tok/s = 67.89 ms/tok | **14.498 tok/s = 68.98 ms/tok** |
| **Δ `p4k`** | **+6.62%** (0.6 pp margin) | **+3.35% paired / +3.33% unpaired** (2.7 pp *short*) |
| Δ `p12k` | +1.9% | **+5.85%** |
| control vs t0006 65.4 ms reference | **+10.7%** (C1 drift) | **+9.0%** (drift persists) |
| control day-over-day (S0 → S0b) | — | **+1.56% faster** |
| `-t 12` day-over-day (S0 → S0b) | — | **−1.58% slower** |

The Stage 0 number was inflated in **both** directions at once: its control ran slow and its
treatment ran fast relative to this session. Removing that with pairing costs 3.3 pp — which
is the entire margin over the bar and then some.

Gate roll-up:

| gate | threshold | measured (8 arms) | verdict |
|---|---|---|---|
| **G-L** PCIe | h2d pre **and** post 6.00–6.30 GB/s; `gen1 ∧ util≥20 = 0`; `link_bad = 0` | h2d **6.10–6.12** GB/s, 0 reruns needed; `link_bad_samples = 0` and `gen1 ∧ util≥20 = 0` on all 8 | **PASS** |
| **G-V** VRAM | ≤ 11 000 MiB after load | **6271 MiB** on all 8 | **PASS** |
| **G-G** graphs | `graphs reused` ≥ control, 0 `post_decode() failed`, 0 HTTP 500 | **1581** on all 8 (= control), 0 / 0, 0 bench errors, `align match=True` 7/7 on all 8 | **PASS** |
| **G-R** no axis regresses >3% | cold prefill / TTFT within 3% of control | cold pf range 201.96–203.44 tok/s = **0.73% spread**, TTFT 20.25–20.39 s = **0.69% spread**, max deviation from the control mean **0.38%**, VRAM/h2d/graphs identical | **PASS** |
| **spare** | ≥ 2 logical CPUs | 14 (`-t 6`) / **8** (`-t 12`) on every arm | **PASS** |
| arm status | `status=ok bench_rc=0 parse_rc=0` | 8 / 8 | **PASS** |

Close-out: no `llama-server`, **GPU0 = 1 MiB, GPU1 = 1 MiB**, ports 8091/8093 free,
MemAvailable 87.7 GiB.

---

## 1. Why this stage exists

`STAGE0-RESULTS.md` §6.2 reported *"`-t 12` unprofiled = 14.73 tok/s … **+6.62%** vs the
unprofiled control pair on `p4k`"*, which clears the +6% G-0c bar with **0.6 pp of margin**.
Three properties of that measurement made it non-decisive, and all three are disclosed in
Stage 0's own caveats:

1. **`n = 2` arms per config** (C9), i.e. two control and two treatment arms.
2. **The arms were not interleaved, and treatment and control sat at opposite ends of the
   session.** Stage 0's actual order (from `stage0/logs/*.vram_after_load.txt` mtimes) was
   `F0C-1` (22:17, pos 1) → `F0P-1` → `T12-1` → `T16-1` → `SPLIT-1` → `F0C-2` (22:59, **pos 6**)
   → `T16U-1` → **`T12U-1` (23:51, pos 8) → `T12U-2` (23:57, pos 9)**. The two `-t 12`
   OFF replicates that carry the `+6.62%` claim are the **last two arms of a 1 h 44 m session**,
   while the controls they are compared against ran at positions 1 and 6. Everything the host
   did between 22:17 and 00:01 lands inside that comparison.
3. **The control drifted +10% day-over-day** (C1 / §10): the same unprofiled `-t 6` config
   measured **65.4 ms/tok** in t0006 and **71.9 / 72.9 ms/tok** in Stage 0.

A 0.6 pp margin sitting on top of ~1.6% control-pair noise and a +10% day-over-day drift is
not separable from drift. This stage re-measures it with **4 strictly interleaved pairs in one
session** so that any monotone in-session change applies to both configs at the same session
position and cancels in the paired difference.

## 2. Method

### 2.1 Design (frozen in `PRE-REGISTERED.md`, committed `db6c99287` before the first arm)

| position | arm | `-t` | pair | | position | arm | `-t` | pair |
|---:|---|---:|---:|---|---:|---|---:|---:|
| 1 | `T6-1`  | 6  | 1 | | 5 | `T6-3`  | 6  | 3 |
| 2 | `T12-1` | 12 | 1 | | 6 | `T12-3` | 12 | 3 |
| 3 | `T6-2`  | 6  | 2 | | 7 | `T6-4`  | 6  | 4 |
| 4 | `T12-2` | 12 | 2 | | 8 | `T12-4` | 12 | 4 |

**Strictly interleaved A B A B A B A B**, enforced by `.local/strata-research/run-stage0b.sh`,
which refuses to start position *k+1* until position *k* has been killed and both GPUs read
≤ 5 MiB. The driver aborted on the first non-zero exit; **all 8 positions returned `exit=0`.**

Session clock (one session, no pause): position *k* started 07:00:34 / 07:06:06 / 07:11:28 /
07:16:53 / 07:22:11 / 07:27:36 / 07:32:59 / 07:38:21, session complete 07:43:41
(`logs/session-sequence.log`). Arm-to-arm cadence **5 m 18 s – 5 m 32 s** — no arm is a
session-position outlier. Bench windows per arm: 3 m 27 s – 3 m 36 s.

### 2.2 Everything else is Stage 0 exactly

- **Bench per arm:** warmup (96 tok, `ad920c19…`) → `p4k` (4068 tok, `fa13b451…`) × **3 reps**
  → `p12k` (12283 tok, `2e5eabdf…`) × **3 reps**, `--max-tokens 256`.
  `stage0b/prompts/` is a byte-for-byte copy of `stage0/prompts/`; `sha256sum -c sha256sums.txt`
  was run before arming and printed `OK` for all three files.
- **Flags:** t0006 F0 verbatim, **`--profile-decode` absent on all 8 arms** (R7 OFF replicates
  only), `--spec-type none`, `-c 16384 --parallel 1 -b 2048 -ub 2048`, `--load-mode none`,
  `--moe-expert-cache-size 0`, `--override-tensor per_layer_token_embd=CPU`.
  **`k` stays 10, no `--override-kv`** (`OPTION-C-DESIGN.md` §0.2).
  Full flag string is captured verbatim in `logs/session-transcript.log` for every arm.
- **Cold start per arm:** a fresh `llama-server` per arm — pre-arm gates → h2d pre → 1 Hz link
  sampler → launch → `/health` → sleep 3 → bench → exact-pid `kill` (+ `kill -9` fallback only
  if still alive) → h2d post → parse → post-arm idle check. Identical to `arm-stage0.sh`.
- **GPU1 only** (`CUDA_VISIBLE_DEVICES=1`, `CUDA_DEVICE_ORDER=PCI_BUS_ID`), **port 8093 only**,
  unpinned, `TMPDIR=$WT/.local/tmp`. Ports 8086/8091 untouched; GPU0 never used.
- Harness: `arm-stage0b.sh` is `arm-stage0.sh` with the arm table reduced to `T6`/`T12`,
  `PROF` hard-wired to 0, `EV` pointed at `stage0b/`, the `n ≤ 4` cap added, the h2d band made a
  hard gate with one rerun, and a post-arm GPU-idle check added. `sample_link.sh`,
  `bench3060.py`, `parse_arm_logs.py` are **byte-identical to Stage 0** (md5 verified).

### 2.3 Guards as run

| guard | result |
|---|---|
| both GPUs ≤ 5 MiB pre-arm | **PASS** — `GPU0=1MiB GPU1=1MiB` at session pre-arm and before all 8 arms |
| 8091 not listening | **PASS** (session pre-arm + per arm) |
| 8093 free pre-arm | **PASS** (session pre-arm + per arm) |
| no other `llama-server` | **PASS** (session pre-arm + per arm) |
| MemAvailable ≥ 67 GiB **pre-arm only** | **PASS** — 86.4 GiB session pre-arm; per-arm 87.4–87.9 GiB after |
| spare = `nproc − threads` ≥ 2 | **PASS** — 14 at `-t 6`, **8 at `-t 12`**, recorded per arm in `*.spare.txt` |
| h2d pre **and** post, band 6.00–6.30 GB/s | **PASS** — 16/16 probes in band on attempt 1 (no reruns) |
| k = 10, no `--override-kv` | **PASS** — flag strings in `session-transcript.log` |
| no builds / no podman / no CI / no `/tmp` | **PASS** — pinned existing binary, `TMPDIR` under `.local/tmp` |
| exact-pid kills | **PASS** — one `llama-server` pid per arm, recorded in `*.engine.pid`, killed by pid |
| crash / invalid link / guard failure ⇒ STOP | **not triggered** — no aborts; all 8 arms `exit=0` |

## 3. Results — per-arm cell medians

`stage0b/arms/*.bench.json`; value = **median of the 3 reps' `decode_tps`** (Stage 0 §2.1
reduction). Cold prefill = rep0 `prompt_tokens / ttft`.

| pair | arm | `-t` | p4k reps (tok/s) | **p4k med** | **ms/tok** | p12k reps (tok/s) | **p12k med** | p4k cold pf | p12k cold pf | TTFT p4k / p12k |
|---:|---|---:|---|---:|---:|---|---:|---:|---:|---|
| 1 | `T6-1`  | 6 | 12.89 / 13.77 / 13.67 | **13.670** | 73.15 | 13.30 / 13.46 / 12.98 | **13.300** | 202.43 | 200.93 | 20.35 / 61.39 |
| 1 | `T12-1` | 12 | 14.40 / 14.67 / 14.25 | **14.400** | 69.44 | 14.48 / 14.49 / 14.87 | **14.490** | 202.68 | 201.22 | 20.32 / 61.30 |
| 2 | `T6-2`  | 6 | 14.64 / 14.45 / 13.95 | **14.450** | 69.20 | 13.69 / 13.65 / 14.05 | **13.690** | 201.96 | 200.72 | 20.39 / 61.45 |
| 2 | `T12-2` | 12 | 14.67 / 14.77 / 14.74 | **14.740** | 67.84 | 14.75 / 14.83 / 14.72 | **14.750** | 202.89 | 200.89 | 20.30 / 61.40 |
| 3 | `T6-3`  | 6 | 14.41 / 13.79 / 13.55 | **13.790** | 72.52 | 13.55 / 14.14 / 14.00 | **14.000** | 203.09 | 201.21 | 20.28 / 61.30 |
| 3 | `T12-3` | 12 | 13.70 / 14.09 / 14.40 | **14.090** | 70.97 | 14.56 / 14.29 / 14.77 | **14.560** | 202.92 | 201.07 | 20.30 / 61.34 |
| 4 | `T6-4`  | 6 | 14.09 / 14.21 / 14.50 | **14.210** | 70.37 | 13.98 / 14.36 / 14.48 | **14.360** | 203.44 | 201.37 | 20.25 / 61.25 |
| 4 | `T12-4` | 12 | 14.63 / 14.76 / 14.77 | **14.760** | 67.75 | 14.75 / 14.86 / 14.46 | **14.750** | 203.02 | 201.16 | 20.29 / 61.31 |
| | **`T6` mean** | 6 | | **14.030** | **71.28** | | **13.838** | 202.73 | 201.06 | 20.32 / 61.35 |
| | **`T12` mean** | 12 | | **14.498** | **68.98** | | **14.637** | 202.88 | 201.09 | 20.30 / 61.34 |

Within-arm 3-rep `p4k` spread: 0.7%–6.5% (widest `T6-1` 12.89/13.77/13.67, 6.5%; medians are
median-robust against that, as in Stage 0).

## 4. Paired-difference statistic (as pre-registered)

`delta_k(c) = 100 × ( V(T12-k, c) − V(T6-k, c) ) / V(T6-k, c)`, `V` = arm median of 3 reps.

| pair | p4k `T6` | p4k `T12` | **p4k Δ** | p12k `T6` | p12k `T12` | **p12k Δ** |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 13.670 | 14.400 | **+5.34%** | 13.300 | 14.490 | **+8.95%** |
| 2 | 14.450 | 14.740 | **+2.01%** | 13.690 | 14.750 | **+7.74%** |
| 3 | 13.790 | 14.090 | **+2.18%** | 14.000 | 14.560 | **+4.00%** |
| 4 | 14.210 | 14.760 | **+3.87%** | 14.360 | 14.750 | **+2.72%** |
| | | **mean** | **+3.35%** | | **mean** | **+5.85%** |
| | | **min / max** | **+2.01% / +5.34%** | | **min / max** | **+2.72% / +8.95%** |
| | | signs | **+4 / 4** | | | **+4 / 4** |

| statistic | `p4k` (canonical) | `p12k` (G-R context) |
|---|---|---|
| **mean** | **+3.35%** | **+5.85%** |
| **min** | **+2.01%** | **+2.72%** |
| **max** | **+5.34%** | **+8.95%** |
| sd / sem (n=4) | 1.57 / 0.79 | 2.97 / 1.48 |
| t (df=3) | 4.26 | 3.94 |
| **95% CI on the mean** | **[+0.85%, +5.85%]** | [+1.13%, +10.57%] |
| secondary unpaired arm-mean Δ | **+3.33%** | **+5.78%** |

Read the `p4k` row as the answer: the effect is **real (4/4 positive, t = 4.26)** but its
**95% CI upper bound, +5.85%, still does not reach the +6% bar.**

In ms/tok on the canonical cell: **71.28 → 68.98 ms/tok = −2.30 ms/tok**, i.e. +3.22% when
expressed on the ms/tok axis (the pre-registered Δ is defined on tok/s, so **+3.35%** is the
reported figure).

## 5. Gates / health (`stage0b/logs/*`)

| arm | status | spare | h2d pre / post (GB/s) | h2d reruns | `link_bad` | `gen1 ∧ util≥20` | VRAM (MiB) | `graphs reused` | `post_decode() failed` | HTTP 500 | bench errors | `load1_max` |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `T6-1`  | ok | 14 | 6.11 / 6.10 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 5.89 |
| `T12-1` | ok | 8 | 6.11 / 6.12 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 5.72 |
| `T6-2`  | ok | 14 | 6.12 / 6.11 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 5.52 |
| `T12-2` | ok | 8 | 6.11 / 6.12 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 8.14 |
| `T6-3`  | ok | 14 | 6.12 / 6.11 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 6.78 |
| `T12-3` | ok | 8 | 6.12 / 6.10 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 7.02 |
| `T6-4`  | ok | 14 | 6.11 / 6.12 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 6.18 |
| `T12-4` | ok | 8 | 6.12 / 6.12 | 0 | 0 | 0 | 6271 | **1581** | 0 | 0 | 0 | 6.74 |

All arms: `status=ok bench_rc=0 parse_rc=0`, `align match=True` with 7 requests / 7
`print_timing` blocks, `MemAvailable after` 87.4–87.9 GiB.

**G-R detail** — cold prefill and TTFT vs the session's control mean
(control `p4k` cold pf 202.73 tok/s, TTFT 20.32 s):

| arm | cold-prefill Δ | TTFT Δ | | arm | cold-prefill Δ | TTFT Δ |
|---|---:|---:|---|---|---:|---:|
| `T6-1`  | −0.15% | +0.15% | | `T6-3`  | +0.18% | −0.18% |
| `T12-1` | −0.03% | +0.02% | | `T12-3` | +0.09% | −0.09% |
| `T6-2`  | −0.38% | +0.38% | | `T6-4`  | +0.35% | −0.35% |
| `T12-2` | +0.08% | −0.08% | | `T12-4` | +0.14% | −0.14% |

Max absolute deviation **0.38%** — an order of magnitude inside the 3% bar (Stage 0 saw a
1.2% spread). Cold prefill spans 201.96–203.44 tok/s on `p4k` and 200.72–201.37 on `p12k`;
TTFT 20.25–20.39 s / 61.25–61.45 s. **The `-t 12` config still does not touch the prefill axis.**

`print_timing` line count is 62 on seven arms and 63 on `T6-1`; the extra line is a benign
`prompt processing … progress` chunk line (it prints whenever a chunk exceeds 3 s), not a
structural difference — `graphs reused` final is **1581** on all eight and `align` is 7/7
on all eight.

## 6. Decision against the pre-registered rule

> **SHIP `-t 12` as config only if** mean paired `p4k` delta ≥ +6% **AND** the paired delta is
> positive in ≥ 3 of 4 pairs **AND** `p12k` does not regress by more than 1.5% **AND**
> G-L/G-V/G-G/G-R hold. **Else DO NOT SHIP**; report the measured delta with its spread.

| # | clause | measured | verdict |
|---:|---|---|---|
| 1 | mean paired `p4k` Δ ≥ +6.0% | **+3.35%** (95% CI upper **+5.85%**) | **FAIL** |
| 2 | paired `p4k` Δ positive in ≥ 3 of 4 pairs | **4 / 4** | PASS |
| 3 | `p12k` mean paired Δ ≥ −1.5% | **+5.85%** | PASS |
| 4 | G-L ∧ G-V ∧ G-G ∧ G-R | all four PASS | PASS |

### **VERDICT: DO NOT SHIP.**

**Reported measured delta with its spread: `p4k` mean `+3.35%`, min `+2.01%`, max `+5.34%`
(four pairs: `+5.34 / +2.01 / +2.18 / +3.87`, 4/4 positive).**
Secondary unpaired arm-mean Δ = `+3.33%`. `p12k`: mean `+5.85%`, min `+2.72%`, max `+8.95%`.

**Nothing ships. No default, no config file and no product code changed by this stage.** As
Stage 0 §6.2 notes, there is no in-repo file that sets `-t` for this harness anyway — a
"config change" here would only ever be a documented default, and the documented default stays
**`-t 6`**.

## 7. What this says about the Stage 0 finding

1. **The effect is real but half the claimed size.** 4/4 pairs positive with t = 4.26
   (p ≈ 0.024) rules out "pure noise". What does not survive is the *magnitude*: +6.62% →
   **+3.35%**.
2. **The inflation decomposes cleanly into the day-over-day drift C1 warned about:**

   | | Stage 0 | Stage 0b | change |
   |---|---:|---:|---:|
   | `-t 6` control mean | 13.815 tok/s (72.39 ms/tok, **+10.7%** vs the 65.4 reference) | 14.030 tok/s (71.28 ms/tok, **+9.0%**) | **+1.56% faster** |
   | `-t 12` treatment mean | 14.730 tok/s (67.89 ms/tok) | 14.498 tok/s (68.98 ms/tok) | **−1.58% slower** |

   Both arms of the Stage 0 comparison sat on the wrong side of the drift: the control ran
   ~1.6% slow and the treatment ~1.6% fast *relative to this session*. That is worth
   ≈ 3.2 pp of apparent speedup, i.e. the whole margin over the bar. **Interleaving did its
   job** — this is exactly the failure mode the A B A B design was chosen to remove.
3. **The control drift against the t0006 reference persists** (+9.0% vs +10.7% in Stage 0),
   so **absolute ms/tok still must not be compared to t0006 without C1.** All conclusions here
   are in-session paired ratios.
4. **`p12k` flipped from +1.9% (Stage 0) to +5.85%** — see caveat D2; that cell carries a
   session-position trend and the fixed A→B ordering biases its paired delta upward, so it
   should not be read as a larger `p12k` win.
5. **Stage 3 is not superseded either way** (Stage 0 §6.2): a confirmed +3.35% is a
   thread-count effect on a budget whose CPU-MoE share is ~37 ms/tok; `-t 12` runs the
   CPU-side work at 57.5% of the thread pool's core-equivalents (5.75 / 12) and the dominant
   term does not move.

## 8. Caveats

- **D1 — ordering is strictly A→B, not counterbalanced.** The owner's design is A B A B, so
  within every pair `-t 12` runs immediately *after* `-t 6`. Pairing cancels any monotone
  change **between** pairs (which is what defeats the cross-session drift that sank Stage 0),
  but a monotone change **inside** a pair would bias the delta rather than cancel it. The
  `p4k` cell shows no such trend (control 13.67 / 14.45 / 13.79 / 14.21 — no monotone drift,
  `load1_max` 5.52–8.14 with no position pattern), so the primary statistic is not visibly
  affected; the `p12k` cell *is* (D2). A counterbalanced B A / A B design would remove this
  residual entirely and is the recommended shape if a Stage 0c is ever run.
- **D2 — `p12k` control medians rise monotonically with session position:**
  13.300 → 13.690 → 14.000 → 14.360 tok/s (1st → 7th position, **+7.7%**), while the `T12`
  `p12k` arm medians are flat (14.49 / 14.75 / 14.56 / 14.75). Because `T6-k` always precedes
  `T12-k`, that rise is credited to the treatment, which is why pair 1's `p12k` delta is
  +8.95%. **`p4k` is the canonical cell and shows no such trend**; clause 3 passed by a wide
  margin (+5.85% vs a −1.5% floor) so the verdict does not hinge on it, but the `p12k` number
  itself should be read as an upper bound.
- **D3 — `.h2d.txt` holds the POST probe only.** In this harness the post probe writes with
  `>`, truncating the file that the pre probe created (Stage 0's harness appended). Both
  probes are gated at arm time (each aborts the arm if outside 6.00–6.30), and **both values
  are recorded** in `logs/<ARM>.spare.txt` as `h2d_pre=` / `h2d_post=` and as raw
  `h2d_pre_attempt1:` / `h2d_post_attempt1:` lines in `logs/session-transcript.log`. The table
  in §5 is built from `*.spare.txt`. Left unchanged deliberately so all 8 arms share one
  harness; disclosed rather than patched mid-session.
- **D4 — `VALIDITY WARN: no util>=20 sample during load`** appears on arms where the sampler
  caught no `util≥20` row before the check ran. This is Stage 0 caveat **C8**, expected by
  design: the load phase does no GPU work (the GGUF is memory-mapped; first compute is the
  warmup request, which runs *after* the check). It is **not** part of G-L — G-L's condition
  (`gen1 ∧ util≥20 = 0`) is computed from the persisted `*.link.tsv` and is **0 on all 8 arms**.
- **D5 — `p12k` remains the noisier cell** (Stage 0: control pair spans ±1.6%). Its per-pair
  deltas span +2.72% … +8.95%, sd 2.97 — three times the `p4k` sd of 1.57. Hence the 1.5%
  tolerance in clause 3 and the canonical status of `p4k`.
- **D6 — shared host.** `load1_pre` at arm start 2.38 (session start) then 4.49–6.78 for
  positions 2–8, i.e. mean 5.10 under the `T6` positions (1/3/5/7) vs 4.79 under the `T12`
  positions (2/4/6/8) — a 0.31 spread on nproc 20, and if anything it biases the paired delta
  *upward* (control under slightly higher load), so the `+3.35%` is not understated by it.
  `load1_max` during the arms 5.52–8.14 (mechanically higher at `-t 12`, which runs 12 spinning
  threads vs 6 — not a confound). MemAvailable 86.4 → 87.9 GiB. Recorded as context, not gates.
  Nothing else touched GPU0 or ports 8086/8091 during the session.
- **D7 — G-0b / G-K2 remains NOT RUN** (Stage 0 §7). This stage is a config-axis
  confirmation and does not touch the correctness floor; Stages 1 and 3 stay blocked on the
  CI `llama-perplexity` build exactly as before.

## 9. Evidence files and repro

```
docs/evidence/strata-gap/stage0b/
  STAGE0B-RESULTS.md       this file
  PRE-REGISTERED.md        design + statistic + decision rule, committed db6c99287
                           BEFORE the first arm was armed
  prompts/                 p4k, p12k, warmup + sha256sums.txt (byte-identical to Stage 0;
                           `sha256sum -c` OK before arming)
  arms/                    <ARM>.bench.json, <ARM>.logparse.json   (8 arms × 2)
  logs/
    analysis.txt           full output of analyze_stage0b.py (tables + verdict)
    session-sequence.log   position, pair, start/end, exit code for all 8 positions
    session-transcript.log full harness stdout (flags, gates, h2d pre+post, bench)
    driver.out             session-level driver stdout
    <ARM>-engine.log       full llama-server stderr per arm
    <ARM>.link.tsv         1 Hz link sampler (load + bench), 8 arms
    <ARM>.h2d.txt          POST h2d probe (see caveat D3)
    <ARM>.spare.txt        pre-arm gates, spare, h2d_pre= AND h2d_post=, load1_max
    <ARM>.summary.txt      one-line verdict per arm
    <ARM>.vram_after_load.txt
```

Repro (from the worktree root; the harness is untracked under `.local/`, not committed —
the same convention Stage 0 used):

```bash
cd .local/wt-stage0b
bash .local/strata-research/run-stage0b.sh          # T6 T12 T6 T12 T6 T12 T6 T12, aborts on first failure
python3 .local/strata-research/analyze_stage0b.py docs/evidence/strata-gap/stage0b
```

Harness (untracked, under `.local/`, not committed):
`.local/strata-research/{run-stage0b.sh,arm-stage0b.sh,analyze_stage0b.py,sample_link.sh,
bench3060.py,parse_arm_logs.py}` — the last three byte-identical to Stage 0 (md5 verified).

**Rig state at close-out:** no `llama-server`, **GPU0 = 1 MiB, GPU1 = 1 MiB**, ports 8091 and
8093 free, MemAvailable 87.7 GiB, `nproc` 20.
