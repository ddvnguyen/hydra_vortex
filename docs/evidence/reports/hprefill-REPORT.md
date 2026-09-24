# h_prefill: does PREFILL predict DECODE?

Windows (same runs as phase0-rank): prefill=[0,P), static-train=decode[0,50), eval=decode[100,200).
h = micro-averaged fraction of eval-decode route selections in the pin set. Oracle = in-sample eval-window top-N ceiling.

| run (P) | pins | h@20 | h@30 | h@42 |
|---|---|---|---|---|
| coding (P=79) | static(decode-train) | 28.54% | 34.35% | 40.62% |
| coding (P=79) | prefill | 28.97% | 36.22% | 42.33% |
| coding (P=79) | oracle(eval) | 50.08% | 59.87% | 68.56% |
| general (P=8) | static(decode-train) | 27.52% | 35.06% | 42.07% |
| general (P=8) | prefill | 10.08% | 16.54% | 26.51% |
| general (P=8) | oracle(eval) | 51.83% | 61.55% | 70.04% |

Chance (uniform): 20/512=3.91%, 30/512=5.86%, 42/512=8.20%.

## h_prefill - h_static (pp; negative = prefill worse than static table)

| run | d@20 | d@30 | d@42 |
|---|---|---|---|
| coding | +0.43pp | +1.87pp | +1.71pp |
| general | -17.44pp | -18.52pp | -15.56pp |

## Headroom check (oracle - static, pp)

| run | g@20 | g@30 | g@42 |
|---|---|---|---|
| coding | +21.54pp | +25.52pp | +27.94pp |
| general | +24.30pp | +26.49pp | +27.97pp |

## Prefill-vs-decodeTrain top-42 Jaccard (per layer)

- coding: median 0.302, min 0.183, max 0.527, frac>0.5 1/48
- general: median 0.302, min 0.153, max 0.456, frac>0.5 0/48

## Per-layer detail (eval window, N=42)
- coding: static med 40.2% (14.8-81.3); prefill med 36.4%; per-layer delta med +2.45pp, prefill wins on only 29/48 layers — a wash.
- general: static med 38.6% (24.5-78.9); prefill med 25.3%; delta med -15.60pp, prefill wins on 1/48 layers — strictly worse.
- Caveat: general prefill window is only P=8 tokens (vs 79 coding), so its prefill top-N is a noisier estimate; but the direction (never better) is unanimous across layers.

## Verdict
h_prefill - h_static ~= 0 on the well-sampled coding run (+0.43/+1.87/+1.71pp at N=20/30/42, vs +21-28pp oracle headroom) and strongly negative on general (-15 to -19pp). Prefill routing adds no usable signal over the static decode table: **Phase 1b killed, ship 1a only**.

Method: /tmp/opencode/hprefill/hprefill.py (CPU-only replay of existing traces; zero GPU). Numbers: /tmp/opencode/hprefill/hprefill.json. Static column reproduces /tmp/opencode/phase0-rank/REPORT.md self-heldout exactly.

## Band split at N=42 (h_prefill − h_static, pp)

| run | early (0-15) | mid (16-31) | late (32-47) |
|---|---|---|---|
| coding | static 38.1 / prefill 39.7, **+1.54** (med +1.65, wins 10/16) | static 41.8 / prefill 39.4, **−2.35** (med −3.35, wins 6/16) | static 42.0 / prefill 47.9, **+5.94** (med +6.25, wins 13/16) |
| general | static 38.0 / prefill 26.8, **−11.21** (wins 1/16) | static 43.9 / prefill 27.3, **−16.66** (wins 0/16) | static 44.2 / prefill 25.4, **−18.82** (wins 0/16) |

## Interpretation
Neither template fits. Not token-frequency (that predicts early-concentrated gains replicating across runs — early is a wash on coding and −11pp on general). Not a clean semantic prior either (that predicts mid-concentrated gains — mid is negative on both runs). The only positive signal anywhere is coding-late (+5.9pp, 13/16 layers), i.e. prompt content carrying into generation in late layers of one run — but it anti-replicates: general-late is where prefill is *worst* (−18.8pp, 0/16). A deployable prior must replicate across runs; this one flips sign per band. And even taken at face value, +6pp on one third of layers << +28pp oracle headroom. **Phase-2-refresh not revived; Phase 1b stays dead, ship 1a only.**

## Length scatter: h_prefill − h_static vs prefill window size k (N=42)

Each point = one prefix-truncated prefill window [0,k) from a single run, pins=top-42 on that window, eval=fixed decode[100,200), baseline=same-run static decode-train top-42. NOT independent prompts — one run per corpus, truncated post hoc. (length_curve.json has N=20/30 too.)

| k | coding d42 | general d42 |
|---|---|---|
| 2 | — | −39.04pp |
| 4 | −36.31pp | −35.97pp |
| 8 | −20.83pp | −15.56pp |
| 16 | −15.81pp | — |
| 32 | −5.34pp | — |
| 48 | −0.16pp | — |
| 64 | +2.77pp | — |
| 79 | +1.71pp | — |

Matched-length check (k=8): coding −20.8pp vs general −15.6pp — the same collapse, and coding is if anything worse. The full-window gap (coding +1.7 vs general −15.6) sits on this curve, not off it.
Breakeven on coding ≈ k 48–64; below k≈32 prefill top-N loses to static by 5–36pp.

## Relationship and limits
Relation: strongly length-driven. Loss magnitude is a monotone-ish function of k within each run, and cross-run points at matched k agree within ~5pp while same-run points across k span ~38pp. Domain per se contributes ~nothing detectable at matched length.
Limits: (1) two prompts total — the cross-run leg is one matched pair (k=8), not a population; (2) points are prefix truncations, not genuine short prompts — early-position tokens (BOS/system region) may differ in kind from a real 8-token prompt; (3) small-k top-N is a sparse sample (k positions × 10 experts vs 512 experts/layer), so part of the curve is pure sampling noise, not routing structure; (4) breakeven k≈50 is descriptive of these runs, not a constant. Per the instruction, this does NOT read as "prefill fails on general" — at equal evidence mass both corpora behave the same; general's run just ships 8 prefill tokens against a ~50-token breakeven.

## N=36 re-emission for the 64K operating point (n=1 PROVISIONAL)
Same self-heldout windows as phase0-rank (train=decode[0,50), eval=decode[100,200)); exact CPU replay of existing traces, zero GPU. S(h)=1/(1−0.57·h).

| run | h@30 | h@36 | h@42 | S(h@36) |
|---|---|---|---|---|
| coding | 0.3435 | **0.3760** | 0.4062 | **1.2728** (~+27.3% decode) |
| general | 0.3506 | **0.3885** | 0.4207 | **1.2845** (~+28.5% decode) |

Cross-check: linear interpolation h30→h42 at N=36 gives 0.3749/0.3857 (S 1.2717/1.2818) — within 0.003 of exact; curve is near-linear in this range. h30/h42 reproduce phase0_rank.json exactly. N=42 retained for ≤32K only; 1a ships N=36 at 64K. Numbers: /tmp/opencode/hprefill/n36_64k.json.

## N=38 re-emission — SUPERSEDES N=36 (owner ruling; 523/441 MiB margins, n=1 PROVISIONAL)
Same windows/method as above; exact CPU replay, zero GPU. S(h)=1/(1−0.57·h).

| run | h@38 | S(h@38) |
|---|---|---|
| coding | **0.3869** | **1.2829** (~+28.3% decode) |
| general | **0.3978** | **1.2933** (~+29.3% decode) |

Numbers: /tmp/opencode/hprefill/n38_64k.json.
