# Stage 0g — `-ub` decision data: **prefill + decode both measured; KL still NOT measured**

**Epic:** #811 · **Branch:** `docs/811-stage0g` (off `origin/epic/811-strata-gap-analysis`)
**Date:** 2026-10-04 · **Rig:** RTX 3060 = GPU1 only · artifact `34068ff9`, **flag OFF**
**No fork change, no CI cycle, no production config changed.** Raw JSON + engine logs in
`stage0g-ubatch/`.

> **PARTIAL.** §1 (paired sweep), §2 (decode rate, added by task t0028 / Stage 0h) and §4 (VRAM
> trade) are complete. **§3 teacher-forced KL is still NOT measured** — explicitly deferred by the
> leader as a separate long job, with exact commands in §3. Nothing unfinished is rounded up.

## 0. Decision summary

| question | answer |
|---|---|
| Does a larger `-ub` help prefill? | **Yes, reproducibly.** p12k `-ub 8192` = **1.6286×** (sd 0.0139, n=3 paired) |
| Is the control trustworthy? | **Within this session, yes** — control sd **0.48** (p4k) / **0.36** (p12k) across 3 rounds. **Across sessions it is not** (see §1.3) |
| What does it cost? | compute buffer **1466 → 2433 → 4693 MiB**; after-prefill VRAM **6535 → 7759 → 10533 MiB** |
| Does the freed VRAM cost decode here? | **On this rig config, no** — `--n-cpu-moe 99` keeps experts CPU-resident, so freed VRAM buys **context**, not decode experts (§4.2) |
| Does decode move with `-ub`? | **No.** Paired **0.9961** (4096) / **0.9907** (8192), both 95% CIs contain 1.0 (§2) |
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

## 2. Decode rate — **MEASURED (task t0028, Stage 0h): decode does NOT move with `-ub`**

`STAGE0F-UBATCH.md` §9.1 already flagged that its `decode_tps` was **invalid** (it divided by the
whole request wall including prefill). Re-measured **2026-10-05** on the same rig using the
server's own `timings.predicted_per_second`, which is a genuine decode-only rate. 256 generated
tokens after the p4k prefill, `cache_prompt: false`, streaming, `-ub` 2048/4096/8192, n=3,
rotating arm order, one `llama-server` lifetime per arm. **All 9 arms completed, 0 failures,
nothing rounded up.**

**Verdict: decode does not move with `-ub` on this rig config.** Paired same-round ratios vs
`-ub 2048` are **0.9961** (4096) and **0.9907** (8192), and **both 95% CIs contain 1.0**. The
absolute means drift only **−0.9%** from 2048 → 8192 (11.996 → 11.886 tok/s), against a per-`-ub`
sd of 0.13–0.61 tok/s. That is not a signal.

### 2.1 Per-round decode (tok/s) — `timings.predicted_per_second`

| `-ub` | r1 | r2 | r3 | mean | sd | n |
|---:|---:|---:|---:|---:|---:|---:|
| 2048 | 12.077 | 11.850 | 12.062 | **11.996** | 0.127 | 3 |
| 4096 | 11.763 | 12.010 | 12.073 | **11.949** | 0.164 | 3 |
| 8192 | 12.595 | 11.551 | 11.512 | **11.886** | 0.614 | 3 |

In ms/token: **83.36 / 83.69 / 84.13**.

### 2.2 Paired ratios vs the same-round `-ub 2048` control

| `-ub` | r1 | r2 | r3 | paired mean | sd | n | 95% CI | contains 1.0? |
|---:|---:|---:|---:|---:|---:|---:|---|---|
| 4096 | 0.9740 | 1.0134 | 1.0009 | **0.9961** | 0.0201 | 3 | [0.9461, 1.0462] | **yes** |
| 8192 | 1.0430 | 0.9748 | 0.9544 | **0.9907** | 0.0464 | 3 | [0.8755, 1.1059] | **yes** |

**No ratio is distinguishable from 1.0 at n=3.** The `-ub 8192` CI is wide (±11%) because that
arm's r1 value (12.595) sits well above its own r2/r3 (11.551, 11.512); the honest reading is
"flat within noise", **not** "slightly faster at 8192".

