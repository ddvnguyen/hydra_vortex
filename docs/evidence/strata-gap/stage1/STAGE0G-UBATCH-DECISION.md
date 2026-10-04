# Stage 0g — `-ub` decision data: **n=3 paired complete; decode rate and KL NOT measured**

**Epic:** #811 · **Branch:** `docs/811-stage0g` (off `origin/epic/811-strata-gap-analysis`)
**Date:** 2026-10-04 · **Rig:** RTX 3060 = GPU1 only · artifact `34068ff9`, **flag OFF**
**No fork change, no CI cycle, no production config changed.** Raw JSON + engine logs in
`stage0g-ubatch/`.

> **PARTIAL.** §1 (paired sweep) and §4 (VRAM trade) are complete. **§2 decode rate and §3
> teacher-forced KL are NOT measured** — §2's harness died and is being re-run; §3 was explicitly
> deferred by the leader as a separate long job, with exact commands in §3. Nothing unfinished is
> rounded up.

## 0. Decision summary

| question | answer |
|---|---|
| Does a larger `-ub` help prefill? | **Yes, reproducibly.** p12k `-ub 8192` = **1.6286×** (sd 0.0139, n=3 paired) |
| Is the control trustworthy? | **Within this session, yes** — control sd **0.48** (p4k) / **0.36** (p12k) across 3 rounds. **Across sessions it is not** (see §1.3) |
| What does it cost? | compute buffer **1466 → 2433 → 4693 MiB**; after-prefill VRAM **6535 → 7759 → 10533 MiB** |
| Does the freed VRAM cost decode here? | **On this rig config, no** — `--n-cpu-moe 99` keeps experts CPU-resident, so freed VRAM buys **context**, not decode experts (§4.2) |
| Does decode move with `-ub`? | **NOT MEASURED** (§2) |
| Do the numerics move with `-ub`? | **NOT MEASURED** (§3) |

## 1. Paired sweep — n=3, complete

`34068ff9` flag OFF, `-b == -ub`, Stage 0 F0 flags otherwise verbatim, sha256-pinned prompts,
`cache_prompt: false`, `max_tokens: 1`, tok/s = `prompt_tokens/ttft`, first request after a 1-token
warmup. Rotating arm order per round (r1 `2048,4096,8192`; r2 `4096,8192,2048`; r3 `8192,2048,4096`)
so order effects cannot alias with `-ub`.

### 1.1 Per-round values (tok/s)

| cell | `-ub` | r1 | r2 | r3 | mean | sd |
|---|---:|---:|---:|---:|---:|---:|
| p4k | 2048 | 153.37 | 152.53 | 153.35 | **153.08** | 0.48 |
| p4k | 4096 | 223.19 | 224.32 | 223.55 | **223.69** | 0.58 |
| p4k | 8192 | 213.24 | 213.73 | 214.33 | **213.77** | 0.55 |
| p12k | 2048 | 163.17 | 162.75 | 163.47 | **163.13** | 0.36 |
| p12k | 4096 | 232.41 | 232.19 | 229.07 | **231.22** | 1.87 |
| p12k | 8192 | 263.18 | 266.84 | 267.01 | **265.68** | 2.16 |

### 1.2 Paired ratios vs the same-round control

| cell | `-ub` | paired mean | sd | n | 95% CI |
|---|---:|---:|---:|---:|---|
| p4k | 4096 | **1.4612** | 0.0083 | 3 | [1.4519, 1.4706] |
| p4k | 8192 | **1.3964** | 0.0055 | 3 | [1.3901, 1.4027] |
| p12k | 4096 | **1.4174** | 0.0140 | 3 | [1.4016, 1.4333] |
| p12k | 8192 | **1.6286** | 0.0139 | 3 | [1.6128, 1.6444] |

Ratios against the **pooled** `-ub 2048` mean are numerically identical (1.4612 / 1.3964 / 1.4174 /
1.6286) because the control is so tight within this session.

### 1.3 Control spread — stated honestly

**Within this session the control is stable**: p4k 153.37 / 152.53 / 153.35 (sd **0.48**, 0.3%),
p12k 163.17 / 162.75 / 163.47 (sd **0.36**, 0.2%).

**Across sessions it is not.** The same binary at `-ub 2048` has read **152.5–167.3** tok/s:
152.5 (STAGE0D "clean run"), 153.08 (this session), 166.26 (STAGE1B-R `#830` p4k control mean),
167.30 (`#830` p12k control mean), 167.32 (STAGE0D nsys run). That is a **~10% session-to-session
spread**, larger than most of the effects in play. So:

