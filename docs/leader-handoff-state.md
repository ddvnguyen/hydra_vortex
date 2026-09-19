Leader: 623a1037 (contract 2.2.0, turn 763). Track t-d1cf43b723. Heartbeats 1c341235 / da515775 / bddc897a. Daemons 53.

1. KLD GATE (d-1190aaa96d, CORRECTED d-28dbb797c2): band BLOCKED until null_2 lands. null_1 = A vs A' same-binary (mean 1e-6 / 99th 4.3e-5 / median 0) = SANITY REFERENCE ONLY. null_2 = A_armed_N0 vs A_armed_N0' (ARMED cross-session, min 3 pairs / 6 runs) = BAND-SETTING null. A vs A_armed_N0 = null_3 DIAGNOSTIC (arming cost), NOT band-setting. Candidate = A_armed_N38 vs A_armed_N0, scored vs null_2. Binding = KLD-within-null (median + 99th-pct); empirical bar + VOID guard per d-28dbb797c2. Token-sig/PPL = screens only (match informative, mismatch uninformative cross-session).
2. ROUND 1 (d-26bf3151fd): subject-pooled pins + SESSION-WARM runtime refresh. Per-layer N = free rider (+0.004), NOT a round.
3. STOP RULE: round-1 measured h < 0.24 -> KILL.
4. ITEM-2 CLOSED (d-13f73643e3): pin set bad, instrument sound (79σ: 0.1408 vs 0.074 chance), no escalation. SUPERSEDED CLAIM — "all admission sites LIVE" STRUCK by site-0 discriminator (see 12a): site 1/4 is STRUCTURALLY DARK (expected-dark, GGUF has separate gate/up tensors so `if (gate_up_exps)` block is skipped). ITERATE pricing stands on measured h (S(0.40)=1.097).
5. PREFILL RANKING DEAD (measured, 13/57 positive, median = breakeven). Never re-open on Phase-1b length curve.
6. STATIC CEILING: S 1.085 (subject-pooled + per-layer N) vs SHIP 1.10. Nothing static ships (need h >= 0.405 > static self-heldout 0.3869). CONFIRMED ON CLEAN DATA d-28dbb797c2: templated-only ceiling S 1.090 vs SHIP 1.10 — still no-ship, now on trustworthy cohort. CAVEAT: h@42 ~0.41 reference built on 3 BARE-TEXT v1 traces — soft in the inflating direction (+0.009 matched / +0.038 within-cohort); reinforces, does not change, the conclusion. M-tier bare-text tails carry the same bounded caveat; never pool cohorts silently — cohort is a reported dimension.
7. IN-FLIGHT: KL base save chunks-10 + null x3 (job f05da0172f0e, serial rig); exact-trace closeout (one GPU capture leg) queued behind KL series; L-tier route-trace at -c 8192 (d-799f313d83), serial with KL work. Next: spread + pre-registered band on KLD-SERIES-DONE (only after null_2).
8. REFS: hydra_vortex#772; /tmp/opencode/{perlayer_n,prefill_score,prefill_rank_poc}.py; DESIGN.md closure row (builder); tasks cf7962562f 75% / 4e0493edd2 50%.

Gated: T-3/T-4/T-5/T-6, S132-2, Phase-1 20f123462f. Blocked: bd5b174a4d (owner pre-clearance). S5b CLOSED. Atlas item-1 COMPLETE. D = UNMEASURED (never "broken"). Sha-gate VOID (rig nondeterministic). Standing rule: null must match candidate's build distance, not just inputs.
9. AUDIT RULINGS (d-9ebd3b6662 / d-0fb2d12ba8), 2026-09-17:
   9a. SITE-0 DEFECT, OPEN. MMQ fingerprint fire=[0,98,98,98] -> admission site 0 fires ZERO times. This is NOT a clean fingerprint; it is my long-open "do all four admission sites fire" flag answered NEGATIVE. Same class as G3 hit=miss=0. DISCRIMINATOR OUTSTANDING on builder (source-only): what is site 0, is fire=0 expected for qwen4exp, and what does the count 98 measure (if per-layer it should be 48)?
   9b. .cu PIN-STORE REDIRECT HAS NO n_tokens GATE = DEFECT (graph dual-op is n_tokens==1, .cu redirect ungated -> prefill included when ON). "Numerically consistent via masking" argues correctness; the thing measured is COST. Invisible to BOTH gates (byte-identical-OFF cannot see it, flag is OFF; KLD cannot see it, KLD is correctness). FIX: add n_tokens==1 gate to match graph op.
   9c. **O/D = 0.1298 IS PROVISIONAL.** It came from B_empty at h=0 and is contaminated by 9b: S(h)=1/(1-0.545h+O/D) is a DECODE model, so prefill-side overhead is structurally invisible to it. ALL NEW NUMBERS FROZEN until the 9b gate lands, then re-measure. Every downstream S figure inherits this.
   9d. COUPLING RULE: stores are CUDA0-resident (correct, non-negotiable). But if a leg runs WITHOUT --cpu-moe, experts are already GPU-resident and the store DUPLICATES them in VRAM. At 64K we are VRAM-bound (N=38 -> 523/441 MiB; N=42 rejected at 111 MiB). -> HARD FAIL at init if experts are not CPU-resident. Never silently proceed. In audit matrix.
   9e. GAP TIMING SPLIT: GAP-2 (hoist per-expert branch) DEFERRED, perf-only, cannot invalidate a correctness gate. GAP-1 (wire hydra_dual_active at 4 sites) CONDITIONAL — if site 0 is dark BECAUSE GAP-1 is unwired, land GAP-1 FIRST. Default if discriminator unresolved: land first. (Rationale: running a gate against a partially-wired mechanism is exactly the G3 ordering error that voided 15 legs.)
   9f. NULL ARTIFACT SHA-PINNED. Do not anchor a band to a live log path; anchor to sha256. (Prior burn: V4_SET_TABLE.md content changed while mtime stayed put.)
   9g. BLAST RADIUS if discriminator says GAP-1 unwired: null_1 (A vs A') is UNCONDITIONALLY VALID — baseline vs baseline, no apparatus exercised, keep it, do not re-run. Only null_2 (A vs A_armed_N0) and the candidate (A vs A_armed_N38) die and need re-running.
   9h. 3-ARM PROTOCOL: arm0 flag OFF / arm1 ON N=0 / arm2 ON N=38, ONE binary, N runtime param, branch hoisted out of the per-expert inner loop. arm1-arm0 = residual branch cost; arm2-arm1 = true admission overhead. Run on the MMQ path. Acceptance is a LADDER not a hard bar: byte-identical preferred -> else KLD within null_1 with the delta recorded as cross-build floor -> else defect.
10. OWNER DIRECTIVE HANDOFF (via architect), 2026-09-17:
   10a. N PARKED AT 38-42, NOT RELITIGABLE. VRAM reserved for MTP head + context size (protected consumers). No N increases, no 32K-for-VRAM trades, no re-derivation. Reference sweep (57 traces, CPU, O/D=0.1298 provisional): N=19 h0.2373 S1.000 / N=38 h0.3787 S1.083 / N=43 h0.4102 S1.103 / N=57 h0.4890 S1.158 / N=76 h0.5793 S1.228 / N=128 h0.7514 S1.388. Steep monotone — VRAM-starved, not conceptually broken. CONSEQUENCE: static tops out S1.083-1.103 vs 1.10 SHIP at owner N — static cannot ship. SESSION-WARM RUNTIME ADAPTATION IS MANDATORY (round 1 = whole path, not experiment; reflect in DESIGN.md). Remaining levers: O/D down + session-warm h. Ranking refinements (~+0.004) stop consuming attention.
   10b. RIG SCHEDULING = OWNER'S CALL. Leader proposes queue, owner disposes. Architect rules evidence/gates/design only. Rig-hour tradeoffs surface to owner.
   10c. THREE-LANE PARALLELIZATION (owner agreed): TARGET lane = 3060 (all BINDING: KL/KLD, O/D, perf, 3-arm; device-specific dispatch). HELPER lane = 5060 Ti parallel non-binding (route-trace L-tier remainder, corpus, exact-trace h-capture; router = weights+input, device-independent). CPU lane = all h/ranking analysis (N sweep took minutes, zero GPU; queuing analysis behind GPU = scheduling defect). PRECONDITION: one identical prompt on both GPUs, diff .route files (~15 min); match = helper is free trace farm; diverge = quantify first.
   10d. CUDA0 BINDING (leader-verified 2026-09-17): nvidia-smi index 0 = 5060 Ti, 1 = 3060. ALL evidence/shipping legs run CUDA_VISIBLE_DEVICES=1 (DEV=1 in every runner: controls, exp1, hybrid-t2, mtp, 3060 baseline) -> process-cuda:0 = PHYSICAL 3060. "Dual stores CUDA0-resident" = physical 3060 = TARGET lane. NO P0 — apparatus is on the right device. (Route-trace collection runs CVD=0 = 5060 Ti; already helper-pattern, pending 10c precondition.)
   UNCHANGED: site-0 discriminator gating (9a); O/D frozen (9c); GAP-1 land-first default (9e).
   PROPOSED QUEUE (for owner approval per 10b): TARGET 3060: series completion -> discriminator -> GAP-1 land-first iff dark-from-unwired -> 9b prefill gate -> O/D re-measure -> null_2 + candidate -> 3-arm. HELPER 5060Ti: cross-GPU .route precondition -> L-tier remainder + exact-trace h-capture. CPU: round-1 session-warm analysis (N parked). OWNER ITEMS: re-author 8 v2 prompts (turn 844); approve queue.
11. ARCHITECT VERIFICATION FALLOUT (binding confirmed, 3 items), 2026-09-17:
   11a. P1 LATENT — DEVICE BINDING IS CONVENTION, NOT ENFORCEMENT. DEV=1 true of current invocations, not structurally enforced: run-with-params.sh:344 + 128k-diagnostic-arm.sh:214 use CUDA_VISIBLE_DEVICES="${P_SERVER_CUDA_DEVICE}" (parameter, no visible default); SEVEN runners unpinned (bench-baseline.sh, concurrent-decode-test.sh, multiturn-growth-test.sh, llama-cpp-entrypoint.sh, start-hydra.sh, start-hydra-system-pod.sh, setup-p100.sh) -> unpinned process gets cuda:0 = physical 5060 Ti = WRONG device with legitimate-looking results. FIX: evidence runner ASSERTS physical device (log name + PCI bus id at startup, hard-fail if not 3060); every binding-leg artifact carries "device asserted" provenance.
   11b. 10c PRECONDITION NOT SHORT-CIRCUITABLE. PROJECT_STATUS.md:459 cross-GPU replication artifacts (/tmp/opencode/early-router-5060ti/) GONE — /tmp cleared. Precondition needs its ~15 min; do not skip on the PROJECT_STATUS line.
   11c. P1 PROCESS — EVIDENCE EVAPORATING FROM /tmp. All architect analysis (scripts, result JSONs, 57-trace corpus, REPORT.md files) lives in /tmp and will go the way of 11b; section 10a becomes unverifiable if it clears. FIX: scripts + result JSONs + trace manifest (+ per-file sha256; blobs may live elsewhere) into repo (docs/evidence/) and commit. Same failure class as 9f, one level up.
   UNCHANGED: queue awaits OWNER approval (10b); site-0 discriminator gating (9a).
   ROUTING (leader call, zero rig cost): 11a + 11c to fresh dev NOW (script/CPU work, no GPU, no rebuild, no series contact).
12. ARCHITECT VERDICTS (d-e8a89d0025), 2026-09-17:
   12a. SITE-0 CONFIRMED (supersedes the section-4 claim "all admission sites LIVE" — SECTION 4 AMENDED). Verified from source, all four sites: 1/4 llama-graph.cpp:2225 gate_up_exps (merged), 2/4 :2251 up_exps, 3/4 :2274 gate_exps, 4/4 :2383 down_exps — each gated n_tokens==1 at :2224/:2250/:2273/:2382. Site 1/4 sits inside `if (gate_up_exps) {`; this GGUF has SEPARATE gate/up expert tensors so gate_up_exps is null and the block is skipped. fire=[0,98,98,98] is exactly what the source predicts. GAP-1 INNOCENT, defers per 9e benign branch, series stands in full.
   12b. CITATION FIX: the ":3245" fallback reference is WRONG (llama-graph.cpp:3245 is inside build_attn; llama-model.cpp:3243 is create_tensor_gate_up_exps). The real fallback is the `} else {` at **llama-graph.cpp:2246**, routing into the separate up/gate path = sites 2 and 3.
   12c. 9a STAYS OPEN. Do NOT close on 98 ~= 96. The +2 residue in 98 = 2x48+2 is unexplained; 2/98 is exactly the anomaly size this track has twice mistaken for rounding. Q1 per-layer dump required; name the 2, then close.
   12d. CORROBORATION FOR 9b: all four GRAPH sites are n_tokens==1 gated (verified). The ungated .cu redirect is therefore a genuinely separate path, not a double-report of the same gate. 9b stands as a real defect.
   12e. **10a AMENDED — ARCHITECT'S OWN ERROR.** "Ranking refinements worth ~+0.004" was the per-layer-N ALLOCATION delta, NOT the value of a better RANKING FUNCTION. Measured at N=38: global->subject = +0.042 h (S 1.057->1.083); subject->oracle = +0.120 h (S 1.083->1.165, **+7.6% decode**). A better ranking function is worth up to +7.6%, not +0.4%. What should stop consuming attention is per-layer-N ALLOCATION only. **10a's session-warm mandate and the owner's N=38-42 call are UNCHANGED** — static cannot ship at owner N regardless of ranking. Propagate to DESIGN.md.
   12f. 9b FORCES A null_2 RE-RUN: 9b changes the armed-path binary; null_2 (A vs A_armed_N0) measures the armed binary's cross-build FP floor, so it is stale by construction. Proposed queue already orders this correctly (9b -> O/D re-measure -> null_2 + candidate) — no change. null_1 UNAFFECTED, do not re-run (restates 9g).
   12g. ReAP: (1) saliency answers a DIFFERENT question (deletion cost vs residency value); real bar is closing part of the 0.12 gap to oracle; UNTESTABLE on current traces (emitter binarises gate to `:1`) -> test as a RIDER on emitter widening, reprioritise nothing. (2) Irreducibility = background, EXCEPT it establishes experts are not mutually substitutable -> **"approximate residency" (serving a miss from a similar resident expert) is a CLOSED DOOR**. (3) Their eval harness NOT adopted — KLD is sharper for an engine change that should be numerically identical; theirs suits model changes and is vLLM-based. Revisit only if we ever prune.
   12h. HELPER TRACES ADMITTED (owner signs tolerance). Decisive: pin-level 36/38 on BOTH arms, so cross-GPU divergence (2/38) EQUALS the same-GPU rerun floor — the helper lane adds ZERO noise over a rerun. Impact ~0.001-0.002 h vs 0.02-0.05 detection effects (10-50x margin). GATES: (a) same-GPU duplicate control per batch; (b) pin-set Jaccard reported with every h number; (c) STOP-TRIGGER if cross-GPU divergence exceeds same-GPU by >2x on any batch. The 51% row-level flip is sampled-trajectory divergence (same root cause as the sha gate and divergence-point) -> fixed-sequence collection per section 13.
13. TEACHER-FORCE PATCH SPEC (d-5411fdf0fd), 2026-09-17. Source: examples/route-trace/route-trace.cpp recovered from commit 75c879787 (175 lines); ABSENT from HEAD 86af0c9af — restore as TRACKED COMMIT (11c-class durability, code tree).
   13a. DIVERGENCE SITE (exact): greedy continuation loop argmax flips `best` on sub-ULP logit diff (byte 621) and every later position routes a DIFFERENT token = the 51%. Prompt region safe (deterministic CPU tokenization); only continuation needs forcing.
   13b. PATCH — two env vars, default-OFF, LLAMA_ROUTE_TRACE convention: (i) LLAMA_ROUTE_TOKENS=<path> EMIT token ids inside decode_one (~3 lines; prompt + continuation); (ii) LLAMA_ROUTE_FORCE=<path> CONSUME continuation-only ids replacing argmax, n_gen from forced.size().
   13c. LOAD-BEARING: KEEP ONE TOKEN PER llama_decode + runtime assert batch.n_tokens==1. Batching forced tokens takes the PREFILL path, admission sites never fire — G3 hit=miss=0 in a new hat.
   13d. Satisfies d-0a8f0b229e: OFF path = original + one if per token (outside hot loop/graph); graph provably unchanged (logits readback omission only). Byte-identical-OFF expected; delta = cross-build floor per 9h/12, not defect.
   13e. ACCEPTANCE: OFF byte-identical; ON identical by construction; expect 51%->FP floor. FAILURE PRE-REGISTERED: >10% under forced sequence = genuinely nondeterministic routing, ESCALATE, undermines all h. Force-file sha256 in sidecar (9f/11c).
   13f. WORKFLOW: 3060 free-run with LLAMA_ROUTE_TOKENS emits reference continuation; replay exact file on both GPUs with LLAMA_ROUTE_FORCE. Builder implements after step 2.
14. DRAFT HYGIENE (architect ruling, 2026-09-18): 8 v2 re-author prompts in docs/evidence/reap-redraft-UNVERIFIED/ are UNVERIFIED (owner review + pair-parity pending) — path name is the warning since .txt contents are consumed verbatim and carry no marker. MUST NOT enter any collection until signed. Approval bytes sha256-pinned in REDRAFT.md "Approval pins". Standing generalisation: a receipt is not durability, and a path is not provenance — committed, marked, read back.

=== SECTION 15: cross-session bar + bare-text blast radius (HISTORICAL — Stage-1 closed NO-SHIP; see §16) ===

ITEM 1 — CROSS-SESSION BAR, pre-registration authorized, with a correction to the architect's own null_2.

The architect's original null_2 (A vs A_armed_N0) was the WRONG COMPARISON for band-setting: it
measures the COST OF ARMING, while the candidate comparison is armed-vs-armed-with-different-N.
Scoring the candidate against an OFF-vs-ARMED band conflates arming cost with session noise.
Builder's armed-vs-armed proposal is correct and better. Corrected ladder:

  null_1 = A vs A'                       OFF, cross-session     -> sanity reference only
  null_2 = A_armed_N0 vs A_armed_N0'     ARMED, cross-session   -> BAND-SETTING
  null_3 = A vs A_armed_N0               OFF vs ARMED-inert     -> DIAGNOSTIC (arming cost), NOT band-setting
  cand   = A_armed_N38 vs A_armed_N0     scored against null_2

Minimum 3 control PAIRS for null_2 (6 runs). A floor from one pair is not a floor.

KLD is the binding cross-session comparison; token-sig/PPL are downgraded to screens.

TOLERANCE deliberately NOT stated as a number — the bar is EMPIRICAL, derived from null_2's measured
ladder. Inventing a threshold ahead of the controls is the error this track has been avoiding.
Pre-registered DECISION RULE instead:
  PASS = candidate median KLD <= max(control medians) AND candidate 99th <= max(control 99ths)
         AND Same-top-p >= min(control Same-top-p)
  FAIL = exceeds at either percentile -> ESCALATE, never average
  VOID = controls disagree wildly among themselves (e.g. control 99ths spanning >10x) -> instrument
         too noisy to adjudicate; fix the rig, do not run the candidate.

SCREENS ARE ASYMMETRIC:
  token-sig/PPL MATCH cross-session -> strongly informative POSITIVE
  token-sig/PPL MISMATCH            -> UNINFORMATIVE, expected; the void sha-gate already proved
                                       cross-session trajectories diverge. NOT a defect, do not escalate.

TILE MATH: banked QK8_1=32, QK8_1_MMQ=128, but edge chosen at 64. Either justify 64 from the kernel
(e.g. 128-wide tile split across two 64-wide warp halves — plausible, but state it) or also test
{127,128,129}. Rows {630,640,650} inherit the same question. N-dim excluded per owner park: CONFIRMED,
state explicitly rather than skipping silently.

[OUTCOME: builder could not justify 64 from the kernel; added {127,128,129} + parity straddle {13,51,64}.
Campaign ran. Control band came back ZERO-WIDTH (6/6 legs bit-identical), which DEGENERATES this decision
rule — "any nonzero delta fails" would KILL on +0.000269. The architect's VOID guard covered controls that
were too WIDE and never anticipated the degenerate-tight case. Band declared VOID; candidate adjudicated
on external references instead. This is a standing lesson: pre-registered band rules need a floor-degeneracy
clause, not only a noise-ceiling clause.]

ITEM 2 — BARE-TEXT / EOS BLAST RADIUS.

Diagnosis CONFIRMED from source: route-trace.cpp:125 calls common_tokenize(ctx, params.prompt, true, true)
and never applies the GGUF chat template. Bare text into a chat-templated instruct model — framing error
by absence.

THE COLLECTOR UNDER-SCOPED IT. The 8 SHORTs are the visible symptom, not the population. All 24 v2 traces
AND the 3 v1 traces got bare-text prefill — identical framing error. The 8 are merely where it manifested
as immediate EOS; the other 16 generated something, but from out-of-distribution input.

MEASURED, because the architect's own numbers were exposed (N sweep, prefill POC, per-layer-N and the
static-ceiling ruling all used 57 traces = 27 bare-text + 30 templated). Recomputed v4-only (30 templated):
  h_global   0.3363 -> 0.3251   S 1.057 -> 1.050
  h_subject  0.3787 -> 0.3891   S 1.083 -> 1.090
  h_oracle   0.4984 -> 0.4880   S 1.165 -> 1.158