### 2.3 Why this is a valid decode number (not another invalid `decode_tps`)

Verified in the fork source, not assumed:

- `predicted_per_second = (n_gen − 1) × 1000 / predicted_ms` (`tools/server/server-common.h`).
- `predicted_ms = t_gen_last − t_prompt_last`, and `t_prompt_last` is stamped when the **first**
  token is sampled, i.e. immediately after prefill. The decode window is therefore **disjoint from
  `prompt_ms`** — prefill is excluded. This is the specific defect that made §0F's number invalid.
- It arrives in the **final** streaming chunk with no server flag required. Those two source files
  are byte-identical between the working tree and artifact `34068ff9`.

Three independent checks passed on **every one of the 9 arms**:

| check | result |
|---|---|
| Timing identity `(predicted_n − 1) × 1000 / predicted_ms` vs `predicted_per_second` | relative error **≤ 4e-06** on all 9 |
| Full generation (`predicted_n` = 256, no early EOS) | **9/9** |
| Output coherence (non-empty, alphanumeric, non-degenerate) | **9/9** |

### 2.4 The rig really was GPU1, and the arms really were paired with §1/§4

The decode sweep independently reproduced the §4.1 VRAM/compute-buffer table **exactly**, which
is what makes the decode ratios comparable with the prefill ratios rather than a separate rig:

| `-ub` | CUDA0 compute buffer | VRAM after load | §4.1 says |
|---:|---:|---:|---|
| 2048 | 1466.28 MiB | 6271 MiB | 1466.28 / 6271 ✅ |
| 4096 | 2432.56 MiB | 7237 MiB | 2432.56 / 7237 ✅ |
| 8192 | 4693.13 MiB | 9499 MiB | 4693.13 / 9499 ✅ |

VRAM was bit-identical across all 3 rounds at each `-ub`. Process `CUDA0` was verified per arm to
be `NVIDIA GeForce RTX 3060 (0000:02:00.0)` — **never the 5060 Ti** — and `CUDA_DEVICE_ORDER=
PCI_BUS_ID` was pinned so device 1 could not alias.

The prefill side of these same 9 requests also reproduces §1, as an independent check that the
session was healthy (server-side `timings.prompt_per_second` here vs §1's client-side
`prompt_tokens/ttft`):

| `-ub` | this session | §1.2 paired mean |
|---:|---:|---:|
| 4096 | **1.4393** (sd 0.0250) | 1.4612 |
| 8192 | **1.3987** (sd 0.0158) | 1.3964 |

### 2.5 Absolute decode vs the historical `-ub 2048` figures — stated honestly

Our `-ub 2048` decode is **83.36 ms/tok (11.996 tok/s)**. The historical `-ub 2048` numbers in the
tree are **65.4–73.1 ms/tok (13.7–15.3 tok/s)** (`NSYS-DECODE.md` §7, `PROFILE-DECODE.md`
13.58–13.83 tok/s). So this session's absolute decode is **~15% slower** than those runs — the same
kind of cross-session drift §1.3 already documents for prefill (~10%). **Do not mix the two.**
Every claim in §2.1–§2.2 is a **same-session paired ratio**, which is the trustworthy form; the
absolute 11.9–12.0 tok/s figure should not be quoted as "the" decode rate of this rig.

### 2.6 Reconciliation notes / open items

1. **VRAM after request ≠ §4.1's "VRAM after prefill" at `-ub 8192`.** Ours is **10019 MiB** vs
   §4.1's **10533 MiB** (−514 MiB); at 2048 and 4096 the two agree exactly. Ours is sampled after
   prefill **plus 256 decode tokens**; §4.1's was `max_tokens: 1`. An ubatch-count effect is
   plausible — at `-ub 8192` p4k (4119 tok) fits in **one** ubatch while p12k (12334 tok) needs
   **two** — but that does **not** explain the 2048 case agreeing exactly, so this is **UNRESOLVED**.
   Flagged rather than papered over.
2. **Decode output is not bit-reproducible at `temperature: 0`.** All 9 arms produced 3 distinct
   `sha12` values *within* each `-ub`, i.e. run-to-run drift at a fixed `-ub`. This is **not**
   evidence that `-ub` perturbs numerics (it varies with `-ub` held constant), but it does mean
   the numerics question **cannot** be settled by comparing generated text. §3's teacher-forced KL
   remains the only valid way to answer it, and is still open.
