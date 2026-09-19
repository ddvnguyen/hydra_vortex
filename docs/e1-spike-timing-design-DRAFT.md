# E1 spike timing design (REVISED per architect §30 — all seven required changes incorporated; freeze list closed except the open items at the end; AMENDED per §34/§34a — site re-freeze, differential verdict, amended leg order)

Epic #148, milestone E1 (ARM 008). One-site per-expert backend selection.

## 0. Placement prerequisite + frozen site (§34a, BLOCKING precheck done)

- E1 legs MUST carry the heterogeneous tensor-override placement (288
  `--override-tensor` lines; D22 bands blk.0-9 CUDA0 / blk.10-21 CUDA1 /
  blk.22-47 host). Under all-GPU `-ngl 99` nothing is host-resident
  anywhere — an E1 leg without the override placement is VOID, not a result.
- Site: layer 31, `ffn_moe_up-31` (weight `blk.31.ffn_up_exps`; merged
  `ffn_moe_gate_up-31` recognized-never-engaged). Layer 15 was top-h overall
  (0.813/0.789) but GPU-resident under both documented band variants —
  wrong direction, disqualified on residency. Layer 31 is highest-h WITHIN
  the host band on both corpora (coding 0.766, general 0.742; next 44/0.704
  and 32/0.626) — convergent, no judgment call. h and residency are
  independent axes; the first freeze constrained only one.
- Layer-31 GGUF ground truth (shard 5, direct parse): up (2560,640,512)
  IQ2_XXS, gate (2560,640,512) Q8_K, down (640,2560,512) IQ1_S. MMVQ +
  device-dequant coverage for IQ2_XXS verified in-tree (mmvq.cu, convert.cu,
  getrows.cu). Code stays type-agnostic (opaque packed planes).

Epic #148, milestone E1 (ARM 008). One-site per-expert backend selection.

## 1. What is built (one site only)

- Site: layer 31 `ffn_moe_up-31` — FROZEN per §0 (§34a re-freeze).
- Engaged path (decode only, ne12 == 1): for the k = 10 lookups of one token
  at one site, route each lookup to GPU (pinned set, on-device compacted
  rows) or CPU (misses, synchronous host compute). On-device compaction:
  pinned rows staged in a device buffer once at attach (sized from the
  VALIDATED pin set, `hydra_e0_fit_check` gates the bytes BEFORE allocating).
  Sizing invariant (§31.4): every E1 buffer size derives from the validated
  pin set's ACTUAL TENSOR BYTES (src0 ne/nb at attach), never from layer
  counts or uniform VRAM constants (dead since the mapping x heterogeneous
  per-layer quant). A size derived from a layer count is a defect, not a
  shortcut; the fit-check-bytes-before-allocating pattern is the only form.
- NO host readback on the engaged path. The ids consult must be resolved
  without D2H of per-token data (graph-side or pre-staged consult — method
  stated at design-freeze; a D2H ids readback build is NOT a spike candidate,
  it is disqualified at design time because the sync alone exceeds the bar).
- Graph-capturable from the first line: no stream sync, no event sync on the
  engaged path. CUDA-graph veto MUST NOT exist for engaged nodes; graphs
  reused is a leg gate (on leg type (ii), §2), not a follow-up.
- Work conservation (S3): total expert-row work across BOTH branches == k
  per invocation. 2k doubling is NOT excluded by argument — it is excluded
  by a rows_computed counter per invocation with a HARD GATE
  rows_computed == 10 on every spike leg, reported as a counter line in the
  log like the engagement gate (§6). A leg that fails this gate produces no
  timing number.

## 2. Measurement protocol (the number)

- Unit: wall time per invocation (one token, one site), microseconds.
- Method: CUDA events recorded on the stream around the engaged region
  (gpu branch), host wall time for the CPU branch. Lazy harvest: event pairs
  pooled, `cudaEventElapsedTime` queried on a LATER invocation or at
  teardown. NEVER `cudaEventSynchronize` on the critical path. A host timer
  around an async call measures launch (~5 us) and fails toward false GO —
  inadmissible method, stated here so no build uses it.
- Reported per (layer, site), three branches NEVER merged:
  gpu_branch_us, cpu_branch_us, combine_us (n / p50 / p99 / mean each),
  plus launches-per-invocation (attribution: launch cost vs event time) and
  lookup-basis hits/lookups with h. E0 recorders: `hydra_e0_branch`,
  `hydra_e0_launches`, `hydra_e0_lookups`, `hydra_e0_engage`.
- Every µs number ships with its load context (§31.2): decode CPU% and
  quiescence status are REPORTED ALONGSIDE every timing leg, not just
  throughput legs — the CPU branch is directly CPU-availability-sensitive
  (misses run as synchronous host compute), so a µs without its CPU% is
  an incomplete result.
