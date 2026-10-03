# RANKING-POC-2 — Held-out trace-ranked expert profile, n=3 (RTX 3060)

- **Task**: t0012 / #811 — re-run the ranking/Atlas test with the three `RANKING-POC` weaknesses fixed.
- **Date**: 2026-10-03 · **Branch**: `docs/811-ranking-poc-2` · **Status**: COMPLETE
  (capture + all 9 arms `status=ok`, `link_bad_samples=0`, rig left clean).
- **Verdict (one line)**: at **cold start (T1)** the held-out trace-ranked profile is **significantly
  worse** than shipped (**A − S = −11.00 pp** hit rate, 36.7× the 0.30 pp same-profile range) while
  beating a clean shuffle by **+6.50 pp** — so the ranking methodology *does* extract real signal,
  but 16 probes / 31 K prompt tokens are nowhere near enough to match the shipped profile; by T3 all
  three arms sit inside the same-profile noise range.

---

## 1. What changed vs `RANKING-POC.md`

| # | Weakness in t0008 | Fix in t0012 |
|---|-------------------|--------------|
| 1 | Candidate ranking built from probes on **a different model** (`APEX-I-Mini` atlas vs served `GSQ-RCO-IQ3_S`) | Ranking built on the **served model** via `--dump-routing` during a dedicated capture session on this rig |
| 2 | **n=1** per arm → noise floor only, per-turn envelope up to 11 pp made most claims unresolvable | **n=3 per profile**, arms **interleaved** `S,A,R,S,A,R,S,A,R` so drift is spread across arms |
| 3 | 6-turn **median** as headline — profiles only differ while the cache is still profile-dominated | **T1 cold-start hit rate is the primary metric** (first request, no traffic-shaped residency yet) |

Owner rulings applied (2026-10-03): **Q1** one serve session / 16 independent conversations / one trace
file; **Q2** `--short-read 8192` added to the **capture config only** (measurement arms keep stock
flags, deviation documented here); **Q3** existing corpus, trimmed, sized 1.0–2.0 K tokens, disjoint
from `rank-t1..t6`.

## 2. Design

One GPU (RTX 3060, driver 595.91.07, Strata **v0.1.29**, model `gsq-rco-iq3_s`, 16 K ctx), everything
sequential, port 8092 only.

**Phase 1 — capture** (2026-10-03 12:50:22 → 13:09:31, exit 0): one serve session, stock shipped
profile, **16 independent conversations** (fresh `messages` list each, no shared leading text),
`--dump-routing` + `--short-read 8192` → one trace file.

**Phase 2 — profile build** (CPU only): `A = make_profile.py trace.bin --no-base`,
`R = shuffle(A, seed 1)`.

**Phase 3 — measurement** (13:12:47 → 13:44:43): 9 cold-start arms, one engine load per arm,
interleaved `S,A,R,S,A,R,S,A,R`, turns **T1–T3** from the held-out eval prompts, `temp=0 seed=42
max_tokens=256`, stock engine flags (no `--dump-routing`, no `--short-read`).

| Arm | role | replicate | `--expert-profile` |
|------|------|-----------|--------------------|
| S1 S2 S3 | shipped | 1–3 | `/mnt/WorkDisk/strata/src-v0.1.29/data/expert-profile.bin` |
| A1 A2 A3 | trace-ranked (held-out) | 1–3 | `.local/ranking2/A.bin` |
| R1 R2 R3 | shuffled A (negative control) | 1–3 | `.local/ranking2/R.bin` |

Profiles (STRP v1, **24 576 ranked pairs each, byte size 196 632, identical membership — the three
differ only in order**):

| profile | sha256 | how built |
|---------|--------|-----------|
| S shipped | `8f59b4aa8873209dff11c11e37bcda9529a1335b724a1afeea37bf6388975baf` | as shipped |
| A | `f1f5321a67d246799e77a25c6a2391c3419723e75ceeda48e41cf2bc1f813c0e` | `make_profile.py trace.bin --no-base` → **0 base + 24 368 trace-ranked + 208 fill** |
| R | `aa5135e818d84ab8fbce4386ec12b1c773916df0b16adc093d6086ef055fd86f` | `random.Random(1).shuffle(A)` |

