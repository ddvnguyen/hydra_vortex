# Stage 1b-R results — staging overlap: **G-P FAILS (measured null); G-K1 is not a usable gate on this model**

**Epic:** #811 · **Branch:** `docs/811-stage1b-r` (off `origin/epic/811-strata-gap-analysis`)
**Date:** 2026-10-04 · **Rig:** RTX 3060 = GPU1 only · **GPU0 never touched** (1 MiB before, during, after)
**Design + pre-registration:** `STAGE1B-REDESIGN.md` (PR #829, merged)
**Fork:** `ddvnguyen/llama.cpp` branch `feat/moe-prefill-stream`, **DRAFT PR #161, never merged**
**Verdict in one line:** the mechanism works and is safe, but delivers **+6.5–7.7%** cold prefill,
not the required **≥1.15×** — and the measured reason (`f ≈ 0.2`) says **no implementation of this
design could have passed G-P on this rig.**

---

## 0. Gate summary

| gate | threshold | measured | verdict |
|---|---|---|---|
| **G-P** p4k | ≥ 1.15× paired mean | **1.0774** (95% CI 1.0725–1.0824, n=4) | **FAIL** |
| **G-P** p12k | ≥ 1.15× paired mean | **1.0652** (95% CI 1.0553–1.0752, n=4) | **FAIL** |
| **G-K1** | decode tokens 100% identical | flag-off control is **itself 6–35% LCP** | **NOT A USABLE GATE** (see §3) |
| **G-K2** | Mean KLD ≤ 0.010814 at K=14 | **not run** | **NOT RUN** (§7) |
| **G-V** | ≤ 11000 MiB | 7171 load / 7435 bench, both arms | **PASS** |
| **G-L** | 6.00–6.30 GB/s, no gen1 sample | 6.10–6.12 GB/s, 0 invalid samples, 16/16 arms | **PASS** |
| **G-G** | `graphs reused` ≥ control | **760 both arms, all 8 runs** | **PASS** |
| mechanism | link duty ↑, per-chunk GiB flat | not measured (dmon duty not extracted) | **NOT MEASURED** (§6) |

---

## 1. Provenance — one binary, flag toggles only

G-P is only valid if the flag is the *only* difference. It was: both arms ran the **same**
`llama-server` from the **same** artifact.

| | |
|---|---|
| artifact | `.local/stage1b-ci/524a7659/bin/llama-server` |
| **sha256** | `5124e43f057af25fcf2ee0b9235fa0b640d652ba04777a252d260d7eb3a65401` |
| `build-info.txt` `source_commit` | `524a76593694f43db1a8aa3389e3a9efe19a105c` |
| `built_at` | `2026-10-04T09:48:38Z` |
| cmake args | identical to the Stage 0c perplexity artifact set (`GGML_CUDA_ARCHITECTURES=86;120`, `GGML_CUDA_GRAPHS=ON`, `GGML_CUDA_FA_ALL_QUANTS`, CUDA 13.2, Release, shared libs) |
| `ldd` missing | 0 |
| binary shape | ELF 64-bit PIE, x86-64 |

Flag-off arms were verified to contain **no** `--moe-prefill-stream` in their launched argv
(count 0, from the harness's own flag echo), so the toggle is real.

**CI cycles used: 6 of 6.** `37185050092`(ffbbe31e) · `37187207931`(286536a6) ·
`37189268364`(13963d6b) · `37193211117`(524a7659) · `37203238393`(c1ee4f2e) ·
`37203862664`(34068ff9). All green. Nothing was built locally; validation was
`g++ -fsyntax-only` + `nvcc -c -o /dev/null` only.

---

## 2. G-P — per-pair data

Paired, interleaved, `n = 4` pairs, cold prefill tok/s = `prompt_tokens / ttft` on rep 0 with
`cache_prompt` disabled (so every rep is a genuine cold prefill). Prompts are the sha256-pinned
Stage 0 set (`warmup.txt` `ad920c19…`, `p4k.txt` `fa13b451…`, `p12k.txt` `2e5eabdf…`, all
`OK` against `sha256sums.txt`).

**p4k**

| pair | flag OFF | flag ON | ratio | delta |
|---:|---:|---:|---:|---:|
| 1 | 166.01 | 179.53 | 1.0814 | +8.14% |
| 2 | 165.14 | 178.67 | 1.0819 | +8.19% |
| 3 | 167.66 | 179.69 | 1.0718 | +7.18% |
| 4 | 166.23 | 178.64 | 1.0747 | +7.47% |
| **mean** | **166.26** | **179.13** | **1.0774** | **+7.74%** |

sd 0.0050, 95% CI **[1.0725, 1.0824]**. The whole CI is below 1.15.

**p12k**

| pair | flag OFF | flag ON | ratio | delta |
|---:|---:|---:|---:|---:|
| 1 | 168.07 | 177.53 | 1.0563 | +5.63% |
| 2 | 166.93 | 176.61 | 1.0580 | +5.80% |
| 3 | 166.38 | 179.39 | 1.0782 | +7.82% |
| 4 | 167.80 | 179.29 | 1.0685 | +6.85% |
| **mean** | **167.30** | **178.21** | **1.0652** | **+6.52%** |

sd 0.0102, 95% CI **[1.0553, 1.0752]**.

**G-P: FAIL on both cells.** Warmup (1193 output chars, ~18 tok/s) is not a G-P cell.

### 2.1 This ratio is, if anything, *optimistic* — and still fails

> **Leader erratum (review of PR #830, 2026-10-04).** The worker's draft called this ratio
> "conservative" and said the true ratio is "somewhat above" 1.077/1.065. That has the direction
> backwards and is corrected here.

The `524a7659` control arm is **handicapped by a bug found during this run** (§5): flag-off was
allocating the 900 MiB bank and taking one extra `ggml_backend_synchronize` per split. That makes
the control (denominator) artificially *slow*, which **inflates** ratio = ON/OFF. A correctly-gated
flag-off would be faster, so the true ratio is somewhat **below** 1.077/1.065 and unknown. Fixed
in `34068ff9` (cycle 6, not rig-verified — see §8). The bias therefore does not rescue G-P; it
makes the null slightly stronger. Two consequences: the implied `f` in §6 is an **upper bound**
(`f ≤ 0.20–0.23`), and the "VRAM / graphs-reused identical on both arms" rows in §4 are partly
vacuous, since the control also held the bank (7171 MiB flag off instead of the 6271 MiB
baseline), so G-V and G-G show only that the flag-on arm stays inside its limits.

---

## 3. G-K1 — the control says the gate cannot exist on this model

The decision rule was **pre-registered** in `STAGE1B-REDESIGN.md` §10.4 *before* any flag-on data
was seen. Metric: longest common prefix of the **full concatenated output** (`content` then
`reasoning_content`), in characters, as a fraction of the shorter run. Full text is stored per arm
(`<label>.<cell>.rep0.full.txt`, 24 files), never a 64-char snippet.

### 3.1 The control: flag-OFF vs flag-OFF, same binary

| cell | pair | lengths | LCP | LCP % |
|---|---|---|---:|---:|
| warmup | p1/p2, p2/p3, p3/p4, p1/p4 | 1193/1193 | 1193 | **100.0000%** (all four) |
| p4k | p1 vs p2 | 1072/1139 | 95 | 8.8619% |
| p4k | p2 vs p3 | 1139/1161 | 68 | 5.9701% |
| p4k | p3 vs p4 | 1161/974 | 341 | 35.0103% |
| p4k | p1 vs p4 | 1072/974 | 68 | 6.9815% |
| p12k | p1 vs p2 | 1167/1174 | 108 | 9.2545% |
| p12k | p2 vs p3 | 1174/1176 | 92 | 7.8365% |
| p12k | p3 vs p4 | 1176/1145 | 103 | 8.9956% |
| p12k | p1 vs p4 | 1167/1145 | 92 | 8.0349% |

**`L` = 0.059701 → the control is NOT bit-identical.** Two runs of the *same binary* with the flag
**off** share only 6–35% of their output on the p4k and p12k cells. Only the short warmup cell
reproduces exactly.

⇒ By the pre-registered rule, **G-K1 as written (100% identical decode tokens) is not achievable
on this model and is downgraded to a distributional gate.** This is a property of the rig, not of
the patch.

### 3.2 Flag-ON vs the flag-OFF pool (16 comparisons per cell)

| cell | min LCP | median | mean | control worst | verdict |
|---|---:|---:|---:|---:|---|
| warmup | 0.9246 | 1.0000 | 0.9811 | 0.0597 | PASS |
| p4k | 0.0192 | 0.0679 | 0.1144 | 0.0597 | **FAIL on min, indistinguishable on mean** |
| p12k | 0.0686 | 0.0810 | 0.1215 | 0.0597 | PASS |

Control mean LCP on p4k is 0.1421 across the four control pairs; flag-on mean is 0.1144. **The
two distributions overlap almost entirely.** The single `min` comparison that fails does so by
0.04 LCP-fraction against a control whose own spread is 0.06–0.35.

**G-K1 verdict: INCONCLUSIVE — it provides no evidence of a defect, and it cannot certify
identity.** Reported as *not a usable gate* rather than as a pass or a fail, because calling it a
fail would blame the patch for the model's own nondeterminism, and calling it a pass would be
false. This is consistent with `STAGE1-IMPL-SPEC.md` §7.1 R5 (temp-0 divergence between model
builds) and `STAGE0C-RESULTS.md` §3.2 (1.45e-4 run-to-run Mean KLD at build-vs-itself).

**Recommendation for the project, beyond this task:** any future correctness gate that needs
token-level determinism on this rig must be **teacher-forced** (fixed token ids, compare logits)
rather than free-running generation. Free-running `temperature: 0` cannot support it.

---

## 4. G-V / G-L / G-G — all pass

| arm | VRAM load | VRAM bench | `graphs reused` | h2d pre / post |
|---|---:|---:|---:|---|
| S1OFF p1–p4 | 7171 MiB | 7435 MiB | 760 | 6.10–6.12 / 6.10–6.12 GB/s |
| S1ON p1–p4 | 7171 MiB | 7435 MiB | 760 | 6.10–6.12 / 6.10–6.12 GB/s |

- **G-V PASS** — 7171 MiB vs the 11000 limit; the +900 MiB is exactly the predicted
  `2 × 450 MiB` bank. (The §6 prediction of 7171 in the REDESIGN correction was right.)
- **G-L PASS** — every one of the 16 probes inside 6.00–6.30; zero `link_invalid` markers.
- **G-G PASS** — `graphs reused = 760` identical in **all 8 runs**. This is the check the leader's
  F1 review predicted would fail without the parity reset; it passes, so the bank is a
  deterministic function of the graph and no re-capture occurs.

---

## 5. Defects found and fixed during this task

Recorded because both were invisible without a rig measurement.

1. **`13963d6b` — the staged path never engaged.** The bank pre-scan ran after pass 5 had
   rewritten `node->src[j] = tensor_id_copy(...)`, so `node->src[0]` was the CUDA staging copy and
   `ggml_backend_buffer_is_host()` was false for every candidate ⇒ `bank_bytes == 0` ⇒ no bank ⇒
   every split fell back to legacy. **Tell:** VRAM byte-identical flag on/off (6271 both) while
   `graphs reused` also matched. Fixed in `524a7659` by scanning `split->inputs[]`.
2. **`524a7659` — flag-off was not byte-identical.** The pre-scan and `moe_stage_ready()` gated on
   `iface.set_tensor_async_staged != NULL`, a **compile-time** property present in every build.
   Once fix 1 made the scan find candidates, the bank was allocated on *every* run, including
   flag-off: VRAM 7171 with the flag off, plus one extra `ggml_backend_synchronize` per split
   from the `stage_stream_wait_event` refusal path. Results were still correct (the CUDA hook
   declines when the flag is off) but the hard "flag off stays byte-identical" requirement was
   violated, and it handicapped the G-P control. Fixed in `34068ff9` with a third optional iface
   slot `moe_stage_enabled`, consulted at graph-split time.

**Two of my own measurement errors, also recorded:** (a) the first G-K1 pass hashed the `content`
field, which is **empty** for this reasoning model — every sha was `e3b0c442…`, the hash of the
empty string, making the in-arm verdict vacuous; (b) `tail -25` truncated my own sha256 listing,
briefly making two different perplexity binaries look byte-identical.

---

## 6. Why G-P failed — the mechanism check that *was* possible, and `f`

The mechanism gate (link duty ↑, per-chunk GiB flat) was **not extracted** — `dmon` duty was not
parsed out of the per-arm `.dmon.tsv`, so that gate is **NOT MEASURED**. But G-P itself pins the
quantity that matters, and it is the most useful number in this document.

Using the REDESIGN §7a model (per chunk: wall 10.2 s, transfer 5.45 s ⇒ `c = 38.7 ms` per weight
tensor per layer; non-transfer 4.75 s ⇒ `g = 33.7 ms` per GEMM per layer, 47 layers, saving
`= 2·min(g,c)` per layer):

| cell | measured mean ratio | implied saving/chunk | implied `min(g,c)` | implied **`f`** = GEMM share of non-transfer |
|---|---:|---:|---:|---:|
| p4k | 1.0774 | 0.733 s | 7.80 ms | **0.23** |
| p12k | 1.0652 | 0.625 s | 6.65 ms | **0.20** |

**`f ≈ 0.2`.** Only about a fifth of the non-transfer prefill time is expert-GEMM work that a copy
engine can hide behind; the other ~80% is attention, norms, graph launch, the 47 per-layer ids
host syncs, and the split overhead. Plugging `f = 0.2` into the REDESIGN §7a table lands on the
**1.08× row** — which is *exactly what was measured*.

**So the null is explained, and it was arithmetically unreachable.** At the measured `f`, the
best any correct implementation of "hide 2 of 3 expert-weight copies per layer behind the GEMMs"
can do is ≈1.08×. G-P's 1.15× needs `f ≥ 0.5` (§7a table). The design was sound; **the premise that
cold prefill is staging-bound was wrong for the second time** — first the FLOP-migration premise
(erratum, §1 of the REDESIGN), now the staging-bound premise. The 51% link duty is real, but the
compute it would hide behind is not there.

**This is the reportable result, not a failed implementation.**

---

## 7. G-K2 — not run, and a provenance problem that must be resolved first

Not run. It needs a ~57 GB base dump plus three ~30 min K=14 passes, and the leader's ordering
(G-K1 → G-K2 → G-P) meant it landed after the G-P pairs in the available window.

**Provenance defect found, unresolved.** The `llama-perplexity` built from this branch is
**byte-identical** to the binary `STAGE0C-RESULTS.md:12` records as the G-K2 floor:

```
92594986686a4bbe23a2834695792ce32f8b18b9d57bf3c50e0ac87aba715fdd   branch build (13963d6b)
92594986686a4bbe23a2834695792ce32f8b18b9d57bf3c50e0ac87aba715fdd   recorded floor binary (7a03f921d)
```

yet **that binary's `--help` contains `--moe-prefill-stream`**, a flag introduced *after*
`7a03f921d`. Both cannot be true. Either the floor's recorded hash is wrong, or the artifact
served for the floor was a later build. **The G-K2 floor's binary provenance is therefore not
established**, and the bar 0.010814 (= 2 × 0.005407) rests on it.

Explained but not identical: the two perplexity builds *on this branch* (`ffbbe31e` vs
`13963d6b`) are also byte-identical, which is expected — `BUILD_SHARED_LIBS=ON` and every change
of mine lives in `libggml-base.so`, not in the executable. Only the **library** differs between
flag states, which is what the flag actually toggles.

**Required before G-K2 is quoted, per the leader's instruction:** re-dump a fresh
flag-off/flag-off K=14 pair with the binary actually used, and quote G-K2 against
`max(0.010814, 2 × new floor)`, stating which was used. The 57 GB base file must go under
`/mnt/WorkDisk`, never `/tmp`.

---

## 8. Untested / known-open — honest list

1. **`34068ff9` (the flag-gate fix) is NOT rig-verified.** Cycle 6 (`37203862664`) is green, but no
   arm was run with it. The claim "flag off is byte-identical again" rests on code review plus the
   local compile, **not** on a measured VRAM of 6271 with the flag off. **That is the single most
   important unverified claim here.**
2. **`c1ee4f2e`'s `moe-stage: bank=<bytes> staged=<n> declined=<n>` line was never observed** — the
   artifacts used predate it. Staging engagement is evidenced only by the +900 MiB VRAM.
3. **The mechanism gate was not measured** — per-chunk GiB and link duty were not extracted.
4. **`f ≈ 0.2` is inferred from G-P**, not measured directly. An NSYS capture of one prefill ubatch
   would confirm it and is the cheapest way to close this out.
5. **G-K2 not run**; floor provenance unresolved (§7).
6. **G-K1 unresolved**, and probably unresolvable as specified (§3).
7. **Decode throughput** fell 5–11% in the earlier single-arm comparison (12.25 vs 13.77 tok/s
   p4k). That was the *inert* build, so it cannot be attributed to staging; it was not
   re-measured on `524a7659` and **G-D was not evaluated**.
8. Only `p4k`/`p12k` at ubatch 2048 were tested. Smaller ubatches, where the per-layer ids host
   sync is amortised over fewer tokens, are untested and are where this design would fare *better*
   — a plausible v2 direction, not a claim.

---

## 9. Recommendation

**Do not pursue this design further at ubatch 2048 on this rig.** The mechanism is correct,
safe (G-V/G-L/G-G all pass, decode untouched by construction) and worth ~7%, but the ceiling at
the measured `f ≈ 0.2` is ~1.08×, so G-P was never reachable here. Before spending more CI cycles,
either (a) change the pre-registered target to match reality (~1.05×) and bank the 7%, or
(b) attack the *actual* 80% — attention, norms, launch overhead and the 47 per-layer ids host
syncs — which is a different piece of work, or (c) test at a smaller ubatch where the ids sync
amortises, which is the one configuration the arithmetic says could clear 1.15×.

**Do fix G-K1 as a project-level gate.** It is currently unmeetable, and any future task that
relies on token-identity will draw the same false conclusion.

---

## Change summary

**Changed:** this document only, on branch `docs/811-stage1b-r`. No product, harness, export or
config file touched in the parent repo. On the **fork** (`ddvnguyen/llama.cpp`, branch
`feat/moe-prefill-stream`, DRAFT PR #161, **not merged**): commits `ffbbe31e`, `286536a6`,
`13963d6b`, `524a7659`, `c1ee4f2e`, `34068ff9`.

**Artifacts:** `5124e43f057af25fcf2ee0b9235fa0b640d652ba04777a252d260d7eb3a65401`
(llama-server, `524a7659`). CI runs `37185050092`, `37187207931`, `37189268364`, `37193211117`,
`37203238393`, `37203862664` — all green, 6 of 6 budgeted.

**Raw evidence:** `.local/docs/evidence/strata-gap/stage1b-r/logs/` — 8 ×
(`*.gate.json`, `*-engine.log`, `*.h2d.txt`, `*.vram_after_load.txt`, `*.vram_after_bench.txt`,
`*.dmon.tsv`, `*.link.tsv`) plus 24 full-text outputs `*.<cell>.rep0.full.txt`.

**Rig:** GPU1 only, both GPUs returned to 1 MiB, ports 8091/8086/8093 free, `MemAvailable` ≥ 67 GiB
at every pre-arm gate, nothing written under `/tmp` (`TMPDIR` pointed at `.local/tmp` throughout),
no sudo, no local binaries produced.