- Serialisation: ggml-backend-sched is EXPECTED to serialise gpu+cpu
  branches, so cost reads as SUM not max. The report states observed
  sum-vs-max; the design does not assume overlap. The win comes from ~42%
  of expert work moving to the faster device, not from overlap.
- TWO LEG TYPES on one binary (S2): graph capture and CUDA-event timing are
  in tension (timing ON poisons graphs), so one binary runs both types and
  the report carries both — neither alone verdicts:
  - (i) attribution legs: timing ON, graphs OFF → the per-invocation µs
    (armed state; the verdict number is armed MINUS disarmed, §3).
  - (ii) no-regression legs: timing OFF, graphs ON → throughput vs the
    disarmed control (§3) plus the graphs-reused gate, which is PASS/FAIL:
    graphs-reused collapse out of the 164-212 family = VOID (design
    violation, fixed in code), never a covariate and never a NO-GO.
  - (iii) disarmed-site timing legs: disarmed + timing ON, graphs OFF →
    the STOCK path bracketed at the site (own event pool, `stock` branch
    in the E0 ring). This is the differential baseline: the verdict
    isolates the mechanism's added cost ONLY as armed minus disarmed.
- Bias, stated explicitly (S2): graphs-OFF execution OVERSTATES launch cost
  (no replay amortization) and therefore biases toward NO-GO — the safe
  direction. A MARGINAL (boundary-zone) NO-GO measured graphs-OFF IS NOT FINAL:
  the known bias direction moves boundary results into CONDITIONAL
  handling (§4), not STOP.
- Capture semantics (§31 instrument-semantics): on graphs-on legs the host
  hook runs at capture time only — host counters describe capture
  iterations, not replayed ones; type-(ii) legs are verdicted on throughput
  + graphs-reused ONLY, never on host counters.

## 3. Amendment-1 constraint (same binary, same session — CORRECTED per S1)

- Arm/disarm A/B runs on the SAME BINARY, SAME STAMP, SAME SESSION.
  Disarmed = `HYDRA_PIN_FILE` unset (E0 hooks still present, one branch).
  Armed = pin file set, same flags otherwise. This STANDS.
- What the disarmed control IS: a NO-REGRESSION CHECK, not a gain check.
  E1 CANNOT be verdicted on throughput, by arithmetic: one engaged site of
  144 invocations per token is a 0.051% throughput effect against a ±2.4%
  measurement band — 47× below resolution. A throughput delta at E1 scope
  is noise by construction, in EITHER direction.
- E1's verdict = DIFFERENTIAL per-invocation µs (§34 structural): armed_us
  MINUS disarmed_us at the same site (stock branch, leg type (iii)). An
  absolute armed number against the bar biases FALSE STOP, and STOP ends
  the epic — the most expensive possible protocol error. The bridge-B3
  anchor (27.4374 tok/s) remains the no-regression reference for leg type
  (ii); it is never a gain target at E1 scope.
- A cross-binary mechanism delta is inadmissible — no separate armed build.
- E1 is a MECHANISM-COST GATE and does NOT test C3:
  hits-convert-to-throughput is E2's job. No one may read an E1 GO as
  evidence for the ranking thesis.

## 4. GO / CONDITIONAL / STOP (on DIFFERENTIAL us, bands confirmed verbatim)

- Bar: 34.5 us per invocation break-even, 11.5 us at 3x margin (robust to
  configuration, §24/§25). Applied to armed_us − disarmed_us, never to an
  absolute armed number.
- Below 11.5 us: GO — stated next to the verdict: single-site timing
  EXCLUDES the per-token scheduler-switch cost (144-site scope only), so GO
  is optimistic by an unmeasured amount and E2 re-prices it.
- Above 34.5 us: STOP. Conversion refuted on this hardware; epic not built.
  No re-tuning, no second mechanism without new pre-registration + reason.
  (Boundary-zone results measured graphs-OFF are CONDITIONAL per the S2
  bias rule, not STOP.)
- 11.5–34.5 us: CONDITIONAL (A4, the registered middle band) — proceed to
  the E2 timing harness ONLY; no correctness work; re-priced at
  144-invocation scope. Asymmetry, stated so the band is not misread as a
  soft GO: scope only ADDS cost (scheduler overhead from 144 backend
  switches is invisible at E1), so a marginal E1 predicts an E2 failure —
  CONDITIONAL is entered with that expectation written down.
- Noisy-box caveat (§31.3): the 34.5 us bar was derived at NORMAL LOAD. A
  starved box inflates CPU-branch µs and biases toward NO-GO (safe
  direction, same family as the S2 graphs-off bias). A marginal NO-GO
  measured on a noisy box IS NOT FINAL — it is re-run on a quiet box
  before any STOP is read.

