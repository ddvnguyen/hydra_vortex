# E1 Post-Mortem — ARM 008 Single-Site Mechanism Spike

Status: DRAFT (builder-drafted per §44 Q4, §5 reframed per §47; architect review
pending). Open item: two KL replicate legs IN FLIGHT (`ARM_KLREPL_DONE`
pending) — curiosity-closing only, nothing waits.

Bank: `docs/leader-handoff-state.md` §§30–44. Fork: `src/llama-cpp`, branch
`e0/measurement-foundation`. E1 commits `81f92d5bc` (part 1: site
match/attach/consult harness, fail-closed gates), `739084e4a` (part 2: split
compute kernels, event-pool harvest, per-branch split counting, KL leg),
`4c2b9cb67` (part 3: layer-31 re-freeze, differential stock timer, bounded event
pools, amended leg protocol). Certified measurement base: stamp `be4676f11`
(§29). KL-gate script commit `87e897d34` (§35a).

## 1. What E1 was for — a mechanism-cost gate for C3, never a throughput test

The owner's correction (§22) re-anchored the campaign on proving EXPERT RANKING
(C3: ranked hits convert into throughput), with C1 (skew exists — phase-0
self-heldout @42: coding global 0.4062, general 0.4207; derivation doc §1,
committed `b69c55f8f`) PROVEN and C2 (value if realised, ~1.14–1.17x coverage
model) MEASURED, but C3 NEVER DEMONSTRATED (dual-construction 0.82x; gather
moved weights instead of saving them; layer placement cannot express ranking).
Note on figures (both sourced, different quantities — kept distinct so this
file reproduces `b69c55f8f`): 0.4207 is the general-corpus GLOBAL h@42; the
N=38 OPERATING POINT used downstream is h-bar ≈ 0.39 (banked; log-interp
reproduces 0.3899 coding / 0.4011 general; derivation doc Point 1). The §24
E2-scope pricing used h=0.4207; the corrected derivation gates on h-bar/f at
the 0.39 operating point.

E1 (epic milestone E1, §25 Track 3) was the smallest experiment that proves or
kills C3: per-expert backend selection at ONE expert-matmul site, with the whole
deliverable being WALL TIME PER INVOCATION against the mechanism budget rule
(§22): ranking prize 5.23 ms/token over 144 invocations/token (48 layers x
3 sites) => 36.3 us break-even, 12.1 us at 3x margin — the ORIGINAL budget
(§22); re-stated as 34.5/11.5 us on the 26-layer E2 scope (§24). The
REGISTERED verdict bands (§30 A4, re-confirmed §34/§35) are quoted ONLY as:
<11.5 us GO (FUND THE EPIC); 11.5–34.5 us CONDITIONAL (E2 harness only, no
correctness work); >34.5 us STOP — with the ARM 008 STOP commitment banked
verbatim (§25: no re-tuning, no second mechanism on momentum).

The architect's §30 design approval made this explicit (S1): one invocation of
144/token means E1's prize is 0.019 ms/token vs 36.45 ms baseline = 0.051% vs
the ±2.4% band = 47x BELOW RESOLUTION — E1 CANNOT be verdicted on throughput.
Its verdict is per-invocation microseconds against 34.5, FULL STOP; the
disarmed control stands as a NO-REGRESSION CHECK, not a gain check.
Hits-convert-to-throughput is E2's job at 48-layer scope. NO ONE may read an E1
GO as evidence for the ranking thesis. The ARM 008 STOP was never triggered —
E1 died on engagement and capture before any us number existed.

## 2. What was built; which assets survive

- Part 1 (`81f92d5bc`): site match/attach/consult harness (`ggml/src/hydra-e1.h`
  new, +194), fail-closed gates, KL gate script hooks. Frozen site: layer 15,
  `ffn_moe_up-15` (§33a; later re-frozen, see below).
- Part 2 (`739084e4a`, +431/-55 across `ggml-cuda.cu` + `hydra-e1.h`): split
  compute kernels (GPU hits path + CPU miss path + combine), event-pool
  harvest, per-branch split counting, KL leg wiring. Three review defects fixed
  in-code (output misalignment, uninit packet, hardcoded sizes) (§33a).
- Part 3 (`4c2b9cb67`, +166/-16): layer-31 re-freeze (`HYDRA_E1_LAYER` 15→31
  with node/weight/gate_up renames), differential stock timer
  (`hydra_e1_stock_begin`, 4th E0 ring channel `stock`, verdict = armed minus
  disarmed from the same log), bounded fixed-16-slot event pools (lag = counted
  skips; slot_recorded/stock_recorded masks), amended leg protocol (§0 placement
  prerequisite, differential definition, VOID-not-NOGO graph veto, A6
  graphs-OFF both sides, KL at position 3) (§34b/§34c).
