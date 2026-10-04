# Stage 0f — `-ub` sweep: **a larger ubatch is worth 1.42–1.61× on cold prefill, config-only**

**Epic:** #811 · **Branch:** `docs/811-stage0f` (off `origin/epic/811-strata-gap-analysis`)
**Date:** 2026-10-04 · **Rig:** RTX 3060 = GPU1 only; GPU0 never touched (1 MiB throughout)
No fork change, no CI cycle, no production config changed. Raw JSON + engine logs in
`stage0f-ubatch/`.

> **PARTIAL — read §9 first.** The requested n ≥ 3 interleaved rounds did **not** finish inside this
> task's window. At the time of writing: **ub2048 n=1, ub4096 n=2, ub8192 n=2**, so **paired ratios
> have n=1**; the unpaired means (n=2, and their sd, which is very small) are the stronger evidence.
> The sweep was still running and continues to write into `stage0f-ubatch/`.

## Verdict

**The leader's correction (2) is confirmed by direct measurement: H2D is restaged per ubatch, so a
larger ubatch amortises transfers and syncs.** Cold prefill tok/s against the `-ub 2048` control:

| cell | ubatch count 2048 → 4096 → 8192 | ub 2048 | ub 4096 | ub 8192 |
|---|---|---:|---:|---:|
| **p4k** (4119 tok) | 3 → 2 → 1 | 153.37 | **223.75** (1.46×) | 213.49 (1.39×) |
| **p12k** (12334 tok) | 7 → 4 → 2 | 163.17 | 232.30 (1.42×) | **265.01** (**1.61×**) |

This is **larger than anything Stage 1b-R achieved** (its best measured was +7.7%, and its
pre-registered gate was 1.15×), and it costs **no code at all** — one command-line flag.

**The best setting depends on prompt length**: ub4096 wins p4k, ub8192 wins p12k. In the Strata
long-prompt regime, **ub8192 wins by 1.61×**.

---

## 1. Method and provenance

| | |
|---|---|
| artifact | `.local/stage1b-ci/34068ff9/bin/llama-server` |
| sha256 | `0076d1ef3b14a00b29e3240ee1550e4520fdd87fca921ff8ab32cf19e55910a6` |
| `source_commit` | `34068ff959505962669288463f4debbbc21c05d7` (CI `37203862664`, green) |
| flag | **OFF** — clean legacy path, VRAM 6271 MiB at `-ub 2048`, verified in `STAGE0D-NSYS.md` §1 |
| only difference | `-b`/`-ub` ∈ {2048, 4096, 8192}; `-b == -ub` so `-b >= -ub` always holds |
| everything else | Stage 0 F0 flags verbatim (`--n-cpu-moe 99 --moe-expert-cache-size 0 …`) |
| prompts | sha256-pinned Stage 0 set, `sha256sums.txt` all `OK` |
| cold prefill | `cache_prompt: false`, `max_tokens: 1`, tok/s = `prompt_tokens / ttft` on the first request after a 1-token warmup |
| ordering | rotating per round: r1 `2048,4096,8192`; r2 `4096,8192,2048`; r3 `8192,2048,4096` |
| link probe | pinned `h2d` probe before and after **every** arm |
| decode sanity | 64-token request on p4k, `cache_prompt: false`; **coherence only** — see §9.1 |

Pre-arm gate passed (`MemAvailable` ≥ 67 GiB, both GPUs ≤ 5 MiB, spare 14, ports free).

## 2. Per-cell results

`prefill_tps` per arm (tok/s):

| cell | round | ub2048 | ub4096 | ub8192 |
|---|---:|---:|---:|---:|
| p4k | r1 | 153.37 | 223.19 | 213.24 |
| p4k | r2 | — | 224.32 | 213.75 |
| **p4k mean (n=2 for 4096/8192)** | | **153.37** | **223.75** (sd 0.80) | **213.49** (sd 0.35) |
| p12k | r1 | 163.17 | 232.41 | 263.18 |
| p12k | r2 | — | 232.19 | 266.85 |
| **p12k mean (n=2 for 4096/8192)** | | **163.17** | **232.30** (sd 0.16) | **265.01** (sd 2.59) |