## 5. First milestone if the spike passes (E2)

- 48-layer timing harness BEFORE any correctness work: single-site timing
  cannot see scheduler overhead from 144 backend switches per token.
  E2 measures the switch overhead; GO/NO-GO re-priced at full scope
  (this is where the §7 78/144-invocation pricing is spent — see A7 note).

## 6. Amended leg order (§34) + gates on every spike leg (non-negotiable)

Order: 1 dry-run verify (match/engage/rows==10) → 2 site-residency
precheck, DONE §34a (layer 31 frozen, §0) → 3 KL correctness gate, MOVED UP
(fast-and-wrong invalidates every later number: no performance leg runs
before KL passes) → 4 type-(ii) no-regression + graphs-reused PASS/FAIL
(VOID on collapse, §2) → 5 A6 instrument negative control WITH GRAPH STATE
HELD CONSTANT (armed+timing vs armed-no-timing, BOTH sides graphs-OFF;
comparing across graph states measures graphs, not the instrument) →
6 disarmed-site timing (leg type (iii), the differential baseline) →
7 type-(i) attribution → DIFFERENTIAL verdict (§3, §4).

- Quiet box + no-build rule (§31.1): NO builds/compiles of any kind during
  a rig session, and every E1 leg runs only on a confirmed-quiet box
  (quiescence checked with the owner before the leg; decode CPU% captured
  per §2). A leg on a noisy box is a wasted leg, not a result.
- Build-provenance stamp catted at launch; abort if missing/CONTAMINATED/
  stale (objects newer than stamp).
- Engagement gate: summary + counter wired; verdict OPEN required before any
  effect number is read.
- Work gate (S3): rows_computed == 10 per invocation, counter line in the
  log; a leg that fails it produces no timing number.
- Correctness gate AT E1, FIRST performance gate (A5): teacher-forced
  --kl-divergence armed vs disarmed — ships in the fork
  (perplexity.cpp:1949). Not sampled trajectories, not PPL. Runs at
  position 3 in the amended order, before any performance leg.
- Instrument negative control (A6): armed+timing vs armed-no-timing, same
  binary, same session, GRAPH STATE HELD CONSTANT (both sides graphs-OFF).
  If the instrument moves throughput, the µs describe the instrument, not
  the mechanism — the attribution legs are invalid and the instrument is
  rebuilt, not argued with.
- Fused-op fingerprint + graphs-reused lines captured per leg
  (graphs-reused is a PASS/FAIL gate on leg type (ii): collapse = VOID).
- D1 (predicted_n == 200) + D2 (listener == child) + context-identical.

## 7. Per-invocation cost estimate (E2 pricing shown for risk framing — E1 runs 1 invocation per token)

(A7 label: the 78/144-invocation pricings below are E2 pricings. E1
measures ONE site — one invocation per token. These numbers are risk
framing for the CONDITIONAL band and E2 scope, NOT E1 expected cost.)

Mechanism budget rule, mechanism side (E1), from the §27 risk note:
- Prize above Stage-1 config: 2.689 ms/token.
- Launch hypothesis under test: 2-3 launches/invocation (compaction,
  matmul, combine) at ~5-10 us each.
- 78-invocation scope: 2 x 5 us = 0.78 ms (29% of prize); 3 x 10 us =
  2.34 ms (87%). 144-invocation scope: 3 x 10 us = 4.32 ms (161%).
- Launch overhead alone is the PRIMARY hypothesis. The spike attributes
  cost to launches specifically (launch count per invocation alongside
  event-measured time — separated, not inferred).
- Consequence: graph replay is load-bearing (criterion 6). A syncing
  prototype produces no believable number.

## 8. VRAM attribution (owed before GO/NO-GO)

- Free from existing allocation lines: pinned store bytes (attach-time,
  fit-checked), compact buffer per invocation (pool), ids staging.
  Reported per leg alongside the timing, from the same log.
- Allocation lines are compared EXACTLY from now on in every leg report
  (this §8 per-leg allocation reporting stays — the base attribution is
  discharged).
- Banked, not E1-blocking: the VRAM −40 MiB is ATTRIBUTED — 3.0× delta in
  the secondary KV/checkpoint-snapshot cache (issue #153). One
  continuation/cache-reuse clean-vs-bridge leg is required before the epic
  merge gate (#153).

## Open at design-freeze

- Site choice: CLOSED §34a (layer 31, §0).
- ids consult method without host readback (graph-side vs pre-staged).
- Harvest cadence for lazy event reads (every Nth invocation).
- S2 leg-split parameterization (timing-ON/graphs-OFF vs timing-OFF/
  graphs-ON flags per leg type on the one binary).
- S3 rows_computed gating implementation (counter + log line + gate).