Top-K overlap (random expectation at K=2638 is **282**):

| K | \|S∩A\| | \|A∩R\| | \|S∩R\| |
|---|--------|--------|--------|
| 1024 | 179 | 53 | 37 |
| 2041 | 537 | 171 | 163 |
| **2638** | **767** | **287** | **281** |
| 4096 | 1464 | 680 | 674 |
| 8192 | 4256 | 2807 | 2783 |

`|A∩R|=287` and `|S∩R|=281` both sit on the 282 random expectation → **R is a clean uniform
permutation of A**, and S has no accidental affinity for R. `|S∩A|=767` = 2.7× random → A is
genuinely a *different* ranking, not a perturbation of shipped.

## 3. Capture session (phase 1)

Config `configs/rank-capture.json` = the P0/stock argument list **plus two trailing capture-only
flags** (and the log path):

```
… --max-context 16384
   --dump-routing <worktree>/.local/ranking2/trace.bin
   --short-read 8192
```

`exe` is the stock binary — no wrapper, no code edit; `/mnt/WorkDisk/strata` untouched.

**Why `--short-read 8192` was mandatory (not cosmetic)**: `windows_ok` (`generate.cpp:3883-3884`) requires
`b − a <= o.short_read`, and the default is `short_read = 64` (`:296`), parsed at `:1026`. Batched
prefill has **no routing hook at all** (`src/prefill/{prefill.cpp,kernels.cu,…}` contains zero
references to `Drive` or `pool_fn`; the only `dump` hits there are unrelated `STRATA_QSA_DUMP` /
`STRATA_PREFILL_DUMP_R` debug paths), so prompt tokens only get written when the request is routed
through the windowed read path (`win_pool_fn` → `drive_pool_split`/`drive_pool_multi`, `:3243`).
With the default the ~1.9 K-token probe prompts would fail `windows_ok` and the **prompt tokens would
be untraced**. Two alternatives were checked and rejected: `--prefill 0` is **illegal in serve**
(`prefill_chunk <= 0` → `return 2`, `:3087-3091`), and native IQ packs require `--prefill` for
>1-token prompts (`:1379-1383`, also `return 2`), which would have blocked the full-prompt one-shot
path anyway. Cost: windowed read ≈16 ms/token vs ≈5.5 ms batched — acceptable, because this is a
**separate capture session that contributes no measurement data**.

### 3.1 Prefix-cache guard (owner's condition a/b/c)

| check | result |
|-------|--------|
| conversations | **16/16 ok**, **16/16 `prefix_ok`** |
| `reused` on every conversation | **0** (set of all `reused` values = `{0}`) |
| `read == prompt_tokens` on every conversation | **true for all 16** |
| prompt tokens served | 30 995 (exactly equals Σ `read`) |
| completion tokens | 4 096 (16 × 256) |
| traced tokens (records ÷ 48) | **35 730** = 35 091 served + **639 (+1.82 %)**, consistent with spec-decode draft/verify rows |