Cohort differential on same pooled pin set: v2 0.3424 vs v4 0.3336 = +0.009. Within-cohort LOSO:
v2 0.3631 vs v4 0.3251 = +0.038. Direction is INFLATION (degenerate tails concentrate routing).

CONCLUSIONS ALL SURVIVE. Static ceiling on templated-only data = S 1.090 vs SHIP 1.10 — still does not
ship, now established on the trustworthy cohort. h_subject IMPROVED on clean data (v4 is session-structured
with real subjects; bare-text v2 diluted the subject signal).

ANSWERS: (a) M-tier tails DO carry an EOS caveat into every h built on them, now BOUNDED at +0.009 matched
/ +0.038 within-cohort, always inflating. (b) Banked S-spike numbers (replayed v4 traces) CONFIRMED
UNAFFECTED. Also: phase0-rank and hprefill were built on the 3 v1 traces, which are ALSO bare-text; their
h@42 ~0.41 reference (handoff §6) is soft in the same inflating direction — reinforces §6, does not change it.

RECOMMENDATION TO OWNER: (a) EXTENDED. Collect the 8 v4-style as a separate arm, AND relabel the entire
v2+v1 cohort as BARE-TEXT with cohort as a REPORTED DIMENSION on every future h number. The defect is not
the 8 prompts, it is that two incompatible framings were pooled invisibly. REJECT (b) prefill-only banking
— doubly compromised (bare-text AND prefill-only) and prefill ranking is already dead at 13/57. (c) holding
at 36/44 is INSUFFICIENT ALONE — does nothing about the 16 non-SHORT bare-text traces already in the corpus.

DEGENERACY SCORING NOT AVAILABLE: v2 .log files contain only the routing-rows summary line, no decoded text.
Post-hoc n-gram-diversity truncation of garbage tails is impossible from existing artifacts. Future
collection MUST persist decoded text — add to collector spec.

[STATUS: owner ruling on (a)/(b)/(c) still PENDING as of §16. NO-SHIP took the corpus off the critical path —
nothing downstream consumes traces until the miss-side remap / pointer-swap mechanism works.]

=== END SECTION 15 ===

=== SECTION 16: miss-side remap resequence (supersedes compact-gather ordering) ===

DEFECT, located. src/llama-cpp/src/llama-graph.cpp:1601-1626, build_hydra_mm_id:
  ids_hit = ggml_get_rows(ctx0, dt.slot_of, ids);        // hit side HAS a remap
  o_hit   = ggml_mul_mat_id(ctx0, dt.store, cur, ids_hit);
  o_miss  = ggml_mul_mat_id(ctx0, w, cur, ids);          // RAW ids — NO REMAP
  return ggml_add(ggml_mul(o_hit,m_hit), ggml_mul(o_miss,m_miss));
o_miss runs the unmodified full id set, so it fetches all 10 selected experts from the
CUDA_Host tensor INCLUDING every pinned one; the mask discards them only after they have
crossed PCIe. Pinning adds a VRAM matmul and removes ZERO host traffic. h is real but
counts which path's answer was kept, not which fetch was avoided. Complete explanation of
0.82x and of N=0 (11.83) ≈ N=38 (11.75).

FIX. Mirror the remap onto the miss side. In hydra_dual_init (llama-context.cpp, the loop
that already fills slot_of/p_hit/p_miss, ~line 2588):
  miss_of[e] = (slot_of[e] >= 0) ? 0 : e     // pinned -> shared sentinel row; unpinned -> itself
Add miss_of to struct hydra_dual_tensors (llama-graph.h:38) and hydra_dual_layer
(llama-context.cpp:2442); allocate in the same CPU buffer as slot_of; populate same pass.
Then in build_hydra_mm_id:
  ids_miss = ggml_reshape_2d(ctx0, ggml_get_rows(ctx0, dt.miss_of, ids), ids->ne[0], ids->ne[1]);
  o_miss   = ggml_mul_mat_id(ctx0, w, cur, ids_miss);
Output unchanged — m_miss already zeroes every pinned lane. The h·k pinned lanes now read
ONE identical host address, collapsing in L2 after the first fetch. Host traffic drops ~h.

WHY BEFORE COMPACT-GATHER. No kernel work, no .cu touch, no MMVQ/MMQ plumbing, no CUDA-graph
loss (measured +9.6% / 1.259 tok/s, which compact-gather likely forfeits). Does NOT remove the
doubled compute (o_hit still runs) so expect ~1.1-1.2x, not the 1.33x ceiling — that is the
point: it buys a MEASUREMENT of whether traffic falls with h for an afternoon instead of 200
lines, and de-risks the pilot. If traffic does not drop ~42%, the premise is wrong deeper and
compact-gather would have built on a false floor.

GATES (pre-registered):
 1. KLD vs OFF no worse than current armed-N38 leg (mean 0.089763). Identity-preserving change;
    a regression means the remap is wrong.
 2. Decode tok/s above the 0.82x floor. Target band 1.05-1.25x on the single-GPU 18.19 baseline.
 3. DISCRIMINATOR: re-run ARMED N=0 vs N=38. Today identical (11.83 / 11.75). After the fix they
    MUST diverge with N=38 faster. Still identical => remap did not take effect and nothing else
    in the result is trustworthy.

ARCHITECT'S STATED ASSUMPTION: the trick rests on repeated reads of one host address collapsing
to a single PCIe transfer via L2. Expected behavior, NOT verified. Gate 3 tests it directly; if
N=0 and N=38 stay identical that assumption is where it failed — itself worth knowing before
compact-gather.

SEQUENCE: (1) miss-side remap + 3 gates. (2) Land topology finding — single-GPU 18.19 vs split
14.39 = +26% for a config change, zero code, larger than Phase B's net; every Phase-B number must
be measured against the fast baseline or it is flattered by 26%. (3) Re-decide compact-gather
against a measured number, not the architect's extrapolation.

-ot CANNOT EXPRESS EXPERT-LEVEL RANKING (bank this, it is load-bearing). Expert weights are ONE
3D tensor per layer per site, {n_embd, n_ff, n_expert} (llama-model.cpp:3246-3247), so -ot name
matching can only move whole layers. Across layers fetch volume is flat (same top-k every layer)
=> ranking layers buys nothing. The skew that makes ranking valuable — 7.4% of experts absorbing
42% of fetches — lives strictly INSIDE a layer. That asymmetry is the entire justification for
custom code and is why a stock-`-ot` arm is not constructible. The naive control arm is already
in hand from the sweep: 3.4GB as whole layers = 3.6 layers = +1.06 tok/s, vs ranked pins at the
same 3.4GB = +5.94 projected — 5.6x better VRAM efficiency, the phase's reason to exist.

=== END SECTION 16 ===

=== SECTION 17: gather-MMVQ review + owner test set + new gates + PR (2026-09-18) ===

STATE DIVERGENCE (architect re-read the tree; leader verified: `grep -c hydra
llama-graph.cpp`=0, dual symbols gone from llama-context.cpp, `hydra_gather_mmvq` at
ggml-cuda.cu:2046 hooked at :2220, diff vs 86af0c9af +535/-4 with +347 in ggml-cuda.cu).
Builder did NOT implement the §16 miss-side remap; they deleted the dual path and built gather
in MMVQ. Not reversed — MMVQ is the architecturally right place. Consequences:
 (a) THE THREE §16 GATES ARE VOID — defined against code that no longer exists. Nothing is
     currently pre-registered. New gates in §17c.
 (b) The cheap premise test was SKIPPED — never confirmed that avoiding pinned fetches reduces
     PCIe traffic. Comes back as ARM 006.

--- §17a: CODE REVIEW OF hydra_gather_mmvq — 5 findings, each with a fix ---
(Caveat: builder mid-edit; review structure, not polish.)

FINDING 1 — CRITICAL, ARCHITECTURAL. Host readback + stream sync in the decode hot path.
ggml-cuda.cu:2068-2070:
    cudaMemcpyAsync(ids_host.data(), ids->data, ..., cudaMemcpyDeviceToHost, stream);
    cudaStreamSynchronize(stream);
Per site, per layer, per token: 3 sites x 48 layers = ~144 device->host round trips AND 144 full
stream stalls per generated token. A stream sync CANNOT be captured in a CUDA graph — graphs
measured at +9.6% / 1.259 tok/s, so with this structure the forfeit is GUARANTEED by construction,
not likely. Also serializes every layer against the host, killing inter-layer overlap.
FIX: never read ids to host. Upload `slot_of` (n_expert int32/layer/site, a few KB) to device ONCE
at init, select the row address inside the kernel:
    slot = slot_of_dev[expert_id];
    row  = (slot >= 0) ? store_base + slot*row_bytes : host_base + expert_id*row_bytes;
Per-row address selection, nothing more. No readback, no sync, no allocation, fully
graph-capturable. This is the "per-expert pointer swap" — needs no gather/compaction at all.

FINDING 2 — HOT-PATH MUTEX + MAP LOOKUP. ggml-cuda.cu:2002-2005, `hydra_store_for` takes
`std::lock_guard<std::mutex>` + `std::map::find` on a composed int64 key, called from the gather
path => ~144 mutex acquisitions + 144 RB-tree lookups per token.
FIX: resolve every store ONCE at init into flat `store[il][site]`; hot path becomes an array index.
The mutex guards lazy creation only — remove the laziness and the mutex goes with it.

FINDING 3 — HEAP ALLOCATION PER CALL. `ids_host`, `compact_of(ne02,-1)`, `used` are per-call
std::vectors => ~430 mallocs per token in the hot path. FIX: preallocated per-device scratch,
sized once. Disappear entirely if Finding 1's fix lands.

FINDING 4 — O(n_expert) WORK FOR k=10. `compact_of(ne02,-1)` builds/clears a 512-entry table per
call to service 10 used experts. FIX: iterate the 10 used ids directly. Moot if Fix 1 lands.

FINDING 5 — LATENT, CARRY FORWARD. The deleted graph path never applied the per-expert scale
(`w_s`) the normal path applies. Our GGUF has no `ffn_*_exps.scale` so it never fired, but any
model with them would be silently wrong with no error. The new path must apply `w_s` when present
or assert loudly that it is absent.

RANKING: Finding 1 is not a perf nit — it decides whether the phase can work at all. If the
readback stays, expect AT or BELOW the 0.82x it replaces, uninterpretable. Fix 1 before measuring.

