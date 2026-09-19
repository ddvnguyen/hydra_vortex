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
