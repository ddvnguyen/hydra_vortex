# S5b allpool convergence splits (N=38, k=10, chance 7.42%)

CPU-only replay, no GPU, no fork changes, no repo writes. Reuses
`/tmp/opencode/s5_allpool_stability.py` (corpus, pooling, k=10 asserted on
every load). Train = decode[0,50) pooled; eval = decode[100,200).
Bootstrap = turn-level cluster resampling within the eval half,
2000 iters (seeds 12345 primary / 999 full-relative).

S5 reference: halves A/B Jaccard median 0.671; raw o-o-s deltas
dAB = AoB−BoB = +0.91pp, dBA = BoA−AoA = −2.02pp (latter CI excludes 0);
full-pool-on-A 0.2840 vs on-B 0.2935 (0.95pp noise-floor ref).
Result is a MONITOR item only — does not gate Stage-1
(−2.01pp ≈ −1.3% throughput scale).

## Splits (all disjoint by session/prompt; whole v4 sessions kept together;
## never by token; all differ from S5 A/B — see partition distances in
## s5b_results.json, 28–65% of S5-half membership flipped per split)

| split | design | nA / nB turns |
|---|---|---|
| SUBJ | subject-stratified balanced: singles block-2 alternation per domain by ID; v4 A={S1,S2}/B={S3,S4} per subject | 26 / 25 |
| LENbal | length-balanced: 33 units (21 singles by prefill P + 12 sessions by T1 P) ranked; alternated within short tier (16 singles, P≤1124) and long tier (5 singles + 12 sessions) | 24 / 27 |
| LENext | length-extreme composition probe: A = 16 short-tier singles; B = 5 long-tier singles + all 12 v4 sessions | 16 / 35 |
| RAND | seeded random re-split (seed 7) of 33 units, 17 A / 16 B | 28 / 23 |

## top-38 Jaccard, half-A vs half-B (48 layers)

| split | median | mean | min | max | frac>0.5 |
|---|---|---|---|---|---|
| SUBJ | 0.689 | 0.682 | 0.520 | 0.810 | 48/48 |
| LENbal | 0.727 | 0.711 | 0.551 | 0.900 | 48/48 |
| LENext | 0.462 | 0.455 | 0.310 | 0.652 | 12/48 |
| RAND | 0.689 | 0.665 | 0.490 | 0.810 | 46/48 |

Balanced/stratified/random splits reproduce S5-scale overlap (0.69–0.73
median). Only the length-extreme split collapses (0.462) — ranking
structure is length-composition sensitive.

## out-of-sample h deltas with bootstrap 95% CIs (pp)

dAB = h(A-pins on B) − h(B-pins on B); dBA = h(B-pins on A) − h(A-pins on A).
Full-relative variants subtract full-pool pins on the SAME eval half,
removing eval-half difficulty confounds.

| split | dAB obs + CI | dBA obs + CI | dAB_full + CI | dBA_full + CI |
|---|---|---|---|---|
| SUBJ | −1.19 [−2.24,−0.26] | −0.48 [−1.39,+0.45] | −1.05 [−1.60,−0.50] | −0.64 [−1.10,−0.20] |
| LENbal | −1.24 [−1.79,−0.71] | −0.24 [−0.68,+0.19] | −0.81 [−1.01,−0.64] | −0.55 [−0.82,−0.30] |
| LENext | −5.18 [−6.79,−3.59] | −1.00 [−3.27,+1.20] | −4.53 [−5.60,−3.37] | −1.02 [−1.68,−0.38] |
| RAND | +0.10 [−0.99,+1.25] | −1.50 [−2.47,−0.54] | −0.44 [−1.02,+0.12] | −1.11 [−1.63,−0.58] |

Raw h (full-budget micro recall): SUBJ AoB 0.2935 BoB 0.3054 BoA 0.2675
AoA 0.2724 FoA 0.2739 FoB 0.3040; LENbal AoB 0.2671 BoB 0.2795 BoA 0.2980
AoA 0.3004 FoA 0.3035 FoB 0.2755; LENext AoB 0.2472 BoB 0.2990 BoA 0.2694
AoA 0.2794 FoA 0.2797 FoB 0.2928; RAND AoB 0.2802 BoB 0.2792 BoA 0.2802
AoA 0.2951 FoA 0.2918 FoB 0.2849.

