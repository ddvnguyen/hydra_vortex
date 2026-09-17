# Task B — Router-key clustering vs trace subject signal

Source: `ffn_gate_inp.weight`, all 48 layers, from
`/mnt/SSD/qwen3.8-flash-next-apex-mini/*.gguf` (shards 3–6).
Each tensor is `[2560, 512]` GGUF-order = **512 expert keys × 2560 dims, F32**
(verified memmap shape `(512, 2560)`). Offline CPU/numpy only; no GPU.
Script: `/tmp/opencode/router/cluster_router_keys.py` (seed 0).
Numbers: `/tmp/opencode/router/taskB_results.json`.
Trace signal: coding-vs-general full-decode top-42 sets per layer from
`/tmp/opencode/phase0-rank/phase0_rank.json` (`full_counts_top42`), plus
per-layer self-heldout h@42 (`per_layer["42"]`).

Null reference (same 512×2560 shape, Gaussian): cosine std 0.0197, max 0.093,
PC1 0.004, participation ratio 426, spherical-kmeans(k=8) WCSS 444.8.

## 1. Do experts cluster? YES — decisively, on all 48 layers

| metric (per layer) | observed (48-layer mean [range]) | null |
|---|---|---|
| raw cosine mean | +0.349 [0.235 … 0.494] | 0 |
| raw cosine std | 0.085 (4.3× null) | 0.0197 |
| centered cosine std | 0.103 (5.2× null) | 0.0197 |
| max pairwise cosine | 0.87 … 0.99 (near-duplicate experts exist every layer) | 0.09 |
| frac pairs cos > 0.5 | 0.080 | ~0 |
| top-PC explained variance (centered) | 0.054 [0.038 … 0.079] | 0.004 (13×) |
| participation ratio | 81 [53 … 105] | 426 (5× lower dim) |
| spherical k=8 WCSS / null | 0.71 | 1.0 |
| silhouette (k=8, centered) | +0.097 [+0.067 … +0.166], positive all 48 | ~0 |
| key-norm CV | 0.17; max/min up to 5.5× | — |

Two structural facts, not one: (a) a large **common direction** (raw mean
+0.35, all-positive pairs, min raw cos ≥ 0.02) — the router keys live in a
narrow cone, plausibly tracking the mean-hidden-state direction; (b) **real
sub-cluster structure on top of it** — after removing the mean, spread is
still 5× null, k=8 finds imbalanced clusters (sizes 17…112 vs balanced 64)
with positive silhouette on every layer. Strongest structure: layers 15, 16,
31, 32 (silhouette 0.13–0.17, PR 53–66).

## 2. Do clusters align with the subject signal? YES — modestly but pervasively

Test per layer: mean centered-cosine within coding-top42 / within
general-top42 vs between the two sets (`within − between` = dC),
permutation test (2000 shuffles) + k=8 cluster-overlap enrichment
(chi-square, 2000 perms).

- **dC > 0 on 48/48 layers** (median +0.042, range +0.007 … +0.154, in units
  where the pairwise-cosine std is ~0.10 — i.e. up to ~1.5σ, typically ~0.4σ).
- **p < 0.05 on 47/48** layers (43/48 at p < 0.001); only layer 47 misses
  (p = 0.10). Raw (uncentered) space agrees: 48/48 dC > 0.
- Cluster enrichment significant **48/48** (all p < 0.0005): coding-heavy and
  general-heavy experts occupy different k=8 clusters far beyond chance.
- Overlap caveat cuts *against* us: the two top-42 sets share ~13 experts
  (Jaccard median 0.175, REPORT.md), which attenuates dC — the true
  separation is, if anything, larger than measured.
- Cross-check: **dC correlates +0.72 with per-layer pinnability h@42**
  (both domains). The layers where coding/general separate most cleanly in
  key space (15, 16, 31, 32, 39 — dC 0.10–0.15) are exactly the most
  pinnable layers (h@42 0.6–0.8). Cluster strength itself (silhouette)
  correlates only weakly with h@42 (+0.28/+0.36): *alignment*, not raw
  clusterability, predicts pinnability.

## Verdict

- **Experts cluster: CONFIRMED.** Every layer shows 4–13×-null structure by
  every metric (spectrum, k-means, silhouette, near-duplicate pairs).
- **Clusters align with the trace-derived subject signal: CONFIRMED, modest
  effect.** Within-subject expert pairs are systematically more similar than
  cross-subject pairs on all 48 layers (47 significant), coding/general sets
  enrich different clusters on all 48, and the separation magnitude predicts
  pinnability (r = +0.72).
- Practical reading: router geometry carries a real but diffuse subject
  axis — consistent with Phase-0's finding that static domain rankings beat
  chance yet transfer poorly (cross-domain h@42 only ~6pp over chance).
  Geometry alone will not yield clean subject-expert partitions; it is a
  prior, not a pin set.