- KL gate script `scripts/hydra-kl-gate.sh` committed as `87e897d34` BEFORE the
  KL leg, satisfying §35 condition 3 (§35a).

Surviving assets (reusable, not sunk — §37): fixed-slot event pools, E0 ring
channels (now incl. `stock`), engagement/rows_computed counters, fail-closed
gates (graph-capture abort, stock-void check, rows==10 work-conservation gate),
the calibrated KL instrument (§41/§44; see §7). E1 leg protocol work
(differential verdict, A6, VOID-not-NOGO) transfers to any successor mechanism.

## 3. Terminal defect 1 — reachability: host-resident site ⇒ CPU backend ⇒ CUDA intercept unreachable

The dry-run leg NEVER ENGAGED — zero `[HYDRA e1]` lines (§36 Finding 1), on a
leg whose placement VERIFY, fingerprint, D1/D2, and binary-identity gates all
passed (D1 n=200 @ 27.35). Cause, evidenced from the log: layer-31 expert
weights are CPU-resident (156 host overrides, log-confirmed) ⇒ ggml's
scheduler assigns layer-31 `mul_mat_id` to the CPU backend ⇒
`ggml_cuda_mul_mat_id` and the E1 intercept never see the node. Deeper:
`attach_device` D2D-copies src0→data and therefore PRESUMES device-resident
weights BY DESIGN — which is exactly why a host-resident site has no viable
engaged path; and per §46 source facts it never executed in dryrun anyway
(sole caller is the full-engage path at `hydra-e1.h:541`, after the dryrun
early-return at `:519-524`).