3. **The 64-token warmup is itself noisy** (11.20–13.56 tok/s across arms). It is there to stop
   first-request cost aliasing with arm order, not to be a measurement; the 256-token window is
   the measurement.
4. `--ple-prefetch` was held **ON and constant** across all arms, matching `ubarm.sh`. The fork
   documents its CPU `GET_ROWS` advice as running "in both prefill and decode", so it is **not**
   decode-neutral — but holding it fixed is what keeps the `-ub` contrast clean. It was **not**
   A/B'd and needs its own paired experiment at fixed `-ub 2048` if anyone wants to size it.

### 2.7 Root cause of the Stage 0g failure (now closed)

The leader's diagnosis was correct and is now **verified against the binary**, not inferred:

```
$ llama-server --ple-prefill --help
error: invalid argument: --ple-prefill      # exit 1
$ llama-server --ple-prefetch --help
                                           # exit 0
```

`--ple-prefill` does not exist in this fork; only `--ple-prefetch` does. The broken harness passed
it on **every** arm, so every `llama-server` died at argument parse before `/health`, and with no
per-arm log surviving there was nothing to diagnose from. The Stage 0g re-run's empty
`decode.jsonl` / `decode-sweep.out` are consistent with that.

**Three further defects were found in the same script while fixing it** (the leader asked for a
full read, not just the typo — all three would have produced wrong or empty numbers):

1. **Missing `CUDA_DEVICE_ORDER=PCI_BUS_ID`.** Both working harnesses (`ubarm.sh:23`,
   `arm-stage0.sh:145`) set it; `decode.sh` set only `CUDA_VISIBLE_DEVICES=1`. Without PCI_BUS_ID
   ordering, CUDA may enumerate FASTEST_FIRST and "device 1" is not reliably the 3060 — so the arm
   would not have been paired with §1/§4.
2. **Compute-buffer regex never matched.** It looked for `compute buffer size is <n> MiB`, but the
   line emitted at load time is `compute buffer size =  <n> MiB` (equals sign, padded); the `is`
   wording only appears at **teardown**, after the request. `ubarm.sh:55` has the same latent
   mismatch. Grepping before the request therefore always yields empty.
3. **No pre-arm gate of its own** (it depended on a `PRE-ARM` line printed by another script) and
   **no `trap` on `EXIT`**, so any failure could leave a `llama-server` holding the GPU.

Also fixed: `/health` answered at 112 s while `model loaded` was only logged at 120 s, so the
harness now waits for the load sentinel before issuing a request; and `WT` resolved one level too
high (`../../..` for a script two levels below the worktree root), which pointed `TMPDIR` at a
non-existent path.

**Harness:** `.local/wt-stage0h/.local/stage0h/decode.sh` (working copy, git-ignored) — `smoke`
mode = one arm, `sweep` mode = the full 3×3. Raw records at `.local/stage0h/decode-sweep.jsonl`,
per-arm engine logs at `.local/stage0h/dec-ub<N>-r<R>.log`, unfiltered run log at
`.local/stage0h/decode-sweep.out`.

### 2.8 What this settles for the decision

§4.2's **INFERRED** `decode ≈ 0.908×` (−9.2%) at `-ub 8192` was explicitly conditioned on a
production path with **GPU-resident** experts, and was marked *not applicable* to the
`--n-cpu-moe 99` configuration actually measured. **This measurement supports that caveat**: on the
`--n-cpu-moe 99` rig the realised decode cost is **−0.9%, ~10× smaller than the inferred figure and
not distinguishable from zero.** So the §4 VRAM trade does **not** show up as a decode regression
here — the freed VRAM has nothing to compete with, exactly as §4.2 argued.

**It does not license a GPU-resident-experts conclusion.** The −9.2% remains untested on the path
where it would apply; a sweep without `--n-cpu-moe 99` is a separate job on a 12 GiB card that
cannot hold 42.81 GiB of expert weights, so it likely needs a different rig (5060 Ti or P100).

## Leader review note (2026-10-05)