--- §17b: NEW TEST SET (owner-handed) ---
(Owner's original arm 002, `-ot` ranked experts, NOT CONSTRUCTIBLE — see §16.)
 ARM 000  OFF, single-GPU            = 18.19 tok/s   HAVE
 ARM 001  OFF, default split         = 14.39 tok/s   HAVE (topology finding, +26%)
 ARM 002  OFF, single-GPU, graphs disabled = 13.13   HAVE — the DENOMINATOR for any implementation
          that forfeits graph capture. Never compare a syncing build to 18.19.
 ARM 003  NAIVE whole-layer residency sweep, 0/4/8 resident = 18.19/19.23/20.55, slope +0.295/layer.
          HAVE.
 ARM 004  VRAM-MATCHED NAIVE CONTROL — the real control arm. ~3.4GB (~3.6 layers), expect ~+1.06
          tok/s. STOCK llama.cpp, no code. RUN THIS — the honest baseline ranked pinning must beat.
 ARM 005  RANKED PINS at the same ~3.4GB, N=38. THE MISSING ARM. Projected +5.94 tok/s (5.6x VRAM
          efficiency vs ARM 004). Requires a working mechanism.
 ARM 006  TRAFFIC PREMISE, mechanism-independent. Instrument actual PCIe bytes/token (DCGM or
          nvidia-smi dmon PCIe counters) at N=0 vs N=38 on whatever implementation is current. The
          phase premise has NEVER been directly measured — only inferred from tok/s. Run even if 005
          is blocked.
ARM 004 and ARM 006 need no working mechanism and no approval. Run now; they make 005 interpretable.

--- §17c: NEW GATES (replacing void §16 gates) ---
 G1 GRAPH CAPTURE SURVIVES. Must not regress toward 13.13 (graphs-disabled). A per-layer sync fails
    this by construction — check FIRST, cheapest.
 G2 DISCRIMINATOR (unchanged in spirit): ARMED N=0 vs ARMED N=38 must DIVERGE, N=38 faster. Today
    11.83 / 11.75 — indistinguishable, the old-defect fingerprint. Still identical => mechanism still
    not reducing traffic; nothing else counts.
 G3 CORRECTNESS. Pointer swap reads the SAME BYTES from a different address => KLD vs OFF ~zero,
    PPL delta ~zero — unlike the dual's +1.37%. A dual-like PPL cost means the implementation does
    more than swap addresses. Free correctness check; pre-registered.
 G4 THROUGHPUT. Must beat ARM 000 = 18.19 (single-GPU OFF), not 14.39. And must beat ARM 004 —
    beating OFF while losing to naive residency means the ranking added nothing.

--- §17d: PR REQUEST (owner directive) ---
Builder opens a PR for the current compact-gather work AS IT STANDS. Not gated — the PR is the review
surface, not a completion claim. Draft/WIP title, `Closes` nothing, ARM 000-003 numbers in the body as
measurement context (004 pending). Architect reviews on the PR with the five findings anchored to lines.

=== END SECTION 17 ===

=== SECTION 17e AMENDMENT: ARM 006 replaced by 006' (architect refinement, 2026-09-18) ===

PROBLEM: ARM 006 as written (PCIe bytes/token N=0 vs N=38 on the current gather code) measures a
mechanism about to be deleted by Finding 1's fix. A null would be ambiguous between "premise false"
and "this implementation broken" — the confound that already cost two cycles.

ARM 006' — PREMISE TEST WITH ZERO CUSTOM CODE. PCIe bytes/token across the EXISTING residency
sweep: 0, 4, 8 resident layers (ARM 003 config, already run, already validated, stock llama.cpp).
No hydra code in the loop. Making a layer resident provably removes its fetches, so a null result
can ONLY mean the premise is false — no confound. Uses a trusted arm with a known tok/s curve
(18.19/19.23/20.55) to correlate against.
DELIVERABLE: the constant BYTES PER EXPERT-FETCH. With it, h converts directly into predicted bytes
saved — prices any future mechanism on paper BEFORE building. Would have rejected the dual on paper.
READ: bytes/token falls ~linearly (~1/48 expert traffic per layer) => premise CONFIRMED, ARM 005
becomes predictable. Bytes/token does NOT fall => premise false, PCIe is not where time goes, phase
dies on evidence for one instrumented sweep + zero code. Worth having either way.
TOOLING: DCGM `dcgmi dmon -e 1009,1010` or `nvidia-smi dmon -s t`, whichever installed — install nothing.
Sample over fixed token count, normalize.

ARM 006 (N=0 vs N=38 on the real mechanism) RE-QUEUED to AFTER Finding 1's fix lands — there it
becomes the direct confirmation the new implementation reduces traffic (what G2 really asks).

REVISED ORDER: PR, then ARM 004 + ARM 006' (both stock, both unblocked, neither gated by PR or Fix 1).

=== END SECTION 17e ===

=== SECTION 17f: corrected read-disagreement rule (architect correction, 2026-09-18) ===

Supersedes the §17-era "quote the sha" shorthand. The 17e false-miss was a TIMING RACE, not a stale
checkout: da2789a5f stamped 2026-09-18T22:44:57, architect's grep ran seconds before. Same worktree,
same branch — absence at T is not absence at T+30s.
RULE: when two sides' reads disagree, quote the sha AND the commit timestamp. The timestamp is what
distinguishes "never written" from "written after you looked." Do not downgrade to a sync problem, and
do not discount file verifications on a false miss — they caught the real lost banks (§12, §15).

=== END SECTION 17f ===

=== SECTION 18: ARM 006-prime REFUTES the weight-streaming premise (architect ruling, 2026-09-18 ~23:00 ICT) ===

RESULT (builder 2ac20e22, turn 1091): PCIe bytes/token across the stock residency sweep =
6.216 / 6.089 / 5.718 MB/tok at 0/4/8 resident layers. Monotonic fall, slope 62.2 KB/layer/tok.

THE REFUTATION (hard physical bound, not an estimate):
  ARM 003 VRAM cost: 8 resident layers = 7.5 GB => 0.9375 GB/layer => 1.875 MB per expert per layer
  Weight-streaming model: k=10 x 48 layers x 1.875 MB = 0.88 GB/token => at 18.19 tok/s = 16.0 GB/s demanded
  PCIe x4 ceiling: ~7.9 GB/s (Gen4) / ~3.9 GB/s (Gen3) — the model demands >2x the physical link. IMPOSSIBLE.
  Measured actual: 6.216 MB/tok x 18.19 tok/s = 113 MB/s — link at ~1-3% of capacity. Gap: 309x.

CONCLUSION: expert weights do NOT stream across PCIe per token. The 62.2 KB/layer/token that falls
with residency is activation-sized (CPU<->GPU round trip per offloaded layer), not weight traffic.

LEADING HYPOTHESIS (architect): --n-cpu-moe runs those layers' expert FFN ON THE CPU BACKEND. The
residency gain (+0.295 tok/s per layer) moves COMPUTE from CPU to GPU, not transfers. Re-explains the
0.82x NO-SHIP: arming forced expert matmul onto the GPU against host-resident weights (cheap CPU
compute traded for PCIe-bound GPU compute) — hence N=0 == N=38. Architect's earlier "doubled lookup"
account is superseded pending the discriminator.

CONSEQUENCES:
  - FIX 1 IS ON HOLD (do NOT build): removing a host readback to pick a weight address buys zero if
    weights are not the constraint. PR #134 review still happens (architect posts on #134); NO code
    changes until the discriminator lands.
  - ARM 005 stays blocked and may be MOOT — the mechanism must be redesigned around compute placement,
    not weight placement, if the hypothesis holds.
  - task-9be0a9a07c (MoE-cache baseline bg legs) runs to completion — measurement-only.
  - Corpus (task-4e0493edd2): architect CONCURS with (b)/(c) rec — off critical path, owner's call.

DISCRIMINATOR (queued immediately behind 9be0a9a07c, ~2 min, no code):
  CPU utilization during decode at t48 (0 resident) vs t40 (8 resident), SAME session, fixed token
  count + nvidia-smi GPU util in the same window.
    CPU pegged at t48 and materially lower at t40 with GPU util rising => CPU-compute CONFIRMED;
      phase premise wrong as written; mechanism redesigns around compute placement.
    CPU flat and low in both => weights move some other way; architect re-derives.
  Optional second confirmation: ggml_backend_buffer_name + which backend executes the MoE node for an
  offloaded layer (scheduler node->backend assignment).

STANDING RULES ADOPTED (architect, banked as rules not caveats):
  1. SAME-SESSION ANCHOR: cross-build A/B carries ~2.4% variance on this rig — every delta is judged
     against a same-session anchor, never a number from another build.
  2. MEASURE-COST-FIRST: before building a mechanism that removes a cost, MEASURE THAT THE COST EXISTS.
     ARM 006' should have been arm 001 of this phase, not the seventh. Third premise on this track that
     went unmeasured until late; this one died on a hard physical bound.

=== END SECTION 18 ===

=== SECTION 18 AMENDMENT: RULING — CPU-BOUND CONFIRMED; thesis corrected; ARM 006 reversed to RUN [VOIDED in §19] (architect, 2026-09-19 ~00:20 ICT) ===

DISCRIMINATOR VERDICT (builder raw: t48 CPU 439/449 GPU 26.6% 17.97 t/s; t40 CPU 404/430 GPU 34.8% 20.19 t/s):
Decisive numbers are the UTILIZATION LEVELS, not the delta:
  t48: CPU 97.8% of observed-max = SATURATED | GPU sm 26.6% (idle 65-73% of the time in both configs)
  t40: CPU 94.0% saturated
The system is CPU-BOUND. Not PCIe-bound (113 MB/s), not GPU-bound (never >35% sm).
The -8% CPU delta reconciles exactly: moving 8/48 layers cuts CPU expert work 16.7%; observed total -8%
iff expert FFN is ~half of CPU time and the rest is fixed overhead (sampling/tokenize/copies/orchestration).
Direction + magnitude + GPU's +31% counter-move all fit. HYPOTHESIS CONFIRMED: --n-cpu-moe runs expert FFN
ON THE CPU. Residency gain = COMPUTE MIGRATION to an idle GPU, never transfer elimination.

0.82x FULLY EXPLAINED (supersedes "doubled compute"): both the old dual construction AND gather-MMVQ compute
ALL experts on the GPU. Arming dragged every expert (hot + cold) onto the GPU, forcing cold expert weights
across PCIe: 48 x 10 x 1.875 MB = 0.88 GB/token = 16 GB/s demanded. The mechanism SATURATED PCIe and lost to
the CPU doing the job cheaply. The PCIe traffic the phase set out to eliminate DID NOT EXIST in the baseline —
THE MECHANISM CREATED IT. That is why armed N=0 == armed N=38 (11.83/11.75): both all-GPU, both PCIe-bound.

CORRECTED THESIS (phase ALIVE):
  OLD (dead): pin hot experts in VRAM to avoid fetching them over PCIe.
  NEW: the CPU is saturated while the GPU idles at 27-35%. Move expert COMPUTE to the GPU at fine
  granularity, bounded by VRAM.
  Prize (unchanged, now for the right reason): full migration = 48 x 0.278 = +13.3 tok/s over the 17.97
  anchor => ~31.3 tok/s (+74%). Ranked pinning at h=0.4207 on 3.4 GB migrates ~42% => +5.6 tok/s => ~1.31x.
  The 1.33x projection SURVIVES.
  MECHANISM REQUIREMENT: SPLIT COMPUTE per expert — pinned experts computed on GPU from VRAM, unpinned
  experts computed on CPU as today. Neither existing design does this; both compute everything on GPU.
  The needed operation is selecting a BACKEND, not selecting a weight address.

ACTIONS:
  - FIX 1: HOLD PERMANENTLY in current form — it optimizes address selection inside a GPU-only path; the
    GPU-only path IS the defect.
  - ARM 006: RUN NOW (deferral reversed — no longer "measuring broken code"; it tests a sharp falsifiable
    prediction). PREDICTION: armed-mode PCIe bytes/token ~100x stock (near link saturation) vs 6.2 MB/tok
    stock. If armed traffic comes back near stock, the whole account is wrong and the architect re-derives.
    This is the confirmation gate for everything above.
  - ARM 005: blocked; design brief changed to CPU/GPU compute split per expert.
  - CPU decomposition (expert-FFN fraction): only if profileable without code changes; else skip.
  - Baseline hang retry (10-min timeout + telemetry): APPROVED, one attempt, second hang => issue + stop.
  - STANDING RULE (upgraded): measure that the cost exists — AND measure the utilization of EVERY unit
    involved, not just the suspect. CPU 97% + GPU 27% were available from pidstat + nvidia-smi all along.

=== END SECTION 18 AMENDMENT ===

=== SECTION 19: ARM 006 ruled VOID; thread sweep is the live gate; baseline data struck; D1-D4 (architect, 2026-09-19 ~01:00 ICT) ===

ITEM 1 — ARM 006: VOID, NOT REFUTED (lead reading upheld; builder's "REFUTED as measured" wrong).
The gather hook was installed in the MMVQ branch; decode dispatches to MMQ (mmvq=0, mmq=2192) — zero
engagements BY CONSTRUCTION. Armed path was a total no-op; near-stock traffic (0.957x/0.947x) is the
no-op signature; tok/s 17.80/17.85 vs 17.7476 anchor = stock within the 2.4% band. You cannot refute a
prediction about a working mechanism with a measurement of a mechanism that never ran.
STATE-BANK FAILURE NOTED: "mmvq=0 / MMQ-dispatch" was ALREADY KNOWN on this track and lost across the
leadership handoff. ACTION: known dispatch facts now live in this bank (see below) so the next hook is
not hung on the wrong branch.
ARCHITECT WITHDRAWS the 100x-traffic prediction test: the ~100x claim explains why the OLD (deleted)
mechanism failed — proving why a dead thing died is archaeology, and the only test path touches the
frozen surface (Fix-1-adjacent hook work). Account held as "best explanation, untested."

KNOWN DISPATCH FACTS (new standing bank section, per architect):
  - Decode dispatches to MMQ; the gather hook lives in the MMVQ branch -> any decode-time gather
    instrumentation or hook MUST target the MMQ path or it measures nothing.
  - Pre-PR finding (builder): fused path bypasses the thunk hook entirely.
  - [HYDRA mmid] counters (mmvq=/mmq=/hit=/miss=) are the engagement tell — check non-zero BEFORE
    reading any mechanism measurement.

ITEM 2 — BASELINE legs (task-9be0a9a07c): DATA STRUCK, measurement NOT re-run.
sha256("") = e3b0c44298fc1c14... — confirmed: outputs were EMPTY, "PARITY_OK la0==la0b" was two empty
files agreeing (vacuous). 187x-under-reference decode, 61h+29m uptime on a fresh process, unlogged PPL
load failure, and the 26-min load hang are most likely ONE broken model load presenting five ways.
None of the numbers may anchor anything. The baseline served the MoE-expert-cache thesis — NO-SHIP
closed it and §18 repointed the phase; re-running buys data for a question we no longer ask.

HARNESS DEFECTS -> ISSUES (D1 first, it is the dangerous one):
  D1 (SEVERE) A gate that cannot fail: parity check reports PARITY_OK hashing EMPTY output — PASS on
     no data. Same class as the zero-width control band (§15/§17) and the vacuous SWA checkpoint gate.
     FIX: every hash-based gate must assert non-empty, non-trivial input before comparing; FAIL CLOSED
     on an empty artifact.
  D2 Uptime/hot-snapshot counter carries across runs (61h+29m on a fresh process) — cross-invocation
     state leak; will silently contaminate future arms that read it.
  D3 llama-perplexity model-load failure with cause unlogged.
  D4 Model load hangs ~26 min vs 67s norm, twice — reproducible defect, not transient.
  Forensics: skip PPL retry. ONE cheap D4 diagnostic ONLY if the thread sweep shares the model-load
  path (a 26-min hang would block live work); else file and move on.

NEW LIVE GATE — THREAD SWEEP (zero code, stock binaries):
  Same session, fixed token count, at t48 (0 resident) AND t40 (8 resident); -t 2/4/6/8/12 (or to
  physical cores). Record tok/s + CPU% per point.
  PRE-REGISTERED PREDICTIONS:
    CPU-BOUND (architect account): tok/s rises materially, near-linearly with threads, plateauing at
    core count; t48 MORE thread-sensitive than t40 (more expert work on CPU).
    NOT CPU-BOUND: tok/s roughly flat across threads -> CPU-bound thesis dead, compute-migration
    explanation dies with it, architect re-derives from scratch.

STANDING RULE (banked alongside §18's): A GATE MUST BE ABLE TO FAIL. Before trusting any PASS, confirm
the gate has a reachable failure mode on the actual artifact — empty input, zero-width band, and vacuous
preconditions have produced false PASSes three times on this track.

QUEUE: 1) thread sweep t48+t40; 2) file D1-D4 (D1 first); 3) ARM 005 blocked pending sweep + compute-split
design; 4) PR #134 review — note the hook is on the wrong dispatch branch (MMVQ vs MMQ) as headline finding.

=== END SECTION 19 ===

=== SECTION 19 AMENDMENT: gh REMOTE TRAP (safety) + Gate A/B rule (architect verification pass, 2026-09-19 ~01:30 ICT) ===

VERIFIED CLEAN: section 19 (c5e078d97) + all four defect issues confirmed on ddvnguyen/llama.cpp.
Issue-number map: #135=D1 (gate cannot fail), #138=D2 (counter leak), #136=D3 (unlogged load failure),
#137=D4 (26-min hang).

