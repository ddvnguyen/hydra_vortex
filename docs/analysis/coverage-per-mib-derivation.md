# Coverage-per-MiB: expert pinning vs layer placement (§37 re-derivation)

> **SUPERSEDED — v1 below contains a verdict-flipping unit error (§38).**
> v1's §3 L1 "6 experts/layer" is the count of expert *tensor names* per
> layer (the 48×6=288 override count). The live model routes
> **n_expert = 512 per layer** (runtime banner, armB2p3r-D22.server.log:
> `arch = qwen4exp, n_layer = 48, n_expert = 512, n_expert_used = 10`).
> v1 is preserved verbatim for the audit trail; the operative derivation
> is **§38 v2** at the end, which reverses the branch read. The v1 method
> caught its own error (crossing-assumption report + 42/512 line).

Status: derivation for architect validation → owner cost decision. **Zero rig.**
Nothing here was fitted to reach a branch. All inputs measured/banked.
Precision beats optimism: this may retire the expert-ranking line.

## 0. Registered branches (pre-decided; this doc only supplies the honest number)

- Ratio ≥ 3× (pinning vs placement) → build the hot/cold slab (#132)
- Ratio 1.5–3× → marginal; owner cost decision
- Ratio < 1.5× → ranking dominated by plain `-ot` layer placement; line retired

The old 5.7× claim is dead (uniform 916.7 MiB/layer constant retracted, §31).
The architect's own re-estimate lands ~1.15×. This derivation is independent;
agreement or disagreement is reported raw, not harmonised.

## 1. Inputs (used as given, not re-derived)

1. **Per-layer expert bytes (B2' loader-pass, measured):** blk.0–3 ≈ 1058.5
   MiB/layer, blk.4–7 ≈ 937.0, blk.8–21 ≈ 887.0, blk.22–47 host ≈ 887.0
   (inferred: 43,462 − 20,400 = 23,062 over 26 layers = 887.0/layer —
   reconciles exactly). Total expert bytes = **43,462 MiB**
   (4·1058.5 + 4·937 + 40·887 = 4234 + 3748 + 35480 = 43,462 ✓;
   reconciles with D0 host 43,812 within 0.8%).
   288 experts = 6/layer × 48 layers. The uniform 916.7 constant is DEAD
   and used nowhere below.
2. **h ranking:** `/tmp/opencode/phase0-rank/phase0_rank.json`,
   `self_heldout per_layer @42` (the file the layer-31 freeze used).
   coding global h@42 = 0.4062, general = 0.4207; layer 31 = 0.766 / 0.742.
   No per-expert h exists in the file → **method limit** (see §3).
3. **Placement reference:** D22 = 20,400 MiB (9756 C0 + 10644 C1),
   measured 27.7349 tok/s. Identity check (exact):
   layers 0–21 expert bytes = 4·1058.5+4·937+14·887 = 4234+3748+12418
   = **20,400** ✓ — D22 IS whole-expert bytes of layers 0–21. Further,
   CUDA0 9756 = L0–9 (4234+3748+2·887 = 9756 ✓); CUDA1 10644 = L10–21
   (12·887 ✓). Split fully reconciled.
4. **Marginal coefficient:** B2's 0.377 tok/s per CUDA0-layer-equivalent
   (≈975.6 MiB/equiv on CUDA0). Used for the secondary tok/s sanity only.
5. **D0 (all-host) excluded** per §32 — not used as an anchor anywhere.
6. **Banked mechanism facts respected:** one turn touches ~54% of experts
   per layer (cancels in ratios, §2); same-subject pools saturate at N=54;
   h@38 ≈ 0.39; per-layer h varies (layer 31 vs ~0.39–0.42 background).

## 2. Coverage-point definition (identical both sides)

```
Cov(S) = Σ_l  h_l · (k_l / 6)          [h-weighted layer-equivalents resident]
CovPerMiB(S) = Cov(S) / MiB(S)
Ratio  = CovPerMiB(pinning slab) / CovPerMiB(D22 placement)
```

- `h_l` = phase-0 self_heldout per-layer h@42 (coding primary; general as
  sensitivity). It weights a layer by routing predictability: pins in a
  high-h layer cover actual future routed slots; pins in a low-h layer
  are guesses.
- `k_l/6` = resident expert fraction in layer l (uniform-within-layer
  split — the flagged assumption, §3).
- The 0.54 touch-fraction multiplies both sides equally and cancels.
- Byte heterogeneity is explicit: every MiB sum uses the §1 band table.

## 3. Method limits and assumptions (all inline, as required)

- **L1 (binding): no per-expert h.** Phase-0 N is *per-layer* pins
  (top-N per layer, N∈{20,30,42}) on a model with hundreds of
  experts/layer (chance line 42/512 = 8.2%). The current model has
  6/layer. Mapping per-layer h onto 6-expert layers via uniformity is a
  regime transfer across both pin fraction (8% → k/6) and model. The
  ratio self-cancels the *level* of h (same h_l both sides) but NOT the
  *selection* (which layers get pinned). Caveat stands on every number.
- **L2 (binding): h_l measured at one operating point** (N=42/layer,
  short single-turn traces) applied to multi-turn slab sizing.
- **L3:** slab membership for point A uses the h≈0.42 value threshold
  (≈ global h@42 = 0.406/0.421); threshold 0.40/0.45 moves layers
  in/out — checked: the ratio moves <0.06, no branch crossing either way
  at 0.40 (coding 1.51) — reported, not hidden.
- **L4:** freed-MiB→tok/s conversion at the CUDA0 marginal rate is
  optimistic (freed bytes scatter across bands/GPUs).

## 4. Placement side (reference)

S_place = all 6 experts × layers 0–21 (the D22 set, §1.3).
Cov_place = Σ_{l=0..21} h_l = **8.499** (coding; general also 8.499 —
verified exact float sum, coincidence recorded as found).
CovPerMiB_place = 8.499 / 20,400 = 4.1662e-4 per MiB.

## 5. Pinning side, point A — architect slab (layers with h ≥ 0.42, whole)

Coding: 21 layers [3,4,5,9,11,14,15,16,17,20,23,25,27,31,32,39,40,43,44,45,46].
Cov_A = 12.086, MiB_A = 18,898.5 (frees 1,501.5 MiB vs D22).
**R_A = (12.086/18898.5)/(8.499/20400) = 1.535** (marginal, edge).

Sensitivity (same definition, general h for selection+weight):
20 layers, Cov = 10.979, MiB = 17,961.5 → **R_A = 1.467 (retire band)**.
Subject choice alone crosses the 1.5 line — reported, both stand.

## 6. Pinning side, point B — D22-budget fill (expert granularity)

Rank layers by h desc, take whole layers, marginal layer partial to fill
exactly 20,400 MiB (experts are the granularity; ties → cheaper bytes —
no ties occurred).
Coding: 22 full layers + layer 26 ×4 experts; Cov = 12.782, MiB = 20,376.8
→ **R_B = 1.506** (marginal, razor). General: 22 + layer 14 ×4;
Cov = 12.049 → **R_B = 1.423 (retire band)**.
Note R_B < R_A both subjects: extra budget buys lower-h mass
(diminishing returns, as expected).

## 7. Definitional sensitivity (required: assumption crosses branches)

The §2 definition h-weights fully-pinned layers. Physically, a fully
resident layer serves 100% of its routed slots regardless of h_l.
Unweighted rerun (layer-equivs resident per MiB):
- Point A: coding (21/18898.5)/(22/20400) = **1.030**; general **1.033**.
- Point B (analytic): budget buys cheapest-first = 23 × 887-band layers
  (22 full + ~6th-expert sliver: 22.998 equivs) → **1.045**.
All firmly in the retire band. The h-weighting assumption moves the
number from ~1.03 to ~1.5 — across the branch boundary — so both stand
and neither is hidden.

Extra reading sensitivity (expert-ranked slab = top-42 experts ≡ top-7
layers by h, 6 each): coding Cov 5.012 / 6,209 MiB → **1.938**; general
4.692 → **1.814**. Still marginal (< 3) on both subjects.

## 8. tok/s secondary (sanity, not the registered quantity)

Freed_A (coding) 1,501.5 MiB → 1.539 CUDA0-equivs × 0.377 = +0.58 tok/s
→ ≈ 28.3 vs D22 27.73 (ratio ≈ 1.02). Architect's 28.6 (≈1.03) is
convergent; their ~1.15× reads as tok/s-per-MiB, a different ratio from
the registered coverage-per-MiB — the two are not contradictory, and
neither reaches any build threshold. Coverage ≠ throughput is the point:
pinning concentrates *predictable* work without moving much *total* work.

## 9. Numbers table and branch read

| variant | coding | general | branch |
|---|---|---|---|
| R_A h-weighted (slab, h≥0.42) | 1.535 | 1.467 | marginal-edge / RETIRE |
| R_B h-weighted (20.4 GB fill) | 1.506 | 1.423 | marginal-razor / RETIRE |
| R_S1 h-weighted (top-42 experts) | 1.938 | 1.814 | marginal / marginal |
| R unweighted (any point) | 1.030–1.045 | 1.033–1.045 | RETIRE |
| tok/s-per-MiB (secondary) | ~1.02 | — | RETIRE |

**Raw read:** nothing reaches ≥3 under any variant — the line cannot be
built on this evidence. The h-weighted central mass sits 1.42–1.94
(marginal band, straddling the 1.5 line by subject); the unweighted
physical-hit-rate reading sits ~1.03–1.05 (retire). The retired-vs-marginal
call hinges on (i) subject, (ii) h-weighting vs hit-rate, (iii) slab
reading — all reported above. The old 5.7× is refuted by measured bytes
under every variant. Recommendation to the owner: retire, or marginal-hold
at most; do not build #132 on coverage-per-MiB grounds.

## 10. Reproduction

```python
import json
d = json.load(open('/tmp/opencode/phase0-rank/phase0_rank.json'))
hc = d['self_heldout']['coding']['per_layer']['42']
hg = d['self_heldout']['general']['per_layer']['42']
B = [1058.5]*4 + [937.0]*4 + [887.0]*14 + [887.0]*26
place = sum(hc[:22]) / 20400
sel = [l for l in range(48) if hc[l] >= 0.42]
R_A = (sum(hc[l] for l in sel) / sum(B[l] for l in sel)) / place  # 1.535
```

Assumptions ledger: L1 per-layer→6-expert regime transfer (binding);
L2 single operating point (binding); L3 threshold ±0.02 checked, no branch
crossing; L4 optimistic freed-MiB conversion; D0 excluded; 0.54 cancels;
no fitting performed (h(N) extrapolation deliberately NOT used — point B
fills by rank, coverage from measured h_l only).

---

# §38 CORRECTED RE-DERIVATION (v2 — operative; v1 above superseded)

## V2.0 Unit identity (verified against the runtime, not docs)

Runtime load banner (`armB2p3r-D22.server.log`, same APEX shard 1 the traces
were collected on):

```
arch = qwen4exp, n_layer = 48, n_expert = 512, n_expert_used = 10
```

~100 banners agree (the two `n_expert_used = 8` lines are a k=8 ablation
config, `a1-k8-off.log`; the trace regime is 10, matching topk=10 rows).
Corroboration: `.route` expert ids range into the 500s (e.g. 505, 498).
**512 experts/layer, top-10 routed.** v1's "6/layer" counted placement
override tensor names, not routed experts. v1 slab bytes were therefore
charged at whole-layer granularity for what is an 8%-mass pin set.

## V2.1 Corrected algebra (cancellation shown)

Pin N experts/layer of 512 (uniform spread baseline; bounds in V2.2):

- Pin side: Cov_pin = Σ_l h_l = 48·h̄ (measured recall — pins cover h̄ of
  routed mass per layer). MiB_pin = 48 · N · (B̄_pin/512), B̄_pin = mean
  layer-bytes over pinned layers.
- Place side (D22 = whole layers 0–21): Cov_place = **22** (fully resident
  layers serve 100% of slots — the unweighted physical content, §V2.4).
  MiB_place = 22 · B̄_place, B̄_place = 20400/22 = 927.27.

```
Ratio = [48·h̄ / (48·N·B̄_pin/512)] / [22 / (22·B̄_place)]
      = (h̄·512/N) · (B̄_place/B̄_pin)
      = (h̄/f) · (B̄_place/B̄_pin),   f = N/512
```

Everything cancels except the skew ratio h̄/f times a byte residual.
Uniform pin spread: B̄_pin = 43462/48 = 905.46 →
**factor = 927.2727/905.4583 = 1.024092 (≈2.4%)**.
Byte tables and heterogeneity are no longer load-bearing (bounds V2.2).

Branches restated (same 1.5/3.0 thresholds transformed):
**build ⟺ h̄/f ≥ 2.9294; retire ⟺ h̄/f < 1.4647; between = marginal.**

## V2.2 Operating points (raw numbers)

**Point 1 — N=38 (banked h@38 = 0.39).**
Cross-check (not taken on faith): log-fit through phase-0 coding
(20,.2854),(42,.4062) gives b=0.1628 → h(38) = 0.3899 ✓; general gives
0.4011. Banked 0.39 reproduced to 3dp.
f = 38/512 = 0.074219. h̄/f = 0.39/0.074219 = **5.2547**.
**Ratio = 5.2547 × 1.024092 = 5.3813 → BUILD.**

**Point 2 — N=54 (saturation point).**
No direct h@54 in phase-0 (measured N ∈ {20,30,42}).
Interpolated (log-fit, stated): coding h(54) = 0.4471, general 0.4700.
Coding: f = 54/512 = 0.105469. h̄/f = 0.4471/0.105469 = **4.2392**.
**Ratio = 4.2392 × 1.024092 = 4.3413 → BUILD.**
Conservative floor (zero marginal gain 42→54, h = h@42 = 0.4062):
3.8516 × 1.024092 = **3.9442 → BUILD.**
General interpolated: 4.4558 × 1.024092 = **4.5632 → BUILD.**

**Robustness (all BUILD) — strongest statement first:** at N=38 the build
gate needs only **h̄ ≥ 0.2174** (2.9294 × 0.074219). Every measured h
clears it with room — **including h_prefill = 0.238, the ranking declared
dead in §20d**. The build case therefore does not depend on decode-window
quality at all: even the weakest ranking on record clears the gate.
Adversarial byte bound (all pins in the 1058.5 band, factor 0.8761):
point 1 → 4.6037; floor point 2 → 3.3744. Opposite bound (887 band,
1.0454): yet higher. Multi-turn-regime sensitivity (scorer_v4 static
h@38 = 0.3126, a different, harsher regime): 4.2119 × 1.024092 = 4.3133
→ BUILD. No variant is near 2.93 from below; retirement would require
h̄/f < 1.46, i.e. pins barely better than uniform — refuted by every
measurement on record (h@20 alone gives 0.2854/0.0391 = 7.3).

## V2.3 Branch read

Both operating points BUILD with margin (5.38 and 4.34 vs 2.93 gate;
floors and adversarial bounds included). The §37 v1 read (marginal →
retire) is withdrawn as artefactual of the 6/layer unit error. The old
5.7× claim's *magnitude* is incidentally rehabilitated by the corrected
units — but on the corrected basis (skew ratio × byte residual), not on
the retracted uniform constant, which stays dead.

## V2.4 How the unweighted reading coexists with h/f

v1's unweighted 1.03 compared 21 whole pinned layers vs 22 whole placed
layers — a moot comparison: the slab pins *subsets* (42/512 of mass),
never whole layers. The unweighted physical content (a fully resident
layer serves 100% of slots regardless of h_l) is not discarded — it is
the **placement half** of the corrected algebra (Cov_place = 22, §V2.1).
h/f = measured pin recall over pinned mass fraction, set against
placement's 1.0 over full mass. v1's error was whole-layers on *both*
sides (degenerating to layer-counting); the correction keeps unweighted
placement and pairs it with measured-h pinning.

## V2.5 Σh anomaly resolved (coding == general Σh over L0–21 exactly)

Full-precision check: both sums = 8.499000000000, diff 0.00e+00, with
22 differing element pairs (±0.006…0.189) cancelling exactly. Cause
hunt: `phase0_rank.py::hit_rate_per_layer` uses independent
accumulators per domain/trace/window — no shared state, no copy path
(values differ elementwise). **Independent recompute from the raw
`.route` traces reproduces the JSON bit-for-bit** (replication script
in project history) — ranker exonerated, no serialization bug. The
coincidence is isolated (N=20: 5.915 vs 5.376; N=30: 7.198 vs 7.008
differ normally) and integer-lattice-explicable (each per-layer h is
hits/1000 exactly; Σhits = 8499 both — a ~0.5% draw, 1 of 6 cells).
Verdict: verified data coincidence, not a bug; and moot for v2, which
uses global h̄, never L0–21 partial sums. Not outlived — closed here.

## V2.6 Assumptions ledger (v2)

U1: h̄@38 = 0.39 banked, cross-checked by interpolation (0.3899/0.4011)
— agreement, not faith. U2: h(54) interpolated (log-fit) + zero-gain
floor reported alongside — branch-proof both ways. U3: uniform pin
spread for the 1.024, bounded [0.876, 1.045] — branch-proof. U4: h̄
(macro mean) ≡ micro global h under equal eval windows (holds exactly
at @42: 0.4062 both). D0 excluded throughout. No fitting to branches;
thresholds pre-registered.
