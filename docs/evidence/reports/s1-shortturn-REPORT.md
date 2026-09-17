# S1 short-turn robustness (N=38, k=10 frozen, n=18 windows)

Acceptance: static + warm_session(500) reproduce scorer_v4.json exactly
(18 windows x primary+extended) — PASS (enforced at startup, exit otherwise).
Method: /tmp/opencode/s1-shortturn/s1_shortturn.py (CPU-only replay of banked
v4 traces; zero GPU, no fork/rig changes). Numbers: s1_shortturn.json.

Design: each completed prior turn contributes only its first L decode tokens
to the warm prior; eval is the FIXED held-out span (primary [100,200),
extended [200,500)), identical tokens for every L -> paired deltas valid.
T2 prior(L) = T1[0,L) + T2[0,min(L,100)); T3 prior(L) = T1[0,L) + T2[0,L)
(D3 PRIMARY). warm_seeded = top38(allpool_T1[0,50) + session_prior(L));
warm_session = top38(session_prior(L)) control. Matched-50 per D2
(20 draws, seed 20260917, arm_idx 30/31/32). Eval slots: 48,000/pos-window
primary (100x48x10), 144,000 extended.

## PRIMARY: cross-turn warm vs prior length L (fixed held-out eval)

| L | warm_seeded primary | warm_session primary | warm_seeded extended | warm_session extended |
|---|---|---|---|---|
| 50 | 0.3089 (n=18, spr=0.3589) | 0.3487 (n=18, spr=0.3799) | 0.2383 (n=18, spr=0.2303) | 0.2886 (n=18, spr=0.3046) |
| 100 | 0.3381 (n=18, spr=0.3691) | 0.3856 (n=18, spr=0.3625) | 0.2706 (n=18, spr=0.2639) | 0.3339 (n=18, spr=0.3706) |
| 200 | 0.3599 (n=18, spr=0.3343) | 0.4067 (n=18, spr=0.2790) | 0.2930 (n=18, spr=0.2503) | 0.3515 (n=18, spr=0.2917) |
| 500 | 0.3885 (n=18, spr=0.1199) | 0.4009 (n=18, spr=0.1643) | 0.3539 (n=18, spr=0.1548) | 0.3969 (n=18, spr=0.1640) |

Reference (scorer_v4.json, N=38 primary full-budget, n=18):
static 0.3126 (spr 0.4026) | warm 0.4009 | offline 0.3051 | global 0.2504 |
allpool 0.2851 | oracle 0.5729.

Matched-50 (D2, 20 draws seed 20260917, arms 30/31 — same L-shape, so the
length effect is knowledge, not sample-size masquerade):

| L | warm_seeded/primary/m50 | warm_session/primary/m50 |
|---|---|---|
| 50 | 0.2877 (n=18) | 0.3395 (n=18) |
| 100 | 0.3080 (n=18) | 0.3642 (n=18) |
| 200 | 0.3260 (n=18) | 0.3785 (n=18) |
| 500 | 0.3456 (n=18) | 0.3682 (n=18) |

## Per-subject means (primary, n=6 each)

| L | warm_session coding | reasoning | essay | warm_seeded coding | reasoning | essay |
|---|---|---|---|---|---|---|
| 50 | 0.3560 | 0.3452 | 0.3450 | 0.3255 | 0.2805 | 0.3207 |
| 100 | 0.3899 | 0.3630 | 0.4040 | 0.3515 | 0.3118 | 0.3509 |
| 200 | 0.4030 | 0.3905 | 0.4267 | 0.3657 | 0.3397 | 0.3743 |
| 500 | 0.4017 | 0.3867 | 0.4143 | 0.3838 | 0.3790 | 0.4028 |

The L-shape holds in all three subjects. Reasoning seeded at L=50 (0.2805)
is below the allpool mean (0.2851): seeding actively hurts short-turn
reasoning, not just dilutes.

## T2 (has current-turn-so-far evidence) vs T3 (pooled only) at equal mass

| L | warm_session T2 (n=12) | T3 (n=6) | warm_seeded T2 | T3 |
|---|---|---|---|---|
| 50 | 0.3695 | 0.3073 | 0.3306 | 0.2655 |
| 100 | 0.4121 | 0.3327 | 0.3615 | 0.2913 |
| 200 | 0.4278 | 0.3646 | 0.3801 | 0.3196 |
| 500 | 0.4019 | 0.3988 | 0.3945 | 0.3765 |

At L<=200, T2 beats T3 by +6pp at EQUAL evidence mass (L=50: both 100
tokens) — current-turn-so-far tokens are the highest-value evidence.
At L=500, T3 with 1000 pooled tokens only ties T2 with 600: accumulation
beyond ~300-600 tokens adds nothing.

## Throughput framing S(h)=1/(1-0.57h), primary means

static +21.7% | allpool +19.4% | warm_session: L=50 +24.8%, L=100 +28.2%,
L=200 +30.2%, L=500 +29.6% | warm_seeded L=50 +21.4%, L=500 +28.4%.
Full warm-minus-static gap = +8.8pp h (+7.9pp throughput). Retained at
L=50: 41% of h-gap (+3.1pp throughput, 18/18 windows positive vs static);
L=100: 83% (+6.5pp); L=200: ~100%.

## SECONDARY diagnostic: within-turn self-heldout (mixed eval spans — NOT comparable across L)

| L | selfheld h | eval span |
|---|---|---|
| 50 | 0.3453 (n=18) | [100,200) |
| 100 | 0.3959 (n=18) | [100,200) |
| 200 | 0.3969 (n=18) | [200,500) |
| 500 | n/a | n/a |

Within-turn estimation saturates by ~100 tokens — same conclusion as the
cross-turn primary via an independent estimator.

## Worst case

warm_session primary worst window: L=50 reasoning_S1_T3 0.1148 (static there
0.0883, full-warm 0.4640); L=500 reasoning_S1_T2 0.2997. Short priors fail
hardest exactly where warm matters most (static-collapse windows) — another
vote for accumulating before refitting rather than trusting lone short turns.

## Recommendation: per-ACCUMULATED-N update, N~200-300 session tokens

Refit pins when ~200-300 session tokens have accumulated (pool the current
short turn with prior short turns), ALWAYS including current-turn-so-far
tokens. Do not refit on a lone 50-token turn (leaves ~60% of warm value on
the table; never harmful — 18/18 wins over static — just underpowered), and
do NOT allpool-seed the update (dilutes session signal -1 to -4pp at every
L; restrict any seed to turn-1 cold start only). The warm gain does NOT
evaporate at production lengths: it degrades gracefully and saturates early
(L=200 ~= L=500), so the operating point is accumulation threshold, not
turn boundary.

## Spec/code discrepancy (flagged, not acted on)

V4_SET_TABLE H5 text says the T2 extended-eval prior is frozen at 200
(includes eval-primary [100,200)); scorer_v4.py freezes it at 100 for both
spans. This study follows the code artifact (acceptance reproduces
scorer_v4.json exactly). Owner call whether H5 text or code is canonical.