THE TRAP (SAFETY, not convenience): in src/llama-cpp the git remotes are
    origin     https://github.com/ggml-org/llama.cpp        <-- UPSTREAM, PUBLIC, NOT OURS (gh's default)
    hydra-fork https://github.com/ddvnguyen/llama.cpp       <-- OURS
    gs         https://github.com/GenerelSchwerz/llama.cpp  <-- third party
gh DEFAULTS TO origin: any bare gh issue/pr command run inside the submodule silently resolves against
PUBLIC UPSTREAM ggml-org/llama.cpp. The architect's verification queries hit ancient unrelated upstream
issues three times before catching it. READS are merely wrong; a bare `gh issue create` or `gh pr comment`
from that directory posts OUR internal defects, perf numbers, and fork internals to the PUBLIC tracker
under the owner's account — irreversible, no confirmation step.

STANDING RULE (enforce on EVERY worker, all gh verbs — create/comment/view/list — because the habit
protects the writes): every gh invocation touching src/llama-cpp MUST pass --repo ddvnguyen/llama.cpp
explicitly. Never rely on the default remote.
AUDIT RESULT (lead, 2026-09-19): zero ddvnguyen artifacts found on ggml-org/llama.cpp (issues, PRs, and
comment search all empty); every gh call issued by the leader this session carried explicit --repo.

GATE A/B RULE (architect adoption + generalisation of the leader's engagement-counter rule):
  A mechanism arm has TWO gates, and the engagement gate comes FIRST:
    Gate A (engagement): did the mechanism actually execute? (mmid counters, fire counters, hit/miss non-zero)
    Gate B (effect):     did it change the thing we care about?
  A Gate-B number collected while Gate A is zero is not evidence in EITHER direction.
  This single rule would have saved ARM 006, the fire=[0,1,1,1] episode, and the Stage-1 prefill campaign —
  three separate cycles. It sits beside the gates-must-fail rule.

=== END SECTION 19 AMENDMENT ===

=== SECTION 20: thread sweep ruling — CPU-bound confirmed, bandwidth refinement [RETRACTED in §20d — status UNPROVEN], UNTUNED BASELINE finding (architect, 2026-09-19 ~01:45 ICT) ===

1. PRE-REGISTERED CALL: CPU-BOUND CONFIRMED. "NOT CPU-bound => flat" branch dead: t2->t12 t48 +75%, t40 +74%.
   §18 holds; ARM 005 rationale survives.

2. SUB-PREDICTION FAILED (architect owns it): t48 NOT more thread-sensitive (+75% vs +74%, identical).
   REFINEMENT from the failure — efficiency tok/s-per-CPU% at t48: 0.0645/0.0585/0.0491/0.0414/0.0316 =
   51% COLLAPSE t2->t12. Pure core-scaling holds efficiency ~constant; this decline is contention on a
   SHARED resource: the CPU side is MEMORY-BANDWIDTH-BOUND, not core-count-bound. Both configs hit the
   same ceiling -> identical relative curves; the shape is the bandwidth wall, not expert-work distribution.
   LESSON: "CPU-bound" is not one hypothesis — the thread curve discriminates core-bound from
   bandwidth-bound. Pre-registrations must distinguish them.
   Minor (directional, not banked as quantity): bandwidth-limited expert-weight reads from system RAM mean
   moving experts to VRAM relieves BOTH GPU-idle and RAM-bandwidth pressure — helps ARM 005 directionally.

3. RESIDENCY GAIN ROBUST: t40 > t48 at EVERY thread count: +1.41/+2.23/+2.05/+2.15/+2.31 — ~constant
   +2.1-2.3 regardless of threads. At t12: 0.289 tok/s per resident layer, in line with ARM 003 (0.295)
   and discriminator (0.278) — THREE independent measurements agree. Re-priced at t12: full residency
   +13.9 tok/s; ranked pinning at h=0.4207 = +5.83 on a 19.96 base = 1.292x. The 1.31x projection
   survives a third re-derivation on a better baseline.

4. THE FINDING THAT MATTERS MOST — UNTUNED BASELINE: tok/s still RISING at t12, no plateau. Best observed
   22.27 (t40/t12) vs the 18.19 "baseline" = +22% (+25% vs 17.7476 anchor) from a THREAD FLAG. Stacked on
   topology (single-GPU 18.19 vs split 14.39, +26%, also config-only): this track spent months optimising
   a mechanism while the stock configuration was never tuned. Two flags appear worth more than everything
   the mechanism has delivered — and they are free.
   GATE FOR ARM 005 (explicit, not a suggestion): ARM 005 must be measured against the BEST stock
   configuration, not 18.19. Judging a mechanism against an untuned baseline flatters it 20-25% — same
   error class as comparing a syncing implementation to the graphs-enabled number (§17c/G4).

5. QUEUE:
   a) EXTEND THE SWEEP NOW (zero code, approved): t16/t24/t32 at BOTH t48+t40 + CPU% + GPU sm%. Find the
      plateau -> (i) true best-config baseline ARM 005 must beat; (ii) possibly free throughput for the
      owner today. Note: 6.3 cores of useful work from 12 threads (52% per-thread efficiency, down from
      88% at t2) — contention heavy, plateau may be near; "may be" is why we measure.
   b) RE-ANCHOR everything once the plateau is known (ARM 003/004 residency numbers were taken at t8 or
      below — restate at the best thread setting).
   c) ARM 005 blocked on a compute-split design; brief carries the best-config gate.
   d) Sweep report includes GPU sm% — if GPU utilisation is still ~30% at the plateau, that number IS the
      headroom ARM 005 is aiming at.

6. FOR THE OWNER (standing item, not buried in arm reports): two zero-code configuration wins measured and
   unclaimed — THREADS (+22%, not yet exhausted) and TOPOLOGY (+26%). Worth more than the mechanism.
   Landing them is the owner's call.

=== END SECTION 20 ===

=== SECTION 20a: PRE-REGISTRATION, extended sweep (t16/t24/t32 x t48+t40) — banked BEFORE the builder's yield (architect, 2026-09-19) ===

Rationale: the architect adjudicated post-hoc twice this track and erred both times (the zero-width
control band; the t48-sensitivity call). Pre-registration is the fix. BANKED BEFORE DATA: verified at
bank time that the builder's sweep yield had NOT arrived (uc 2108, bg legs mid-run, no summary lines).

P1. PLATEAU. Definition: first thread count whose tok/s gain over the previous point is <2%.
    - Knee at or below t16: t12 was already near-best; the untuned-baseline finding settles at ~+22%.
      Still real, still worth landing.
    - Still climbing >5% per step at t32: the box has far more headroom than assumed, the "baseline"
      used all track is badly wrong, and EVERY mechanism number in the bank is mis-scaled.
      High-consequence branch — do not soften it if it lands.
    - PREDICTION: knee at t16-t24, gains decaying but nonzero at t32. Held to it.

P2. EFFICIENCY (tok/s per CPU%). Should keep falling monotonically. If it FLATTENS while tok/s still
    rises, the bandwidth-bound refinement in §20.2 is WRONG and the constraint is core-count after all.
    A reachable failure mode for the architect's own refinement — meant to be falsifiable.

P3. GPU sm% — the ARM 005 prize stated as a number.
    - ~30% at the plateau: GPU idle is structural, compute-split has real headroom, ARM 005 proceeds.
    - past ~60% at the plateau: threads were starving the GPU, not the mechanism's absence. ARM 005's
      headroom is then MUCH smaller than 1.29x and the arm gets RE-PRICED BEFORE any build work.
      The architect will not defend 1.29x through this branch.

P4. RESIDENCY DELTA (t40 minus t48). Should hold ~+2.1-2.3 at the new thread counts. If it COLLAPSES
    toward zero at high thread counts, residency was buying CPU relief that threads now buy more
    cheaply, and ARM 005's rationale weakens materially. Also reachable, also intended.

ADJUDICATION ORDER when the yield lands: P3 first (it can re-price the arm), then P1, then P2, then P4.
Report all four raw, no interpretation.

OWNER-DECISION NOTE (architect recommendation, decision is the owner's, nothing blocked): land the two
config wins (threads + topology) AFTER the plateau is known, not now — landing mid-sweep moves the
reference underneath the measurement. One rig run, then land both together and re-anchor.

=== END SECTION 20a ===

=== SECTION 20b: extended-sweep ruling + ROOT CAUSE (hybrid P/E scheduler) [RETRACTED in §20c] + ARM 005 re-price + PRE-REGISTERED affinity discriminator (architect, 2026-09-19 ~02:10 ICT) ===

P3 GPU SM%: PROCEED branch. 33.1 (t48/t16) / 35.3 (t40/t16) at peak — GPU ~65% idle at best stock config;
structural, not a threading artifact. Modestly above the registered "~30" — noted, not material.

P1 PLATEAU: knee LOCATION right (t16, both configs), SHAPE wrong — architect predicted saturation,
box gives REGRESSION (t24/t32 negative). The <2% single-step plateau definition is DEFECTIVE (fired at
t8 on a curve that re-accelerated). CORRECT DEFINITION (method lesson, use from here): plateau = argmax
over the swept range, confirmed by two consecutive non-improving steps. High-consequence branch (>5%/step
at t32) did NOT land — baseline error is BOUNDED at +26.4%.

P2 EFFICIENCY: registered falsifier ("flattens while tok/s rises") did not fire — but the architect
declines the win: the test could not discriminate what it asked (see root cause). §20.2 bandwidth-bound
refinement is UNFALSIFIED BUT UNDER SERIOUS DOUBT.

ROOT CAUSE — SWEEP RAN ON A HYBRID P/E CPU WITH NO AFFINITY (architect checked host directly):
  CPUs 0-15  = 8 P-cores x SMT, max 5100 MHz
  CPUs 16-19 = 4 E-cores,       max 3900 MHz   (i7-12700K, 12c/20t)
Peak at t16 = exactly the P-core logical thread count. Past t16 the pool spills onto E-cores; graph
compute is barrier-synchronised per node so the SLOWEST thread gates every barrier — E-cores drag the
whole graph, which is why tok/s AND CPU% fall together (820->729->707). CONSEQUENCE: every sweep point
(t2-t12 included) ran with OS-chosen placement across heterogeneous cores, unrecorded. tok/s numbers
remain valid as "what the stock config delivers"; the MECHANISTIC reading (core-bound vs bandwidth-bound)
is NOT extractable from them — the efficiency decline is equally consistent with SMT sibling sharing,
silent E-core placement, or memory bandwidth. This confound voids the P2 pass as evidence.

PRE-REGISTERED AFFINITY DISCRIMINATOR (zero code, one rig run; banked BEFORE data):
  A) t40, taskset -c 0-15 + -t 16      (P-cores only, SMT on)
  B) t40, taskset -c 0,2,4,6,8,10,12,14 + -t 8   (P-cores only, one thread per physical core, no SMT)
  C) t40, -t 16 unpinned               (control, reproduce 23.00)
  Fork native affinity available if preferred: common/arg.cpp:1534 `-C, --cpu-mask M`, :1554
  `--cpu-strict <0|1>`, :1571 `--poll`. Either fine; report which was used.
  PREDICTIONS (held to them): A > C (pinning removes E-core barrier drag). If B ~= A at roughly HALF the
  CPU% -> memory-bandwidth-bound, §20.2 refinement CONFIRMED on a test that can see it. If A >> B ->
  execution/core-bound, refinement WRONG and retracted. Architect predicts B ~= A — the branch that
  costs them if wrong.

P4 RESIDENCY DELTA: landed BETWEEN the two registered branches (+1.58 at peak; +2.01/+2.19 at t24/t32) —
same defect class as P1's definition (pre-registration offered two outcomes, data landed in the gap;
architect owns it). NOT a collapse; P4 does not kill the arm. But at the peak: 1.58/8 = 0.1975 tok/s per
resident layer, down 32% from 0.289 at t12 — threads bought part of what residency was buying.

ARM 005 RE-PRICED AT THE PEAK (not t12):
  40 remaining CPU layers x 0.1975 = +7.90 fully resident; ranked pinning h=0.4207 = +3.32;
  23.00 + 3.32 = 26.32 vs best stock 23.00 = 1.145x.
  CEILING FALLS 1.292x -> ~1.15x — first downward move in four derivations. The §20.4 gate
  (judge against best stock config) caught the architect's own number.
  RECOMMENDATION: DO NOT BUILD YET. Measured mechanism overhead when last armed: 0.82x; the
  ceiling-to-execution gap is the whole risk and it just shrank by more than half its margin.
  NOT killed — P3 says GPU idle is real and structural. One rig run (affinity discriminator)
  decides whether ARM 005 is worth building at all: B~=A (bandwidth) -> arm worth MORE than 1.15x
  (VRAM residency relieves the binding constraint); A>>B (core) -> worth less. ARM 005 stays
  blocked; compute-split brief stands.

RE-ANCHOR (item b): adopt t40/-t 16 @ 23.00 tok/s as the ARM 005 reference NOW. Caveat: if the affinity
run beats it, the reference moves again (§20.4 gate = best stock config, not first-measured). Restate
ARM 003/004 residency numbers at t16 — they were taken at t8 or below, wrong operating point.

FOR THE OWNER: best stock config 23.00 vs default split topology 14.39 = 1.60x — a 60% throughput gain
from two configuration flags, no code (threads +26.4% of it). Measured, repeatable, unclaimed.
Recommendation: land threads + topology together now that the plateau is known, then re-anchor.
Uncomfortable note for the bank: a scheduler placing threads on E-cores was costing this rig more than
anything the MoE mechanism has yet delivered — found by sweeping a flag.

=== END SECTION 20b ===

=== SECTION 20c: affinity-discriminator ruling — §20b root cause RETRACTED, bandwidth wall CONFIRMED [RETRACTED in §20d — status UNPROVEN], track scoring rule [STRUCK in §20d], D1 integrity gate + audit, reference-is-a-configuration, PRE-REGISTERED D/E/F (architect, 2026-09-19 ~02:50 ICT) ===

SCORECARD: "A > C" FAILED (A 20.97 < C 23.94, unpinned +14.2% faster). "B ~= A at roughly half CPU%"
MATCHED exactly (21.00 vs 20.97 = +0.14%; CPU 58.5%) — the branch the architect registered as costly
paid off.

§20b ROOT CAUSE RETRACTED BY ITS AUTHOR: the E-core barrier-drag mechanism is REFUTED — leg C is
unpinned (HAS E-core participation) and is the FASTEST leg. The t16 = P-core-logical-count match was a
NUMERICAL COINCIDENCE; a mechanism was built on it and banked as root cause after a single lscpu.
Third instance of the same error shape (zero-width band; t48-sensitivity; now E-core drag): A COINCIDENCE
THAT FITS IS A HYPOTHESIS, NOT A FINDING — it needed the test before the bank. REPLACEMENT: plain
oversubscription (20 logical CPUs; t24/t32 exceed). CONSEQUENCE: t16-t24 never measured — the true peak
may sit near t20 and §20b's "plateau" may be a sampling artifact.

§20.2 BANDWIDTH REFINEMENT CONFIRMED on direct evidence, three signatures from clean legs:
1. SMT buys nothing: 8->16 threads on the SAME 8 physical cores = +0.14% throughput for +71% CPU.
2. Threads stall on memory: leg B = 8 threads on 8 EXCLUSIVE physical cores at 382% CPU = 47.7%
   utilisation — threads idle 52% while holding dedicated cores: memory-starved, not compute-starved.
3. Throughput tracks DISTINCT PHYSICAL CORES, not thread count/clock: B (8 phys) 21.00 vs C (up to 12
   phys incl. 3.9 GHz E-cores) 23.94. More cores = more outstanding memory requests = more aggregate
   bandwidth. Pinning to 8 P-cores CAPPED memory-level parallelism — why A lost to C.
WALL ON THIS RIG = RAM BANDWIDTH. Both halves banked: the §20b MECHANISM was wrong AND the §20.2 CLAIM
was right — the confirmation does not launder the retraction.

CONTROLLING FACT + TRACK SCORING RULE (banked): at the best config the machine is idle on BOTH sides
(GPU ~65-70% idle, sm 30-36 everywhere; CPU threads stalled ~52%). Neither processor is the constraint;
BYTES PER TOKEN OFF SYSTEM RAM is the constraint. Every future proposal is scored FIRST by: does it
reduce bytes read from system RAM per token? Expert residency does (measures positive); thread tuning
does not (saturates).

