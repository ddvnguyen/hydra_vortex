# E1 spike timing design (REVISED per architect §30 — all seven required changes incorporated; freeze list closed except the open items at the end)

Epic #148, milestone E1 (ARM 008). One-site per-expert backend selection.

## 1. What is built (one site only)

- Site: TBD by ranking skew (highest-h layer, `ffn_up_exps` or `ffn_down_exps`;
  chosen at design-freeze, stated in the spike report).
- Engaged path (decode only, ne12 == 1): for the k = 10 lookups of one token
  at one site, route each lookup to GPU (pinned set, on-device compacted
  rows) or CPU (misses, synchronous host compute). On-device compaction:
  pinned rows staged in a device buffer once at attach (sized from the
  VALIDATED pin set, `hydra_e0_fit_check` gates the bytes BEFORE allocating).
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
- Serialisation: ggml-backend-sched is EXPECTED to serialise gpu+cpu
  branches, so cost reads as SUM not max. The report states observed
  sum-vs-max; the design does not assume overlap. The win comes from ~42%
  of expert work moving to the faster device, not from overlap.
- TWO LEG TYPES on one binary (S2): graph capture and CUDA-event timing are
  in tension (timing ON poisons graphs), so one binary runs both types and
  the report carries both — neither alone verdicts:
  - (i) attribution legs: timing ON, graphs OFF → the per-invocation µs
    (the verdict number).
  - (ii) no-regression legs: timing OFF, graphs ON → throughput vs the
    disarmed control (§3) plus the graphs-reused gate.
- Bias, stated explicitly (S2): graphs-OFF execution OVERSTATES launch cost
  (no replay amortization) and therefore biases toward NO-GO — the safe
  direction. A MARGINAL (boundary-zone) NO-GO measured graphs-OFF IS NOT FINAL:
  the known bias direction moves boundary results into CONDITIONAL
  handling (§4), not STOP.

## 3. Amendment-1 constraint (same binary, same session — CORRECTED per S1)

- Arm/disarm A/B runs on the SAME BINARY, SAME STAMP, SAME SESSION.
  Disarmed = `HYDRA_PIN_FILE` unset (E0 hooks still present, one branch).
  Armed = pin file set, same flags otherwise. This STANDS.
- What the disarmed control IS: a NO-REGRESSION CHECK, not a gain check.
  E1 CANNOT be verdicted on throughput, by arithmetic: one engaged site of
  144 invocations per token is a 0.051% throughput effect against a ±2.4%
  measurement band — 47× below resolution. A throughput delta at E1 scope
  is noise by construction, in EITHER direction.
- E1's verdict = per-invocation µs against 34.5, full stop. The bridge-B3
  anchor (27.4374 tok/s) remains the no-regression reference for leg type
  (ii); it is never a gain target at E1 scope.
- A cross-binary mechanism delta is inadmissible — no separate armed build.
- E1 is a MECHANISM-COST GATE and does NOT test C3:
  hits-convert-to-throughput is E2's job. No one may read an E1 GO as
  evidence for the ranking thesis.

## 4. GO / CONDITIONAL / STOP

- Bar: 34.5 us per invocation break-even, 11.5 us at 3x margin (robust to
  configuration, §24/§25).
- Below 11.5 us: GO.
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

## 5. First milestone if the spike passes (E2)

- 48-layer timing harness BEFORE any correctness work: single-site timing
  cannot see scheduler overhead from 144 backend switches per token.
  E2 measures the switch overhead; GO/NO-GO re-priced at full scope
  (this is where the §7 78/144-invocation pricing is spent — see A7 note).

## 6. Leg gates on every spike leg (non-negotiable)

- Build-provenance stamp catted at launch; abort if missing/CONTAMINATED/
  stale (objects newer than stamp).
- Engagement gate: summary + counter wired; verdict OPEN required before any
  effect number is read.
- Work gate (S3): rows_computed == 10 per invocation, counter line in the
  log; a leg that fails it produces no timing number.
- Correctness gate AT E1 (A5): teacher-forced --kl-divergence armed vs
  disarmed — ships in the fork (perplexity.cpp:1949). Not sampled
  trajectories, not PPL.
- Instrument negative control (A6): armed+timing vs armed-no-timing, same
  binary, same session. If the instrument moves throughput, the µs describe
  the instrument, not the mechanism — the attribution legs are invalid and
  the instrument is rebuilt, not argued with.
- Fused-op fingerprint + graphs-reused lines captured per leg
  (graphs-reused gates leg type (ii)).
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

- Site choice (highest-h layer/site from ranking data).
- ids consult method without host readback (graph-side vs pre-staged).
- Harvest cadence for lazy event reads (every Nth invocation).
- S2 leg-split parameterization (timing-ON/graphs-OFF vs timing-OFF/
  graphs-ON flags per leg type on the one binary).
- S3 rows_computed gating implementation (counter + log line + gate).
