# E1 spike timing design (DRAFT for architect review — paper before number)

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
  reused is a leg gate, not a follow-up.

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

## 3. Amendment-1 constraint (same binary, same session)

- Arm/disarm A/B runs on the SAME BINARY, SAME STAMP, SAME SESSION.
  Disarmed = `HYDRA_PIN_FILE` unset (E0 hooks still present, one branch).
  Armed = pin file set, same flags otherwise.
- GO/NO-GO anchor: 27.4374 tok/s on base+E0-disarmed (bridge B3, certified).
  A cross-binary mechanism delta is inadmissible — no separate armed build.
- Verdict bands are priced against the same-session disarmed control, never
  a stale scalar.

## 4. GO/NO-GO

- Bar: 34.5 us per invocation break-even, 11.5 us at 3x margin (robust to
  configuration, §24/§25).
- Above 34.5 us: STOP. Conversion refuted on this hardware; epic not built.
  No re-tuning, no second mechanism without new pre-registration + reason.

## 5. First milestone if the spike passes (E2)

- 48-layer timing harness BEFORE any correctness work: single-site timing
  cannot see scheduler overhead from 144 backend switches per token.
  E2 measures the switch overhead; GO/NO-GO re-priced at full scope.

## 6. Leg gates on every spike leg (non-negotiable)

- Build-provenance stamp catted at launch; abort if missing/CONTAMINATED/
  stale (objects newer than stamp).
- Engagement gate: summary + counter wired; verdict OPEN required before any
  effect number is read.
- Fused-op fingerprint + graphs-reused lines captured per leg.
- D1 (predicted_n == 200) + D2 (listener == child) + context-identical.

## 7. Per-invocation cost estimate (before building)

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

## Open at design-freeze

- Site choice (highest-h layer/site from ranking data).
- ids consult method without host readback (graph-side vs pre-staged).
- Harvest cadence for lazy event reads (every Nth invocation).