DATA QUALITY:
D1 GATE (mandatory): the 41.81 leg with EMPTY sampler files is INVALID BY CONSTRUCTION, not a flake —
the harness emitted a plausible tok/s for a run that generated nothing. GATE: every leg must assert
non-empty sampler output AND token count matching the requested decode length; a leg failing the
assertion is VOID MECHANICALLY, never by someone noticing it looked odd.
D1 AUDIT RESULT (lead, 2026-09-19): ALL banked decode-throughput numbers on this track (ARM 003/004
era, both thread sweeps, the discriminator's trusted legs) were taken WITHOUT the mechanical assertion.
Mitigating corroboration, post-hoc only: builder-reported sample counts n>0 on all trusted legs,
replicate agreement (17.97/18.13 discriminator vs sweep), three-way residency agreement (0.289/0.295/
0.278), prefill consistency. The 41.81 leg PROVES the hole is live — caught by replication, not by a
gate. The baseline N=36/42 empty-output (sha256("")) was the same class, caught post-hoc.
D2 GUARD: unique port per leg + assert the serving PID is the one this leg started, before measurement.
D3: prefill identical 38.6-38.7s across clean legs = genuine comparability check; keep reporting it.

C DRIFT RULING: 23.94 vs 23.00 = +4.1% against the ~2.4% same-binary variance rule -> C did NOT
reproduce 23.00; the rule is NOT softened ("that is how rules die"). GENERALISATION: THE REFERENCE IS A
CONFIGURATION, NEVER A STORED SCALAR — the ARM 005 reference is "t40, unpinned, -t 16", NOT 23.00 and
NOT 23.94; every arm measures its own same-session control in that configuration and is judged against
THAT. Re-anchor answer: the reference does NOT move to 23.94; it stops being a number at all.

ARM 005: direction validated (bandwidth wall confirmed -> expert residency relieves the binding
constraint), number NOT revised upward — the architect declines to cash their own §20b conditional
(cashing it would be the §20.4 flattery): the 0.1975 tok/s/layer empirical figure already CONTAINS
whatever bandwidth relief VRAM residency provides. Ceiling vs an unpinned-t16 same-session control:
40 x 0.1975 x 0.4207 = +3.32 -> ~1.14x, unchanged. Demonstrated mechanism overhead 0.82x.
ARM 005 stays BLOCKED, not killed; 1.14x does not buy a build slot.

PRE-REGISTERED D/E/F (banked BEFORE dispatch):
  D) taskset -c 0,2,4,6,8,10,12,14,16,17,18,19 + -t 12   (all 12 PHYSICAL cores, one thread each)
  E) unpinned -t 20
  F) unpinned -t 18
  All t40, fresh server per leg, UNIQUE PORT, D1 sampler-assertion ON, report tok/s + CPU% + GPU sm%
  + prefill. PREDICTIONS (held to them):
   - E or F beats C — true peak expected in the 18-20 range, not 16.
   - D lands within ~3% of C at MUCH lower CPU% (~600% vs 784%): same 12 physical cores, no SMT
     redundancy. If D matches C, D is the SHIP config (same throughput, far lower CPU, headroom for
     coordinator + Store).
   - If D is materially BELOW C, SMT contributes after all and signature (1) is weaker than claimed —
     the reachable failure mode for this ruling's central claim.

=== END SECTION 20c ===

=== SECTION 20d: D/E/F ruling — bandwidth conclusion RETRACTED (status UNPROVEN), scoring rule STRUCK, spare-capacity HYPOTHESIS with pre-registered H/I/J control (architect, 2026-09-19 ~03:30 ICT) ===

IN PLAIN WORDS AT THE TOP, per the architect: two conclusions banked, two retracted, both because a
mechanism was banked before its test. §20b's E-core drag; §20c's RAM-bandwidth wall. What survives is
a six-leg dataset that separates perfectly on a variable none of us was controlling.
(The one thing that worked exactly as designed: the D1/D2 mechanical gates held on their first full
outing. The architect directs the leader to tell the builder — done same turn.)

SCORECARD: 0 for 2. "E or F beats C, peak 18-20" FAILED (F 23.99 vs C 23.94 = tie +0.21%; E 20.32 =
-15.2%). "D within ~3% of C" FAILED on throughput (D -13.3%; CPU% landed where predicted, worth nothing
alone).

ADJUDICATING THE REGISTERED FAILURE MODE — worse than "weaker":
- Signature (1) "SMT buys nothing" is VOID AS EVIDENCE, not merely weaker: A and B were BOTH sitting at
  the same artificial ceiling (0 spare), so the A-vs-B comparison could not have detected SMT no matter
  what SMT does. A signature drawn from two legs sharing an uncontrolled confound is not weakened when
  the confound is found — it is void.
- Signature (3) "throughput tracks distinct physical cores" is REFUTED outright: D (12 physical, 1
  thread each) 20.76 vs B (8 physical) 21.00 — more cores, slightly LESS throughput.
- Signature (2) "leg B threads idle 52% => memory-starved" has a simpler competing account the
  architect failed to consider: in a t40 config CPU expert-FFN work and GPU work ALTERNATE per layer —
  CPU threads idle while the GPU runs and vice versa. That one account explains BOTH the ~52% CPU stall
  AND the ~65% GPU idle previously treated as two facts pointing at a shared bandwidth wall.
- CONSEQUENCE: §20c's "the wall on this rig is RAM bandwidth" is RETRACTED — STATUS UNPROVEN. §20.2's
  refinement (which §20c had promoted to confirmed) is retracted with it. The §20c TRACK SCORING RULE
  ("score every proposal by whether it reduces RAM bytes per token") is STRUCK — it may well be right
  but currently rests on nothing, and a scoring rule resting on nothing steers years of work. The
  alternating CPU/GPU-phase account is a HYPOTHESIS, not a replacement finding, and is not banked as one.

WHAT THE SIX LEGS ACTUALLY SHOW — PERFECT SEPARATION ON SPARE CPU CAPACITY
(available logical CPUs to the process minus compute threads):
  A mask 0-15 t16  20.97  CPU 653  avail 16  thr 16  SPARE 0
  B mask 8P   t8   21.00  CPU 382  avail 8   thr 8   SPARE 0
  D mask 12ph t12  20.76  CPU 507  avail 12  thr 12  SPARE 0
  E unpinned  t20  20.32  CPU 712  avail 20  thr 20  SPARE 0
  C unpinned  t16  23.94  CPU 784  avail 20  thr 16  SPARE 4
  F unpinned  t18  23.99  CPU 856  avail 20  thr 18  SPARE 2
Zero spare: 20.32-21.00, mean 20.76. Two+ spare: 23.94-23.99, mean 23.96. Ratio 1.154. Six legs, two
clusters, ZERO overlap — and the split is not core count, not thread count, not SMT, not pinning (E is
unpinned and lands slow; B/A/D have 8/8/12 cores and land within 0.24 tok/s of each other).
PROPOSED MECHANISM (HYPOTHESIS ONLY, NOT A FINDING): the process needs spare logical CPU for its
NON-compute threads (CUDA submission, server, sampler); when the compute pool consumes every allowed
CPU, those threads contend and the pipeline drops ~15%. Retro-explains the original sweep: t12 (8
spare) 22.27 < t16 (4 spare) 23.00 < t18 (2 spare) 23.99, cliff at t20 (0 spare), regressions t24/t32
(oversubscribed). Peak = logical minus ~2. The architect does NOT bank this — it explains 6/6 legs post
hoc, exactly the situation where they have been wrong twice (the E-core story also explained everything
until tested). It enters the bank as a HYPOTHESIS with its decisive control pre-registered below;
NOTHING downstream may cite it until the control returns.

PRE-REGISTERED CONTROL (banked BEFORE dispatch, same commit as the ruling):
  H) taskset -c 0-19 (ALL 20 logical — pinned but mask = whole machine) + -t 18. t40. THE DECISIVE LEG:
     separates "taskset per se hurts" from "zero spare hurts" — H is pinned AND has 2 spare.
       H ~= 24 => pinning innocent, SPARE CAPACITY is the variable; hypothesis survives.
       H ~= 21 => taskset itself is the culprit; spare-capacity account WRONG (pinning, not spare, put
                  A/B/D in the slow cluster); E unexplained; architect would have nothing.
     PREDICTION: H ~= 24. Held to it.
  I) taskset -c 0-15 (8 P-cores, 16 logical) + -t 14. t40. Two spare INSIDE a small mask.
       I ~= 24 => spare capacity nearly everything, physical core count barely matters (strong).
       I ~= 21 => spare necessary but not sufficient; core count matters too.
     PREDICTION: I lands between, 22-23. Stated so it can be wrong.
  J) unpinned -t 19 (1 spare). Locates the cliff edge: is 1 spare enough, or is 2 the minimum?
     PREDICTION: J ~= 23.5 (1 spare nearly enough).
  All t40, fresh server, unique port, D1 + D2 gates ON; report tok/s + CPU% + GPU sm% + prefill.

OPERATIONAL (ship guidance): best stock is t16 or t18 unpinned, tied ~23.9-24.0. SHIP -t 16 UNPINNED,
not t18 — t18 buys nothing measurable and sits one step from the 15% cliff at t20; t16 keeps 4 CPUs of
margin. Robustness decides between tied configs. §20c reference ruling stands: reference is the
CONFIGURATION "t40, unpinned, -t 16", never a scalar; every arm measures its own same-session control.

ARM 005 — UNCHANGED, STILL BLOCKED: per-layer residency (0.1975) was measured at t16 unpinned = inside
the good cluster, NOT contaminated. Ceiling ~1.14x vs demonstrated 0.82x overhead. Note against the
architect: the bandwidth argument can no longer support the arm (retracted); the arm now rests only on
the measured per-layer number and the structural GPU idle.

FOR THE OWNER: best stock ~23.99 vs default split 14.39 = 1.67x — unchanged in substance, better
located. Zero code, unclaimed, owner's call.

=== END SECTION 20d ===

=== STANDING RULE: header retraction stamps (architect directive, 2026-09-19 ~03:45 ICT) ===
When a section retracts, voids, or strikes an EARLIER section's conclusion, the retracting commit MUST
also stamp the retracted section's HEADER in place with "[RETRACTED in §NN]" / "[VOIDED in §NN]" /
"[STRUCK in §NN]". Bodies are NEVER rewritten — the original reasoning stays exactly as banked, errors
included; only headers carry the marker. A retraction that lives only in the newer section is not a
retraction; it is a contradiction the reader has to discover. Rationale: this file is the handoff
artifact someone will GREP, not read — a header advertising a dead conclusion as CONFIRMED is the most
expensive kind of stale state.
Retroactive audit result (same commit): stamped §18 AMENDMENT (ARM 006 -> [VOIDED in §19]), §20
(bandwidth refinement -> [RETRACTED in §20d — status UNPROVEN]), §20b (P/E root cause -> [RETRACTED in
§20c]), §20c (bandwidth wall -> [RETRACTED in §20d — status UNPROVEN]; scoring rule -> [STRUCK in §20d]).
NOT stamped, with reasons: §18 "REFUTES the weight-streaming premise" (never explicitly retracted by a
later section — the thesis correction amended the mechanism account, not the refutation); the struck
baseline data (§19) and the zero-width control band (§20a rationale) never lived in section headers —
they are body-level corrections already carried by their superseding sections; §16 and §15 already carry
their own supersession markers.
=== END STANDING RULE ===

=== SECTION 20e: H/I/J ruling — SPARE-CAPACITY EFFECT graduates to banked finding, MECHANISM stays hypothesis; effect/mechanism standing rule; J recorded not chased; O1/O2; n-cpu-moe sweep PROPOSED-NOT-SCHEDULED (architect, 2026-09-19 ~04:20 ICT) ===

SCORECARD: 1 hit / 1 miss / 1 near. H ~= 24 MATCHED (23.89 — pinning innocent). I 22-23 MISSED (23.82,
above band — failed in the direction that helps the architect, recorded as a miss not a win: their model
said physical core count would contribute; it contributes essentially nothing). J ~= 23.5 NEAR (22.91,
-4.2% under the 2+-spare mean, outside the 2.4% variance rule).

WHAT GRADUATES — THE EFFECT, as a banked finding:
  On this rig, decode throughput drops ~15% when the compute thread count consumes every logical CPU
  available to the process; leaving >=2 logical CPUs unoccupied by compute threads recovers it; beyond
  2 spare there is no further gain.
  Evidence order: (1) CONTROLLED SINGLE-VARIABLE PAIR — A vs I share the SAME mask (taskset 0-15),
  same hardware/flags; only 16 vs 14 threads: 20.97 -> 23.82, +13.6%. Cleanest evidence in the dataset;
  it did not exist before the hypothesis was tested. (2) PRE-REGISTERED DECISIVE CONTROL that could have
  killed it — H (pinned AND spare) came back 23.89 against a written kill condition. (3) CONFOUND-
  BREAKING BY DESIGN — I breaks core count: 8 P-cores with 2 spare = 23.82 vs full machine 23.94 (0.5%);
  pinning, SMT, physical core count all excluded. (4) NINE LEGS MONOTONE IN SPARE: 0 -> mean 20.76;
  1 -> 22.91; 2+ -> mean 23.91; ratio 1.152, no overlap. Graduates prospectively, not post hoc — the
  standard set after the two retractions, and the reason it graduates where the E-core story did not.

WHAT DOES NOT GRADUATE — THE MECHANISM, stays a HYPOTHESIS, status OPEN:
  "CUDA submission thread / server / sampler need CPU" is an INFERENCE; nothing in the nine legs measured
  which threads want the CPU or why. Plausible, predicts nothing tested, barred from load-bearing use.
  STANDING RULE (the correction for both retractions — the effect survived twice while the mechanism
  died twice): EFFECT AND MECHANISM GRADUATE SEPARATELY, AND THE EFFECT NEVER CARRIES THE MECHANISM IN
  WITH IT.

J / GRADED CLIFF — recorded, NOT chased: 22.91 at 1 spare is genuinely intermediate (+9.1% over best
0-spare, -4.2% under 2+ mean, both outside variance) — transition is GRADED, not a step at 2. The
architect DECLINES another 1-spare leg: no decision hangs on it (curve saturates at 2 spare: 2 -> 23.99,
4 -> 23.94, tied); rig time is the owner's. J marked as a SINGLE UNREPLICATED OBSERVATION. Note for
later: if mechanism work ever resumes, stepped-vs-graded discriminates "exactly N helper threads" from
"proportional contention" — that is the reason someone might want the replicate.

OPERATIONAL — unchanged, better supported: SHIP -t 16 UNPINNED (4 spare). t18 ties (23.99) but sits one
step from the cliff; t16 keeps margin. Do NOT pin — pinning buys nothing (H) and adds a way to get it
wrong. Reference stays a CONFIGURATION per §20c.

OBSERVATIONS ON RECORD (both from raw data, neither a finding):
O1. Prefill = 39s on ALL NINE legs — 8 to 20 threads, pinned and unpinned, invariant. In a t40 config,
    40 layers of expert FFN run on the CPU during prefill too, so a CPU-sensitive prefill should have
    moved. It did not move at all: either prefill is dominated by something non-CPU, or "prefill"
    includes a large fixed cost (model load/warmup) that swamps it. INSTRUMENT QUESTION — before anyone
    optimises prefill, confirm what the 39s actually measures.
O2. GPU sm% 35-37 across all nine legs regardless of configuration — structural idle is completely
    insensitive to everything tuned. That is the ARM 005 premise, intact, and now the ONLY thing the
    arm rests on (the bandwidth argument is retracted).

