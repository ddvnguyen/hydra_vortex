# Stage 0b pre-registration (frozen before any run) — 2026-10-04

**This file was written and committed BEFORE the first Stage 0b arm was armed.** No
Stage 0b measurement exists at the time of this commit.

Source: `../STAGE0-RESULTS.md` §1 (method), §2 (results), §6 (config arms / G-0c),
§10 (G-0a closure + day-over-day control drift), PR #822. Spec `OPTION-C-DESIGN.md`
§3 Stage 0, gates G-L/G-V/G-G/G-R (G-0c's bar, +6%, is re-used as the ship threshold).

Owner approval for this session: 2026-10-04 — *confirm the Stage 0 `-t 12` finding
with n≥4.*

## 1. Finding under test

Stage 0 reported (§6.2, unprofiled, canonical `p4k` cell, arm means of median-of-3):

| quantity | value |
|---|---|
| `-t 6` unprofiled control mean | 13.815 tok/s (F0C-1 13.91, F0C-2 13.72) |
| `-t 12` unprofiled mean | 14.73 tok/s (T12U-1 14.81, T12U-2 14.65) |
| **Δ** | **+6.62% p4k decode** (−4.5 ms/tok) |
| margin over the 6% G-0c bar | **0.6 pp** |
| `p12k` | +1.9% (inside the cell's own ±1.6% noise) |
| n | **2 arms** (4 total arms across both configs) |
| control-pair noise | ~1.6% |
| control drift day-over-day | **+10%** (71.9 / 72.9 vs the 65.4 reference, §10 C1) |

Because Stage 0's `-t 6` and `-t 12` arms were **not interleaved** and the control
itself drifted +10% against the prior session, a +6.62% delta with 0.6 pp of margin
is not yet separable from session drift. This stage exists to decide it.

## 2. Design (frozen here)

**Eight arms, strictly interleaved A B A B …, in one session**, so any monotone
in-session drift applies to both configs within each pair and cancels in the paired
difference.

| position | arm label | config | pair |
|---:|---|---|---:|
| 1 | `T6-1`  | `-t 6`,  unprofiled | 1 |
| 2 | `T12-1` | `-t 12`, unprofiled | 1 |
| 3 | `T6-2`  | `-t 6`,  unprofiled | 2 |
| 4 | `T12-2` | `-t 12`, unprofiled | 2 |
| 5 | `T6-3`  | `-t 6`,  unprofiled | 3 |
| 6 | `T12-3` | `-t 12`, unprofiled | 3 |
| 7 | `T6-4`  | `-t 6`,  unprofiled | 4 |
| 8 | `T12-4` | `-t 12`, unprofiled | 4 |

**Exactly as Stage 0** (`.local/strata-research/arm-stage0.sh`, `stage0/PRE-REGISTERED.md`):

- **Bench per arm:** warmup (96 tok, `ad920c19…`) → `p4k` (4068 tok, `fa13b451…`) × **3 reps**
  → `p12k` (12283 tok, `2e5eabdf…`) × **3 reps**, `--max-tokens 256`.
  Prompts byte-identical to Stage 0 / t0006 — `stage0b/prompts/sha256sums.txt` re-verified
  with `sha256sum -c` before arming.
- **Flags:** t0006 F0 verbatim — `--split-mode layer -fit off -ngl 99 --n-cpu-moe 99
  --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0 -c 16384 --parallel 1
  --flash-attn on --jinja -t <6|12> --experimental-logs --load-mode none --ple-prefetch
  -b 2048 -ub 2048 --host 127.0.0.1 --port 8093 --alias gsq-rco-iq3_s --spec-type none`.
- **`--profile-decode` OFF on every arm** (R7: only OFF replicates are ship-relevant).
- **No sched-debug, no `-lv`, no `--override-kv`; `k` stays 10**
  (`OPTION-C-DESIGN.md` §0.2, `BIASED-ROUTING-DESIGN.md:30`).
- **GPU1 (RTX 3060) only**, `CUDA_VISIBLE_DEVICES=1`, `CUDA_DEVICE_ORDER=PCI_BUS_ID`,
  port **8093** only. GPU0 (5060 Ti) never touched. Ports 8086/8091 never touched.
- **Cold start per arm:** a fresh `llama-server` process per arm (load → `/health` → sleep 3 →
  bench → exact-pid kill), identical to Stage 0. `--load-mode none` on every arm.
- **Strictly sequential** — one GPU = one compute task. The driver refuses to start arm *k+1*
  until arm *k* has been killed and both GPUs read ≤ 5 MiB.
- Unpinned (no `taskset`); `TMPDIR` under `.local/tmp`, **never `/tmp`**.
- Binary: existing fork build `.local/q2g/src/build/bin/llama-server` — **no builds, no podman,
  no CI.**

## 3. Paired-difference statistic (what will be reported)

Per-arm value **V(arm)** = **median of the 3 reps' `decode_tps`** for that cell, taken from
`stage0b/arms/<ARM>.bench.json` (same reduction Stage 0 used in §2.1).

For pair *k* (k = 1..4), on cell `c ∈ {p4k, p12k}`:

```
delta_k(c) = 100 * ( V(T12-k, c) − V(T6-k, c) ) / V(T6-k, c)      [%]
```

**Reported statistic for the primary (`p4k`) axis: `mean`, `min` and `max` of the four
per-pair `delta_k(p4k)`**, plus the four individual `delta_k` values and the sign count.
The same `mean / min / max` triple is reported for `p12k`. All deltas are paired
*within position*, i.e. `T6-k` is always the arm immediately preceding `T12-k`.

Secondary (disclosure only, never a gate): the unpaired arm-mean delta of Stage 0 §6.2 form,
`100 * (mean_k V(T12-k) − mean_k V(T6-k)) / mean_k V(T6-k)`.

## 4. Decision rule (frozen)

**SHIP `-t 12` as a config change** — and only if **ALL FOUR** hold:

| # | condition |
|---:|---|
| 1 | **mean** paired `p4k` delta **≥ +6.0%** |
| 2 | paired `p4k` delta **positive in ≥ 3 of 4 pairs** |
| 3 | **`p12k` does not regress by more than 1.5%** → mean paired `p12k` delta **≥ −1.5%** (the operative reading, symmetric with clause 1's mean; `min`/`max` also reported so the owner can see the spread) |
| 4 | **G-L, G-V, G-G, G-R all hold** (definitions §5) |

**Else: DO NOT SHIP**, and report the measured delta with its spread (`mean / min / max`,
all four per-pair values, and the sign count).

"Ship" here means exactly what Stage 0 §6.2 says a config change means on this rig: a
**documented default**. No in-repo file sets `-t` for this harness — the default lives in
`.local/strata-research/arm-stage0b.sh` (gitignored) and the operator's launch command.
No product code, no model file, no fork change either way.

## 5. Gates as read for this stage

- **G-L (PCIe validity):** H2D pinned probe **pre and post every arm** inside
  **6.00–6.30 GB/s** (rerun **once** if outside the band; if still outside → abort), and
  `gen1 ∧ util≥20 = 0` samples in the arm's `*.link.tsv`, `link_bad_samples = 0`.
- **G-V:** VRAM after load ≤ **11 000 MiB** (Stage 0 saw 6271 on every arm).
- **G-G:** `graphs reused` ≥ control (Stage 0: **1581** on every 3-rep arm),
  zero `post_decode() failed`, zero HTTP 500.
- **G-R:** no non-target axis regresses > **3%** vs the session's control arms —
  cold prefill tok/s (rep0 `prompt_tokens / ttft`), TTFT, VRAM, h2d, `graphs reused`.
- **Spare rule:** `spare = nproc − threads ≥ 2` recorded pre-arm (at `-t 12`, 20 − 12 = 8).
- **Pre-arm (before the session, and re-checked by the harness before each arm):** both GPUs
  ≤ 5 MiB, 8091 not listening (not ours), 8093 free, no other `llama-server`.
  **MemAvailable ≥ 67 GiB is a PRE-ARM-only gate** (Stage 0 convention; recorded per arm after).

## 6. Hard rules / abort conditions (owner)

GPU1 (3060) only, never GPU0 · one GPU = one compute task · 8091 not listening ·
MemAvailable ≥ 67 GiB pre-arm only · spare ≥ 2 CPUs for `-t 12` (recorded) · h2d pre/post each
arm (band 6.00–6.30 GB/s, one rerun) · k stays 10, no `--override-kv` · existing fork binary
only — **no builds, no podman, no CI** · **never `/tmp`** (`TMPDIR` under `.local/tmp`) ·
exact-pid kills only · ports 8086/8091 and the owner's cachyos-dev VM untouched.

**On crash / invalid link / guard failure: STOP and report** — no workarounds, no re-arming
with relaxed gates.

**Close-out:** all `llama-server` processes stopped, both GPUs confirmed ~1 MiB, 8093 free.

## 7. Known risks carried in

- **C1 (Stage 0)** — the control drifted **+10% day-over-day** (65.4 → 71.9/72.9 ms/tok).
  This is precisely what the interleaved design neutralises *within* the session; it does
  **not** make absolute ms/tok comparable to t0006. Every verdict is a paired, in-session ratio.
- **p12k is the noisier cell** (control pair spans 13.62–14.07 tok/s, ±1.6%) — hence clause 3
  uses a 1.5% tolerance and `p4k` remains the canonical cell.
- **Arm-to-arm variance of ±3%** was assumed for single-run arms in Stage 0 (C9); with 4 pairs
  and within-pair adjacency that variance is now largely inside the pairing.
- Host is shared (Stage 0 `load1_max` 3.85–9.18 on nproc=20); `load1_pre` and `load1_max` are
  recorded per arm as context, not as gates.