**Traced tokens by category (owner's condition c)** — category balance is visible and near-equal:

| category | conversations | prompt tokens | completion tokens | reused | read |
|----------|---------------|---------------|-------------------|--------|------|
| coding (`train-c01..c08`) | 8 | **15 453** | 2 048 | 0 | 15 453 |
| general (`train-g01..g08`) | 8 | **15 542** | 2 048 | 0 | 15 542 |
| **total** | **16** | **30 995** | **4 096** | **0** | **30 995** |

No conversation reused a prefix, so there is **no traced-token deficit to account for** and the trace
is not dominated by a shared preamble.

### 3.2 Trace facts

| | |
|---|---|
| path / size | `.local/ranking2/trace.bin` — **150 923 520 bytes** (≥ the 10 MB assertion, by 15×) |
| sha256 | `699ea0acd6d8b1f1aa231d0503b8c429720ab36b6b685e012b1263495fbe8242` |
| records | **1 715 040** = 35 730 tokens × 48 layers |
| arithmetic check | 35 730 × 4 224 B/token (88 B/layer × 48) = **150 923 520 B — exact** |
| distinct (layer, expert) | **24 368 / 24 576 = 99.15 %** of all pairs touched |
| H2D pre/post | 6.11 / 6.12 GB/s (expected ≈6.1) |
| `link_bad_samples` | 0 |
| Mem after | 85.3 GiB |

Startup facts match t0008 P0 exactly (`expert cache auto → 2041`, `expert cache 2638 slots, 5.06 GiB`,
`pre-filled 2638 of 2638`, `prompt chunk auto: 8192`, `borrows 2313 cache slots (4.43 GiB)`).

### 3.3 Provenance of the 16 prompts (contamination check)

**Capture prompts are the existing corpus, trimmed — owner's Q3.** Each of the 16 is an *exact
substring* (a leading trim) of a source file under `/mnt/WorkDisk/trace-collect-backup/prompts/`,
verified by containment, 16/16 (some sources occur at more than one corpus path; first path shown):

| capture prompt | source file(s) |
|----------------|----------------|
| `train-c01..c08` | `v2/c13`, `v2/c14`, `v2/c15`, `v2/c16`, `v2/c18`, `v2/c19`, `v2/c20`, `v2/c21` |
| `train-g01..g04` | `v2/g19`, `v3/e02`, `v3/e03`, `v3/r02` |
| `train-g05..g08` | `v2/g14`, `v2/g15`, `v2/g17`, `v2/g20` |

**The eval prompts are not in that corpus at all** — all 6 of `rank-t1..t6.txt` return 0 verbatim,
0 substring and 0 shared-first-60 matches against all 126 files there. So the capture and evaluation
sides come from disjoint origins, which is the property the experiment needs.

Measured with the pack tokenizer (`tools/strata_tokenizer.py`, `TPATH=/mnt/SSD/strata-models/packs/iq3_s/tokenizer`,
via `/mnt/WorkDisk/strata/src/.venv/bin/python`):

| set | files | token range |
|-----|-------|-------------|
| eval (`rank-t1..t6.txt`) | 6 | 868 – 1057 (T1 = 1006) |
| train (`train-{c,g}NN.txt`) | 16 | **1861 – 1899**, all inside the required [1000, 2000] |

Disjointness, evaluated over exactly those 22 files:

| check | result |
|-------|--------|
| duplicate sha256 | **0** (22/22 unique) |
| exact content overlap | **0** |
| 150-char prefix overlap | **0** |
| 80-char prefix overlap | **0** |
| 60-char prefix overlap | **0** (22/22 unique first 60 chars → **no shared preamble anywhere**) |
| no system prompt / `#` header / leading whitespace | true |
| exactly one trailing LF | true |

`train-sha256sums.txt` and `train-manifest.json` record the hashes and metadata.

## 4. Measurement protocol (phase 3)

- Prompts: held-out `prompts/rank-t1..t3.txt` (the T1–T3 subset of the t0008 eval set),
  `convo3060.py --turns 3` → `parse_ranking.py --expected-turns 3`. Cumulative single chat session,
  `temp=0`, `seed=42`, `max_tokens=256`, one request at a time.
- Engine `prompt_tokens` per turn, identical in all 9 arms: **1057 → 2344 → 3481**.
- Configs `configs/rank-{s,a,r}{1,2,3}.json` = `rank-p0` with only `exe`, `--expert-profile`, `log`
  and `env.STRATA_STDOUT_LOG` changed — **no `--dump-routing`, no `--short-read`** (verified by diff).
- **stdout capture (owner's "if achievable")**: `exe` points at
  `.local/strata-research/strata-tee.sh`, which does
  `[ -n "$STRATA_STDOUT_LOG" ] && exec > >(tee -a "$STRATA_STDOUT_LOG")` then `exec`s the stock
  binary. `server.py` uses `cfg["exe"]` verbatim, so no `/mnt/WorkDisk/strata` edit was needed.
  Result: **all 9 arms captured stdout** (`logs/<arm>.stdout.log`, 785 lines for S1, incl. 3 `DONE`).

### 4.1 The stdout summary block does not exist in serve mode

t0008 §3 attributed the missing `speculation` / `adaptive tier` lines to `server.py`'s protocol
loop swallowing the pipe. **That was wrong, and this run corrects it.** Direct stdout capture shows
the block is absent, and the source explains why:

```
generate.cpp:3086   if (o.serve) {
generate.cpp:4371       return 0;      }      ← serve block ends here
generate.cpp:4940-4990  … speculation / accepted per round / verify window / pool multi /
                        dispatch / adaptive tier / pcie experts / mtp   ← AFTER the return
```

The block is in the **one-shot** path only and is unreachable in `--serve`. It is not a capture
failure; there is nothing to capture. Consequence, unchanged from t0008: **adaptive expert-swap
counts remain unobservable in serve mode** (`adapt_every` *does* act — `generate.cpp:4160` — it just
never reports). The per-turn hit-rate trajectory stays the only adaptation proxy, and T1–T3 is too
short to exercise the `T4+` adaptation window anyway.

## 5. Integrity gates (all 10 runs, all passed)

| run | H2D pre/post (GB/s) | link_bad | Mem after | ports 8090/8091/8092 | verdict |
|-----|---------------------|----------|-----------|------------------------|---------|
| capture | 6.11 / 6.12 | 0 | 85.3 GiB | down (pre-gate) | `status=ok` |
| S1 | 6.12 / 6.11 | 0 | 87.5 GiB | down (pre-gate) | `status=ok` |
| A1 | 6.11 / 6.13 | 0 | 87.4 GiB | down (pre-gate) | `status=ok` |
| R1 | 6.13 / 6.11 | 0 | 87.5 GiB | down (pre-gate) | `status=ok` |
| S2 | 6.11 / 6.12 | 0 | 87.2 GiB | down (pre-gate) | `status=ok` |
| A2 | 6.11 / 6.12 | 0 | 87.2 GiB | down (pre-gate) | `status=ok` |
| R2 | 6.12 / 6.11 | 0 | 87.4 GiB | down (pre-gate) | `status=ok` |
| S3 | 6.11 / 6.12 | 0 | 87.4 GiB | down (pre-gate) | `status=ok` |
| A3 | 6.12 / 6.12 | 0 | 87.3 GiB | down (pre-gate) | `status=ok` |
| R3 | 6.12 / 6.11 | 0 | 87.2 GiB | down (pre-gate) | `status=ok` |

Every run required `MemAvailable ≥ 67 GiB` before start (never lowered), stopped by exact PID, and
ended with 8092 down + GPUs at 1 MiB. No run under swap churn; final `MemAvailable` 86.6 GiB.

Startup facts per arm:

| arm group | profile line | slots | pre-filled | borrow | prompt chunk |
|-----------|--------------|-------|------------|--------|--------------|
| S1 S2 S3 | `…/data/expert-profile.bin: 24576 ranked pairs` | **2638** | 2638/2638 | 2313 (4.43 GiB) | 8192 |
| A1 A2 A3 | `…/.local/ranking2/A.bin: 24576 ranked pairs` | **2666** | 2666/2666 | 2340 (4.43 GiB) | 8192 |
| R1 R2 R3 | `…/.local/ranking2/R.bin: 24576 ranked pairs` | **2663** | 2663/2663 | 2333 (4.43 GiB) | 8192 |

Slot count is **deterministic per profile, not random** (identical across its 3 replicates, and
t0008 measured S at exactly 2638 again). A and R get **+28 / +25 slots (+1.1 % / +0.9 %)** over S —
a confound that biases A and R *toward* larger caches, i.e. **against** the A deficit reported below.
Parser verdict per arm: `aligned=True`, `counts = {serve:3, hit:3, decode_timing:3, prefill:3, convo:3}`.

## 6. Results

### 6.1 Per-arm raw: hit rate (%) / decode tok/s

| Arm | T1 | T2 | T3 | **median hit** | **median tps** |
|-----|----|----|----|----------------|----------------|
| S1 shipped | **56.1** / 30.6 | 60.5 / 29.3 | 47.7 / 35.0 | **56.1** | 30.6 |
| S2 shipped | **56.4** / 37.4 | 60.9 / 37.6 | 56.2 / 34.3 | **56.4** | 37.4 |
| S3 shipped | **56.4** / 27.8 | 63.5 / 40.3 | 54.7 / 36.7 | **56.4** | 36.7 |
| A1 trace | **45.6** / 31.8 | 59.6 / 39.2 | 56.3 / 37.0 | 56.3 | 37.0 |
| A2 trace | **45.4** / 29.8 | 52.7 / 37.9 | 57.0 / 36.2 | 52.7 | 36.2 |
| A3 trace | **44.9** / 27.2 | 53.8 / 28.4 | 57.1 / 33.4 | 53.8 | 28.4 |
| R1 shuffle | **38.2** / 35.2 | 59.4 / 27.6 | 55.3 / 34.9 | 55.3 | 34.9 |
| R2 shuffle | **38.3** / 23.0 | 59.7 / 36.7 | 56.5 / 36.6 | 56.5 | 36.6 |
| R3 shuffle | **39.9** / 22.8 | 60.2 / 32.7 | 55.8 / 36.0 | 55.8 | 32.7 |

Prompt-cache reuse was byte-identical across all 9 arms — `reused = 0, 1052, 2339` on turns 1–3 in
every arm, taken from the engine's `strata serve: prompt …` line (client `convo.json` does not carry
reuse) — with prefill 6317–7131 ms per turn (6.3–7.1 s). The conversation structure is therefore a
controlled constant.

### 6.2 Aggregates (n=3 per profile, mean and within-profile range)

**T1 — the primary metric:**

| role | mean | range | spread |
|------|------|-------|--------|
| **S** shipped | **56.30** | [56.1, 56.4] | **0.30** |
| **A** trace | **45.30** | [44.9, 45.6] | 0.70 |
| **R** shuffle | **38.80** | [38.2, 39.9] | 1.70 |

**T2:**

| role | mean | range | spread |
|------|------|-------|--------|
| S | 61.63 | [60.5, 63.5] | 3.00 |
| A | 55.37 | [52.7, 59.6] | 6.90 |
| R | 59.77 | [59.4, 60.2] | 0.80 |

**T3:**

| role | mean | range | spread |
|------|------|-------|--------|
| S | 52.87 | [47.7, 56.2] | 8.50 |
| A | 56.80 | [56.3, 57.1] | 0.80 |
| R | 55.87 | [55.3, 56.5] | 1.20 |

**3-turn medians and decode tok/s:**

| role | median-hit mean | range | spread | median-tps mean | range | spread |
|------|-----------------|-------|--------|-----------------|-------|--------|
| S | 56.30 | [56.1, 56.4] | **0.30** | 34.90 | [30.6, 37.4] | 6.80 |
| A | 54.27 | [52.7, 56.3] | 3.60 | 33.87 | [28.4, 37.0] | 8.60 |
| R | 55.87 | [55.3, 56.5] | 1.20 | 34.73 | [32.7, 36.6] | 3.90 |

### 6.3 Claim rule applied — *difference must exceed the within-arm S range*

| comparison | T1 Δ (rule) | T2 Δ (rule) | T3 Δ (rule) | 3-turn median Δ (rule) |
|------------|-------------|-------------|-------------|------------------------|
| **A − S** | **−11.00** vs 0.30 → **36.7×, SIGNIFICANT** | −6.27 vs 3.00 → 2.1×, **significant** | +3.93 vs 8.50 → 0.46×, no | −2.03 vs 0.30 → 6.8×, significant *(but see below)* |
| **R − S** | **−17.50** vs 0.30 → **58.3×, SIGNIFICANT** | −1.86 vs 3.00 → 0.62×, no | +3.00 vs 8.50 → 0.35×, no | −0.43 vs 0.30 → 1.4×, marginal |
| **A − R** | **+6.50** vs 0.30 → **21.7×, SIGNIFICANT** | −4.40 — inside A's own 6.90 spread → no | +0.93 → no | −1.60 vs 0.30 → within A's own 3.60 spread → no |

Decode tok/s: **no resolvable separation anywhere.** The smallest within-profile spread is 3.90 tok/s
(R) while the largest between-profile median delta is 1.03 tok/s — every tps comparison fails the
rule and is reported as noise only.

## 7. Verdict

1. **Does a ranking built on the served model beat the shipped profile at cold start? — NO.**
   Primary metric T1: **A − S = −11.00 pp** (45.30 vs 56.30), 36.7× the 0.30 pp same-profile range,
   and A's own range [44.9, 45.6] does not touch S's [56.1, 56.4]. The gap is unambiguous and it is
   a *deficit*: the trace-ranked profile is materially worse exactly where the profile should matter
   most. A also had **1.1 % more slots** (2666 vs 2638), so the confound runs against this finding
   rather than producing it.

2. **Does the trace-derived ordering carry real signal? — YES.** **A − R = +6.50 pp** at T1, 21.7× the
   S range, with tight and non-overlapping ranges (A [44.9, 45.6] vs R [38.2, 39.9]). A and R share
   *identical membership and byte size* — only the order differs — so this delta is attributable to the
   ranking alone. Held-out traces beat a uniform permutation cleanly.

3. **How much ordering matters is a function of how cold the cache is.**
   T1: S ≫ A ≫ R (0.30 pp spread, fully resolved). T2: only A−S still clears the rule. T3: all three
   arms fall inside S's own same-profile 8.50 pp range (52.87 / 56.80 / 55.87) → **no resolvable
   difference** — residency has self-organised from actual traffic and the static profile ranking has
   stopped dominating. This reconciles cleanly with t0008, where shuffled ≡ shipped at 6-turn median
   (P2−P0 = +0.25 pp): *permutation matters at cold start; it does not matter once warm.*

4. **A is not only worse but less stable.** A's median-hit spread is 3.60 pp vs S's 0.30 pp (12×),
   driven by T2 (52.7 / 53.8 / 59.6). Ranking from 16 probes produces a profile that is both
   lower-performing and more variable.

5. **Practical conclusion for #811.** The t0008 weaknesses are now closed and the answer is firmer,
   not softer: **atlas/trace candidates built from small probe sets do not beat shipped**, and the
   binding constraint is *coverage*, not provenance. Fixing the model mismatch (t0012's original
   hypothesis) was necessary but not sufficient — the capture is on-model now and the candidate still
   loses by 11 pp. Reaching shipped parity would require far more captured traffic than 31 K tokens
   (note the trace already touches 99.15 % of pairs, so the problem is *frequency ordering accuracy*,
   not coverage). **Not recommended to invest further in small-n profile candidates without a
   substantially larger capture corpus.**

## 8. Caveats

- **Capture-only deviation (owner Q2)**: `--short-read 8192` and `--dump-routing` are present in the
  capture config and **absent from all 9 measurement configs** (diff-verified). The capture is a
  separate session contributing prompts and traces only; it cannot contaminate the measurements.
  Windowed prompt reads are ~3× slower than batched — that cost lands on phase 1 only.
- **Prefix guard met**: 16/16 `reused=0`, `read==prompt_tokens`, no shared preamble (22/22 unique
  first-60 chars). Category balance 15 453 / 15 542 prompt tokens. No traced-token deficit to correct.
- **Unit weights are irrelevant to A** — and in serve mode they are unavoidable. Inside the serve
  block the only routing-trace writers are `win_pool_fn` (bound at `:3243`, called at `:3910` and
  `:4150`) → `drive_pool_split`/`drive_pool_multi`, and `drive_pool_multi` (`:522-543`) writes unit
  weights (`static const float one[64] = {}; // zeros read as unit weights`, `:539-540`). The
  real-weight writer `pool_fn = &drive_pool` (`:2474`) is only consumed at `:4549`/`:4554` — both
  **after** the serve `return 0` (`:4371`), i.e. it is unreachable in serve, same as the summary
  block. The source anticipates this (`:530-533`): *"records carry unit weights: tools/make_profile.py
  ranks pairs by routed frequency, which is the signal that matters"*. Consistent with that,
  `make_profile.read_trace` **reads ids only and skips the weights** (`off += 8*k`), so profile A is
  weight-independent by construction and no weighted ranking was lost.
- **`--no-base`**: A = 24 368 trace-ranked pairs + 208 pairs never routed during the capture, placed by
  interleaved fill. Those 208 sit below every traced pair; the top 2638 that defines the cache is
  entirely trace-derived.
- **Slot confound** (§5): S 2638 / A 2666 / R 2663, deterministic per profile. A and R are favoured
  by +0.9–1.1 %; the reported A deficit is therefore an understatement. S-vs-S replicates are the
  exact 2638 clean pair.
- **Stdout summary block is not a capture problem — it does not exist in serve mode** (§4.1). Corrects
  t0008 §3, which blamed `server.py`'s pipe. Adaptive swap counts remain unobservable either way.
- **`n=3`, one GPU, one model, 16 K ctx, T1–T3 only.** T1–T3 is a deliberate narrowing to the
  profile-sensitive window; it is **not** comparable row-for-row with t0008's 6-turn medians, and it
  cannot speak to `adapt_every=4` adaptation (needs T4+).
- **A-level claims vs median**: A−S at 3-turn median (−2.03 pp) clears the S range (0.30) but sits
  *inside A's own* 3.60 pp spread — treated as *directionally consistent with the T1 result*, not as an
  independent claim.
- **Decode tok/s is not usable** as a discriminator at this n (§6.3).
- The +639 tokens traced beyond the 35 091 actually served (1.82 %) are attributed to spec-decode
  draft/verify rows. Whatever their origin they are counted identically for every routed pair, so they
  cannot bias the ordering; "tokens traced" (35 730) is simply slightly larger than "tokens served".
- **Single GPU / single model / `v0.1.29`**; generalisation untested.

## 9. Evidence inventory

```
docs/evidence/strata-gap/
├── RANKING-POC.md                       t0008 (superseded where §3/§6 overlap)
├── RANKING-POC-2.md                     ← this file (t0012)
├── prompts/
│   ├── rank-t{1..6}.txt                 eval set (held out; measurement used t1..t3)
│   ├── rank-sha256sums.txt
│   ├── train-c{01..08}.txt              capture corpus, coding (1861–1899 tok)
│   ├── train-g{01..08}.txt              capture corpus, general  (1861–1899 tok)
│   ├── train-sha256sums.txt
│   └── train-manifest.json              source path, bytes, tokens, category per prompt
└── ranking-2/
    ├── configs/
    │   ├── rank-capture.json            stock args + --dump-routing + --short-read 8192
    │   └── rank-{s,a,r}{1,2,3}.json     stock args, only exe/--expert-profile/log/env differ
    ├── capture/
    │   ├── capture16.json               16 conversations + totals + by_category + prefix check
    │   ├── capture-engine.log           engine stderr (startup facts, per-request prompt lines)
    │   ├── capture-startup.txt
    │   ├── capture.metrics.json         /metrics?requests=all
    │   ├── capture.h2d.txt / capture.link.tsv / capture-summary.txt
    │   ├── capture-server.out
    │   └── capture-arm.out              full lifecycle transcript
    ├── arms/
    │   ├── {S,A,R}{1,2,3}.convo.json    per-turn client trace
    │   └── {S,A,R}{1,2,3}.rank.json     parsed turns + startup facts + medians
    └── logs/
        ├── {S,A,R}{1,2,3}-arm.out       lifecycle transcript + verdict line
        ├── {S,A,R}{1,2,3}-engine.log     engine stderr (capture source, order-aligned)
        ├── {S,A,R}{1,2,3}-startup.txt
        ├── {S,A,R}{1,2,3}.stdout.log     tee-wrapped engine stdout
        ├── {S,A,R}{1,2,3}.metrics.json   /metrics?requests=all snapshot
        ├── {S,A,R}{1,2,3}.h2d.txt / .link.tsv / .summary.txt / -server.out
        └── run-arms-8.joblog            raw transcript of the 8-arm driver job
```

Not committed (git-ignored scratch, per t0008 convention): `.local/strata-research/*` harness
(`capture16.sh`, `convo16.py`, `convo3060.py --turns`, `parse_ranking.py --expected-turns`,
`arm-sar.sh`, `strata-tee.sh`) and `.local/ranking2/{trace.bin,A.bin,R.bin}` — recorded here by
sha256 and byte size instead (trace 150 923 520 B `699ea0ac…8242`; A `f1f5321a…13c0e`; R
`aa5135e8…fd86f`; all profiles 196 632 B).