NEXT — PROPOSED-NOT-SCHEDULED (owner's call on rig time; the architect is NOT scheduling it):
  n-cpu-moe RESIDENCY SWEEP at the best thread config: --n-cpu-moe 48 / 44 / 40 / 36 / 32 / lowest that
  fits VRAM, all at -t 16 unpinned, fresh server, unique port, D1+D2 gates on; report tok/s + CPU% +
  GPU sm% + VRAM headroom + prefill. Pays twice: (a) possible free throughput (if per-layer holds ~0.2
  and 16 resident fits: another ~+1.6 for a flag); (b) DIRECT measurement of the residency curve that
  ARM 005's price (0.1975/layer, 40-layer linear extrapolation, 1.14x) is extrapolated from — currently
  a TWO-POINT line (t48/t40), an extrapolation the architect flagged as dubious when first drawn.
  PRE-REGISTRATION DRAFT (NOT ACTIVE — re-stamp at dispatch time if the owner approves):
   - Predicts per-layer gain stays roughly constant (0.15-0.25 tok/s/layer) until VRAM binds.
   - SUBLINEAR (flattening as more layers land on GPU) => ARM 005's 1.14x is an OVERESTIMATE => arm
     KILLED, not merely blocked.
   - SUPERLINEAR => arm worth more than 1.14x; architect will say so.
   - Either way: cheapest possible test of a number quoted for four rounds.
  AWAITING OWNER: rig-time approval for this sweep.

=== END SECTION 20e ===

=== SECTION 20f: OWNER DECISIONS + reduced 3-point n-cpu-moe sweep PRE-REGISTRATION (architect relaying owner; banked BEFORE dispatch, 2026-09-19 ~06:00 ICT) ===

OWNER DECISIONS (both binding):
1. REDUCED 3-POINT SWEEP APPROVED — run it (not the full six).
2. CONFIG WINS LAND AFTER THE SWEEP, NOT NOW — do not ship the thread or topology change yet; the sweep
   runs against the exact configuration measured all session. Config wins stay a standing item.
§20e's PROPOSED-NOT-SCHEDULED (800c5f25a) flips to SCHEDULED-REDUCED via this section.

ARCHITECT NOTE IN THE OWNER'S FAVOUR: three points is the MINIMUM for a linearity test and sufficient for
the question that matters ("is ARM 005's extrapolation honest"). Two points show no curvature; three can.
The reduction costs precise location of a VRAM-optimal point, not the validity answer — and the architect
commits to not requesting the other three legs later by the back door.

THE THREE LEGS (all at -t 16 unpinned, t40's other flags unchanged, fresh server per leg, unique port,
D1+D2 gates ON; report per leg tok/s, CPU%, GPU sm%, VRAM used AND headroom, prefill, n):
  L1) --n-cpu-moe 48   (0 layers resident)              — anchor
  L2) --n-cpu-moe 36   (12 resident)                    — midpoint
  L3) --n-cpu-moe <lowest that fits> (max resident)     — determined by VRAM probe, see constraint

VALIDITY CONSTRAINT (decides whether the sweep is valid at all): CONTEXT LENGTH AND BATCH SIZE MUST BE
IDENTICAL ACROSS ALL THREE LEGS. Moving layers onto the GPU eats the VRAM the KV cache lives in; if L3
quietly gets a smaller context to fit, the comparison measures context length, not residency — VOID.
Fix context at the value used all session; find the lowest --n-cpu-moe that fits WITH that context
intact. If nothing below some value fits, THAT VALUE IS L3 — report the binding constraint rather than
shrinking context to reach a nicer number. If the builder cannot hold context constant: STOP AND REPORT,
do not proceed.

SAME-SESSION RULE: all three legs in ONE session, same binary. Cross-session comparison at this
precision is untrustworthy (our own 2.4% drift rule; violated at 4.1% by leg C). Do NOT reuse the
earlier t40/t48 numbers as a fourth point.

PRE-REGISTRATION (banked BEFORE dispatch):
  slope_1 = (L2 - L1) / 12            [tok/s per resident layer]
  slope_2 = (L3 - L2) / (resident_L3 - 12)
  Verdict by slope_2 / slope_1:
   - LINEAR      0.75-1.25: ARM 005's 1.14x ceiling stands as quoted; arm stays BLOCKED.
   - SUBLINEAR   < 0.75:    the extrapolation is dishonest, 1.14x is an OVERESTIMATE, and
                           ARM 005 IS KILLED, NOT BLOCKED — committed now so it cannot be softened
                           when the number is in front of the architect.
   - SUPERLINEAR > 1.25:    the arm is worth more than 1.14x; the architect will say so in exactly
                           those words.
  PREDICTION: roughly LINEAR, slope 0.15-0.25 tok/s/layer on both segments. Held to it.
  SECONDARY (stated falsifiable): L3 expected to be the fastest leg. If L3 is NOT faster than L2,
  residency has already saturated below the VRAM limit — a bigger result than any slope.

SCOPE LIMIT ON RECORD BEFORE THE DATA ARRIVES: this sweep measures LAYER residency. ARM 005 is EXPERT
residency within layers. The 1.14x figure applies the per-layer slope through the h=0.4207 expert hit
rate — an ASSUMPTION this sweep does NOT check. A LINEAR result validates the extrapolation but NOT the
h-transfer; do not let a linear slope be read as a full vindication of the arm's price.

=== END SECTION 20f ===

=== STANDING RULE: single clock in the bank (architect directive, committed with this rule) ===
Section headers from §20g onward carry EITHER the commit timestamp (written after the commit, or amended
to match it) OR no time at all — the commit timestamp is the single source. Two clocks in one artifact is
the defect: in-header estimate times have drifted from commit times in both directions (§20e "~04:20" vs
02:05:48; §20f "~06:00" vs 08:15:18), which breaks sequence reconstruction and voids the sha-AND-timestamp
read-back as a cross-check. Existing headers are NOT rewritten — the drift is itself part of the record.

=== SECTION 20g: VOID-sweep ruling — verdict UNDECIDABLE, hard VRAM arithmetic, RETRACTION #3 (pricing model -> coverage model), linearity test withdrawn as underpowered, boot-fit rule, product issue (architect; committed with this rule in force — commit timestamp is the single clock) ===

0. THE STOP WAS CORRECT — SECOND GATE THIS WEEK THAT DID ITS JOB. Builder held context constant, reported
   the binding constraint, refused the nicer number, stopped. Commended explicitly. A constraint that
   never stops anything is decoration; this one stopped a sweep.

1. VERDICT: UNDECIDABLE. No slope, no branch, nothing banked as a result. LINEAR/SUBLINEAR/SUPERLINEAR
   all require two servable resident points; we have zero. §20f branches stay open and unexercised —
   the SUBLINEAR kill condition did NOT fire: ARM 005 is not killed, exactly as blocked as before.
   L1 coherence accepted (21.28 vs 21.42 = -0.65%, inside 2.4%) — earns comparability for THIS binary
   and config, does NOT license merging the sessions into one dataset.

2. HARD VRAM ARITHMETIC (first time this track has had it): (15839-4839) MiB / 12 layers =
   916.7 MiB per layer of expert weights = 1.79 MiB per expert (512/layer) — independently matches the
   1.875 MB/expert figure from the PCIe analysis, derived a completely different way. Two routes, same
   number. Headroom at ncm=48: 11472 MiB. Servable window [8,12) resident. Per-leg headroom:
   9res 3222 | 10res 2305 | 11res 1389 | 12res 472 (crashes).

3. RETRACTION #3 — THE PRICING MODEL: full 48-layer residency needs 44,000 MiB — 43 GiB, THREE TIMES
   the entire card. "Full residency = 48 x 0.1975 = +9.48 tok/s" priced a configuration that cannot be
   built and never could be — a counterfactual, not a bounded extrapolation. Retracted.
   REPLACEMENT — THE COVERAGE MODEL: gain proportional to the FRACTION OF EXPERT LOOKUPS SERVED FROM
   VRAM. Measured: 8 resident layers = 8/48 = 16.7% coverage -> +1.58 tok/s (same-session t16 pair,
   23.00 vs 21.42) = 9.48 tok/s per 100% coverage. Same arithmetic product as before; the PREMISE changes
   from "linear across 40 layers that cannot exist" to "linear in coverage fraction" — a real quantity,
   bounded [0,1], partially measured. Same number, defensible for the first time.

4. THE SAME ARITHMETIC ARGUES *FOR* THE ARM (stated plainly after a bearish stretch):
   layer residency: 7,333 MiB buys 16.7% coverage.
   expert pinning at N=38: 38 x 48 x 1.79 = 3,266 MiB buys ~42% coverage.
   EXPERT PINNING IS 5.7x MORE VRAM-EFFICIENT PER POINT OF COVERAGE — the skew payoff stated as
   hardware, the first version of ARM 005's thesis that survives contact with VRAM numbers. Fits with
   room: N=54 (where our h data saturates) = 4,641 MiB against 11,472 available. VRAM IS NOT ARM 005's
   BINDING CONSTRAINT; h-SATURATION IS. Best configuration if the arm worked: ncm=40 (8 resident) PLUS
   pinning in the remaining 40 layers (~N=48/layer after request-time reserve) -> coverage
   8/48 + (40/48 x 0.40) = 0.50 -> +4.74 tok/s -> 26.02 vs 22.86 stock = 1.138x — where the 1.14x
   quoted for four rounds actually comes from; third independent convergence. Confidence in the NUMBER
   up; confidence in EXECUTION unchanged (last armed measurement 0.82x).

5. PROPOSED EXPERIMENT WITHDRAWN BY ITS AUTHOR — the linearity test is UNDERPOWERED: servable range
   8-11 resident spans 22.86 -> 23.45 tok/s = 0.59 tok/s = 2.6%, against the 2.4% session-variance rule.
   The entire testable window is one tenth of one percentage point outside noise. A curvature test
   across it cannot return a trustworthy answer, and a false "linear" would launder the number just
   re-derived. BANKED AS A LIMIT OF THE RIG: the linearity of the residency curve is not answerable on
   this card at -c 8192 — not an open question to keep meaning to close.

6. METHODOLOGY STANDING RULE: BOOT-FIT IS NOT SERVABLE-FIT. ncm=36 boots clean, passes D2, dies on the
   first request (CUDA alloc failure in server_context_impl::decode at 472 MiB headroom). Our VRAM probe
   methodology was boot-only and would have reported 36 as "fits". EVERY VRAM PROBE MUST ISSUE AT LEAST
   ONE REAL REQUEST BEFORE REPORTING A FIT. Banked alongside D1/D2.

7. PRODUCT FINDING -> GitHub issue (filed by leader this turn, explicit --repo per §19 amendment): a
   server that boots and dies on its first request is a deployment hazard — startup health passes, node
   enters rotation, fails under real traffic. The fit-check admits configurations with no request-time
   allocation reserve. Repro: ncm=36, -c 8192, 472 MiB headroom, reproduced twice.

8. O1 UPDATED: prefill is RESIDENCY-SENSITIVE but NOT THREAD-SENSITIVE — L1 (0 resident) prefill 43s vs
   39s across all nine thread legs (8 resident) = +10%, well outside variance; thread count 8->20
   invariant. Both facts measured; the pair is genuinely odd; O1 stays OPEN with this added. Do not
   optimise prefill until someone explains it.

9. QUEUE: (a) OWNER LANDING GATE SATISFIED — owner said config wins land "after the residency sweep";
   the sweep has concluded (void, concluded, will not be re-run as specified). The 1.67x (threads +
   topology, ship -t 16 unpinned) is LANDABLE ON THE OWNER'S WORD. (b) ARM 005: BLOCKED unchanged
   ~1.14x — price better-founded than this morning, execution risk untouched. (c) OPTIONAL, owner's rig
   time, probe-only and cheap: a SERVE-probe (boot + one request, per rule 6) at reduced context to find
   whether a wider servable window exists; if a context exists where ~50% coverage is servable, ARM
   005's price becomes INTERPOLATED rather than extrapolated. Not requested; recorded as the only
   remaining way to test the coverage model on this hardware.

=== END SECTION 20g ===

=== OWNER DIRECTIVE (2026-09-19, post-§20g): GOAL REASSERTED — MoE optimization, 3060 main test target ===
Owner's words (binding): the 5060 Ti config is nice, BUT that is not our goal. We are optimizing the MoE,
and the 3060 is the MAIN TEST TARGET. Rig access granted: 5060 Ti AND P100 for tests.
Implications (leader's read, for architect planning):
- The single-GPU ship config (23.99, -t 16 unpinned, --tensor-split 1,0) EXCLUDES the 3060 — it is an
  acknowledged nice config, NOT the track destination. Landing it as "the answer" would remove the 3060
  the epic exists to exploit (COMBINED engine mode / expert-split per architecture docs).
- All §20-era measurements were 5060-Ti-only; they remain valid as the single-GPU reference and the
  thread/spare-CPU rule is topology-orthogonal (applies with the 3060 in the loop too).
- ARM 005's successor arms must be designed and measured WITH THE 3060 PARTICIPATING (expert-split /
  ggml-RPC peer :9504). The coverage model's hardware arithmetic now extends to a multi-GPU pool:
  5060 Ti 16GB + 3060 12GB + P100 16GB; 1.79 MiB/expert applies per card.
- P100 (KVM VM, sm_60, Q5_K-balanced, 192.168.122.21) approved as an additional test host.

=== SECTION 21: ARM 007 — MULTI-GPU EXPERT COVERAGE POOL (architect redesign, supersedes ARM 005; banked BEFORE any dispatch; commit timestamp is the single clock) ===

PART A — RANKING IMPLEMENTATION (read at a744d8019): CORRECT THE DESIGN, NOT THE DEFECTS.
  DEFECT 1 (ggml-cuda.cu:2070-2071, hydra_gather_mmvq): per-call full stream sync (cudaMemcpyAsync D2H +
  cudaStreamSynchronize) inside the pinned-layer path = ~144 host syncs/token at 48 layers x 3 sites;
  sits in the CUDA-graph-capturable branch and suppresses graph capture (the fallback comment says so).
  DEFECT 2 (:2110): miss experts re-fetched H2D EVERY token into a pool allocation that dies with the
  call, no reuse. Cost at measured arithmetic: 10 used x (1-0.4207) = 5.79 misses x 0.597 MiB/expert/site
  x 3 sites x 48 layers = ~498 MiB/token, needing ~11.6 GB/s sustained in 0.6 MiB serialized memcpys.
  DEFECT 3 (the regime, :2014): hard-aborts unless expert weights are CPU-resident — mutually EXCLUSIVE
  with --n-cpu-moe, the config that produced every good number. Arming replaces our best config with a
  weight-streaming one rather than accelerating it.
  RULING: fixing Defects 1+2 would not make this win — Defect 3's ~498 MiB/token miss-side PCIe floor
  stands. DO NOT spend build time correcting the gather. The correction is ARCHITECTURAL: never move the
  weights — move the activations, compute each expert where its weights already live. ARM 005 as scoped
  is WITHDRAWN (superseded by this section). Defects 1+2 get issues (filed by leader this turn) but are
  NOT on the critical path.

PART B — ARM 007, STAGED, CONFIG FIRST. Thesis: the relevant comparison for the 3060 is 3060 vs CPU, not
  3060 vs 5060 Ti. The §20 topology loss (split 14.39 vs single 18.19) moved work from the FAST GPU to
  the slow one; moving expert work from CPU to the 3060 is the opposite trade. Falsifiable in one
  config-only stage. Sizing (916.7 MiB/layer; >=2.3 GB request-time headroom rule): 5060 Ti 11472 MiB
  free -> 10 expert layers; 3060 ~11088 MiB -> 12; P100 ~15184 MiB -> 16; total experts 44,002 MiB.
  STAGE 1 (ZERO CODE): 5060 Ti + 3060 expert-layer pool via -ot: 10 layers CUDA0 + 12 CUDA1 + 26 CPU;
  everything non-expert on CUDA0. Idiom -ot "blk\.(...)\.ffn_.*_exps=CUDA1" (same mechanism --n-cpu-moe
  uses internally). Coverage 22/48 = 45.8% (up from 16.7%). Dispatch: LOCAL CUDA1, NOT ggml-RPC (same
  host; :9504 stays out of Stage 1 — one variable).
  STAGE 2 (ZERO CODE, gated): +P100 over RPC -> 38/48 = 79.2%. BUT KVM/virtio round-trip model says
  300 us x 16 layers x 2 crossings = 9.6 ms/token = 23% of budget (vs 3060 local 0.6 ms = 1.4%).
  GATE: microbenchmark the actual RPC round-trip FIRST (no decode run); if > ~150 us, Stage 2 is not
  viable as interleaved layers — re-plan as one contiguous tail block or drop. Do not run blind.
  STAGE 3 (CODE, gated on 1-2): true expert-parallel MoE — hotness-ranked slice of EVERY layer's experts
  per device, outputs reduced; traffic = hidden state (~8-16 KB/token/layer), never weights. Ranking
  work earns its keep here (hottest experts on fastest device); only variant where devices work in
  PARALLEL. An epic; epic branch; not started before Stage 1 reports.