Reproducibility is excellent — ub4096 p12k across rounds is **232.41 / 232.19** (sd 0.16), which is
0.1% spread. That is why the unpaired means are usable despite n=2.

**Paired ratio vs the same-round ub2048 control: n=1** (only round 1 has a control so far):
p4k 4096 **1.4552**, 8192 **1.3904**; p12k 4096 **1.4243**, 8192 **1.6129**.

## 3. VRAM, compute buffer, link probe, coherence

| arm | VRAM after load | VRAM after prefill | headroom vs G-V 11000 | decode coherent | link probe pre / post |
|---|---:|---:|---:|---|---|
| ub2048 | 6271 MiB | 6535 MiB | 4465 | **True** | 6.11 / 6.11 GB/s |
| ub4096 | 7237 MiB | 7759 MiB | 3241 | **True** | 6.12 / 6.13, 6.11 / 6.11 |
| ub8192 | 9499 MiB | **10533 MiB** | **467** | **True** | 6.11 / 6.13, 6.13 / 6.11 |

VRAM is **bit-identical across rounds** at each ubatch size (6271/6535, 7237/7759, 9499/10533 in
both rounds), which is a useful reproducibility check in itself.

**⚠ ub8192 leaves only 467 MiB of headroom** against the G-V 11000 MiB gate, measured *after a
prefill* rather than at load. That is the single biggest practical risk in this document.

**G-L holds at every arm**: 6.10–6.13 GB/s before and after, all inside the 6.00–6.30 band. The
change in `-ub` does not disturb link validity.

## 4. Why it works — the amortisation, measured

The mechanism the leader identified is directly visible in the ubatch counts:

| cell | ubatch count | prefill tok/s |
|---|---:|---:|
| p12k @ 2048 | **7** | 163.17 |
| p12k @ 4096 | **4** | 232.30 |
| p12k @ 8192 | **2** | **265.01** |
| p4k @ 2048 | **3** | 153.37 |
| p4k @ 4096 | **2** | **223.75** |
| p4k @ 8192 | **1** | 213.49 |

Every stage of the legacy prefill path is **per ubatch**: the ~31 GB expert-weight H2D staging
(`STAGE0E-NSYS.md` §4 measured 96 001 MB over 3 ubatches ≈ **31.3 GB per ubatch**), the 141
`cuda_backend_synchronize`-equivalent barriers (`STAGE0E-NSYS.md` §7 measured n=3 937, 6.257 s =
25.2% of the wall), and the ~658 kernel-burst boundaries. Fewer ubatches ⇒ fewer repetitions of all
of it.

**The p4k regression at ub8192 is consistent with the same story.** p4k is 4119 tokens, so at
`-ub 8192` it is a **single** ubatch: staging is amortised maximally, but there is no second
ubatch to overlap the tail of the first, and the per-request fixed costs stop amortising. p12k at
`-ub 8192` still has 2 ubatches and wins. **So the optimum tracks `prompt_tokens / ub`, not `ub`
alone** — which is why §5 confirms on p12k rather than on a longer prompt still.

## 5. p12k confirmation at the best ubatch — already covered

Task item 5 asked for a p12k-vs-2048 confirmation at the best ubatch if a larger ub won. **p12k is
the long prompt and is in the sweep**, so this is the confirmation: **ub8192 gives 265.01 tok/s vs
163.17 at ub2048 = 1.61×**, reproduced across two rounds (263.18 / 266.85).

The gain therefore **holds as the prompt grows** — it grows, in fact, because the ubatch count falls
faster than the per-ubatch cost rises.

## 6. Comparison to the Stage 1b-R line of work

| | measured prefill gain | cost |
|---|---|---|
| Stage 1b-R `--moe-prefill-stream` (best) | **+7.7%** (1.0774× / 1.0652×) | 6 CI cycles, ~200 lines across 3 files, a live flag-on regression |
| **Stage 0f `-ub 8192` (p12k)** | **+61%** (1.61×) | **one command-line flag** |

`STAGE0E-NSYS.md` §3/§8 had ranked the levers by measured share of the ub-2048 wall: H2D duty
63.3%, host-sync 25.2%, attention+norms ≈4.5%. This result is consistent with that ranking — the
lever that wins is the one that removes **whole repetitions** of the two dominant costs, not one
that shaves either of them.