Structural form (§36): a host-resident site (REQUIRED for the mechanism
direction — routing work toward GPU where weights are already VRAM-resident is
the prize) and a CUDA-side intercept (where the code lives) are MUTUALLY
EXCLUSIVE under ggml's scheduler, because residency DETERMINES
backend-assignment and one ggml node runs on exactly ONE backend (§37: "experts
are ONE 3D tensor per layer; one node runs on exactly ONE backend").

The three-axis trap (§36): h × residency × backend-assignment. §34a fixed
residency (re-freeze 15→31: layer 15 GPU-resident under both band variants,
would have routed work TOWARD CPU = wrong direction; layer 31 highest-h within
host-resident blk.22-47, coding 0.766 / general 0.742) and thereby made the
intercept unreachable — the two fixes were individually validated, jointly
incompatible. Nothing downstream was runnable: type-(ii)/A6/stock/type-(i) all
presume engagement (§36).

Ruling (§37): (a) CPU-side intercept REJECTED (same one-node-one-backend wall;
launch+sync per invocation, sync alone 10–50 us vs 34.5 budget — disqualified
at design); (c) GPU-site reversal REJECTED (wrong direction); (b) HOT/COLD
EXPERT SLAB (issue #132) IS THE PATH — split `blk.N.ffn_*_exps` at LOAD TIME
into hot slab (pins → GPU buffer) + cold slab (remainder → CPU buffer) = two
tensors = two `mul_mat_id` nodes with remapped ids + combine; the scheduler
assigns hot→CUDA / cold→CPU FOR FREE. No intercept, no per-token transfers, no
syncs, graph capture preserved natively.

## 4. Terminal defect 2 — capture infeasibility: SYNC-JOIN = cudaStreamSynchronize ⇒ engaged path never graph-capturable by construction

Condition-2 answer (§36 Finding 3, confirmed in code at `hydra-e1.h:568`
SYNC-STAGE and `:681` SYNC-JOIN, both `cudaStreamSynchronize(stream)`, plus D2H
readbacks): the FULL engaged path can NEVER execute under CUDA-graph capture —
the capture-check aborts first. A type-(ii) leg (timing-OFF/graphs-ON) would
exercise only the no-op hook/dry-run path, never the engaged mechanism.

Second independent terminal defect of the CUDA-side approach (§37 Finding 3):
defect 1 says the hook never fires at a host-resident site; defect 2 says even
a firing hook could never run under capture. DO NOT run type-(ii) (§37);
ARMED+DRYRUN as capture-neutrality evidence DECLINED (proves a property of an
intercept that (b) deletes). Corroboration that (b) is aimed correctly: #132
has no syncs and no intercept ⇒ capture native — both terminal defects vanish
in the same redesign (§37).

## 5. Unexplained measurement anomaly (open)

A single matched armed-vs-disarmed KL pair read 0.032906 mean (92× the measured floor median, 23× at the chunk median) with zero engagement. Both registered mechanisms — weight modification via `attach_device`, and allocation/scheduler perturbation — are eliminated by source facts (§46); a third, thread-timing-dependent reduction order, is eliminated by ggml's CPU chunking being disjoint-output. No known mechanism remains. n=1; replication ordered. Leading candidate is that the result is not systemic. Not established as a defect in E1 code.

Provenance (§46 → §47, bank `9dd71404d`): the §45 source-fact pass landed as
§46 (arm path issues ZERO CUDA/ggml/scheduler/allocator calls — `attach_device`
never executes in dryrun, all copies/allocations live inside it; per-invocation
armed+no-match = flag check + immediate return + cached bools + `strstr` match,
zero data-pointer derefs). The §44 empty-pin discriminator stays UNANSWERABLE
(§45 fail-closed boundary: `ggml-cuda.cu:2008` via `hydra-pins.h:119`, rc=134 —
the gate working as designed, asset side, §7). The "median 23× argues systemic"
argument was refuted in §47 (median across 88 chunks within ONE pair is not
replication; n=1).
IN FLIGHT: two replicate legs on the rig per §43 protocol (a) armed-vs-armed
pair + (b) second armed-vs-disarmed pair; `ARM_KLREPL_DONE` pending. Run as
cheap curiosity-closing, not critical path — #132 is immunized by the three-way
decomposition (instrumentation constant on both sides of each comparison).

## 6. Why review missed them — the protocol was reviewed, the premise never checked

Three review rounds (§30 design approval with seven changes; §34 leg-protocol
review; §35 GO with three leg-specific conditions) went deep on the instrument:
differential-vs-absolute verdict, graphs-vs-events tension (S2 two-leg-type
resolution), rows_computed work-conservation gate (S3), middle-band
registration (A4), teacher-forced KL (A5), instrument negative control (A6),
scope labeling (A7), site-residency precheck, SYNC-JOIN primitive naming. None
asked whether the op reaches the hook's backend in the target configuration —
despite the same review having written "one node runs on exactly ONE backend"
(§37). The architect acknowledged it as their own miss (§37).

Reachability was assumed from the wrong layer of the stack: the freeze
constrained h (layer 15, top overall 0.813/0.789) while residency — which
DETERMINES backend assignment — varied independently underneath it. The §34a
re-freeze repaired residency and, by the same coupling, broke reachability.

Standing rule minted (§37, banked as rule): REACHABILITY PRECHECK PRECEDES
INSTRUMENT DESIGN — prove the hook site executes in the target configuration
(one counter, throwaway build) before any harness is built. It sits beside the
Gate A/B rule (engagement before effect, §19 amendment) and the gates-must-fail
rule (§19): this campaign has now paid for the missing precheck three times
(ARM 006's MMVQ-vs-MMQ hook, the fire=[0,1,1,1] episode, Stage-1 prefill — all
would have been saved by Gate A; E1 would have been saved by reachability).

## 7. Assets retained

- Fixed-16-slot event pools + 16-cadence lazy harvest (bounded; lag = counted
  skips; no growth possible; no CUDA at teardown) (§34b, `hydra-e1.h`).
- E0 ring channels incl. the 4th `stock` channel (n/p50/p99/mean) and the
  differential verdict code (armed − disarmed from the same log) (§34b).
- Fail-closed gates: graph-capture abort, rows_computed == 10
  work-conservation HARD GATE (S3, §30), CONDITION-1 stock-void
  (`stock.n == 0` ⇒ VOID, never disarmed_us = 0 — prevents differential
  reverting to absolute, §35), pin-load hard-fail + load summary (M3/#142),
  zero-pin-arm refusal demonstrated live (§45: `hydra-pins.h:119` gate →
  `ggml-cuda.cu:2008` abort, rc=134, no KLD — the gate working as designed),
  per-invocation timing on the engaged path (M1/#143), split counters (M2/#144).
- Calibrated KL instrument: floor ~4e-4 (median 0.000358, §40), gate 0.01
  validated (10.6x above max floor, 7.2x below armed signal, §41), dump-PPL
  spread 0.031% + wall-time spread 4.7% as independent run-to-run variance
  corroboration of the ±2.4% replication band (§41).
- Proven value of the instrument: the §44 three-way #132 gate decomposition
  (1) stock vs split-both-on-CPU = split/combine numeric cost alone = THE
  GATE; (2) split-both-on-CPU vs split-hot-on-GPU = backend-assignment effect;
  (3) stock vs full slab = end-to-end ≈ (1)+(2) — exists BECAUSE the instrument
  was calibrated: #132 changes summation order BY CONSTRUCTION (two nodes +
  combine), so "armed ≈ disarmed at floor" is an unachievable standard, and
  without the decomposition #132 would be gated against it. The decomposition
  was designed before code (§44 Q3).

Related standing state (not E1's verdict, recorded so the file is not
misread): #132 BUILD branch fired on the corrected coverage-per-MiB
re-derivation (Ratio 5.38 at N=38, floor 3.94 at N=54, gates 2.93/1.46 —
§38a/§39, doc committed `b69c55f8f`), but re-scoped BEFORE CODE: payoff = VRAM
EFFICIENCY AT MATCHED COVERAGE (~4.6 GB slab @ ~0.447 vs D22 20.4 GB @ 0.4583),
NOT decode throughput; static ranking capped at ~45% coverage on this model at
any VRAM budget (§39). Owner package delivered (§39a); line-retirement vs build
is the owner's call. ARM 008 STOP untriggered (§37).

## 8. Rules minted (standing)

Cost basis, one sentence: three code parts (~800 lines across `81f92d5bc` /
`739084e4a` / `4c2b9cb67` plus the KL-gate script `87e897d34`), three architect
review rounds (§30, §34, §35), and a full rig session's legs — all spent on an
instrument whose hook site could never execute — which is what gives the first
rule below its earned weight.

- REACHABILITY PRECHECK PRECEDES INSTRUMENT DESIGN (§37): prove the hook site
  executes in the target configuration (one counter, throwaway build) before
  any harness is built. Beside the Gate A/B rule (engagement before effect,
  §19 amendment) and the gates-must-fail rule (§19).
- STAMP-AFTER-COMMIT MANDATORY (§33a, §34c): dirty-tree builds are
  disclosure-required deviations, never the record of achievement; restamp
  clean after commit, and the stamp records newest-object mtime (§28).
- NO BUILDS OF ANY KIND DURING A RIG SESSION (§30b, §31): "CPU-only" is not a
  scheduling class on a CPU-bound rig; rig sessions own the machine.
  Measurement build dirs are immutable after stamping (§28).
- NO MECHANISM ATTRIBUTION WITHOUT THE LOG/SOURCE CHECK THAT WOULD REFUTE IT
  (§33): a coincidence that fits is a hypothesis, not a finding. (Minted
  against the architect's own §27 resolve_fused_ops attribution, retracted in
  §28, and the MTP attribution, amended in §33.)
- MEASURE A GATE'S FLOOR BEFORE TOUCHING ITS THRESHOLD (§37 question, §40
  isolation, §41 ruling): teacher-forced KLD build-vs-itself read ~4e-4, which
  kept the 0.01 gate (10.6x above max floor) instead of a miscalibrated
  re-thresholding.
- COMPARE ALLOCATION LINES EXACTLY, NOT WITHIN A BAND (§30): any non-identical
  buffer is NAMED and ATTRIBUTED — the ±1% band caught the 3.0x snapshot-cache
  delta by accident; exact comparison would have caught it on purpose.
- QUIESCENCE GATE OVER AN ABSOLUTE CPU% BAND (§31): sample non-rig CPU
  before/during each leg and abort if anything material is active; decode CPU%
  is a reported COVARIATE, flagged when anomalous, NEVER auto-voiding
  (absolute band deliberately rejected as config-dependent).

## 9. Implications for #132 (dev-workable list)

- The slab is NOT bit-identical to stock BY CONSTRUCTION (one `mul_mat_id`
  split into two nodes + combine changes summation order) — gating #132
  against "matches stock" chases a ghost (§44 Q3).
- The three-way decomposition IS the gate (§44 Q3): (1) stock vs
  split-both-on-CPU = split/combine numeric cost alone = THE GATE;
  (2) split-both-on-CPU vs split-hot-on-GPU = backend-assignment effect;
  (3) stock vs full slab = end-to-end, must reconcile as ~ (1)+(2). Exceeds
  (1)+(2) ⇒ real defect; lands at (1)+(2) ⇒ correct despite not matching
  stock. Without it #132 would be gated against an unachievable standard.
- No intercept, no per-token transfer anywhere in the design (§37): residency
  is per-tensor at load time; the scheduler assigns hot→CUDA / cold→CPU for
  free; both E1 terminal defects vanish in the same redesign.
- Payoff = VRAM EFFICIENCY AT MATCHED COVERAGE, not throughput (§39): ~4.6 GB
  slab @ ~0.447 vs D22 20.4 GB @ 0.4583. Static expert ranking cannot exceed
  ~45% coverage on this model at ANY VRAM budget (h-bar saturates at N~54) —
  the campaign's real scientific result. The one genuine throughput angle is
  second-order: a 5060-Ti-fitting slab frees the 3060 entirely, recovering the
  ~3% CUDA1 fixed toll and removing the PCIe x4 constraint.