- **Relative statements anchored to a same-session control are safe** — that is what §1.2 is.
- **Any absolute tok/s figure, or any ratio quoted against a ~166 baseline from another session, is
  an upper edge.** Against 166 the p4k gains fall to ~1.35× (4096) / ~1.29× (8192); p12k 8192 falls to
  ~1.58×. The leader's qualification stands and is **not** retracted by the tighter within-session
  control: a tight control proves the *ranking and magnitude within this session*, not that this
  session's baseline equals the historical one.

## 2. Decode rate — **NOT MEASURED**

`STAGE0F-UBATCH.md` §9.1 already flagged that its `decode_tps` was **invalid** (it divided by the
whole request wall including prefill). A corrected harness was written
(`.local/wt-stage0g/decode.sh`): 256 generated tokens after the p4k prefill, streaming, reading the
server's own `timings.predicted_per_second` and `timings.prompt_per_second`, `-ub` 2048/4096/8192,
n=3, rotating order.

**First attempt produced no result.** It printed `PRE-ARM GATE: PASS` and then stopped with no arm
output and no `llama-server` left running. **Exact failing command:** `bash decode.sh`
(cwd `.local/wt-stage0d/.local/stage0g`, `CUDA_VISIBLE_DEVICES=1`,
`LD_LIBRARY_PATH=<artifact>/bin:/opt/software/cuda/13.2.1/lib64`), launched through a wrapper that
piped it into `grep -aE 'decode predicted|DONE|FAILED'`. Because that filter discarded
everything else, **no per-arm log survived to diagnose from**, so the root cause is still
unidentified. Re-run launched with output redirected to
`.local/stage0g/decode-sweep.out` and per-arm logs at `.local/stage0g/dec-ub<N>-r<R>.log`, unfiltered.

**Whether decode moves with `-ub` is therefore unknown.** This matters for the decision, because
decode is what the VRAM in §4 would otherwise buy.

## 3. Teacher-forced KL — **NOT MEASURED**, deferred by instruction

The leader deferred this: a ~57 GB base dump plus three passes is a separate long job. **Not
started.** The exact command lines and disk budget I would use, for whoever queues it:

**Binary** — `llama-perplexity` from the same artifact set as the measured server, i.e.
`.local/stage1b-ci/34068ff9/…` (the sibling `llama-perplexity` from CI run `37203862664`). Note the
provenance defect from `#830` §7 still stands: the branch `llama-perplexity` hashes **identically**
(`92594986686a4bbe23a2834695792ce32f8b18b9d57bf3c50e0ac87aba715fdd`) to the binary STAGE0C records as
the floor, yet contains a flag that post-dates that floor commit. **Do not quote the 0.005407 floor
until that is resolved; use a fresh same-binary floor from the same session instead.**

**Config** — K=14, same text and same extent as STAGE0C, `-c 4096`, `-b 2048 -ub <UB>`:
```
export PPLX_BIN=<artifact>/bin ; export LD_LIBRARY_PATH=$PPLX_BIN:/opt/software/cuda/13.2.1/lib64
export CUDA_VISIBLE_DEVICES=1 TMPDIR=<worktree>/.local/tmp
# floor: same binary, -ub 2048 vs itself, twice (this is the new floor)
$PPLX_BIN/llama-perplexity -m $MODEL -f 16 -c 4096 -b 2048 -ub 2048 --chunks 8 --parallel 1 \
    -t 6 --kl-divergence --save-kl-base --kl-base-output /mnt/WorkDisk/kl/ub2048-base.kld
$PPLX_BIN/llama-perplexity -m $MODEL -f 16 -c 4096 -b 2048 -ub 2048 --chunks 8 --parallel 1 \
    -t 6 --kl-divergence --kl-divergence-base /mnt/WorkDisk/kl/ub2048-base.kld      # -> floor KLD
# then the two candidates against the SAME base
$PPLX_BIN/llama-perplexity … -b 4096 -ub 4096 … --kl-divergence-base …/ub2048-base.kld
$PPLX_BIN/llama-perplexity … -b 8192 -ub 8192 … --kl-divergence-base …/ub2048-base.kld
```

**Disk budget** — `STAGE0C` measured the base dump at **~57 GB**. One base + four passes ≈ **60 GB**
peak, all under **`/mnt/WorkDisk/kl/`** (never `/tmp`). Delete each pass's `.kld` before the next —
the repo's one-base-at-a-time rule. Budget **~3.5 h**.

**Decision rule** — Mean KLD vs `max(0.005407, 2 × fresh same-session floor)`, i.e. against the
**0.010814** bar only if the fresh floor is ≤ 0.005407; state which was used.