## Reading

1. Composition effect ISOLATED (leader hypothesis confirmed): LENext shows
   short-tier-trained pins fail on long-tier eval (−4.53pp full-relative,
   CI far from 0) while long-trained pins transfer to short eval within
   noise (−1.02pp). S5-style asymmetry reappears in every split in raw
   form, always in the direction of the harder eval half.
2. Eval-half difficulty confounds raw deltas: full-pool pins themselves
   vary across halves by up to 3.0pp within a split (SUBJ FoA 0.2739 vs
   FoB 0.3040; LENbal 2.8pp; S5 ref only 0.95pp). Full-relative deltas are
   markedly more symmetric: SUBJ −1.05/−0.64, LENbal −0.81/−0.55,
   RAND −0.44/−1.11 — all within ~0.5pp of each other, at the ~1pp floor.
3. Convergence bar (symmetric deltas within ~1pp): MET in full-relative
   terms for SUBJ/LENbal/RAND; raw deltas each keep one asymmetric tail
   (−1.2 to −1.5pp, CI excluding 0) because with ~1.1M eval slots the CIs
   are tight (±0.5–1pp) — "excludes 0" is expected for any systematic
   sub-pp effect and is not practical significance. LENext fails by design
   (it is a stress probe, not a convergence split).
4. Throughput scale: worst balanced-split transfer penalty ≈ −1.2pp h
   ≈ −0.8% routed-slot recall scale — monitor only, no Stage-1 impact.

## Artifacts

- `s5b_splits.py` — split definitions + primary metrics + bootstrap
- `s5b_fullrel.py` — full-pool-relative deltas + bootstrap
- `s5b_results.json` — per-split Jaccard stats, h arms, deltas+CIs, half
  memberships, partition distances from S5 A/B
- `s5b_fullrel.json` — full-relative deltas+CIs
- `REPORT.md` — this file

k=10 asserted on every trace load (inherited). No implementation changes
(replay only). Limitation: bootstrap resamples turns, not whole sessions;
session-level resampling would widen CIs (T2 re-reads T1 verbatim).

## Production-direction leg (LONG-ON-SHORT; S5b follow-up)

Production applies mixed/long-trained allpool to short single-prompt turns
(S1 regime). Restated LENext long->short leg (THE production leg):
dBA_full = −1.02pp, 95% CI [−1.68,−0.38] (2000 iters).

New PROD split keyed on production regime (same estimator/bootstrap):
LONG = 30 v4 multi-turn turns (long-prefill train); SHORT = 21 singles
(production proxy eval). LONG-vs-SHORT pin Jaccard median 0.434
(composition-extreme, cf. LENext 0.462).

| eval | h_LONG | h_SHORT_oracle | h_FULL | LONG−FULL (CI) | FULL−SHORT_oracle (CI) |
|---|---|---|---|---|---|
| SHORT std [100,200) | 0.2581 | 0.2655 | 0.2687 | −1.06 [−1.71,−0.40] | +0.32 [−1.04,+1.63] |
| SHORT trunc [50,150) | 0.2745 | 0.2860 | 0.2877 | −1.32 [−1.97,−0.60] | +0.17 [−1.26,+1.65] |
| LONG reverse [100,200) | 0.2513 (short-pins) | 0.3133 (long-oracle) | 0.3024 | short−full −5.11 [−6.14,−4.07] | full−long-oracle −1.09 [−1.61,−0.55] |

Directionality confirmed: short-pins on long eval lose −5.1pp vs full,
while long-pins on short eval lose only −1.0pp vs full — and the shipped
mixed FULL matches the short oracle on short eval (+0.3pp, CI includes 0;
truncated-eval sensitivity agrees at +0.2pp). The mixed corpus protects
short-eval performance vs pure-long training (+1.06pp).

**Production penalty of shipping current allpool = +0.3pp (95% CI
[−1.0,+1.6pp]) vs a short-specialized oracle — i.e. ~zero, point estimate
in full's favor. Recommendation: MONITOR only, no gate; no short-specific
pin set warranted.**

Artifacts added: `s5b_prod.py`, `s5b_prod.json`.
