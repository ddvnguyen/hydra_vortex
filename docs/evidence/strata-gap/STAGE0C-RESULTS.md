# Option C — Stage 0c results: build-vs-itself teacher-forced KL floor (G-0b / G-K2 denominator)

Date: 2026-10-04
Branch: `docs/811-stage0c` (off `origin/epic/811-strata-gap-analysis` @ `9b2a8df54`, the merge of PR #822)
Spec: `OPTION-C-DESIGN.md` §3.0 row **G-K2** (line 168) and §Stage 0 item 4 (line 188);
Stage 0 hand-off: `STAGE0-RESULTS.md` §7 → **`G-0b: NOT RUN`**
Pre-registration: `stage0c/PRE-REGISTERED.md` (commit `0bfc5a079`, frozen before any run)
Extent decision: `PRE-REGISTERED.md` §8 (commit `9c8b93419`, before any measured pair)
Order amendment: `PRE-REGISTERED.md` §9 (commit `6f06b871a`, after `A1`, before `C1`/`P2`/`P3`)
Rig: RTX 3060 **GPU1 only**, one GPU one task, runs strictly sequential, no port bound at all
Binary: CI `llama-perplexity` artifact only — `.local/perplexity-ci/bin/llama-perplexity`,
`sha256 92594986686a4bbe23a2834695792ce32f8b18b9d57bf3c50e0ac87aba715fdd` (re-verified this session)

---

## TL;DR / verdict

**G-0b: PASS — the floor exists and is now a number.**

| Quantity | Value |
|---|---|
| **G-K2 floor (Mean KLD, n = 3 build-vs-itself pairs, max)** | **0.005407** |
| floor (median of the 3 pairs) | 0.005386 |
| floor (min / spread / ratio) | 0.005165 / 2.42e-4 / **1.047×** |
| **G-K2 bar = 2 × floor** (design line 168, E1 practice: gate above observed max) | **0.010814** |
| 2 × median (secondary reference) | 0.010772 |
| control repeat (C1 vs B1) \|ΔMean\| | 1.45e-4 (**2.69 %**) |
| p99-KLD floor (max over pairs) | 0.058931 |
| Same-top-p floor (min over pairs) | 97.457 % |
| measured wall (7 runs, T1 excluded) | **3.48 h** of a 4 h budget |

All pre-registered abort conditions were checked and none fired (§Guards): every run
`rc=0`, every h2d probe inside 6.00–6.30 GB/s, 12 454 link samples with
`gen1 ∧ util≥20 = 0`, peak VRAM **9 007 MiB ≤ 11 000**, ports 8091/8086 never bound,
both GPUs **1 MiB** at the end.

**Three findings the owner must see before Stage 1/3 use this number:**

1. **The instrument is measurably run-to-run nondeterministic** — the pre-registered
   zero-change control `C1` (identical binary, identical flags, `HYDRA_*` all unset, same
   base file) differs from `B1` by **1.45e-4** on Mean KLD, which **exceeds the
   pre-registered 1e-4 repeat threshold** (and is 40× the pre-registered
   "deterministic-in-practice" 1e-5). Disclosed as registered, not discarded.
   *Relative* to the E1 precedent this repeat is still far tighter (2.7 % vs E1's
   4.0× cross-pair ratio), so the control criterion — not the instrument — is what fails.
2. **2 × floor = 0.0108 > 0.01.** The E1-era hard-coded `0.01` threshold used by
   `scripts/hydra-kl-gate.sh` callers **cannot** serve as the G-K2 bar: an identical build
   already sits at 54 % of it. G-K2 must be evaluated as `Mean KLD ≤ 0.010814`
   (or `≤ 2 ×` a re-measured floor for the same extent), never as `≤ 0.01`.
3. **The floor is extent-dependent.** The same binary, same corpus and same flags give
   Mean KLD **0.007457 at K = 1** vs **0.005165–0.005407 at K = 14** (+38 %). G-K2 must be
   run with the **pre-registered K = 14 head-of-corpus extent** or the bar is wrong.

**Instrument caveats (carry into Stage 1/3):** the compared base logits are the
**uint16-quantized** dumps written by run A (16-nat window) — quantization error is *inside*
the floor; `llama-perplexity` never reaches the `DECODE_GROUPED` path, so this floor covers
prefill / legacy-decode teacher forcing only (memory note
`teacher-forced-vs-sampled-gates.md`); `k=10` and `--override-kv` are **not applicable**
(no sampler, no override KV in this flow — verified against `--help`, saved to
`stage0c/help.txt`).

---

## 1. Instrument and provenance

| Item | Value |
|---|---|
| binary | `.local/perplexity-ci/bin/llama-perplexity` (+ bundled `*.so*`, RPATH `$ORIGIN`), never moved, never built here |
| sha256 | `92594986686a4bbe23a2834695792ce32f8b18b9d57bf3c50e0ac87aba715fdd` — re-run and matched `PRE-REGISTERED.md` §0 after the last run |
| build | fork `7a03f921d1cf0a83a89dcbb17bd75d567a7aa3b7`, workflow `3808d4e3`, run `37167416216`, `sm86-sm120`, CUDA 13.2, `GGML_NATIVE=OFF` (`stage0/PERPLEXITY-CI.md` §2/§5) |
| link | `LD_LIBRARY_PATH=<artifact>/bin:/opt/software/cuda/13.2.1/lib64` (`libcudart.so.13` / `libcublas.so.13` are not bundled); `ldd` "not found" = 0 |
| relation to Stage 0 | same fork source tree as the `.local/q2g` Stage 0 server binary (`PERPLEXITY-CI.md` §1) |
| execution | **`llama-server` from this artifact never executed**; no builds, no podman, no CI triggers, no `llama-perplexity` from any other path |

**Flags** (Stage 0 F0 mirror, from `PRE-REGISTERED.md` §2; rejected server-only flags
verified against `--help`):

```
-m /mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
--split-mode layer -fit off -ngl 99 --n-cpu-moe 99
--override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0
-c 16384 -np 1 --flash-attn on -t 6 --experimental-logs --load-mode none -b 2048 -ub 2048
```

**Environment:** all `HYDRA_*` and `LLAMA_*` variables unset for every run; `TMPDIR`
pointed at `<worktree>/.local/tmp` (nothing under `/tmp`; every log records the line
`TMPDIR: …/.local/wt-stage0c/.local/tmp`). `nproc=20`, `-t 6` → spare 14 ≥ 2.

**Corpus:** `scripts/eval/wikitext-2-raw/wiki.test.raw`, sha256
`173c87a53759e0201f33e0ccf978e510c2042d7f2cb78229d9a50d79b9e7dd08`, 1 290 590 bytes.

---

## 2. Method (as pre-registered)

* **Run A** = `llama-perplexity … -f CORPUS --kl-divergence-base BASE --chunks K`
  → computes PPL **and** writes the base logit file.
* **Run B** = `llama-perplexity … --kl-divergence --kl-divergence-base BASE`
  → **no `-f`**: the token stream is read back out of the base file
  (`perplexity.cpp` `kl_divergence()`), so A and B are guaranteed to score the *identical*
  token sequence. This is what makes the comparison build-vs-itself and not build-vs-reload.
* Base file format: `_logits_` magic, `n_ctx`, `n_vocab`, `n_chunk`, tokens, then per-position
  **uint16** quantized full-vocab log-probs (scale/min over a 16-nat window) for
  `n_ctx - 1 - n_ctx/2 = 8191` positions per chunk → `4 068 109 324` B per chunk.
* Metric read exactly as `scripts/hydra-kl-gate.sh` reads it: **`awk $3` of the
  `Mean    KLD:` line**. Secondary columns (`99.0%`, `Median`, `Maximum`, `Same top p`,
  `RMS Δp`, PPL) recorded from the same lines.
* **Extent (§8, committed before any measured pair):** timing pass `T1` gave
  `L_A=119.5 s, c_A=117.7 s, L_B=116.0 s, c_B=120.9 s, s=4.068 GB/chunk` → time bound
  `K ≤ 16`, disk bound `K ≤ 14` → **`K_max = 14`**, estimated total **3.48 h**,
  base file **57.0 GB** (measured 56 953 530 276 B — 0.05 % off the estimate).
* **Order (§9 amendment):** because exactly one base file can fit (95 GB free, 114 GB would
  be needed to keep `A1` alive through `P2`/`P3`), the zero-change control `C1` ran
  immediately after `B1`, then `A1.kld` was deleted, then `P2`, `P3`. Metric, extent, flags
  and env were untouched by the amendment; running the control *adjacent* to its reference
  reduces drift, which is how E1's isolation legs were run.
* Guards per run: pre-arm + per-run h2d probe (one re-run allowed if outside band),
  1 Hz link sampler with `gen1 ∧ util≥20` check, exact-pid teardown, `rc` recorded.

**Pairs (n = 3):** `P1 = A1/B1`, `P2 = A2/B2`, `P3 = A3/B3`. **Control:** `C1` = repeat of
run B against the same `A1` base with everything unchanged.

---

## 3. Results

### 3.1 The floor (Mean KLD, build vs itself, K = 14)

| Run | Mean KLD ± s.e. | p99 | Median | Max | Same top p | PPL(Q) | PPL(base) | wall (s) | rc |
|---|---|---|---|---|---|---|---|---|---|
| **P1** `B1` | **0.005386 ± 0.000200** | 0.058842 | 0.001192 | 13.618022 | 97.512 % | 3.679238 | 3.677281 | 1749.2 | 0 |
| **P2** `B2` | **0.005165 ± 0.000149** | 0.055921 | 0.001171 | 10.020530 | 97.511 % | 3.680226 | 3.682123 | 1764.4 | 0 |
| **P3** `B3` | **0.005407 ± 0.000195** | 0.058931 | 0.001187 | 10.897634 | 97.457 % | 3.681048 | 3.676324 | 2003.5 | 0 |
| **control** `C1` | 0.005241 ± 0.000187 | 0.058054 | 0.001184 | 14.470452 | 97.546 % | 3.678698 | 3.677281 | 1755.0 | 0 |
| `A1/A2/A3` (dump runs) | — (PPL only) | — | — | — | — | 3.6776 / 3.6824 / 3.6766 | — | 1767.5 / 1737.3 / 1757.1 | 0 |
| `T1A/T1B` (timing pass, **K = 1**) | 0.007457 ± 0.000342 | 0.102313 | 0.000344 | 1.790734 | 97.754 % | 2.174632 | 2.175013 | 237.2 / 236.9 | 0 |

Dump-run cross-check (provenance): each A-run's own PPL matches the base PPL that run B reads
back out of its file to within **≤ 0.001 %** (3.6776 vs 3.677281, 3.6824 vs 3.682123,
3.6766 vs 3.676324) — the uint16 dump is being read by the run that wrote it.

### 3.2 Derived gate quantities

| Statistic | Value | Registered criterion | Verdict |
|---|---|---|---|
| cross-pair max − min Mean KLD | **2.42e-4** | ≤ 7.04e-4 (E1 abs spread) | **PASS** |
| cross-pair max / min | **1.0469×** | ≤ 4.0 (E1 ratio) | **PASS** |
| \|Mean(C1) − Mean(B1)\| | **1.45e-4 (2.69 %)** | ≤ 1e-4 for "deterministic-in-practice" | **FAIL — finding 1** |
| any value ≤ 1e-5? | no (all ≈ 5.2e-3) | "deterministic in practice" ≤ 1e-5 | **FAIL** |
| sample sd of the 3 pair Means | 1.341e-4 | (reported, not gated) | — |
| p99 cross-pair spread | 3.01e-3 (5.2 %) | (reported, not gated) | — |
| median-KLD cross-pair spread | 2.1e-5 (1.8 %) | (reported, not gated) | — |
| dump-to-dump PPL spread | 0.158 % (E1: 0.031 %) | (reported, not gated) | — |

**G-K2 bar (the number):**

```
floor = max(Mean KLD over the 3 pairs) = 0.005407
G-K2  = Mean KLD ≤ 2 × floor = 0.010814      (design line 168: "≤ 2 × the S0-measured build-vs-itself floor")
reference: 2 × median = 0.010772   (the two agree to 0.4 % — the bar is not sensitive to the max-vs-median choice)
```

Secondary, if G-K2 is ever read off `99.0% KLD` instead of the Mean: `2 × 0.058931 = 0.117862`.

### 3.3 Where this sits against the record

| Reference | Number | This session |
|---|---|---|
| E1 disarmed floor (4 pairs, ctx 512, head-600) | median 3.58e-4, spread 7.04e-4, ratio 4.0 | **15.1× larger** floor, but 3.1× tighter spread (2.42e-4) and 4× tighter ratio (1.047×) |
| E1 hard-coded gate | `0.01` | 0.005407 = **54 %** of it → 2 × floor **exceeds** 0.01 ⇒ **the old threshold is unusable as G-K2** (finding 2) |
| Stage 0 (PR #822) | `G-0b: NOT RUN` (`STAGE0-RESULTS.md` §7) | **now closed** |
| Stage 0 G-L / G-V | h2d 6.10–6.13, VRAM 6271 MiB (`llama-server`) | h2d 6.10–6.12, VRAM **9007 MiB** (`llama-perplexity` — the tool allocates the large logits workspace itself; still ≤ 11 000) |

---

## 4. Guards — every pre-registered check

| Guard | Registered | Measured | Verdict |
|---|---|---|---|
| pre-arm (both GPUs / 8091 / MemAvailable ≥ 67 GiB / spare ≥ 2) | `stage0c/prearm.log` | 1 MiB + 1 MiB, 8091 down, **87.9 GiB**, spare 14 | **PASS** |
| G-L h2d pre+post, band 6.00–6.30 GB/s | every run, one re-run allowed | 6.10–6.12 avg on all 16 probes (best 6.11–6.17); **0 re-runs needed** | **PASS** |
| G-L link: `gen1 ∧ util≥20` = 0 | 1 Hz, per run | **0 / 12 454 samples** (all 11 `*.link.tsv`) | **PASS** |
| G-V VRAM ≤ 11 000 MiB | after load | **9 007 MiB** on every run | **PASS** |
| exit `rc` | 0 | 0 on all 11 runs (7 measured + T1 ×2) | **PASS** |
| GPU1 only, GPU0 untouched | never GPU0 | GPU0 = 1 MiB, 0 % in every `gpu_state_after` | **PASS** |
| no port bound (perplexity has no `--host/--port`) | 8091/8086 untouched | `ss -ltn` → neither listening, before and after | **PASS** |
| nothing written under `/tmp` | `TMPDIR` override | `TMPDIR` line present in all 11 logs; `*.kld` + `*.tmp` all under `<worktree>/.local/stage0c` | **PASS** |
| disk: base ≤ 60 GB, free ≥ 30 GB | §5 / §7 abort | base 57.0 GB; free min **40 GB** (after `A3`), 93 GB at end | **PASS** |
| budget ≤ 4 h measured wall | §6 | 3.48 h (T1 +0.13 h reported separately) | **PASS** |
| `k=10`, `--override-kv` | not used in this flow | no sampler / no override KV flags accepted by `llama-perplexity` | n/a (as registered) |
| final GPU state | both ≈ 1 MiB | `stage0c/final.log`: `0, 1 MiB, 0 %` / `1, 1 MiB, 0 %` | **PASS** |

---

## 5. How to run G-K2 with this bar (Stage 1 / Stage 3)

```bash
# dump (run A) with the SAME extent K = 14 and the SAME flags as this session
llama-perplexity -m MODEL --split-mode layer -fit off -ngl 99 --n-cpu-moe 99 \
  --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0 \
  -c 16384 -np 1 --flash-attn on -t 6 --experimental-logs --load-mode none -b 2048 -ub 2048 \
  -f scripts/eval/wikitext-2-raw/wiki.test.raw --kl-divergence-base BASE --chunks 14
# compare (run B) — identical flags, no -f
llama-perplexity <same flags> --kl-divergence --kl-divergence-base BASE
# gate on what hydra-kl-gate.sh gates on: awk $3 of "Mean    KLD:"  <= 0.010814
```

Non-negotiable when reading the result: same extent (`--chunks 14`), same corpus head, same
flags, `HYDRA_*` unset, GPU1 only — a different `K` moves the floor by ~38 % (finding 3).
If the environment changes materially (different GPU, driver, `GGML_NATIVE`, model, ctx),
re-measure the floor first: **the bar is only defined against its own floor.**

---

## 6. Evidence inventory (all committed on `docs/811-stage0c`)

`docs/evidence/strata-gap/stage0c/`

| File | Content |
|---|---|
| `PRE-REGISTERED.md` | frozen protocol + §8 extent + §9 order amendment |
| `run-stage0c.sh` | runner: `prearm / A / B / h2d / final` subcommands, gates every launch |
| `sample_link.sh` | 1 Hz link/VRAM sampler |
| `prearm.log` | pre-arm gate snapshot |
| `T1A.log`, `T1B.log`, `T1*.h2d.txt`, `T1*.link.tsv` | timing pass (K = 1) — **reported separately, excluded from the floor** |
| `A1..A3.log`, `B1..B3.log`, `C1.log` + matching `*.h2d.txt` / `*.link.tsv` | the 7 measured runs |
| `final.log` | end-of-session GPU state (both 1 MiB) |
| `help.txt` | artifact `--help` (proves the rejected server-only flags) |

Base files (`A1.kld` 57.0 GB, `A2.kld`, `A3.kld`) and `T1A.kld` are **git-ignored, deliberately
deleted** after their runs (one-base-at-a-time disk rule); every number above is reproducible
from the committed logs alone (log → `Mean KLD` line → `awk $3`).

**Raw `Mean    KLD:` lines:** `B1 0.005386`, `B2 0.005165`, `B3 0.005407`, `C1 0.005241`,
`T1B 0.007457`.