## 4. VRAM-for-experts trade

### 4.1 Measured

| `-ub` | CUDA0 compute buffer | VRAM after load | VRAM after prefill | Δ VRAM vs 2048 |
|---:|---:|---:|---:|---:|
| 2048 | **1466.28 MiB** | 6271 MiB | 6535 MiB | — |
| 4096 | **2432.56 MiB** | 7237 MiB | 7759 MiB | **+1224 MiB** |
| 8192 | **4693.13 MiB** | 9499 MiB | **10533 MiB** | **+3998 MiB** |

Expert weights, from `docs/evidence/model-layer-info/apex-i-mini.tsv`: **48 layers, mean 913.3 MiB
per layer** (min 887.5, max 1100.0), total **42.81 GiB**. VRAM was bit-identical across rounds at
each `-ub`.

### 4.2 INFERRED — and the caveat that matters

**INFERRED, not a run.** At 913.3 MiB per expert layer:

- `-ub 4096` costs **+1224 MiB ≈ 1.3 expert layers**
- `-ub 8192` costs **+3998 MiB ≈ 4.4 expert layers** (9.2% of the 48)

If the freed VRAM would otherwise have held GPU-resident experts **and** decode is linear in resident
expert layers, `-ub 8192` implies **decode ≈ 0.908×**, i.e. **−9.2%**, in exchange for **+62.9%
prefill**.

**Which production path that applies to — and why it does NOT apply to this rig.** The trade only
exists where expert weights are **GPU-resident**. Every arm here ran `--n-cpu-moe 99`, which keeps
**all 48 expert layers on the CPU**, so on *this* configuration the freed VRAM has nothing to compete
with and would buy **context / KV cache**, not decode experts. The −9.2% figure is therefore
**conditional on a production path with experts GPU-resident** (i.e. **without** `--n-cpu-moe 99`),
and must not be applied to the `--n-cpu-moe 99` configuration these numbers were measured on.

The 467 MiB headroom at `-ub 8192` (10533 vs the G-V 11000 gate) was likewise measured at
`-c 16384 --parallel 1` on an idle rig; **the 63K-context runs will have materially less.**

## 5. Tail-ubatch probe (leader item 5 / #833 note point 4) — **NOT RUN**

The `#833` review note's reading is that a sub-threshold tail ubatch (below the 32-token offload
threshold) runs on CPU and is not staged, which would predict p12k's 46-token tail costs a full
restage. **I did not test it**, so that reading stays a hypothesis. It is cheap to test later: trim
p12k to a token count that is an exact multiple of `-ub` and compare against the untrimmed prompt at
the same `-ub`.

## 6. Could not measure

1. **Decode rate** — harness died, no output captured, root cause not yet identified (§2). **This is
   the gap that blocks a decision**, because decode is what §4's VRAM would buy.
2. **Teacher-forced KL** — deferred by instruction; commands and disk budget in §3. So **numerics
   under `-ub` are unchecked**, and `-ub` does change accumulation order.
3. **Per-request H2D bytes at each `-ub`** — not measured (no nsys in this task). The leader's point
   that this would confirm the tail-ubatch reading stands; the mechanism is still inferred from
   ubatch counts.
4. **A measured decode-vs-expert-layer slope** — none exists in the evidence tree, so the −9.2% is a
   linearity assumption, not a fitted number.
5. **Tail-ubatch probe** (§5).
6. Nothing was rounded up: every ratio in §1.2 is n=3 paired, and §2/§3 are reported as absent.

## 7. Rig state

GPU1 only. Both GPUs at **1 MiB / 0%**, no `llama-server`. Pre-arm `MemAvailable` ≥ 67 GiB passed.
All output under `.local/`, `TMPDIR` in `.local/tmp`, **nothing under `/tmp`**. No sudo, no local
binaries, no CI cycle, fork untouched (`feat/moe-prefill-stream` @ `34068ff9`, DRAFT PR #161 open).

## Change summary

**Changed:** this document plus `stage0g-ubatch/` raw JSON, engine logs and the sweep harness, on
branch `docs/811-stage0g`. No product, harness, export or config file touched.

**Assumptions:** the legacy flag-OFF path is the right baseline for a `-ub` comparison; VRAM scales
with `-b` as measured; prompts are the sha256-pinned Stage 0 set.

**Risks:** decode unmeasured (§2) and numerics unmeasured (§3) are both prerequisites for changing a
production default; `-ub 8192` has only 467 MiB of headroom at `-c 16384 --parallel 1`, less at 63K.