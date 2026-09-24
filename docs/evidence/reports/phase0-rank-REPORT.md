# Phase-0 ranking comparison: domain (coding) vs general

## h-vs-N (self-heldout; train=decode[0,50), eval=decode[100,200), disjoint)

| eval domain | h@20 | h@30 | h@42 |
|---|---|---|---|
| coding | 28.54% | 34.35% | 40.62% |
| general | 27.52% | 35.06% | 42.07% |

## Cross-domain transfer (prompt-disjoint, full-decode train -> full-decode eval)

| train -> eval | h@20 | h@30 | h@42 |
|---|---|---|---|
| coding->general | 5.28% | 9.52% | 14.41% |
| general->coding | 5.71% | 9.46% | 14.01% |

Chance (uniform) reference: 20/512=3.91%, 30/512=5.86%, 42/512=8.20%.

## Per-layer skew (self-heldout h@42)
### coding: min 14.80%, p10 16.10%, median 40.20%, p90 68.90%, max 81.30%
- early layers 0-15 mean 38.11%, mid 16-31 41.79%, late 32-47 41.96%
- layers >= 50%: 14/48; >= 70%: 4/48
### general: min 24.50%, p10 28.20%, median 38.55%, p90 60.90%, max 78.90%
- early layers 0-15 mean 38.05%, mid 16-31 43.94%, late 32-47 44.23%
- layers >= 50%: 12/48; >= 70%: 3/48

## Uniform-N vs per-layer-N (same budget B=48*N, adaptive fit on TRAIN only)

| domain | N | uniform h | adaptive h | delta |
|---|---|---|---|---|
| coding | 20 | 28.54% | 29.40% | +0.86% |
| coding | 30 | 34.35% | 35.13% | +0.78% |
| coding | 42 | 40.62% | 41.42% | +0.80% |
| general | 20 | 27.52% | 28.72% | +1.19% |
| general | 30 | 35.06% | 35.40% | +0.33% |
| general | 42 | 42.07% | 42.83% | +0.76% |

## Train top-42 Jaccard coding-vs-general (per layer)
median 0.175, min 0.105, max 0.355, frac>0.5 0/48


## Verdicts

1. Domain vs general: rankings do NOT transfer. Self-heldout h@42 is
   40.62% (coding) / 42.07% (general); cross-domain h@42 is 14.41% /
   14.01% — only ~6pp above the 8.20% chance line. Train top-42 Jaccard
   median 0.175, max 0.355, 0/48 layers above 0.5. A general ranking is
   not a substitute for a domain ranking (nor vice versa): per-workload
   tiers required.
2. Skew is concentrated in specific layers, NOT in early-vs-late thirds.
   At N=42, third means differ by ~4pp (coding 38.1/41.8/42.0%, general
   38.1/43.9/44.2%) while within-third SD is 11-19pp and the layer range
   spans 15-81% (coding) / 25-79% (general). In-sample pins for 50%
   coverage range 10-48 (coding) / 11-55 (general) per layer. Only
   14/48 (coding) / 12/48 (general) layers reach h>=50%; 4/3 reach 70%.
3. Per-layer N beats uniform N at fixed budget (B=48*N), but only by
   +0.33 to +1.19pp (adaptive fit on TRAIN only, scored on EVAL). Static
   reallocation is directionally right yet small — the skew pattern
   drifts between windows, so the larger lever is adapting priors at
   runtime (consistent with the earlier T0.5 +16pp adapting-wins result),
   not a better static split.
4. Absolute levels (self-heldout h@42 ~41%) confirm the prior gate: static
   pinning cannot approach 70% at practical N on either workload.

Method: /tmp/opencode/phase0-rank/phase0_rank.py (CPU-only replay).
Numbers: phase0_rank.json. Split: CORPUS_SPLIT.md.
