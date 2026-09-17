# S5 allpool stability: disjoint-half rebuild (N=38, k=10, chance 7.42%)

Split: disjoint by session/prompt, never by token within a window. Half A: 26 turns (2600 eval positions); half B: 25 turns (2500 eval positions). v4 sessions kept whole (A={S1,S4}, B={S2,S3} per subject); single prompts alternated per domain.

## top-38 Jaccard, half-A vs half-B (per layer, 48 layers)

median 0.671, mean 0.679, min 0.520, max 0.810, frac>0.5 48/48.

## out-of-sample h (full-budget micro-averaged routed-slot recall, eval=decode[100,200))

| arm | h | estimator | eval sample |
|---|---|---|---|
| A-pins on B (out-of-sample) | 0.2942 | full-budget | 25 turns x100 pos x48 layers x10 = 1200000 slots |
| B-pins on A (out-of-sample) | 0.2698 | full-budget | 26 turns x100 pos x48 layers x10 = 1248000 slots |
| A-pins on A (in-sample ref) | 0.2900 | full-budget | 1248000 slots |
| B-pins on B (in-sample ref) | 0.2851 | full-budget | 1200000 slots |
| full-pool on all (in-sample ref) | 0.2887 | full-budget | 2448000 slots |
| chance | 0.0742 | uniform 38/512 | — |

## context: Jaccard vs shipped pins-allpool-N38.txt

| half | median | min | max | frac>0.5 |
|---|---|---|---|---|
| A vs ship | 0.652 | 0.490 | 0.854 | 47/48 |
| B vs ship | 0.689 | 0.520 | 0.810 | 48/48 |
| full-pool vs ship | 0.727 | 0.520 | 0.854 | — (ship=train-recipe check) |

## Sensitivity: T1-only train pools (exact ship-recipe replication)

Train = T1-equivalent turns only (v4 T1 + all 21 singles): A=17 turns,
B=16 turns, decode[0,50) pooled; eval = all held-out turns decode[100,200).

- top-38 Jaccard A-vs-B: median 0.617, mean 0.637, min 0.520, max 0.767,
  frac>0.5 48/48.
- h (full-budget): A-pins on B-eval 0.2806 (25 turns, 1200000 slots);
  B-pins on A-eval 0.2550 (26 turns, 1248000 slots); in-sample refs
  A-on-At 0.2813, B-on-Bt 0.2648. Out-of-sample deltas within ~1.5pp.
- Per-layer out-minus-in (main pools): on B median +0.71pp (range
  -0.70..+4.14pp); on A median -1.65pp (range -7.01..+0.72pp).

k=10 asserted on every trace load. No implementation changes (replay only).