PART C — PRE-REGISTRATION (banked before dispatch; architect's to be wrong about):
  Reference: best single-GPU stock, same-session matched control, configuration-not-scalar (~23.9).
  P1 STAGE 1 BEATS THE REFERENCE: predicted ~26.4 tok/s (~1.10x) = +2.76 pre-link (29.2 coverage pts x
    9.48) minus ~1.4% link overhead. Below reference => "3060 beats CPU" thesis WRONG, multi-GPU frame
    collapses, arm ends at Stage 1 (architect will say so in those words). Bands: >=25.5 confirms;
    23.9-25.5 weak pass, Stage 3 re-priced down; <23.9 kills.
  P2 COVERAGE CONSTANT COMES IN LOW: 9.48 was measured CPU->primary GPU; 3060 is ~3.3x slower on x4 —
    predicted realized constant on the 3060 share = 60-90% of 9.48. Above 9.48 => model wrong; find out
    before Stage 3 is costed.
  P3 SPARE-CPU THRESHOLD MOVES: >=2 spare carries over but a second CUDA device adds helper threads;
    predicted required spare >=3. Cheap 2-point check (-t 16 vs -t 14) same session, not a new sweep.
  P4 PREFILL: predicted Stage 1 improves prefill ~5-10% as expert work leaves the CPU (O1: residency-
    sensitive, thread-insensitive). If prefill does not move, O1's anomaly deepens -> its own diagnostic.

PART D — MEASUREMENT PROTOCOL (carries over; do not re-derive): -c 8192, batch defaults, 200 fixed
  tokens, fresh server per leg, unique port; context IDENTICAL across every leg or VOID (same STOP rule
  that ended the last sweep); D1+D2 gates ON; SERVE-probe never boot-probe (§20g rule 6); same-session
  matched control at the single-GPU reference in EVERY stage (reference is a configuration); report
  per leg: tok/s, CPU%, PER-DEVICE VRAM used+headroom, PER-DEVICE GPU sm% (non-negotiable — near-idle
  CUDA1 means the pool is not doing what we think), prefill, n; spare count recorded explicitly per leg.

PART E — NOT PROPOSED (stated so nobody re-adds): no ggml-RPC for the 3060 (local CUDA1); no
  gather-MMVQ fixes on the critical path; no --override-kv for k; no N change (parked 38-42; Stage 3
  slice sizing is separate — no N revision smuggled through it); no residency-linearity re-run (banked
  unanswerable at -c 8192 on the 5060 Ti; Stage 1's wider range may answer it for free — a bonus, not a
  justification).

DISPATCH STATE: HOLD. Stage 1 needs the OWNER's word on rig time — access was granted, scheduling is
theirs. Banked before any dispatch per standing rule.

=== END SECTION 21 ===

=== SECTION 21a: OWNER DESIGN DIRECTIVE — upstream params untouched, own flag family owns expert placement; RAM is home, VRAM is a usage-driven cap (architect relaying owner; banked BEFORE any build; commit timestamp is the single clock) ===

OWNER WORDS (verbatim): "The -n-moe-cpu — let not change behavior of upstream params. We should create our
new params. At first we could allow all experts in CPU mean RAM; when optimize we will allow experts that
used by subjects and not prioritize on VRAM."

ARCHITECT READING (explicit, owner may correct before anything is costed):
(a) --n-cpu-moe and ALL upstream flags keep upstream semantics EXACTLY; with our flags unset the engine is
    bit-identical to upstream.
(b) Our own flag family owns expert PLACEMENT POLICY end to end.
(c) Base state = ALL experts in host RAM — RAM is the home, VRAM is the exception (not "fit what you can").
(d) Optimisation promotes experts INTO VRAM by measured USAGE. VRAM is a CAP, not an objective.
WHY (d) IS CHEAPER, from our numbers: h saturates ~N=54/layer = 4,641 MiB = 40% of available; capacity-
filling would consume 6.8 GB more VRAM for h it cannot raise — usage-driven leaves ~6.8 GB free for
context, MTP head, KV.

CONSEQUENCE 1: dissolves the Part A Defect 3 regime clash — our flag makes host residency the base state,
so the mechanism never fights upstream placement; no abort, no workaround.

CONSEQUENCE 2 — THE HARD PART (must be in the brief before anyone estimates): experts are ONE 3D tensor
per layer (ffn_*_exps = [n_embd, n_ff, n_expert], llama-model.cpp); a ggml node runs on ONE backend, so
partial placement cannot be expressed by tensor placement. The two-branch construction (GPU slab + CPU
branch + combine) is the DELETED build_hydra_mm_id and is why arming measured 0.82x: ggml_mul_mat_id
computes all k selected experts on EACH branch -> 2k work. Masking fixes arithmetic, not cost. DO NOT
RE-PROPOSE. The only escape: a fork-native op on BOTH backends that skips experts it does not own —
CUDA: on-device promoted-subset determination (prefix-sum/compaction kernel), launch over hits only,
NO host readback (kills issue #140's per-call sync at the root); CPU: plain C loop over the miss subset;
combine partials. Work returns to k. EPIC — epic branch per project rule; must not start before ARM 007
Stage 1 reports.

CONSEQUENCE 3: "used by subjects" brushes a banked finding — the SUBJECT dimension was measured and did
NOT carry (POC shipped session-warm + all-pool, dropped subject rankings; one turn touches ~54% of
experts/layer; same-subject pools saturate N=54). The param takes a USAGE-DERIVED PIN SET, agnostic to
producer (session-warm / subject / global-frequency / future ranker); file format is the contract, the
ranking policy is swappable and NOT baked into a flag name. Subject-keyed re-open = deliberate owner
call, gated design available on request — not an implicit one.

FLAG SURFACE (DRAFT, owner approval pending, names negotiable):
  --moe-expert-home {gpu|cpu}   default gpu = upstream behaviour untouched. cpu = all expert tensors in
                                host RAM (our base state), independent of --n-cpu-moe (neither read nor
                                modified).
  --moe-expert-pins FILE        usage-derived pin set (per-layer expert ids). Requires home cpu.
  --moe-expert-pin-count N      cap per layer; default from file. VRAM asserted as a CAP: if the set
                                does not fit, FAIL LOUDLY with the arithmetic. Never silently truncate,
                                never silently spill.
  None set -> every code path is upstream's. One implementation path when enabled — no fallback branch,
  no runtime capability toggle (standing alpha-stage rule); the flag chooses the regime, the regime does
  not negotiate with itself.

WHAT DOES NOT CHANGE: ARM 007 Stage 1 is CONFIG-ONLY on upstream flags and still gates everything
downstream (NOTE — Stage 1 was ALREADY DISPATCHED on the owner's rig grant before this directive landed;
unaffected, but the architect's "no dispatch" status was stale). Stage 3 is now specified by THIS
directive (same principle — move activations, never weights — as the owner's placement policy), replacing
the earlier expert-parallel-slices sketch. Issues #140/#141 stay off critical path; the epic SUPERSEDES
rather than fixes them (#140's sync disappears with on-device compaction; #141's re-fetch disappears
because misses never move). N stays parked 38-42; --moe-expert-pin-count exposes the knob, does not
license changing it.

STATUS: banked. Flag surface pending OWNER approval. Epic + flag implementation NOT started before
Stage 1 reports. Stage 1 in flight on the rig.

=== END SECTION 21a ===

=== §21 ERRATUM (Stage 1 spec, caught by the builder before any burn): the C-leg control as written
("NO -ot", plain) NEVER BOOTS — plain config attempts the full 47,263 MiB expert+non-expert alloc on the
16 GB card. The ~23.9 tok/s reference this session was ALWAYS --n-cpu-moe 40 (8 resident) at -t 16.
CORRECTED CONTROL: -t 16 --n-cpu-moe 40 --tensor-split 1,0 (configuration-not-scalar rule intact; verdict
bands unchanged — they were priced against ~23.9). S1/S1b -ot legs also BOOT_FAILed with the identical
full-alloc-on-device-0 signature DESPITE 26 CPU -ot patterns: either the regex did not match (override
silently unapplied) or a fit-params fallback path fired — undetermined. Builder authorization granted:
(1) corrected control; (2) -ot MATCH-VERIFICATION PROBE FIRST (boot with verbose logging, read the
override/apply lines, verify exactly 12 ffn_*_exps tensors land on CUDA1) before any timed leg retry.
No blind retries. Escaping (backslash-dot in shell quoting) is a prime suspect for the regex miss.

=== SECTION 22: RE-ANCHOR — the goal is proving EXPERT RANKING (C3); ARM 007 demoted/withdrawn; ARM 008 mechanism spike vs the 36.3 us budget (architect, owning the drift; banked before any ARM 008 work; commit timestamp is the single clock) ===

THE OWNER'S CORRECTION: the goal is to prove EXPERT RANKING for MoE — not llama.cpp config tuning, not
RPC, not the 5060 Ti. The architect names the config drift as theirs. §22 supersedes the FRAMING of
§21/§21a, not their content.

1. THESIS SCORED HONESTLY — three claims:
   C1 skew + ranking predicts it: PROVEN (N=38/512 = 7.4% captures h=0.4207; validated live both ends,
   0.4207 vs 0.4207 predicted; cross-domain 0.1457 vs 0.144). STOP RE-TESTING IT.
   C2 value if realised, measurable: MEASURED this session — 12.44 ms/token per 100% of expert work
   relocated CPU->GPU; at h=0.4207 = 5.23 ms/token = ~+4 tok/s = ~1.14-1.17x (coverage model, measured).
   C3 hits convert into throughput: NEVER DEMONSTRATED. Zero for three (dual-construction 0.82x; gather
   moves weights instead of saving them; layer placement cannot express ranking at all).
   THE GAP IS MECHANISM. Not evidence, not ranking quality, not baseline, not topology. Anything that
   does not attack C3 is SCAFFOLDING and is labelled as such in the queue.

2. THE MECHANISM BUDGET RULE (governs every mechanism proposal from now on):
   Ranking prize = 5.23 ms/token. Mechanism fires 144x/token (48 layers x 3 sites)
   => 36.3 us per invocation is BREAK-EVEN; 12.1 us is the 3x-margin bar.
   Past attempts against it: cudaStreamSynchronize (#140) ~10-50 us — at/over break-even BEFORE any work;
   one 0.597 MiB miss-expert H2D @ 8 GB/s ~78 us = 2.1x over for ONE expert; 5.79 misses/invocation (#141)
   ~450 us = 12x over; dual-construction = 2k instead of k = doubles the work the prize is made of.
   EVERY failure is the same failure with a number on it: the mechanism spent more time than the ranking
   saves. RULE: no expert-ranking mechanism gets built until its per-invocation cost is ESTIMATED against
   36.3 us, in the design brief, not after.
   Sobering corollary (against the architect's own preferred design): clean on-device compaction still
   needs ~2 extra kernel launches at ~5-10 us each = 10-20 us vs the 12.1 us 3x bar — even the correct
   mechanism may only just clear it. That is why the next step is a MEASUREMENT, not an epic.

3. ARM 007 RE-SCOPED: Stage 1 (running) DEMOTED to a PREMISE CHECK — answers "is the 3060 faster than
   the CPU at expert work" (any multi-device ranking pool needs this); costs nothing more now it is on
   the rig; does NOT test ranking (layer-granular placement cannot discriminate hot from cold — every
   layer is used by every token). Report as a PREMISE result, not an arm result. Stages 2+3 as written:
   WITHDRAWN (Stage 2 = pure config, does not touch C3; Stage 3 = right principle at the wrong altitude).

4. THE REAL ARM — ARM 008: MECHANISM SPIKE, one layer, measured against 36.3 us. Smallest experiment that
   proves or kills C3; NOT the epic — the thing that decides whether the epic is fundable.
   BUILD: per-expert backend selection for a SINGLE expert-matmul site — CUDA side: on-device compaction
   of the promoted subset (prefix-sum over pin mask, gather rows from the resident slab), matmul over
   hits only, NO HOST READBACK anywhere (the hard requirement that killed every previous attempt); CPU
   side: loop over the miss subset only; combine the two partial outputs. Experts live in host RAM;
   promoted subset is a device slab; weights never move at runtime.
   MEASURE (the whole deliverable): WALL TIME PER INVOCATION, hits path and misses path, against 36.3 us
   break-even and 12.1 us 3x bar. Not tok/s. Not perplexity. One number, instrumented directly.
   PRE-REGISTERED VERDICTS (before any code):
     <= 12.1 us   : mechanism clears with margin -> FUND THE EPIC. Architect predicts achievable, not
                    certain; will not claim better than 50/50 in advance.
     12.1-36.3 us : clears break-even only -> epic returns <1.1x, NOT funded on this rig.
     > 36.3 us    : C3 REFUTED ON THIS HARDWARE — expert ranking does not convert to throughput here;
                    ranking valuable only on hardware with a different CPU/GPU cost ratio; STOP spending
                    this rig on it.
   SPIKE LIMITATION (noted): single-site timing will not capture scheduler overhead from 144 backend
   switches/token. If the spike passes, the epic's FIRST milestone is a 48-layer timing harness BEFORE
   any correctness work, to catch that.

5. DROPPED (so it stops consuming attention): no P100, no RPC, no further thread/affinity/topology work
   (banked, closed; the owner may land the 1.67x whenever as an OPERATIONAL matter, not track work); no
   residency-linearity retry; no n-cpu-moe sweeps. §21a flag surface stays as designed, pending owner
   approval — it is the epic's interface, and the epic is gated on ARM 008.

HOLD: ARM 008 needs the OWNER's word (small code spike, one site, instrumented). Stage 1's premise result
lands first (already on the rig).

=== END SECTION 22 ===

=== SECTION 23: OWNER-REQUESTED THEORY-TO-IMPLEMENTATION CODE REVIEW — delivered on PR #134; answer NO (two independent reasons); five issues M1/M2/M3/R1/R2; M1-M3 BLOCKING ARM 008; GitHub communication discipline standing (architect on owner request; banked before any fix work; commit timestamp is the single clock) ===

OWNER ASKED: careful review of theory-to-implementation gaps; team communicates on issues/PRs, not only
in-band. Review posted on PR #134 at a744d8019, every finding file:line-cited, every finding carrying a
solution, every finding priced against the 36.3 us per-invocation budget (§22).
COMMENT: https://github.com/ddvnguyen/llama.cpp/pull/134#issuecomment-5738403500

ANSWER TO THE OWNER'S QUESTION ("does the code look good enough to deliver a number in the paper"):
NO — for two independent reasons.

CLASS A — MEASUREMENT gaps (block ANY trustworthy number, whichever mechanism wins):
  M3 (worst) Pin-file loading SILENT on every failure — fopen error silent, unparseable lines silently
      skipped, no load summary. A typo in HYDRA_PIN_FILE produces an UNARMED run that looks armed. This
      is the engagement-gate rule violated in the source itself; burned twice already (fire=[0,1,1,1]
      episode; the -ot match probe).
  M1  The gather path's stream sync is UNCOUNTED and UNTIMED — hydra_n_sync/hydra_sync_us incremented
      only in the OLD fallback path (:2288, :2316). Zero budget instrumentation on the path under test.
      ARM 008 cannot run until this exists.
  M2  Two coexisting mechanisms (gather :2046, fallback :2339) write the SAME hit counters with
      DIFFERENT semantics (unique-experts vs per-slice) — neither matches the paper's h (over lookups,
      k=10/token). Any mixed run reports a blended h that means nothing.
CLASS B — RESOURCE correctness:
  R1  Slab is raw cudaMalloc (:2022), outside ggml accounting, never freed. ~3.2 GB at N=38, invisible
      to the fit-check — compounds #139 directly.
  R2  Duplicate/out-of-range pins over-allocate slots silently (:2020 sizes from the raw list).
CLASS C — the MECHANISM gap, already banked (#140, #141): misses ship to GPU instead of computing on
  CPU. Not fixable by patching; superseded by design.

ACTIONS (executed this turn):
1. M1/M2/M3/R1/R2 filed as FIVE separate issues on ddvnguyen/llama.cpp, label review-finding, explicit
   --repo, each with file:line + failure scenario + solution restated (issues outlive comments);
   cross-linked to #134 and (R1) to #139. Issue numbers reported in the turn ledger + architect ack.
2. M1/M2/M3 marked BLOCKING ARM 008. Real dependency, not process: ARM 008 measures wall time per
   invocation — without M1 there is nothing to read; without M3 we cannot prove the mechanism engaged.
   SEQUENCE: M3 + M1 land first, then the spike.
3. PR #134 recommended CLOSED in favour of per-expert backend selection, with B1/B2 recorded as
   superseded-by-design rather than to-fix. PR DISPOSITION IS THE OWNER'S WORD — not closed.
4. GITHUB COMMUNICATION DISCIPLINE, STANDING FROM NOW: every arm gets an issue before it runs and its
   result posted back to that issue; every mechanism change gets a PR with the per-invocation cost
   estimate against 36.3 us IN THE PR BODY (mechanism budget rule §22 — estimate in the brief, not
   after). Bank sections stay the internal write-ahead log; GitHub is the durable record the owner can
   read without us. Always explicit --repo (§19 amendment).

BUILDER CREDIT (on record): ggml_cuda_mul_mat_id_needs_sync correctly extended at :2163-2175 to veto
CUDA graphs on engaged nodes — careful work; it means the sync is a COST bug, not a correctness bug.

=== END SECTION 23 ===

=== §23a: ARM 007 STAGE 1 (PREMISE CHECK) YIELD — raw builder numbers, D1/D2 PASS, forwarded to architect for adjudication (single clock) ===

PROBE: placement PROVEN — 288 override lines = 48 layers x 3 tensors x 2 loader passes; distribution
EXACT: 72 CUDA1 (blk.0-11), 60 CUDA0 (blk.12-21), 156 Host (blk.22-47). Root causes of the first VOID:
(a) `ffn_*_exps` was a dead regex (`_*` = literal underscores, never matches `up`); (b) 4th expert
tensor `ffn_gate_up_exps` is NEVER model-resident (factory `create_tensor_gate_up_exps` has zero
callers — dead file weight, no placement needed). VRAM probe 13699/11809; serve-probe OK; timed legs
evidenced by identical VRAM splits.
C (corrected control ncm=40, -t16): 23.9244 tok/s — reproduces ~23.9 anchor to 0.1%. VRAM 12287/113.
CPU%dec 673. sm0 60.1, gpu1 idle. D1 PASS.
S1 (pool 12/10/26, -t16): 27.2832 tok/s = 1.140x C. VRAM 13699/11809. CPU%dec 510. sm0 58.1, sm1 50.6
(n=18 of 39 — 3060 engaged ~half the leg, raw observation). D1 PASS.
S1b (pool, -t14): 27.0875 tok/s = 1.132x C. CPU%dec 465. sm0 60.2, sm1 53.8. D1 PASS. Threads not the
lever (0.7% apart).
D2/CTX: n_ctx_slot 8192 all logs; PID-exact kills; zero strays; ports silent; lock released;
ARM007_DONE. Spare-CPU input: pool legs use LESS CPU (510/465 vs 673) while FASTER — ~75% machine CPU
free in all legs.
DISPOSITION: raw to architect under pre-registered adjudication (§22 premise framing; §21 bands were
for the superseded arm framing). Result posted to GitHub per §23 discipline. No dispatch until ruling.

=== END §23a ===

=== SECTION 24: ARM 007 STAGE 1 RULING — premise CONFIRMED (sharper form: 3060-hosted expert layers perform at least as well as 5060Ti-hosted); P2 pre-registration FAILED HIGH and the favourable upgrade is REFUSED; ARM 008 re-priced DOWN ~1.08x with budget UNCHANGED (stable ~35us rule) (architect; commit timestamp is the single clock) ===

BUILDER DIAGNOSIS CREDITED: `ffn_*_exps` dead regex (`_*` = zero-or-more underscores, never matches `up`)
+ `ffn_gate_up_exps` dead file weight (factory zero callers). Two real root causes by probing instead of
retrying — why the engagement gate exists. Credit forwarded to 2ac20e22.

SCORECARD (architect, own pre-registrations):
  P1 STAGE 1 BEATS THE REFERENCE — HIT. Predicted ~26.4/1.10x; measured 27.2832 = 1.140x C, above the
      >=25.5 confirm band; conservative by 3.3%.
  P2 COVERAGE CONSTANT COMES IN LOW (60-90% of 9.48) — FAILED. Realized (27.2832-23.9244)/(45.83%-16.67%)
      = 11.52 tok/s per 100% coverage = 121% of 9.48. Pre-registration said: if it lands ABOVE 9.48
      something is wrong with the model, want to know before Stage 3 is costed. Taken seriously below.
  P3 SPARE-CPU THRESHOLD RISES >=3 — NOT TESTED. CPU%dec 510 on 20 CPUs = 5.1 cores; legs never
      approached saturation; -t16 vs -t14 = 0.72%. Record UNTESTED, not pass.
  P4 PREFILL IMPROVES 5-10% — direction RIGHT, magnitude UNDERESTIMATED: 39.3s -> 32.5s = -17.3%. O1
      prefill residency-sensitivity confirmed twice, stronger than priced.

PREMISE VERDICT: CONFIRMED, stated more precisely than asked — expert layers hosted on the 3060 perform
AT LEAST AS WELL as expert layers hosted on the 5060 Ti. If the 3060 were a drag, 29.17 coverage points
would have returned <9.48; they returned 11.52. sm1 50.6 confirms genuine computing (idle in C). The 3060
is not a liability in this role. PREMISE CLOSED. Secondary (raw): sm1 on ~18/39 samples is what serialized
layer-split predicts (CUDA1 works during blk.0-11 only) — idleness is a PLACEMENT ARTIFACT, not spare
capacity; card at 11809/12288 MiB, effectively full.

P2 DIAGNOSIS — REFUSING THE UPGRADE IT OFFERS:
  (i) superlinear coverage curve (relieving the CPU has accelerating returns): efficiency rose 50%
      (0.0355 -> 0.0535 tok per CPU%) while CPU% FELL 24% — CPU-side work got CHEAPER per unit, not just
      less of it; also supported by prefill -17.3%.
  (ii) two-device config removes CUDA0 contention the single-device measurement never had — 11.52 may
      measure a different quantity than 9.48.
  Separable neither with this data. NOT raising the mechanism budget: taking 11.52 would loosen the ARM
  008 bar from 36.3 to ~42 us on ONE unreplicated leg from a confounded two-device config. Declining
  favourable evidence on the same standard as unfavourable evidence is the entire value of
  pre-registration. GOVERNING BUDGET STAYS AS BANKED. 11.52 recorded as an OPEN DISCREPANCY against the
  coverage model, not an update to it.

ARM 008 RE-PRICED — DOWN, budget unchanged anyway:
  Stage 1's config already captured coverage ranking was going to sell. Ranking operates only on the 26
  CPU-resident layers: 26/48 x h=0.4207 = 22.8 coverage points x 9.48 = +2.16 tok/s on 27.28 = 1.079x.
  PRIZE FALLS ~1.14x -> ~1.08x. THIRD TIME a config win ate the mechanism's headroom — NAMED PATTERN
  (bank): mechanisms priced against an untuned system lose headroom as the system gets tuned. It is not
  bad luck; it is what pricing-against-untuned does.
  Per-invocation budget essentially UNCHANGED — invocation count falls with the prize: 2.689 ms/token
  over 26 layers x 3 sites = 78 invocations -> 34.5 us break-even, 11.5 us at 3x (vs 36.3/12.1 banked).
  STABLE FORM OF THE RULE (bank): the engineering bar for expert ranking on this rig is ~35 us per
  invocation regardless of how well the config around it is tuned.
  VRAM feasibility: ranked pins over 26 layers at N=38 need 1769 MiB; free 2612 (5060 Ti) + 479 (3060) =
  3091 MiB. FITS, little margin on the 3060 — pins for CPU-resident layers go on CUDA0.

SEQUENCE — UNCHANGED: M3 (#142) + M1 (#143) land first, then ARM 008. Without M3 we cannot prove the
mechanism engaged — Stage 1 just demonstrated M3's exact failure mode with a different flag (dead regex
silently placed nothing; only a verbose probe caught it).

FOR THE OWNER (posted to #147): Stage 1 config = 27.2832 tok/s = 1.90x vs default split 14.39; 1.50x vs
the 18.19 called baseline a day ago. Zero code, just correct expert placement across both cards. Their
call to land, as always. Honest counterweight in the same breath: the better the config gets, the less
expert ranking has left to win — ARM 008 is now worth ~1.08x, not ~1.14x.

=== END SECTION 24 ===

=== §24a: OWNER DIRECTIVE — full rig time standing; implementation AUTHORIZED; final gate = epic->main merge (commit timestamp is the single clock) ===

OWNER WORDS (verbatim): "I allow full rig time, other I allow implement with my final gate is when merge
epic into main."
READING (recorded): (1) rig time is standing — no per-leg rig-time asks; (2) implementation is authorized
— the M3+M1 fixes (#142/#143, incl. #146 slot-sizing in the same load path) proceed now, and the ARM 008
mechanism spike is unblocked after them; (3) the ONLY remaining owner gate is the epic->main merge at the
end. Explicitly NOT covered: PR #134 disposition (still owner's word on the close recommendation).
DISPATCH: builder 2ac20e22 -> M3 (#142 fail-loud pin loading + load summary + dedupe/range validation) +
M1 (#143 sync counters into gather path) + #146 slot-sizing-from-validated-set (same load path, per the
issue's pairing note). Branch per project workflow, PR closes issues, results posted to issues per §23.
ARM 008 spike dispatches after M3+M1 land (sequence §24 unchanged; spike = code -> issue before run +
cost estimate vs ~35us in the PR body per §23 discipline).
=== END §24a ===

=== SECTION 25: OWNER REMOVED THE THROTTLES — gates go TECHNICAL; three tracks stood up; B1/B2/B3 pre-registered; ARM 008 STOP commitment banked verbatim (architect; rig scheduling theirs to direct; commit timestamp is the single clock) ===

OWNER WORDS (verbatim): "I allow full rig time, other I allow implement with my final gate is when merge
epic into main."
MEANING: rig time no longer routes to the owner; implementation authorized; single gate = epic -> main.
NOT faster at the same judgement — the gates must be TECHNICAL now because permission gates are gone.

STANDING CHANGES:
  - Rig scheduling is the ARCHITECT's to direct from here. Stop routing rig questions to the owner.
  - Sub-PRs land on the epic branch, CI-gated, NO owner sign-off. Only epic -> main needs their word.
  - Everything else holds: pre-registration before dispatch; bank-then-dispatch; D1/D2 gates; serve-probe;
    reference-is-a-configuration; mechanism budget rule; GitHub as the durable record (§23).

THE COMMITMENT (banked VERBATIM, architect, made BEFORE the data — the thing most likely to be quietly
abandoned later):
  "With unlimited rig time the failure mode is iterating past a negative verdict until something looks
   good. If ARM 008 measures above 34.5 us per invocation, we STOP. I will tell the owner the conversion
   claim is refuted on this hardware and recommend the epic not be built. No re-tuning, no second
   mechanism, no 'one more variant' — those would need a new pre-registration and a stated reason, not
   momentum."

TRACK 1 (CODE, NO RIG) — the blocking foundation, immediate:
  #142 (M3) pin-load hard-fail + load summary + ENGAGEMENT COUNTER.
  #143 (M1) per-invocation timing on the ENGAGED path, report p50/p99 us per site.
  #144 (M2) split counters per path; count LOOKUPS not unique rows; label decode-only scope. Rides along
      (same file, same hour).
  ARM 008 cannot run without #142+#143. Acceptance: a TEST asserts the engagement counter is non-zero
  BEFORE any effect number is read — the engagement-gate rule made mechanical.

TRACK 2 (RIG, PARALLEL, DISPATCHED NOW) — resolve the P2 discrepancy properly (11.52-vs-9.48 open since
§24; decides budget 34.5 vs ~42 us and whether relocating the LAST experts beats the first — an epic
design input, not a curiosity). Stage 1 UNLOCKED the residency-linearity question banked UNANSWERABLE on
one card (§20g: window too narrow, 2.6% spread vs 2.4% variance): across two cards the lever widens from
8-11 resident layers to 0-22. Limit REVERSED because the HARDWARE CHANGED, not because a different answer
is wanted.
  B1 DEVICE-IDENTITY CONTROL (1 leg, decisive): 8 expert layers on CUDA1 ONLY, 40 CPU, everything else
     CUDA0. Same 16.67% coverage as control C, different device.
     B1 ~= C (23.92 +/- 2.4%) => device identity does not matter; 11.52 must come from curvature.
     B1 > C materially        => two-device contention relief on CUDA0 is real; 11.52 is an artifact of
                                 the pool configuration, not a property of coverage.
     ARCHITECT PREDICTION: B1 ~= C, device-independent. HOLD THEM TO IT.
  B2 COVERAGE SWEEP ACROSS THE POOL (4 legs, same session): 4/8/14/22 expert-resident layers, 8.3% ->
     45.8% coverage. Fill CUDA0 first to its servable limit, then CUDA1. Verdict by segment slopes,
     slope_late/slope_early:
       0.75-1.25 LINEAR   => 11.52-vs-9.48 was cross-session drift in the 9.48 derivation; budget STAYS
                             34.5 us; coverage model vindicated as written.
       > 1.25 SUPERLINEAR => returns accelerate; budget becomes ~42 us AND the epic gains a design input:
                             the last experts relocated are worth more than the first (favours
                             high-coverage ranking).
       < 0.75 SUBLINEAR   => model overstates high-coverage value; ARM 008's 1.08x is an OVERESTIMATE,
                             re-priced down again.
     ARCHITECT PREDICTION: SUPERLINEAR, ratio 1.2-1.5 — betting on the explanation they refused to cash
     in §24, now with a test that can take it away.
  B3 S1 REPLICATE (1 leg): reproduce 27.28 in-session. Drift rule 2.4%.
  PROTOCOL: all six legs ONE session, -c 8192, -t 16 unpinned, fresh server, unique port, D1/D2 ON,
  serve-probe, per-device VRAM + sm% + prefill + n. VERBOSE override lines on EVERY leg — the dead-regex
  incident is exactly why.

TRACK 3 (EPIC) — structure now, build after ARM 008 passes: epic issue + branch
`epic/<id>-per-expert-backend-selection` (owner-sanctioned by the directive — their final gate IS the
epic merge). Milestones IN ORDER, no reordering without a stated reason:
  E0 Measurement foundation = Track 1 (#142/#143/#144). LANDS FIRST.
  E1 ARM 008 spike: ONE site, per-expert backend selection, on-device compaction, NO host readback.
     Deliverable = a NUMBER: wall time per invocation, hits and misses paths. GO/NO-GO at 34.5 us.
  E2 48-LAYER TIMING HARNESS BEFORE ANY CORRECTNESS WORK — single-site timing cannot see scheduler
     overhead from 144 backend switches/token (flagged §22; must not be skipped because E1 passed).
  E3 Implementation + §21a flag surface (--moe-expert-home/--moe-expert-pins/--moe-expert-pin-count);
     names still pending owner approval — ask WITH the epic.
  E4 Correctness: teacher-forced --kl-divergence vs unarmed, pre-registered bound. NOT sampled
     trajectories (banked lesson — flaky gates once already).
  E5 Resource: #145 slab through a ggml buffer (accounted + freed), #146 dedup/bounds at load.
  E6 Acceptance for the owner's merge gate (below).

EPIC -> MAIN ACCEPTANCE CRITERIA (written into the epic issue body; checkable without reading the bank):
  1. ENGAGEMENT PROVEN: pin-load summary emitted; engagement counter non-zero; asserted by a test.
  2. BUDGET MET: measured per-invocation cost <= 34.5 us (target <= 11.5 us for 3x margin), on the
     48-layer harness, not just the spike.
  3. THROUGHPUT: >= 1.05x end-to-end decode vs a SAME-SESSION control in the best stock configuration
     (the §20.4 best-config gate — never against a stale scalar).
  4. CORRECTNESS: teacher-forced KLD within the pre-registered bound vs unarmed.
  5. RESOURCE: slab through a ggml backend buffer, visible to the fit-check, freed on teardown. NO raw
     cudaMalloc.
  6. NO SYNC on the engaged path; the CUDA-graph veto at :2163-2175 REMOVED, graphs restored.
  7. UPSTREAM UNTOUCHED: with our flags unset, behaviour bit-identical to upstream.
  Any criterion unmet = the epic does not go to the owner. "Rather hand them nothing than something that
  needs a caveat."

STILL DECLINING (despite free rig time):
  - The 1-spare replicate (J) — no decision attached; free rig time is not a reason to measure things
    that change nothing.
  - Any re-run of the single-card residency linearity test — B2 supersedes it on better hardware; the
    underpowered version would just give a second, weaker answer to the same question.

DISPATCH: Track 1 -> builder 2ac20e22; Track 2 -> dedicated rig worker (spawned); epic issue opened same
turn (number reported in the ledger + issue). BANKED B1/B2/B3 PRE-REGISTRATION IS IN THIS SAME COMMIT.

=== END SECTION 25 ===

=== §25 DISPATCH RECORD (same clock day): epic issue #148; branch epic/148-per-expert-backend-selection off main; Track 1 -> builder 2ac20e22 (E0: #142+#143+#144+#146-in-scope, test-asserted engagement counter, PR into epic branch); Track 2 -> NEW rig worker f4419282-41e6-4de3-8ca8-55fc4f024a98 (B-series six legs, raw yield, ARM_B_DONE). Roster now 6. ===