"Decode does not move with `-ub`" should be read as **no detectable change at this n**, not as proof of none.
The `-ub 4096` interval (0.946-1.046) excludes a change larger than about 5%, but the `-ub 8192` interval
(0.876-1.106, sd 0.046, n=3, one outlier round) cannot exclude a change of about 10% either way, which is the
size of the inferred -9.2%. So "~10x smaller than the inferred figure" (§4.2) is a point estimate, not a bound.
Treat the 8192 decode claim as supported only for `--n-cpu-moe 99`, and still open at the interval's edge; a
decision on 8192 needs either more rounds or the owner accepting that width. The remaining prerequisite for any
default change is the teacher-forced KL (§3).

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
> **Stage 0h note:** the Stage 0h decode sweep re-measured post-request VRAM at `-ub 8192` as
> **10019 MiB**, not 10533 — a 514 MiB gap that is **unexplained** (§2.6 item 1). Treat the
> 467 MiB headroom figure as the conservative bound until that is reconciled.

## 5. Tail-ubatch probe (leader item 5 / #833 note point 4) — **NOT RUN**

The `#833` review note's reading is that a sub-threshold tail ubatch (below the 32-token offload
threshold) runs on CPU and is not staged, which would predict p12k's 46-token tail costs a full
restage. **I did not test it**, so that reading stays a hypothesis. It is cheap to test later: trim
p12k to a token count that is an exact multiple of `-ub` and compare against the untrimmed prompt at
the same `-ub`.

## 6. Could not measure

1. ~~**Decode rate**~~ — **RESOLVED by task t0028 (§2)**: measured n=3 paired at all three `-ub`,
   decode does not move. Removed from this list. The only decode figure still missing is one on a
   **GPU-resident-experts** path, where §4.2's inferred −9.2% would actually apply (§2.8).
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
**Stage 0h / t0028 decode sweep (2026-10-05) re-verified all of this:** pre-arm `MemAvailable`
91.12 GiB, both GPUs ≤ 1 MiB, 9/9 arms completed, and both GPUs returned to **1 MiB / 0%** with no
`llama-server` and ports 8091/8086/8093 free at the end.
All output under `.local/`, `TMPDIR` in `.local/tmp`, **nothing under `/tmp`**. No sudo, no local
binaries, no CI cycle, fork untouched (`feat/moe-prefill-stream` @ `34068ff9`, DRAFT PR #161 open).

## Change summary

**Changed:** this document plus `stage0g-ubatch/` raw JSON, engine logs and the sweep harness, on
branch `docs/811-stage0g`. No product, harness, export or config file touched.

**Assumptions:** the legacy flag-OFF path is the right baseline for a `-ub` comparison; VRAM scales
with `-b` as measured; prompts are the sha256-pinned Stage 0 set.

**Risks:** numerics unmeasured (§3) is now the **only** remaining prerequisite for changing a
production default — decode is measured and flat (§2). `-ub 8192` has only 467 MiB of headroom at
`-c 16384 --parallel 1`, less at 63K; §2.6 item 1 also leaves a 514 MiB VRAM reconciliation gap at
`-ub 8192` against §4.1, unexplained.

### Stage 0h addendum (task t0028, 2026-10-05)

**Changed:** §2 of this document only, plus the four lines that contradicted it (title, PARTIAL
callout, §0 decision row, §6 item 1) and the §7 rig-state note. Raw JSON, per-arm engine logs and
the fixed harness stay under `.local/stage0h/` (git-ignored); nothing was added to `stage0g-ubatch/`.
Branch `docs/811-stage0h-decode` off `origin/epic/811-strata-gap-analysis`. **No product, harness,
export or config file touched; no fork change, no CI cycle.**

**Added:** n=3 paired decode at `-ub` 2048/4096/8192, 9/9 arms, 0 failures. Harness defects fixed
and documented in §2.7 (the leader's `--ple-prefill` typo, plus a missing `CUDA_DEVICE_ORDER`, a
compute-buffer regex that could never match, and the absent pre-arm gate / `EXIT` trap).

**Assumptions:** `timings.predicted_per_second` is decode-only (verified in fork source, and
cross-checked by a timing identity to ≤ 4e-06 on every arm); `--ple-prefetch` is held constant
across arms but **not** decode-neutral, so its own effect remains unquantified.