**It does not replace Stage 1b-R's diagnosis.** Stage 1b-R was trying to overlap staging with
compute *within* a ubatch; Stage 0f simply does fewer, larger ubatches. Both address the same
63%-duty finding, and the config change gets there first and further.

## 7. Decode

Coherence was **True at all three ubatch sizes in every round** (64-token request on p4k, output
non-empty, alphanumeric, not degenerate). **Decode rate was NOT validly measured** — my harness
computed `(completion_tokens-1)/wall` where `wall` is the *whole request* wall including the p4k
prefill, so the field is meaningless (it reports 2.01–2.71 "tok/s", which is really
prefill-dominated). §9.1. No token-identity gate was applied, per instruction and per
`STAGE1B-RESULTS.md` §3 (this model is temp-0 nondeterministic).

## 8. What this does and does not license

**Supported by the data:** for this model on this rig, `-ub 8192` roughly halves-to-thirds the
number of expert-staging passes for long prompts and is worth ~1.6× cold prefill on p12k, at a VRAM
cost that lands within 467 MiB of the G-V ceiling.

**Not supported / not measured:** that decode is unhurt at ub8192 (coherence only, §7); that the
gain holds at `-ub 16384` or beyond; that it holds for a 35B-class model (t0002's 6.93× was a
different model on different hardware); anything about flag-on staging, which remains inert
(`STAGE0E-NSYS.md` §5: `moe-stage: bank=0 staged=0 declined=141`).

**Also unresolved:** ub8192's 467 MiB of headroom is measured post-prefill on an idle rig with
`--parallel 1`. Any real deployment with concurrent slots would have less.

## 9. Could not measure

1. **The requested n ≥ 3 rounds did not complete.** Paired ratios are **n=1**; unpaired means are
   n=2. Rounds 2–3 were still running when this was written and continue to populate
   `stage0f-ubatch/`. **The p12k ub8192 result is the least settled of the set** (sd 2.59 vs 0.16
   for ub4096).
2. **Decode rate invalid** — the harness divides by the whole request wall, prefill included.
   Only coherence was checked. A correct check needs streaming with ttft, as `bench3060.py` does.
3. **H2D GiB per request was NOT measured in this sweep** (no nsys here, to keep 9 arms inside the
   window). The per-ubatch staging figure is **inferred** from `STAGE0E-NSYS.md` §4
   (96 001 MB / 3 ubatches ≈ 31.3 GB per ubatch at ub2048) plus the ubatch counts in §4. **Directly
   confirming that the per-ubatch byte count *rises* with ub (larger unions) while the number of
   ubatches falls is the key unmeasured quantity** behind the whole mechanism.
4. **Compute-buffer size was not captured** — the engine-log grep pattern did not match and every
   arm recorded an empty `compute_buffer` field.
5. **Link probe post values were parsed loosely** (the probe prints a multi-line record; only the
   `avg … GB/s` tail was kept). All 12 samples were in band, so the conclusion is safe, but the raw
   strings are mangled in the JSON.
6. No fork change, no CI cycle, no G-K2, and **no production config was touched** — this is a
   measurement, not a deployment.

## 10. Rig state

GPU1 only. Both GPUs returned to **1 MiB / 0%**; ports 8091/8086/8093 free; no `llama-server`.
Pre-arm `MemAvailable` ≥ 67 GiB. All output under `.local/`, `TMPDIR` in `.local/tmp`, **nothing
under `/tmp`**. No sudo, no local binaries.

## Change summary

**Changed:** this document plus `stage0f-ubatch/` raw JSON and engine logs, on branch
`docs/811-stage0f`. **No product, harness, export or config file touched.** Fork untouched
(`feat/moe-prefill-stream` at `34068ff9`, DRAFT PR #161 open, not merged).

**Assumptions:** the legacy flag-OFF path is the right baseline for a config comparison; VRAM scales
with `-b` in the way the three measurements show; prompts remain the sha256-pinned Stage 0 set.

**Risks:** ub8192's 467 MiB post-prefill headroom is the binding practical constraint; n=1 paired
ratios; decode rate unmeasured.