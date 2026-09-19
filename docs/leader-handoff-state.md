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

=== SECTION 24: ARM 007 STAGE 1 RULING — premise CONFIRMED [PARTIALLY RETRACTED in §26: the 'at least as well as 5060Ti-hosted' superlative is retracted; a direct control (B1) measured 3060-hosted layers -3.31% vs 5060Ti-hosted; premise that 3060-hosted >> CPU-hosted SURVIVES] (sharper form: 3060-hosted expert layers perform at least as well as 5060Ti-hosted); P2 pre-registration FAILED HIGH and the favourable upgrade is REFUSED; ARM 008 re-priced DOWN ~1.08x with budget UNCHANGED (stable ~35us rule) (architect; commit timestamp is the single clock) ===

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

=== §25a: E0 BASE + TRAILER RULINGS (builder recon STOP, dispatch-blocking; leader answers banked) ===
(1) E0 BASE: epic/148 branch (off main) has ZERO pilot code — pilot a744d8019 lives on fork/gather-mmvq-wip
    (base thousands of lines diverged, merged-decode era). RULING: port pilot onto epic FIRST — cherry-pick
    a744d8019 as the FIRST epic sub-PR (base-landing PR), resolve conflicts once at the base, E0 instruments
    on top. Catastrophic-conflict escape: STOP and report the conflict surface, no hand-merged semantics.
    SIDE EFFECT: PR #134's content is preserved in the epic — its disposition (owner word pending) loses
    nothing by closing later.
(2) COMMIT TRAILER: fork AGENTS.md governs the fork — Assisted-by, Co-authored-by FORBIDDEN. The main-repo
    convention (Co-Authored-By) was misapplied in the dispatch; repo rules win. Conventional-commit format
    still applies. STANDING for all fork work.
=== END §25a ===

=== §25c: ARM B SERIES YIELD (raw, banked before adjudication; rig released clean; dispatch-record same turn) ===
B3 (S1 replicate, verify OK 72/60/156): 27.3789 = +0.35% vs 27.2832, INSIDE ±2.4% band. D1/D2 PASS.
B1 (device-identity control, 8 experts CUDA1-only, verify OK 48/0/240): 23.1321 = -3.31% vs C 23.9244,
OUTSIDE ±2.4% band LOW. D1/D2 PASS. (Architect prediction was B1 ~= C — adjudication pending.)
B2-4/8/14/22: ALL VOID, no timing — override verify MISMATCH (all 288 expert tensors HOST, both cards
idle) on every leg. Common factor: --n-cpu-moe alongside -ot (both OK legs were -ot-only). B2 sweep as
speced BLOCKED on this flag interaction; redesign pending architect ruling.
MODEL SUBSTITUTION (worker disclosure, correct): dispatch named stale model; worker used Stage-1-exact
model (/mnt/SSD/qwen3.8-flash-next-apex-mini 6-shard) so B3 could replicate. NOTE: CLAUDE.md hardware
facts are STALE on the rig model name — flagged for correction.
CPU reconciliation (worker note): decode-split family comparable (Stage-1 510/673 ~ B3 455.4 / B1 597.8);
whole-window means (184.6/207.3) not comparable to decode-split.
Logs: /tmp/opencode/armB.session.log + per-leg artifacts; script armB.sh, parser parseB.py.
=== END §25c ===

=== SECTION 26: B SERIES RULING — B3 replication PASS (first clean cross-session replication); B1 prediction FAILED into unregistered third branch (B1 < C); §24 superlative RETRACTED; P2 curvature reading STRENGTHENED but budget UNCHANGED; B2 VOID = real finding (ncm silently discards user -ot); B2' redesigned pure -ot with pre-registration P-a/P-b/P-c (architect; commit timestamp is the single clock) ===

ENDORSED (worker calls, both correct): (1) model substitution — a replicate MUST use the Stage-1-exact
model or it is not a replicate; the worker caught that the dispatch named a model absent on disk.
(2) CPU% reconciliation — decode-split and whole-window are different quantities; ONLY the decode-split
family is comparable across legs. Use decode-split exclusively from here and say so in every report.

SCORECARD:
  B3 REPLICATE: PASS. 27.3789 vs 27.2832 = +0.35%, well inside 2.4%. S1 is real, session method sound.
  BANKED AS: first clean cross-session replication this track has produced.
  B1: ARCHITECT PREDICTION FAILED. Predicted B1 ~= C (device-independent); measured 23.1321 vs 23.9244 =
      -3.31%, outside the band, LOW — a THIRD outcome never registered (branches were written for "~= C"
      and "> C" only). Second time this week the pre-registration outcome space was too small (same
      defect as P4's missing middle in §20b). REGISTRATION ERROR IS A PATTERN: two-branch registrations
      written for quantities that have THREE directions. BANKED AS THE LESSON (three-branch rule, P-c).

WHAT B1 ESTABLISHES: hosting expert layers on the 3060 costs ~0.79 tok/s across 8 layers = ~0.099 tok/s
per layer (~-3.3%) vs hosting the same layers on the 5060 Ti. NET of the slower card and the x4 link
round-trips; this design cannot decompose the two and does not pretend to.
RETRACTION: §24's "3060-hosted expert layers perform AT LEAST AS WELL as 5060-Ti-hosted" was inferred
indirectly (aggregate constant > 9.48); a direct control now contradicts it. §24 header STAMPED
[PARTIALLY RETRACTED in §26]. What survives (the part that mattered): 3060-hosted layers remain enormously
better than CPU-hosted — the premise for using the card holds; the superlative does not.
PRACTICAL PLACEMENT CONSEQUENCE: fill CUDA0 to its servable limit FIRST, then spill to CUDA1. The Stage-1
config (10 CUDA0, 12 CUDA1) is already correctly ordered. ARM 008 pin placement guidance unchanged — pins
go on CUDA0, so the penalty does not apply to them.

B1 vs THE P2 DISCREPANCY — STRENGTHENS THE CURVATURE READING: if the 3060 is WORSE per hosted layer, S1
hit 11.52 tok/s per 100% DESPITE carrying a penalty on 12 of 22 resident layers. CUDA0-equivalent
correction: 27.2832 + 12x0.099 = 28.47 -> gain 4.55 over 29.17 points -> 15.6 tok/s per 100% = 164% of
9.48. STILL NOT RAISING THE MECHANISM BUDGET: that correction leans on a per-layer penalty estimated from
a single point and extrapolated to 12 layers — the exact two-point-extrapolation class retracted in §20g.
It is a reason to BELIEVE curvature is real, not a measurement of it. Budget stays 34.5 us until B2'
measures the curve directly.

B2 VOID = REAL FINDING, NOT A HARNESS PROBLEM: all four legs — -ot overrides not applied when
--n-cpu-moe present; all 288 expert tensors HOST; both cards idle. Both OK legs were -ot-only. Clean
common factor. --n-cpu-moe SILENTLY OVERRIDES user -ot placements for the same tensors (injects its own
overrides; catch-all wins; user placement discarded without warning) — the same silent-override class
filed four times this week. Issue filed (below). NOTE IN BODY: independently VINDICATES THE OWNER'S
DIRECTIVE that our placement policy must own its own flags rather than compose with upstream's — exactly
the collision they anticipated.
ISSUE 2: CLAUDE.md model facts STALE (names Qwopus3.6-35B-A3B Q3_K-mini at /mnt/WorkDisk/LLM-Models —
absent; live model /mnt/SSD/qwen3.8-flash-next-apex-mini, 6-shard). Stale hardware docs produce dispatch
errors; this one nearly voided a replicate. FIXED in the doc, not just filed.

B2' REDESIGN — PURE -ot, NO --n-cpu-moe ANYWHERE, six legs one session, everything else Stage-1-exact:
  D0   0 resident (all experts -ot to CPU)                    anchor
  D4a  4 on CUDA0                                             curve
  D8a  8 on CUDA0                                             curve + METHOD CONTROL (should
                                                              reproduce C 23.9244 at pure -ot)
  D4b  4 on CUDA1                                             penalty control #2
  D14  10 on CUDA0 + 4 on CUDA1                                curve
  D22  10 on CUDA0 + 12 on CUDA1                              curve + METHOD CONTROL (should
                                                              reproduce S1/B3 at ~27.3)
  Two free replications built in: if D8a or D22 misses its target, the pure--ot method is NOT equivalent
  to the mixed method and everything must be re-read BEFORE the curve is fitted — the report says so
  before giving a slope.
PRE-REGISTRATIONS (banked in this same commit, BEFORE dispatch):
  P-a) DEVICE PENALTY PROPORTIONAL to CUDA1-hosted layer count: D4b penalty ~= half of B1's -3.31%, i.e.
       -1.5 to -1.9%. If instead the penalty is roughly FIXED regardless of layer count -> link-setup cost
       rather than per-layer compute cost -> changes epic placement design (want FEW CUDA1 layers or MANY,
       not a middle).
  P-b) CURVATURE on penalty-corrected values, slope_late/slope_early:
         0.75-1.25 LINEAR   => 11.52/15.6 excess was drift in the original 9.48 derivation; budget STAYS
                              34.5 us; coverage model right as written.
         > 1.25 SUPERLINEAR => budget rises to ~42 us; epic gains a design input (last experts relocated
                              worth more than the first).
         < 0.75 SUBLINEAR   => ARM 008's 1.08x is an OVERESTIMATE; re-priced down again.
       ARCHITECT PREDICTION: SUPERLINEAR, ratio 1.2-1.5 (unchanged from §25, better instrument).
  P-c) THREE-BRANCH RULE APPLIED TO SELF: every branch above has a defined outcome including the one that
       embarrasses the architect. No two-branch registrations from here.
PROTOCOL UNCHANGED + PROMOTED: override-verify BEFORE timing is NON-OPTIONAL in every rig dispatch
(alongside serve-probe) — it is 3-for-3 against confident wrong numbers. Two gates, both earned, both
cheap. D1/D2, serve-probe, decode-split CPU% exclusively, per-device VRAM + sm%, temp 0.0 seed 42, 200
tokens, fresh server, unique port.

=== END SECTION 26 ===

=== §26a: B2' STOPPED AT D0 (raw; rig released clean; architect redirect pending) ===
D0 (anchor, all 48 expert layers -ot to CPU, NO ncm) BOOT_FAILURE: GGML_ASSERT ggml.c:6214
(state->ne[0] == S_v*S_v*H) in ggml_gated_delta_net via build_delta_net_fused, during resolve_fused_ops
("fused DeepSeek V4 HC support") / graph_reserve fit-check, ubatch n=4. Placement at death: 144
CUDA_Host overrides, second loader pass never ran. B3 (same session, GPU-resident experts) cleared the
same path. ONLY difference between every booting leg this week and D0: all-host expert placement.
UNRUN: D8a/D4a/D4b/D14/D22. Worker stop-rule executed correctly (no improvisation, exact signature,
rig free). Log: /tmp/opencode/armB2p-D0.server.log:2992. Same neighbourhood as #139/#149 — filing
withheld pending architect ruling.
=== END §26a ===

=== SECTION 27: B2'/D0 RULING + LINEAGE DEFECT (architect; commit timestamp is the single clock) ===

1. D0 STRUCK from B2', not replaced. Run D4a/D8a/D4b/D14/D22 unchanged. P-a carried by D4a/D4b pair (no
   zero point needed); P-b carried by interior points 4/8/14/22. Substituting C/L1 as low anchor is
   CROSS-IDIOM (ncm moves 288 tensors, pure -ot moves 144) and would reproduce the B2-VOID defect —
   FORBIDDEN. Re-idioming D0 costs rig time + crash risk to measure a config we are moving away from.
   PRE-REGISTERED CONSEQUENCE (accepted explicitly, cannot drift): B2' has NO measured zero anchor — no
   leg may be reported as absolute speedup over all-host; curvature + device pair are the ONLY claims.
   P-a/P-b/P-c stand; P-b prediction SUPERLINEAR 1.2-1.5.

2. D0 CRASH = REAL DEFECT, root cause located (measurement lineage source): llama_context::resolve_fused_ops
   (src/llama-context.cpp:505) probes each fused op by full graph_reserve, compares the fused node's
   SCHEDULED backend vs model.dev_layer(node.il), disables op on mismatch. D0 entered the HC probe chain
   once, emitted ZERO enabled lines -> died inside FIRST probe (dsv4_hc_pre) at ggml.c:6214 assert via
   build_delta_net_fused. Every booting leg emits the line TWICE (pre/comb/post enabled). Source comment
   at the device compare already concedes the check "is still wrong for cases like --no-kv-offload" —
   all-host experts is the same class. FILE (do not debug on critical path). THREE BRANCHES (three-branch
   rule): (a) 0-resident is a hard config limit; (b) fit-check artifact that would not fire at serve time;
   (c) the assert fires for ANY config where the HC-pre probe node lands off dev_layer, 0-resident merely
   the cheapest trigger. ARCHITECT PREDICTION: (c).

3. NEW STANDING GATE: FUSED-OP FINGERPRINT pre-timing. Fused-op enablement = scheduler backend assignment
   vs dev_layer — which -ot placement can change; two legs of one sweep can silently run DIFFERENT
   KERNELS. Every leg captures `grep resolve_fused_ops` from the UNFILTERED log; fingerprint mismatch =>
   VOID, never average. Joins override-verify as non-optional. RETROACTIVE AUDIT (architect, self-run):
   B1/B3/B2-4/B2-8/B2-14/B2-22 all carry IDENTICAL fingerprint (HC pre/comb/post enabled, resolution run
   twice) — confound did NOT fire in the B series; a measured control; §26 device-penalty reading
   survives. BUT a7-C/a7-S1 logs are GREP-FILTERED CAPTURES (fused lines discarded at capture time,
   unrecoverable) -> §26's penalty rests on a control whose fingerprint cannot be verified. STATED
   LIMITATION, not a retraction: B3's +0.35% replicate on a FULL log bounds any fused-config difference
   at <=0.35%, far inside ±2.4%. NEW RULE: keep raw server logs UNFILTERED; filter at read time, never
   at capture time.

4. LINEAGE DEFECT — OUTRANKS B2'. HOLD E0 WHERE IT IS.
   - Measurement binary src/llama-cpp/build-merge-full/ (from D0 stack trace), built 2026-09-17 00:18,
     CONTAINS upstream 90e0f5cfc "llama: refactor fused ops (#24646)" (tag b9924) — origin of
     resolve_fused_ops.
   - Epic tip 6f75b8fce does NOT contain 90e0f5cfc (verified both directions); a744d8019 and 86af0c9af
     (baseline-qwen4exp-mtp line) DO.
   - E0/E1 are being built on a lineage LACKING the fused-op refactor while EVERY campaign number (27.28
     anchor, 34.5 us budget, coverage constant) was measured on a lineage that HAS it. E1 GO/NO-GO vs
     34.5 us would be a CROSS-LINEAGE COMPARISON.
   - Checkout is ALSO DIRTY: ggml-cuda.cu modified, hydra-pins.h untracked — gather-MMVQ WIP sitting
     uncommitted on the epic branch.
   - CALL: rebase epic/148-per-expert-backend-selection onto the baseline-qwen4exp-mtp tip (epic carries
     little code; anchors cost weeks of rig time). If rebase not clean, the only alternative is
     re-anchoring on the epic lineage (re-run S1/C/B3, re-derive budget) — NOT recommended. NO E1 NUMBER
     ADMISSIBLE until this is closed. Rebase outcome reported to architect BEFORE E0 continues.
   - BUILD PROVENANCE RULE (same failure mode as the deploy-chain problem — binary mtime predates reflog
     rebase-finish; cannot pin the built commit): require git rev-parse HEAD + dirty flag written at
     build time and logged by EVERY leg.

5. FREE INSTRUMENT, TAKE NOW: "graphs reused" per request (a7-C logs it) = direct CUDA-graph capture
   counter — the instrument for the E1 launch-overhead risk. Add to the standing per-leg capture set
   IMMEDIATELY so we hold it on the stock baseline before the mechanism lands.

EXECUTION ORDER: (4) lineage check -> (1) five B2' legs -> (2)/(3) filings. E1 timing design review still
stands, now blocked behind the lineage answer.

=== END SECTION 27 ===

=== §27a: B2' SECOND STOP (D4a) + PROVENANCE HAZARD (raw; rig released clean; architect adjudication pending) ===
D4a (4 CUDA0 + 44 host, pure -ot, no ncm) BOOT_FAILURE — frame-for-frame IDENTICAL to D0: ggml.c:6214
assert in ggml_gated_delta_net via build_delta_net_fused, resolve_fused_ops -> graph_reserve fit-check.
Worker boundary observation (forensic, verified logs): boots at 26 host (B3) / 40 host (B1); aborts at 44
host (D4a) / 48 host (D0). D8a/D4b/D14/D22 UNRUN; fingerprint + graphs-reused gates never reached a live
leg. GPUs healthy; -ot DEPRECATED warning benign (present in clean boots too).
PROVENANCE HAZARD (facts, no conclusion): builder stamped build-merge-full CONTAMINATED (era-mixed
epic-object relink; attributes their three E0 verify-leg crashes to it) BEFORE the worker's D4a run —
D4a's binary may not have been Stage-1-exact; relink-vs-D0 timing unverified. The §27 provenance-stamp
rule arrived one session too late to settle it. NO further legs can run on build-merge-full (stamped
do-not-measure); next measurement base = builder's fresh build-e0 from the rebased epic (gated on
architect ack of continuation plan).
FORWARDED QUESTIONS: (1) D4a as #151-branch-(c) evidence vs era-mix confound; (2) does the 40/44 host
boot boundary survive a suspect binary; (3) B2' path — hold until fresh build-e0 then re-run five legs
on the new base (also makes the curve E1-comparable), or rebuild merge-full-equivalent first.
=== END §27a ===

=== SECTION 28: CONTAMINATION LEDGER (mtimes read off disk) + B3 BRIDGE CONTROL PRE-REGISTERED + §27 AMENDMENTS (architect; commit timestamp is the single clock) ===

CONTAMINATION ONSET: 2026-09-19 09:22:43 — mtime of build-merge-full/bin/libggml-base.so.0.13.1 +
libggml-cuda.so.0.13.1 (so/.so.0 repointed). Pilot ggml in same dir is libggml-base.so.0.23.0 @ Sept-17
(DIFFERENT SONAME); libllama.so.0.4.0 stayed Sept-17 pilot => D0/D4a binary was pilot libllama calling
EPIC ggml across an ABI boundary; ggml.c:6214 lives in the swapped library.
LEDGER: a7-C/S1/S1b (08:54:27/08:55:55/08:57:23) CLEAN; armB-B3 (09:18:21) CLEAN; armB-B1 (09:19:50)
CLEAN; armB-B2-4/8/14/22 (09:20:04-09:20:38) CLEAN (VOID for ncm, unrelated); armB2p-D0 (09:29:32) VOID
CONTAMINATED; armB2p2-D4a (09:42) VOID CONTAMINATED. CAMPAIGN NUMBERS SAFE — provably, not by assertion.
Caveat: rests on mtimes (mtime-preserving copy could defeat it) — exactly the hole the stamp closes.

(1) D4a is NOT branch-(c) evidence — fully confounded. BUT it kills branch (a) independently: 12 CUDA0
expert overrides = GPU-resident experts, crashed identically; the all-host story is dead either way.
#151 AS FILED STATES AN UNSUPPORTED ROOT CAUSE — ARCHITECT'S OWN ERROR, named as such (ruled a root cause
from a stack trace against source verified and a binary not verified, same turn the fingerprint gate was
minted). CORRECT #151: retitle to the signature (ggml.c:6214 GGML_ASSERT(state->ne[0]==S_v*S_v*H) in
fit-check graph_reserve), cause UNATTRIBUTED pending clean-base repro, record D4a counter-evidence +
contamination ledger.
(2) BOOT BOUNDARY STRUCK: perfectly confounded — every booting leg (26/40 host) before 09:22:43, every
aborting leg (44/48 host) after; zero legs cross the design; host-count and binary era collinear at 100%.
Ordering artifact, not a finding. Worker's forensic instinct commended; the inference was unavailable
without the mtimes.
(3) B2' HELD ENTIRELY. ONE BUILD, NOT TWO: rebased epic/148 @ 86af0c9af = campaign lineage, so a fresh
stamped build from it IS the merge-full-equivalent — the new measurement base for BOTH B2' and E1 (curve
directly E1-comparable). DO NOT ASSUME IT REPRODUCES: BRIDGE CONTROL B3 on the new base BEFORE any B2'
leg (B3 chosen: only leg with a clean cross-session replicate, 27.2832 -> 27.3789 +0.35%).
BRIDGE PRE-REGISTRATION — four branches, conservative default:
  (a) within ±2.4% of 27.3789 -> base CERTIFIED, campaign numbers carry forward, B2' proceeds.
  (b) outside the band -> base differs materially; HALT, re-derive anchors before E1.
  (c) does not boot -> assert is NOT era-mix but LINEAGE-INTRINSIC; #151 reopens with a real cause; B2'
      stays blocked.
  ANY OTHER OUTCOME (boots but fingerprint / graphs-reused / VRAM footprint differs) resolves to (b),
  NOT (a) — the out-of-frame hole is closed by construction (unregistered case lands conservative).
  ARCHITECT PREDICTION: (a) — "my last three predictions were wrong, so the pre-registration is doing the
  work here, not the guess."

AMENDMENTS:
  1. D0 RESTORED — the strike rested on a false premise. B2' is six legs again (D0/D4a/D8a/D4b/D14/D22);
     the §27 "no measured zero anchor" consequence WITHDRAWN.
  2. NEW GATE — BINARY IDENTITY PER LEG AT LAUNCH: leg script cats the PROVENANCE stamp; aborts if
     missing or CONTAMINATED; aborts if ANY object in the build dir is NEWER THAN THE STAMP. Stamp must
     record the NEWEST OBJECT MTIME, not the stamp-writing time (current stamp's date is AFTER the
     09:22:43 relink, so it cannot bound the contamination window). One-command version of the forensics
     just done by hand.
  3. MEASUREMENT BUILD DIRS ARE IMMUTABLE AFTER STAMPING. Never relink objects into one. Fresh dir per
     base, always.
  4. LLAMA_BUILD_UI=OFF approved (mirror merge-full).

ACK: builder continuation APPROVED as planned with amendments 2-4 folded in — replay WIP commit, wipe +
fresh-configure, STAMP, then BRIDGE CONTROL B3 BEFORE E0 VERIFY LEGS (the bridge certifies the base for
E0 and B2' simultaneously — not extra rig time). Worker commended (two clean first-leg stops + the
boundary observation that exposed the collinearity).
E1 TIMING DESIGN REVIEW: unblocked by the rebase, RE-BLOCKED behind the bridge control — route to the
architect when B3 clears.

=== END SECTION 28 ===

=== §28a: BRIDGE B3 YIELD (raw; builder self-reads branch (a); architect adjudication pending on two stated gaps; E0 continuation proceeding under pre-registration unless countermanded) ===
Provenance at launch: sha=be4676f11 branch=e0/measurement-foundation tree=clean objects=1789786240.
NOTE (worker-stated): binary CONTAINS the DISARMED E0 WIP (no HYDRA_* env -> arming gate untriggered);
certifies base+E0-disarmed as the future measurement stack; pristine-base rebuild offered if wanted.
Boot OK port 18090, D2 clean (BRIDGE_B3_DONE, lock released).
Override-verify BEFORE timing: EXACT 72/60/156. VRAM boot CUDA0 13659 / CUDA1 11809 (40 MiB under the
probe's 13699 — worker noted, unmatched).
RESULT: 27.4374 tok/s (wall 39.4s, prompt 32088ms, predicted_n=200, D1 PASS). Ratio vs 27.3789 =
1.00214 (+0.21%) — INSIDE ±2.4% band [26.7218, 28.0360] => worker reads branch (a) BASE CERTIFIED,
campaign numbers carry forward.
FINGERPRINT GAP (worker-stated): no direct per-call kernel-path line exists on this base (no MMVQ/MMQ
counters without arming); path evidence indirect — graphs-reused present (CUDA Graph id 105x reused),
VRAM split, in-band tok/s. Decode-split detail beyond CPU%dec/sm not instrumented on this leg.
CPU%dec 506 (n=9); sm 54.5 (n=39) / 46.5 (n=21).
OPEN FOR ARCHITECT: (1) does disarmed-WIP-in-binary satisfy §28's "fresh stamped build from the rebased
epic" certification, or is pristine-base required; (2) does the indirect-only fingerprint satisfy the
§28 conservative-default rule (unverifiable != differs, or resolves to (b)?). E0 continuation proceeding
per pre-registered (a) unless countermanded.
=== END §28a ===

=== SECTION 29: BRIDGE CERTIFIED — branch (a); both gaps resolved (one a conflation); VRAM threshold registered; E1 anchor RE-BASED to 27.4374 on stamp be4676f11; #151 settles free via B2' legs (architect; commit timestamp is the single clock) ===

CERTIFIED — BRANCH (a). Architect verified discriminators DIRECTLY, not from the summary:
  fused fingerprint: HC pre/comb/post enabled x2 — IDENTICAL to clean B3 (match)
  graphs reused: 198 vs 201 (+4 warmup req in clean B3) — equivalent (~1 reuse per decode step)
  eval tok/s: 27.44 vs 27.38 = +0.21% in band
  VRAM: -40 MiB = -0.29%

GAP (2) DISSOLVES — CONFLATION, not a gap: §28's "fused fingerprint" means resolve_fused_ops output,
which the BASE binary emits regardless of arming; the hydra MMVQ/MMQ kernel-path counters are
arming-dependent and a different thing. Bridge log carries the fingerprint DIRECTLY and matches clean
B-series exactly. Gate satisfied on direct evidence. (Worker's escalate-don't-self-certify instinct
praised even though the premise was wrong — that is the wanted behaviour.)
VRAM -40 MiB: absolute replaced by a REGISTERED THRESHOLD, effective now for all future bases — VRAM
footprint band ±1% (±137 MiB here); 0.29% passes. ATTRIBUTION OWED BEFORE E1 GO/NO-GO (not now): E1
allocates expert slabs, VRAM accounting becomes load-bearing; obtainable free from existing logs'
allocation lines, no rig time.

GAP (1): CERTIFY base+E0-disarmed. NO pristine gate. Decisive argument (asymmetry): if disarmed E0 cost
anything it would DEPRESS the anchor and INFLATE E1's apparent gain — a manufactured GO, the exact
failure mode E1's design must protect against; the bridge bounds that cost at +0.21% in the FAVOURABLE
direction (faster, not slower). Known limitation recorded: lineage change and E0 presence are confounded
in that single comparison; both would have to cancel to hide a real cost, against ±2.4%.
Pristine build: AUTHORIZED as non-blocking CPU-only background shelf artifact — retires the confound if
it ever matters. NOT a gate; E0 not held for it.

AMENDMENTS:
  1. E1 GO/NO-GO ANCHOR = base+E0-disarmed on stamp be4676f11 = 27.4374 (NOT the historical 27.3789).
     Arm/disarm A/B must run on the SAME BINARY, SAME STAMP, SAME SESSION — pre-registered requirement;
     a cross-binary mechanism delta is NOT admissible.
  2. B2' PROCEEDS on the certified base, six legs (D0 restored), all three gates live per leg:
     provenance-at-launch, fused fingerprint, graphs reused.
  3. #151 SETTLES FOR FREE: certified base = clean-base repro platform; D0 and D4a are already B2' legs.
     THREE BRANCHES: both boot => era-mix confirmed, CLOSE #151 as build-hygiene; both die =>
     lineage-intrinsic, #151 gets a real root cause, B2' halts; SPLIT (one boots one dies) => genuine
     placement dependence exists — most interesting result of the week. No extra rig time either way.

ACK: builder CERTIFIED, proceed to E0 verify legs. Worker: B2' six legs on the certified base when the
builder releases the rig.
E1 TIMING DESIGN REVIEW: UNBLOCKED — route the design NOW with the arm/disarm A/B protocol (amendment 1)
already reflected in it.

=== END SECTION 29 ===

=== §29a: E0 LANDED (builder final report; PR #152; gate proven both ways; E1 draft routed) ===
Verify legs on certified tree (stamped build-e0 be4676f11): armed boot -> load summary (48 layers, 384
pins, min8/max8, dups_removed=0) + [HYDRA e0] engaged=0 armed=1 (decode-only); gate CLOSED exit 1
pre-E1 as designed; BADPATH -> GGML_ABORT ggml-cuda.cu:2008; BADLINE -> abort; fixture self-test PASS
(open=0 closed=1 unwired=2). Zero nvcc warnings.
PR #152 -> epic/148 closing #142/#143/#144 (commits c7f36d52b/3553a8749/b07d41651; end tree
byte-identical to verified build; intermediates reviewed as mechanical subsets). #146 CLOSED (pointer
disposal: dedupe->#142/PR#152-c1, range-check->E1 hook). #148 criterion 1 extended (range-check
acceptance). Parent 6b02a71d7 (gate + stamp scripts + E1 draft). No effect numbers read anywhere.
Rig released 02:52:43Z -> B2' six-leg session launched on certified base. Pristine shelf build
in-flight (compile-only). E1 design draft committed (97 lines; amendment-1 constraint verified present
by leader grep before routing) -> routed to architect. E1 code: NONE written.
=== END §29a ===

=== SECTION 30: VRAM ATTRIBUTION (NOT benign — 3.0x snapshot-cache delta, certification stands, continuation-leg requirement) + E1 DESIGN APPROVED WITH SEVEN CHANGES (architect; commit timestamp is the single clock) ===

VRAM -40 MiB ATTRIBUTION DONE — NOT BENIGN: every allocation line byte-identical between clean B3
(pilot) and bridge B3 (certified) EXCEPT ONE — llama_kv_cache CUDA0 KV buffer 38.26 MiB (clean) vs
12.76 MiB (bridge), same log line (5935), same filtered-layer pattern, same 12 unfiltered CUDA0 layers,
identical n_ctx/n_ctx_seq/n_seq_max/kv_unified. It is the SECOND KV cache instance (102.00 MiB one
identical in both), constructed t~0.35s. 38.26/12.76 = EXACTLY 3.0. A 3x difference in a secondary
hybrid KV/CHECKPOINT-SNAPSHOT cache is not a rounding artifact — it is the K snapshot-slot dimension
(same machinery as #469 trailing-token reuse and #641 hybrid checkpoint rewind, the two nastiest bugs in
project history). Certification STANDS (0.29% << ±1%, +0.21%, fingerprint identical). BUT invisible to
all planned legs (B2'/E1 = cold single-turn; snapshot slots bite on CONTINUATION and PROMPT-CACHE REUSE).
NEW REQUIREMENT (not blocking E1): before the epic goes to the OWNER MERGE GATE, run one continuation/
cache-reuse leg clean-vs-bridge. FILED (below). STANDING RULE FROM IT: the ±1% VRAM band caught this by
ACCIDENT; a band on identical-vs-not would have caught it ON PURPOSE — allocation lines are compared
EXACTLY going forward; any non-identical buffer is NAMED and ATTRIBUTED, not banded.

E1 DESIGN: APPROVED with SEVEN required changes, three structural:
  S1 — E1 at one site CANNOT be verdicted on throughput: 1 invocation of 144/token; prize 2.689 ms/token
       /144 = 0.019 ms/token vs 36.45 ms baseline = 0.051% vs ±2.4% band = 47x BELOW RESOLUTION.
       AMENDMENT 1 CORRECTED: same binary/stamp/session STANDS; disarmed control STANDS as a
       NO-REGRESSION CHECK, not a gain check. E1's verdict = per-invocation us against 34.5, FULL STOP.
       The report must say in its own words: E1 is a MECHANISM-COST GATE and does NOT test C3;
       hits-convert-to-throughput is E2's job at 48-layer scope; NO ONE may read an E1 GO as evidence
       for the ranking thesis (the owner's actual goal, still 0-for-3).
  S2 — graph capture vs CUDA events are IN TENSION (events inside captured graph replay every launch;
       pairing across in-flight replays ambiguous; timing can perturb capture). RESOLUTION — two leg
       types on one binary: (i) timing ON / graphs OFF -> attribution us; (ii) timing OFF / graphs ON ->
       no-regression throughput + graphs-reused gate. Report both. BIAS NAMED: graphs-off OVERSTATES
       launch cost, biases toward NO-GO — the safe direction; a MARGINAL NO-GO IS NOT FINAL.
  S3 — the 2k doubling is NOT excluded (exactly what killed the last attempt): a mul_mat_id-shaped
       implementation computes all k per branch = 2k total, returns CORRECT results, yields a WRONG
       verdict. REQUIRE: total expert-row work across both branches == k, enforced by a rows_computed
       counter per invocation with HARD GATE rows_computed == 10 — a counter in the log, like the
       engagement gate, not prose.
  A4 — middle band UNREGISTERED: register <11.5 us GO; >34.5 us STOP; 11.5-34.5 us CONDITIONAL —
       proceed to E2 timing harness ONLY, no correctness work, re-price at 144-invocation scope.
       Asymmetry stated: scope only ADDS cost (144 scheduler switches), never removes it — a marginal
       E1 predicts an E2 failure.
  A5 — correctness gate AT E1: teacher-forced --kl-divergence armed vs disarmed (ships in fork,
       perplexity.cpp:1949) — not sampled trajectories, not PPL, not char-position.
  A6 — instrument negative control: armed+timing vs armed-no-timing, same binary; if the instrument
       moves throughput, the us describe the instrument, not the mechanism.
  A7 — label §7 scopes: the 78/144-invocation pricing is E2 pricing; E1 runs 1 — risk framing, not E1's
       expected cost.
  FREEZE LIST: keep the three open items; ADD the timing/graphs leg split (S2) + rows_computed gating
  (S3). §8 stays — E1 still owes per-leg allocation reporting; the BASE attribution above is discharged.

ROUTE TO BUILDER: approved — implement S1-S3 + A4-A7, return revised draft; NO re-review needed if all
seven are in and the freeze list is closed. B2' yield -> architect raw when the session lands; D0/D4a
settle #151 on the three registered branches.

=== END SECTION 30 ===

=== §30b: B2' YIELD (confounded) + E1 DESIGN CLOSED + LEADER SEQUENCING ERROR OWNED ===
B2' six legs RAN, all gates pass (provenance be4676f11 clean, override-verify exact, fingerprint OK,
D1/D2 clean, zero crashes) — BOTH METHOD CONTROLS MISSED: D8a 11.8480 vs C = -50.48%; D22 16.7709 vs
S1 = -38.53%. Pre-registered rule fired: curve-fitting STOPPED. Raw: D0 14.1568, D4a 22.3940, D8a
11.8480, D4b 9.8755 (below D0 despite 4 GPU layers), D14 12.6443, D22 16.7709. Graphs 164-212 (certified
family 198/201).
CONFOUND — LEADER ERROR OWNED: the pristine shelf build ran on the box 10:06 -> session end (full CUDA
build, compilers at 100-233% CPU; sar %idle 83.8 -> 60.4). I authorized "non-blocking CPU-only
background" WHILE rig legs ran — on a CPU-bound rig that is an oxymoron. Controls' miss is CONFOUNDED
(method-vs-contention indistinguishable). NEW STANDING RULE: NO builds/compiles during a rig session —
"CPU-only" is not a scheduling class on this box; rig sessions own the machine.
#151 INPUTS (boot facts, confound-immune): D0 BOOTED + D4a BOOTED on certified base => three-branch
"both boot" => era-mix confirmed => close as build-hygiene (formalization: architect).
D22 VRAM 14539/10923 = +6.44% vs bridge-implied — OUTSIDE ±1%; worker mapping note (CUDA0-first mapping
differs; per-layer expert quants heterogeneous: blk.0 gate 306 MiB iq3_xxs vs blk.30 206 MiB iq2_xxs) —
attribution: architect.
E1 DESIGN CLOSED: revised draft committed (157 lines); leader grep verified all seven anchors present
(S1x2, S2x4, S3x2, A4x6, A5x1, A6x1, A7x5, freeze x4) => per §30 no re-review needed; E1 implementation
may start (CPU code; builds wait for quiet box per new rule). Pristine shelf artifact COMPLETE (EXIT=0).
=== END §30b ===

=== §31: ARCHITECT B2PRIME RULING — ERROR OWNERSHIP SETTLED, TWO LEGS CLEAN, QUIET-BOX RE-RUN, #151 CLOSED, UNIFORM CONSTANTS INVALIDATED ===
ERROR OWNERSHIP: architect owns the sequencing error (wrote "non-blocking CPU-only background" in §29 while
holding their own CPU-saturation finding); leader's rule adopted mutually — NO compiles during rig sessions.
CLEAN LEGS: D0 (10:04:18) + D4a (10:05:32) completed BEFORE the 10:06 compile start = CLEAN. Decode-CPU%
series breaks EXACTLY at the boundary (485/602 before vs 305/280/283/272 after) — cause + CPU%-as-detector
both independently confirmed. D8a straddles; D4b/D14/D22 void. CLEAN D0->D4a = +58.2% (2.06 tok/s/layer for
first 4 vs 0.60 avg over 22) — SUBLINEAR hint, AGAINST the architect's registered SUPERLINEAR 1.2-1.5; not
ruled (two points, cross-idiom, layer identity changes VRAM 1.5x). Fourth wrong prediction this session;
pattern = directional over-optimism about payoff curve; E1 priced with that in mind.
(1) RE-RUN all six on quiet box. NO splicing — clean D0/D4a = EXTERNAL REPLICATION CHECKS (±2.4%) doubling
as quiescence-gate validation. NEW GATE: system QUIESCENCE — sample non-rig CPU before/during each leg,
abort if any non-rig process materially active. Decode CPU% = reported COVARIATE, flagged when anomalous,
NEVER auto-voiding (absolute band deliberately rejected: config-dependent).
(2) #151 CLOSED per branch (a): root cause = ERA-MIXED LINK (pilot libllama.so.0.4.0 + epic ggml 0.13.1
across soname boundary, onset 09:22:43). Architect's §27 resolve_fused_ops attribution RETRACTED. "40 host
boots / 44 host dies" boundary FORMALLY DEAD (collinear). Lesson disposition: binary-identity gate (live).
(3) D22 VRAM attributed: mapping x heterogeneous per-layer expert quant (1.49x) => INVALIDATES banked
uniform constants (916.7 MiB/layer, 1.79 MiB/expert). Offline re-derivation from actual per-layer tensor
table; servable-window [8,12) + "5.7x more VRAM-efficient" claims RECOMPUTE-OR-RETRACT (rig-free).
Layer-count-indexed sweeps DON'T hold VRAM constant: B2prime re-run states layer-selection rule + reports
actual bytes per leg.
(4) E1 coupling (no design change): no-build rule + quiescence gate apply; E1 CPU branch is
CPU-availability-sensitive (synchronous host compute) => decode CPU% + quiescence reported alongside every
us number; 34.5us bar derived at normal load — marginal NO-GO on a noisy box is NOT final (safe bias).
(5) NEW FINDING (verified by leader: L1 log = -t16 matching D0, completed 08:18:32 << 09:22:43 onset):
clean D0 pure--ot all-host = 14.1568 vs L1 --n-cpu-moe 48 all-host = 21.28 = -33% for pure--ot at
nominally identical placement — method-control failure on CLEAN data => issue filed; second independent
count against ncm/-ot equivalence alongside #149; supports owner directive to ship own flags.
E1 implementation proceeds (CPU code, build queued for quiet box). Bank = §31.
=== END §31 ===

=== §31a: B2' QUIET-BOX RE-RUN YIELD (ARM_B2P3R_DONE) — CONTROLS PASS, D0 NON-REPLICATES, #154 PREMISE SHAKEN ===
All six legs clean (provenance be4676f11, fingerprint OK, D1/D2, quiescence gate HELD every leg — during-leg
transients btop/git/mcp-daemon reported+kept, none toolchain; zero boot failures, second consecutive D0+D4a
boots). REPLICATION: D4a +1.47% INSIDE. METHOD CONTROLS PASS: D8a -1.00%, D22 +1.66% vs S1 (+1.08% vs
bridge) => pure--ot equivalence RESTORED on quiet box; confounded-session misses = pure contention. D0 DOES
NOT REPLICATE: 20.9780 vs 14.1568 = +48.20% (different trajectory content_len 589 vs 655; decode-CPU 610 vs
485) — two ruled-clean D0s disagree. BEARING ON #154: re-run D0 20.98 vs L1 21.28 = -1.4% (vs prior -33%)
=> #154 premise rests on one leg of a non-replicating config — disposition: architect.
RAW CURVE: D0 20.9780 / D4a 22.7226 / D8a 23.6862 / D4b 21.9293 / D14 25.5666 / D22 27.7349. Graphs
196/180/164/212/172/204. Decode-CPU: 610/649/594/601/563/447. Prior SUBLINEAR registration was anchored on
the non-replicating D0 (+58.2% first-4 read collapses to +8.3% on re-run); increments 1.74/0.96/1.88/2.17,
no clean curvature read. BYTES measured: blk.0-3 ~1058.5 / blk.4-7 ~937.0 / blk.8-21 ~887 MiB/layer —
uniform-constant kill CONFIRMED + quantified. D4b vs D4a (same 4 layers, CUDA1 vs CUDA0): -3.5% device-identity
signal, ruling pending. Builder E1 part-1 build window OPENED (rig free). Bank = §31a.
=== END §31a ===

=== §32: ARCHITECT RE-RUN RULING — METHOD-CONTROL CHAPTER CLOSED, #154 WITHDRAWN, D0 FINDING, TWO LEGS BEFORE BUILD ===
SWEEP COHERENCE: architect reconstructed placements from measured bytes — reconciles EXACTLY with override
counts; D22 = Stage-1 by construction (10 C0 / 12 C1).
(4) METHOD CONTROLS CLOSED: 4 independent confirmations pure--ot == --n-cpu-moe on quiet box (D8a/C -1.00%,
D22/S1 +1.66%, D22/bridge +1.08%, D0/L1 -1.4%); confounded-session misses = contention, full stop. E1 verdict
path free — but was ALREADY decoupled by S1 (per-invocation us + same-session disarmed control never
depended on this sweep). D22 27.7349 = highest campaign number but +1.08% over bridge, INSIDE band = replicate,
not record — no win claimed.
(1) #154 WITHDRAWN+CLOSED: premise (clean D0 14.1568 vs L1 -33%) gone (re-run D0 -1.4% vs L1); amendment
unavailable. Owner's own-flags directive UNTOUCHED — stands on #149 (directly observed ncm-discards-ot) +
design argument. #149 stays open on own evidence.
D0 FINDING (do not bury): two gate-passing legs disagree +48.2% with different decode trajectories =>
matches banked MTP draft-acceptance nondeterminism (190x variance drop with MTP off, 2026-09-12); all-host
amplifies (max miss cost). D0 NOT a usable anchor in ANY configuration. NEW RULE: content_len + draft-
acceptance stats = per-leg covariate; >=40-host-layer legs have wider UNQUANTIFIED band until measured.
(2) P-b UNRESOLVED, NO INCUMBENT: architect withdrew BOTH registrations (sublinear rested on dead D0 anchor;
superlinear equally unsupported). Clean sub-segment D0->D4a->D8a (CUDA0-only contiguous): 0.436->0.241
tok/s/layer, 4.12e-4->2.57e-4 tok/s/MiB, ~1.6x decline = sublinear INDICATION from 2 intervals, not a ruling.
ORDERED leg D10a (blk.0-9 CUDA0, nothing CUDA1): third device-constant interval + exact D14 decomposition
(D14 = D10a + 4 CUDA1). CUDA0 VRAM ceiling ~10 layers = last point on this axis; 3 intervals is P-b's budget.
(3) DEVICE IDENTITY PLAUSIBLE, one leg from CONFIRMED: D4a/D4b identical layers/bytes/device-only diff = -3.49%
vs B1's -3.31% for 8 CUDA1 layers => does not scale with count = FIXED-COST signature, but cross-basis pair
won't confirm. ORDERED leg D8b (blk.0-7 CUDA1, mirrors D8a). THREE BRANCHES REGISTERED: ~-3.5% => FIXED
(P-a resolved, constant toll); ~-7% => PROPORTIONAL (per-layer CUDA1 pricing); else => P-a open, D4a/D4b
suspect. Architect prediction: FIXED.
SEQUENCING: D8b + D10a FIRST, then release box to builder for E1 part-1 build window (approved, starts when
legs clear). LEADER EXECUTION NOTE: my earlier window-opening was PREMATURE (crossed with §32) — revoked to
builder (compile killed if started; part-2 code only); legs dispatched D8b->D10a with new covariate rule.
Bank = §32.
=== END §32 ===

=== §32a: D8b + D10a YIELD (ARM_B2P3R2_DONE) — THIRD CURVATURE INTERVAL NON-MONOTONIC, MTP-ABSENCE OBSERVATION ===
Both legs clean (provenance be4676f11, D1/D2, fingerprint 2/2/2/2, graphs 201+4, quiescence baseline-only,
zero boots failures). D8b (blk.0-7 CUDA1, D8a mirror): 23.1017 = -2.47% vs D8a / -3.44% vs C. Direct
mirrors now: D4b/D4a -3.49% (4 layers) vs D8b/D8a -2.47% (8 layers) — neither cleanly FIXED nor PROPORTIONAL;
branch call = architect. D10a (blk.0-9 CUDA0, = D14 minus 4 CUDA1): 24.7432. Third device-constant interval:
I1 +1.7446 (4234 MiB) = 0.436/layer 4.12e-4/MiB; I2 +0.9636 (3748) = 0.241/layer 2.57e-4/MiB; I3 +1.0570
(1774) = 0.529/layer 5.96e-4/MiB — NON-MONOTONIC across all indices; two-interval sublinear indication does
NOT survive interval 3 cleanly. D14-D10a = +0.8234 for exactly 4 CUDA1 layers. MTP OBSERVATION (bears on §32
D0 attribution): NO draft activity on either leg — DRAFT_FIELDS absent, zero acceptance lines, draft 0.000
MiB; if original D0 legs also MTP-inactive (logs: armB2p3-D0 vs armB2p3r-D0), the +48.2% attribution needs
architect re-examination. Covariates: content_len 633/656, decode-CPU 549.8/563.6, host-layers 40/38. Bytes:
D8b C1 7982.0 (identical to D8a), D10a C0 9756.0 (identical to D14/D22). E1 build window RE-OPENED per §32
sequencing (legs cleared, box released). Bank = §32a.
=== END §32a ===

=== §33: ARCHITECT FINAL B2PRIME RULING — P-a RESOLVED (PROPORTIONAL REFUTED), P-b CLOSED UNRESOLVED, B2PRIME CLOSED, MTP ATTRIBUTION SELF-AMENDED ===
P-a RESOLVED: absolute tolls discriminate — D4b/D4a (4 CUDA1 layers) -0.7933 tok/s (-3.49%); D8b/D8a (8
layers) -0.5845 (-2.47%). PROPORTIONAL demands 8-layer toll ~double 4-layer; it is SMALLER => REFUTED
decisively. FIXED not confirmed (registered ~-3.5%, got -2.47% — gate fails on own terms, no retrofit).
Weak form banked: PLAUSIBLE ~3% CUDA1 toll, no count dependence over 4-8 layers; SIGN certain (3/3
negative), constancy not. RETRACT §26's "0.099 tok/s per CUDA1-hosted layer" (proportional model, refuted).
Replacement (architecturally actionable): CUDA1 has a FIXED ENTRY COST and is then nearly free per layer —
never engage lightly; either don't engage CUDA1 or engage it heavily. Corroborated by campaign-best D10a+12
CUDA1 = 24.7432 -> 27.7349 = +2.99 tok/s NET after the toll.
P-b CLOSED UNRESOLVED: intervals 0.436/0.241/0.529 tok/s/layer (4.12/2.57/5.96 e-4 per MiB), non-monotonic,
2.3x spread, I3 highest. Sublinear dead, superlinear dead, NO incumbent. Fragility named: D8a alone drives
I2+I3, no replicate — architect NOT ordering one. B2PRIME CLOSED. Products: (1) pure--ot == ncm on quiet
box (4 confirmations); (2) CUDA1 toll ~3%, proportional refuted; (3) per-layer byte heterogeneity measured
(1058.5/937/887 MiB) — uniform constant killed by numbers; (4) 4th Stage-1 replicate; (5) #151 closed;
(6) #154 withdrawn.
MTP ATTRIBUTION AMENDED — ARCHITECT OWN ERROR: both D0 logs show NO draft activity (one prompt_save line
each, zero acceptance lines) => MTP EXCLUDED. Contention ALSO excluded: slow-decode D0 was FASTER at
prefill (180.63 vs 168.16 tok/s) — a starved box loses both phases. +48.2% is DECODE-SPECIFIC, correlates
with decode-CPU% (485 slow / 610 fast), on the maximally CPU-bound config — MECHANISM UNATTRIBUTED.
Covariate rule stands on own merits; D0 anchors nothing in either direction. NEW STANDING RULE (architect,
minted against self): NO mechanism attribution enters the bank without the log check that would refute it —
"a coincidence that fits is a hypothesis, not a finding."
STANDING: E1 build window PROCEED; no further rig requests from architect; next architect deliverable = E1
leg-protocol review when builder part-2 lands. Bank = §33.
=== END §33 ===

=== §33a: E1 PART-2 LANDED — CLEAN STAMP, PACKAGE ROUTED FOR LEG-PROTOCOL REVIEW ===
Builder part-2 package: commit `739084e4a` (feat(hydra-e1): E1 part 2 — split compute kernels, event-pool
harvest, per-branch split counting, KL leg; +431/-55, 2 files, no trailers per §25a) on part-1 `81f92d5bc`.
3 review defects fixed in-code (output misalignment, uninit packet, hardcoded sizes). FINAL STAMP:
sha=739084e4a tree=clean objects=1789790508 @ 2026-09-19T04:01:48Z (relink-only restamp EXIT=0). Disclosed
deviation: dirty-tree build 81f92d5bc-dirty superseded by restamp — builder proposed + leader adopted
STANDING RULE: stamp-after-commit MANDATORY; dirty-tree builds are disclosure-required deviations, never
the record of achievement. Freeze choices in code: site (15, ffn_moe_up-15, id 1) + weight backup + merged
gate_up never-engaged; 512B device LUT consult (zero D2H); 16-cadence lazy harvest; HYDRA_E1_DRYRUN x
HYDRA_E1_TIMING leg matrix (+GGML_CUDA_DISABLE_GRAPHS type-(i)); fail-closed aborts; KL script (threshold
required); capture-semantics annotation design §2; zero layer-count-derived sizes.
LEADER-PROPOSED LEG SEQUENCE (architect review pending): (1) dry-run verify legs, (2) instrument negative
control (A6), (3) type-(i) attribution timing-ON/graphs-OFF -> us vs 34.5, (4) type-(ii) no-regression
timing-OFF/graphs-ON + graphs-reused, (5) KL gate (A5); same binary/stamp/session A/B, quiescence,
covariates, fingerprint, binary-identity on every leg. Package routed to architect 2731baf3. No legs run.
Rig FREE. Bank = §33a.
=== END §33a ===

=== §34: ARCHITECT E1 LEG-PROTOCOL REVIEW — SITE-RESIDENCY BLOCKING PRECHECK, DIFFERENTIAL VERDICT, AMENDED ORDER ===
BLOCKING (no rig): SITE-RESIDENCY PRECHECK — under Stage-1/D22 placement (blk.0-9 CUDA0 / blk.10-21 CUDA1 /
blk.22-47 host) the frozen site layer 15 falls in the CUDA1 band => experts already GPU-resident =>
mechanism would route work TOWARD CPU at that site = WRONG DIRECTION vs the CPU->GPU prize the 34.5us bar
prices. REQUIRED: state the E1 leg placement config and confirm the site layer is HOST-resident in it; if
not, RE-FREEZE site to highest-h layer WITHIN blk.22-47. h and residency are independent axes; the freeze
constrained only one.
STRUCTURAL: us verdict must be DIFFERENTIAL not absolute — verdict = armed_us - disarmed_us (stock path at
same site, disarmed-site timing leg ADDED to matrix). Absolute-vs-differential-bar biases FALSE STOP, and
STOP is the banked epic-ending commitment = most expensive possible protocol error.
AMENDED ORDER: 1 dry-run verify (match/engage/rows==10); 2 site-residency precheck (no rig); 3 KL gate
MOVED UP (correctness is a gate not a report — fast-and-wrong invalidates every later number); 4 type-(ii)
no-regression + graphs-reused promoted (coarse screen; REGISTERED: graph-veto outcome = VOID not NO-GO —
collapse out of 164-212 family = design violation to fix in code, pass/fail leg gate not covariate);
5 A6 instrument negative control WITH GRAPH STATE HELD CONSTANT (both sides graphs-OFF — proposed version
would have compared across graph states and measured graphs); 6 disarmed-site timing leg; 7 type-(i)
attribution -> DIFFERENTIAL us verdict.
BANDS CONFIRMED VERBATIM on differential us: <11.5 GO / 11.5-34.5 CONDITIONAL E2-only / >34.5 STOP.
REPORTING: E1 report states next to verdict that single-site timing EXCLUDES per-token scheduler-switch
cost (144-site scope only) => GO optimistic by unmeasured amount, E2 re-prices.
CODE INSPECTION BEFORE RIG (two items): (1) event pool growth bound — unbounded if harvest lags engagement
(same class as the never-freed raw cudaMalloc found in the gather path, same file); (2) CPU-branch blocking
semantics — synchronous host compute must not serialize behind unharvested event or force stream sync (sync
forbidden on engaged path; this is where it would sneak back).
APPROVED AS PROPOSED: stamp-after-commit standing rule; pin-set+live-tensor-bytes sizing (closes §31
defect class); 512B LUT zero-D2H; 16-cadence lazy harvest; fail-closed aborts; KL threshold-required;
capture-semantics annotation.
LEGS MAY START once items 2 and 3 clear — site-residency check FIRST (if the site moves, the freeze
changes before anything burns rig time). Bank = §34.
=== END §34 ===

=== §34a: SITE-RESIDENCY PRECHECK VERDICT — SITE INVALID, RE-FREEZE TO LAYER 31 CONFIRMED ===
Builder precheck (zero rig): site layer 15 is GPU-RESIDENT under BOTH documented band variants (ARM007
probe: blk.0-11 CUDA1 / blk.12-21 CUDA0 / blk.22-47 Host; D22: blk.0-9 CUDA0 / blk.10-21 CUDA1 / blk.22-47
Host) => original freeze INVALID for the mechanism direction (would route miss work TOWARD CPU at a
GPU-resident site). Config statement: E1 legs' base placement = heterogeneous tensor-override deployment
(288 --override-tensor lines); PREREQUISITE NOW EXPLICIT: E1 legs MUST carry override placement — under
-ngl 99 nothing is host-resident anywhere.
RE-FREEZE CONFIRMED (leader, deterministic application of architect's §34 rule — highest-h within
blk.22-47): LAYER 31, ffn_moe_up-31 / blk.31.ffn_up_exps (gate_up merged-node recognition as
ffn_moe_gate_up-31). Data: phase0_rank.json self_heldout per_layer @42 — coding 31 @ 0.766 (next 44 @
0.704), general 31 @ 0.742 (next 32 @ 0.626); both corpora converge on 31. Host-resident under both band
variants; pin-covered (E0 verify min8/max8 x 48 layers = 384); code type-agnostic to layer-31 quant.
Layer 15 reference: 0.813/0.789 top overall, disqualified on residency — h vs residency independent axes.
Builder proceeding steps 2-4 (differential verdict code, event-pool/CPU-branch inspections, KL-first +
VOID-not-NOGO + A6 protocol changes) on the new freeze; commit approval due at part-3 code-complete
(stamp-after-commit). No legs until architect clears amended protocol with new freeze. Bank = §34a.
=== END §34a ===

=== §34b: E1 PART-3 CODE-COMPLETE — DIFFERENTIAL TIMER, BOUNDED POOLS, PROTOCOL DRAFT AMENDED ===
Builder part-3: re-freeze applied (HYDRA_E1_LAYER 15->31, node/weight/gate_up to -31 names). GGUF ground
truth direct-parsed shard 5: L31 up (2560,640,512) IQ2_XXS / gate Q8_K / down IQ1_S — MMVQ + device-dequant
+ getrows coverage verified in-tree; zero ffn_moe_up-15 strings in built .so. HONEST FLAG accepted: direct
parse shows L15-up IQ2_XXS (earlier session summary said IQ2_XS — direct parse authoritative; code
type-agnostic, moot).
DIFFERENTIAL VERDICT CODE: stock_begin/stock_end bracket stock dispatch (disarmed + HYDRA_E1_TIMING +
name-match + MMVQ-decode scope gate; silent delegation never aborts disarmed — fail-open instrument /
fail-closed mechanism split, leader-endorsed); 4th E0 ring channel `stock` (n/p50/p99/mean); verdict =
armed - disarmed from same log.
INSPECTION (a) POOL BOUND: fixed 16-slot pools — no growth possible under lag (lag = counted skips);
slot_recorded/stock_recorded masks (elapsed time can never touch unrecorded event); per-slot ordering
argument; drops counted; NO CUDA calls at teardown (tail excluded by construction).
INSPECTION (b) BLOCKING: harvest query-only (zero syncs); two labeled syncs — SYNC-STAGE (consult staging,
prior stream work only) + SYNC-JOIN (single join incl. GPU branch => SUM not max per design §2); host dots
host-memory only; no path waits on unharvested events (fence-ready proves bracket complete).
PROTOCOL DRAFT amended: §0 placement prerequisite (override mandatory, -ngl 99-only legs VOID) + frozen
site; leg type (iii) + differential definition; bands on differential; VOID-not-NOGO graph veto; A6
graphs-OFF both sides; KL position 3; full amended order; scheduler-switch exclusion note on GO; open
items site CLOSED.
Build EXIT=0 zero nvcc warnings (.so relinked). APPROVED: fork commit `feat(hydra-e1): E1 part 3 ...` +
parent docs commit (draft + PROJECT_STATUS) + restamp clean. Package routes to architect for
amended-protocol clearance after stamp. No legs. Bank = §34b.
=== END §34b ===

=== §34c: E1 PART-3 LANDED — PACKAGE ROUTED FOR LEG CLEARANCE ===
Fork commit `4c2b9cb67` (part 3, +166/-16, no trailers); parent `1dc3e43ac` (draft + PROJECT_STATUS,
+89/-21). FINAL STAMP sha=4c2b9cb67 tree=clean objects=1789791310 @ 2026-09-19T04:15:10Z (dirty
verification build disclosed + superseded; stamp-after-commit holds). Package for architect clearance:
81f92d5bc + 739084e4a + 4c2b9cb67 + stamp + amended draft + layer-31 freeze.
LEADER RULING — TWO DEFERRALS into the E1 PR (not per-part commits): submodule pointer bump + untracked
scripts/hydra-kl-gate.sh; architect flag-invited.
CLEARANCE REQUESTED: GO/NO-GO for leg session on amended order (dry-run -> KL pos3 -> type-(ii) graphs
pass/fail -> A6 graphs-OFF both -> disarmed stock timing -> type-(i) attribution -> differential verdict
vs bands) on certified base be4676f11 + stamp 4c2b9cb67; quiescence + covariates live per leg. No legs
until architect clears. Rig FREE. Bank = §34c.
=== END §34c ===

=== §35: ARCHITECT GO — LEG SESSION CLEARED, THREE LEG-SPECIFIC CONDITIONS ===
Site re-freeze independently verified by architect (layer 31 ∈ [22,47] = host-resident under both
variants; deterministic, not judgment). All §34 items addressed specifically. Deferrals (submodule bump,
kl-gate script -> E1 PR) endorsed as PR hygiene.
CONDITION 1: stock.n == 0 => disarmed-site leg VOID, never disarmed_us = 0 — fail-closed on the READING
side (zero baseline would revert differential to absolute = the false-STOP the differential exists to
prevent; complement of the silent-delegation choice).
CONDITION 2: name the exact CUDA primitive behind SYNC-JOIN BEFORE type-(ii) — if cudaStreamSynchronize
(or any blocking stream/event sync) => graphs vetoed BY CONSTRUCTION => type-(ii) would VOID predictably
(a wasted leg under the registered rule); ggml-level or event-dependency join = no issue. Hard stop-point
in execution: report primitive, WAIT for leader ack, then type-(ii).
CONDITION 3: commit scripts/hydra-kl-gate.sh BEFORE leg 3 (KL) — a gate executed from an untracked file
is not auditable/reproducible. Leader pre-approved the commit.
REPORTING (false-GO direction): layer-31 frozen site up = IQ2_XXS (~2.06 bpw) vs gate Q8_K (~8.5 bpw) in
the same layer (~4x row bytes); mechanism overhead largely fixed but COMPACTION SCALES WITH ROW BYTES =>
differential at IQ2_XXS UNDERSTATES cost at heavier-quant sites. State next to verdict: a GO at
layer-31/up/IQ2_XXS does not generalize to heavier sites without re-measurement. Architect explicitly NOT
widening E1 scope to add the gate site (freeze + STOP commitment worth more than the bracket); carried as
E2 MUST-HAVE.
CLEARED ORDER: dry-run -> KL(pos3) -> type-(ii) graphs pass/fail -> A6 graphs-OFF both -> disarmed stock
timing -> type-(i) attribution -> DIFFERENTIAL verdict vs <11.5 GO / 11.5-34.5 CONDITIONAL E2-only /
>34.5 STOP. Base be4676f11, stamp 4c2b9cb67, same binary/stamp/session arm-disarm, quiescence + covariates
per leg. Yield raw -> architect rules against bands WITHOUT adjustment — "including STOP, which stands as
a commitment, not a negotiation." Leader dispatch executed (kl commit pre-approved; hard stop-point at
SYNC-JOIN report). Bank = §35.
=== END §35 ===

=== §35a: CONDITION-3 SATISFIED + LEG SESSION MID-FLIGHT; SUMMARIZER ROLE CORRECTION ===
Builder (via summarizer's verification pass, banked as facts): scripts/hydra-kl-gate.sh COMMITTED 87e897d34
BEFORE the KL leg => §35 condition 3 SATISFIED. Dry-run complete (e1-legs-DRYRUN.server.log shows blk.31
overrides live — layer-31 freeze on the box). KL leg RUNNING (kl-cmp.log chunk ~20). Rig worker idle, no
contention (E1 legs with builder as instrument author — division confirmed). Remaining sequence: KL done ->
SYNC-JOIN primitive report -> LEADER ACK (hard stop-point, condition 2) -> type-(ii) -> A6 graphs-OFF both
-> disarmed stock (stock.n==0 => VOID) -> type-(i) -> differential verdict -> raw yield ARM_E1_DONE ->
architect band ruling without adjustment.
LEADER RULING: summarizer attempted monitoring/ack role — CORRECTED to summarizer-only; monitoring +
SYNC-JOIN ack + routing are LEAD functions; summary rebuild re-requested (deliverable = markdown only).
Native orchestration MCP track_status intermittently failing ("track not found") — REST/curl fallback +
bank remain the state store (known, non-blocking).
=== END §35a ===

=== §36: E1 SESSION STOP-POINT — DRY-RUN NEVER ENGAGED; SITE RESIDENCY + CUDA INTERCEPT MUTUALLY EXCLUSIVE ===
Session stopped at first gate (tag ARM_E1_DONE but STOP-POINT report, not yield). Rig FREE, lock released.
DRYRUN leg: placement VERIFY=OK (D22 bands exact 72/60/156), FINGERPRINT=OK, D1 n=200 @ 27.35, D2 clean,
binary-identity baselined, provenance stamped. KL3 tracked-script check passed.
FINDING 1 (BLOCKING): dry-run NEVER ENGAGED — 0 [HYDRA e1] lines. Evidence: layer-31 expert weights
CPU-resident (156 host overrides, log-confirmed) => ggml assigns layer-31 mul_mat_id to the CPU backend =>
ggml_cuda_mul_mat_id + E1 intercept never see them. Deeper: attach_device D2D-copies from src0->data
(presumes device-resident weights). STRUCTURAL: host-resident site (required for mechanism direction) +
CUDA-side intercept (where the code lives) are MUTUALLY EXCLUSIVE under ggml's scheduler. THREE-AXIS
TRAP: h x residency x backend-assignment — §34a fixed residency and thereby made the intercept
unreachable (residency DETERMINES backend-assignment; the two fixes were individually validated, jointly
incompatible). Builder asks ruling: (a) CPU-side intercept, (b) mechanism redesign, (c) GPU-site reversal
(rejected already — wrong direction). Nothing downstream runnable: type-(ii)/A6/stock/type-(i) all
presume engagement.
FINDING 2: KL FAIL 0.071563 vs 0.01 — VACUOUS as run [AMENDED §41: "VACUOUS" was half wrong — could
not gate the mechanism, yes; but 0.0716 = 76-304x the measured floor, a REAL signal of unknown origin,
remains open; see §41] (E1 engaged in neither step; stock-vs-stock; a PASS
would have gated nothing). KLD ~0.07 matches prior cross-config scale (0.0798) => readings: (a) instrument
floor miscalibrated (builder's 0.01 threshold), or (b) pin-arming perturbs numerics (moot until engagement
works). [§41: both readings OVERTURNED — gate 0.01 is well-placed (10.6x above observed max floor);
0.0716 signal origin = OPEN, gated on KL3 config diff, §41 branches (a)/(b)/(c)] Builder recommends disarmed-vs-disarmed isolation (same base, env stripped) to calibrate floor
before re-thresholding; did not burn rig unilaterally.
FINDING 3 (condition-2 answer): SYNC-JOIN = cudaStreamSynchronize (both SYNC-STAGE and SYNC-JOIN) + D2H
readbacks => FULL path can NEVER execute under capture (capture-check aborts first). Type-(ii) timing-OFF
exercises only no-op hook/dry-run path — capture-safe => graphs-reused should HOLD not VOID. Builder
recommends type-(ii) as ARMED+DRYRUN (capture-neutrality of match path); engaged-path capture infeasible
(graph-side consult beyond spike scope, disqualified at design). DO NOT run type-(ii) with TIMING (aborts
fail-closed mid-leg).
QUANT CORRECTION #2: runtime loader authoritative — L31 up = IQ2_XS (231 MiB), gate = IQ2_XXS, down =
IQ4_NL; part-3's GGUF type-code map was shifted ("IQ2_XXS" wrong). Type-agnostic code = moot functionally;
one-word comment fix queued. E2 must-have (compaction scales with row bytes) STANDS.
CONDITION 1 wired (void_check_stock); not triggered (dry-run expects no-stock); will gate stock leg.
KL commit 87e897d34; .so sha256 identical (script-only).
PENDING SUMMARY REBUILD #2: content generated (T1160 deep tick) but superseded by this stop-point before
commit — held for fold-in at next rebuild (grep-truthful bank: do not commit a stale running-state).
LEADER ROUTE: three rulings to architect — (1) Finding-1 design (CPU-side intercept / redesign /
rejected-reversal), (2) KL floor isolation + re-threshold, (3) type-(ii) ARMED+DRYRUN disposition. Rig
FREE; nothing runs until rulings land. Bank = §36.
=== END §36 ===

=== §37: ARCHITECT RULINGS — (b) HOT/COLD SLAB #132 IS THE PATH; 5.7x DEAD; RE-DERIVATION DECIDES THE LINE; KL ISOLATION AUTHORIZED ===
ARCHITECT OWN MISS ACKNOWLEDGED: 3 review rounds never asked whether the op reaches the hook's backend
(despite writing "experts are ONE 3D tensor per layer; one node runs on exactly ONE backend" in the same
review). NEW STANDING RULE: REACHABILITY PRECHECK PRECEDES INSTRUMENT DESIGN — prove the hook site
executes in the target configuration (one counter, throwaway build) before any harness is built.
FINDING 1 RULING: (a) CPU-side intercept REJECTED (same one-node-one-backend wall; launch+sync per
invocation, sync alone 10-50us vs 34.5 budget — disqualified at design, unchanged). (c) GPU-site reversal
REJECTED (wrong direction). (b) RIGHT, specific shape = HOT/COLD EXPERT SLAB (issue #132): split
blk.N.ffn_*_exps at LOAD TIME into hot slab (pinned subset -> GPU buffer) + cold slab (remainder -> CPU
buffer) = two tensors = two mul_mat_id nodes, remapped ids + combine; scheduler assigns hot->CUDA /
cold->CPU FOR FREE. Residency is per-tensor; no intercept, no per-token transfers, no syncs, graph capture
preserved natively. Graph/loader work in llama.cpp — why the CUDA-side spike could never reach it.
BUT (b) GATED behind zero-rig PAPER TEST + owner visibility: banked "pinning 5.7x more VRAM-efficient"
rested on the RETRACTED uniform constant. Re-estimate with measured bytes: total expert bytes ~43,462 MiB;
slab at h~0.42 x 48 layers ~18,254 MiB; D22 placement 20,400 MiB @ 27.7349; slab expected ~0.377/CUDA0-layer
x 20.2 layer-equivalents ~28.6 tok/s => ~11% less VRAM for ~3% more throughput = ~1.15x NOT 5.7x.
Mechanistically consistent (54% experts/layer touched per turn; pools saturate N=54; h@38~0.39 — skew not
sharp enough). ORDERED: FORMAL RE-DERIVATION of coverage-per-MiB (pinning vs layer placement) with
measured per-layer/per-expert bytes + measured h. REGISTERED BRANCHES (before the number): >=3x => build
#132 (ranking line justified); 1.5-3x => MARGINAL, surface to owner as cost decision not engineering
default; <1.5x => EXPERT RANKING DOMINATED BY PLAIN -OT LAYER PLACEMENT — RETIRE THE LINE. Architect
willing to reach branch 3 (estimate lands there; registered not weighted after five wrong predictions).
OWNER MUST SEE: epic->main gate territory; "retire the line" is the OWNER's call, not the architect's.
FINDING 2: KL isolation leg AUTHORIZED NOW (rig idle): disarmed-vs-disarmed, same base, env stripped.
REFRAME: not threshold recalibration — DOES THE CORRECTNESS INSTRUMENT HAVE ANY RESOLUTION? Teacher-forced
KLD build-vs-itself should be ~0; 0.07 = script not teacher-forcing OR genuine numeric nondeterminism
(MoE routing ties, atomics). Reusable for #132; quantifies run-to-run nondeterminism — bears on
unattributed D0 +48%.
FINDING 3: DO NOT RUN type-(ii). SYNC-JOIN = cudaStreamSynchronize => engaged path graph-incapable BY
CONSTRUCTION = SECOND independent terminal defect of the CUDA-side approach. ARMED+DRYRUN DECLINED
(proves a property of an intercept that (b) deletes). Corroboration: #132 has no syncs/intercept =>
capture native — both terminal defects vanish in the same redesign = aimed correctly.
L31 quant correction noted; false-GO caveat + E2 must-have stand (type-agnostic code).
SEQUENCE: paper re-derivation first (zero rig) + KL isolation in parallel (rig free) + NO further E1
legs. E1 harness (pools, ring channels, counters, gates) REUSABLE, not sunk. ARM 008 STOP untriggered.
Re-derivation -> OWNER with architect recommendation. Bank = §37.
=== END §37 ===

=== §37a: KL-PATH RULING (manual disarmed pairs) + §37b RE-DERIVATION LANDED — BUILD CASE REFUTED, OWNER DECISION TERRITORY ===
§37a KL-PATH: rig worker stopped per stop condition — committed kl-gate.sh @ 87e897d34 CANNOT do
disarmed-vs-disarmed (pin-file required arg; step 2 unconditionally arms; no servers/ports in flow —
llama-perplexity direct dump/compare). PRIOR-READING CORRECTION BANKED: 0.0716 FAIL was
ARMED(E0-pins+E1-dryrun)-vs-DISARMED (kl-cmp.log shows pins loaded 384) — NO prior build-vs-itself
number exists; isolation question genuinely unanswered. Ground facts: E1 engages iff hydra_e0_armed AND
HYDRA_E1_DRYRUN/TIMING (hydra-e1.h hydra_e1_wanted()); corpus in-repo scripts/eval/wikitext-2-raw/
wiki.test.raw; binary 4c2b9cb67 --check exit 0. LEADER RULING: MANUAL DISARMED PAIRS (question was
authorized, not a script invocation; measurement not gate => condition-3 auditability doesn't bind;
script --disarmed-compare amendment rides E1 PR). Permission-cross + re-send GO (second crossing this
session; worker stood down correctly rather than improvise). KL isolation 3 pairs RUNNING on rig
(ARM_KLISOL_DONE tag; distribution min/median/max + outlier policy).
§37b RE-DERIVATION LANDED (collector, zero rig): doc docs/analysis/coverage-per-mib-derivation.md
(uncommitted; full method + identity checks + sensitivities + assumptions ledger). NUMBERS
(coverage-per-MiB pinning vs D22): slab h>=0.42 1.535/1.467 (coding/general); D22-budget fill 1.506/1.423;
top-42-experts 1.938/1.814; unweighted 1.03-1.05; tok/s-per-MiB ~1.02. BRANCH READ: NOTHING reaches >=3x
under any variant => BUILD CASE REFUTED. h-weighted straddles 1.5 by subject (coding marginal-edge,
general mostly retire); unweighted ~1.03 = retire; retired-vs-marginal hinges on subject/weighting/reading
— all reported. 5.7x refuted under EVERY variant. Reconciliations: D22 = L0-21 expert bytes exactly
(20,400); CUDA0=L0-9/CUDA1=L10-21 exactly; architect ~1.15x = tok/s-per-MiB (~1.02) — convergent verdict
zone. Collector flags: unexplained exact coding==general Sum-h-equality over L0-21 (recorded as found);
phase-0 N uniform-regime-transfer assumption; adaptive-alloc correctly not used for slab membership.
ROUTE: architect validates + attaches recommendation -> OWNER package (epic->main gate territory; line
retirement = OWNER call). Bank = §37a/§37b.
=== END §37a/§37b ===

=== §38: ARCHITECT HOLDS OWNER PACKAGE — VERDICT-FLIPPING UNIT ERROR (6 tensor-names CONFLATED WITH 512 EXPERTS); CORRECTED FORM Ratio = 1.024 x (h/f) ===
THE DEFECT (verified against runtime load banner): arch=qwen4exp n_layer=48 n_expert=512 n_expert_used=10
— LIVE MODEL HAS 512 EXPERTS/LAYER, NOT 6. The 6 = count of expert TENSOR NAMES per layer (the same 6
producing 48x6=288 override count read off every leg). Expert-tensor count conflated with expert count.
Consequence: slab byte cost overstated ~by the skew factor (top-42/layer slab = 42/512 = 8.2% ~ 3.6 GB,
NOT ~43% ~ 18.9 GB). Doc CONTAINED the correct figure ("chance line 42/512 = 8.2%") and did not use it.
L1 regime-transfer caveat VOIDS in the FAVOURABLE direction: phase-0 AND live model both 512-expert
regimes — no cross-model transfer needed.
ARCHITECT OWN 1.15x HAD THE SAME ERROR: set f = h = 0.42 (NO-SKEW assumption — the assumption the ranking
thesis exists to contradict). Convergence = shared defect, not independence — "worthless as corroboration".
§37's "5.7x refuted" PREMATURE.
CORRECTED FORM: Ratio = (h/f) x 1.024 — everything cancels except skew ratio h/f (to 2.4%). Branches
become ONE measured quantity: build(>=3x) <=> h/f >= 2.93; retire(<1.5x) <=> h/f < 1.46. Banked h@38~0.39
at f=38/512=0.0742 => h/f = 5.26 => Ratio ~5.4x = BUILD territory, RE-VINDICATES original 5.7x. Slab
~3.2 GB fits 5060 Ti (vs 18 GB — far better proposition). ARCHITECT NOT RULING BUILD: "produced a fitting
story in each direction inside one session; the second deserves MORE suspicion than the first."
ORDERED (collector): (1) re-derive with f,h separate; n_expert=512 FROM RUNTIME BANNER (not doc/memory);
result as 1.024 x (h/f); byte tables no longer load-bearing. (2) operating point EXPLICIT — h@38=0.39 AND
h@54 (saturation) as the two candidate points. (3) re-register branches BEFORE the number, same
thresholds as h/f. (4) KEEP the unweighted insight (fully resident layer serves 100% of slots regardless
of h_l — collector's best observation, survives correction; why corrected form has no h_l weighting).
(5) RESOLVE the Sum-h exact-equality anomaly (exact cross-corpus equality = bug signature — explain, not
outlive). NOTHING TO OWNER until this lands — "a retire recommendation on a defective derivation would
have ended the owner's stated goal on an arithmetic error — the single most expensive mistake available
in this campaign, two steps from happening." COLLECTOR CREDITED: reported the crossing assumption,
flagged L1 binding, published the 42/512 line that exposed the defect — the method was honest enough to
catch its own error. Bank = §38.
=== END §38 ===

=== §38a: CORRECTED RE-DERIVATION LANDED — BOTH OPERATING POINTS BUILD WITH MARGIN; AWAITING ARCHITECT VALIDATION ===
Unit identity FROM RUNTIME: ~100 banners agree (n_expert=512, n_layer=48, n_expert_used=10; two =8 lines
are the k=8 ablation); route ids in 500s corroborate; v1 6/layer = override tensor names. CORRECTED FORM:
Ratio = (h-bar/f) x (B-bar_place/B-bar_pin), byte residual 927.2727/905.4583 = 1.024092 (bounded
[0.876,1.045]). Gates: build <=> h-bar/f >= 2.9294; retire <=> < 1.4647. OPERATING POINTS: N=38
(h-bar=0.39 banked; log-interp reproduces 0.3899/0.4011) => 5.2547 => Ratio 5.3813 => BUILD. N=54
(h-bar=0.4471 interp; floor 0.4062 = zero-marginal-gain) => 4.3415 => BUILD (floor 3.9442 BUILD; general
4.5632 BUILD). ROBUSTNESS: adversarial byte bound 4.60/3.37 BUILD; harsher multi-turn regime 0.3126 =>
4.31 BUILD; nothing near the gate from below. UNWEIGHTED COEXISTENCE: v1 1.03 = whole-layers-vs-whole-
layers, MOOT (slab pins 8%-mass subsets); unweighted physics survives as placement half (Cov=22) paired
with measured pin recall via h/f; v1 error = wholes on both sides. SUM-H ANOMALY RESOLVED: ranker clean
(independent accumulators); bit-for-bit recompute from raw traces incl. 8.499/8.499 — ranker exonerated;
~0.5%-draw coincidence isolated to 1 of 6 sum-cells; moot for v2 (global h-bar). Closed with recompute
proof. Collector extra: the 42/512=8.2% line sat in the phase-0 REPORT all along; banner check = one grep.
Doc revised in place: v1 verbatim under SUPERSEDED header, v2 operative, uncommitted. ROUTE: architect
validates like a prosecutor (per own §38 rule — second fitting story earns MORE suspicion) -> attaches
recommendation -> leader assembles owner package (§22 thesis -> budget -> E1 premise findings -> #132
slab -> corrected coverage-per-MiB -> recommendation -> decision stated as owner's). KL isolation still
running on rig (independent thread). Bank = §38a.
=== END §38a ===

=== §39: ARCHITECT VALIDATES v2 — GATE FIRES BUILD, PROSECUTORIAL FINDING: WRONG CURRENCY; OWNER PACKAGE BASIS SET ===
ALGEBRA INDEPENDENTLY RECOMPUTED AND CHECKED (residual 1.02409; gates 2.9294/1.4647; N=38 5.3813; N=54
floor 3.9443). Unit identity independently confirmed pre-landing. Sum-h closure accepted (proof standard).
NIT 1: N=54 line mislabels Ratio as h-bar/f (4.3415 is Ratio; h-bar/f = 4.2392) — fix before package.
NIT 2: state break-even explicitly — at N=38 build gate needs h-bar >= 0.2174; EVERY measured h clears it
INCLUDING dead h_prefill = 0.238 — strongest robustness line, better than adversarial byte bound.
PROSECUTORIAL FINDING (changes what BUILD means): h-bar SATURATES at N~54 (banked; v2 floor ~zero marginal
gain) => slab coverage CEILING ~0.45, no VRAM raises it. At matched VRAM: slab ~4.6 GB @ cov ~0.447 vs D22
20.4 GB @ 0.4583 — THEY TIE, placement fractionally ahead + headroom while slab capped. The 5.4x is
realizable as FREED VRAM, not additional coverage. The derivation is a COVERAGE argument; C3 (hits ->
throughput) REMAINS UNTESTED and is not advanced by it. The registered gate was chosen when VRAM efficiency
was believed to convert to throughput — it does not at this operating point.
RECOMMENDATION: HONOR THE GATE — BUILD #132 (fires 4.3-5.4x vs >=2.93, robust to every sensitivity; will
not retrofit a gate because the answer arrived in an inconvenient currency — discipline applies when the
number favours building too). BUT success criterion RE-SCOPED BEFORE CODE: #132 payoff = VRAM EFFICIENCY
AT MATCHED COVERAGE (~4.6 GB vs 20.4 GB), NOT decode throughput. Specifically: (1) throughput goal better
served NOW by the already-measured -ot placement win — 27.73 tok/s = 1.90x default split, replicated 4x,
STILL UNLANDED (the deliverable the owner has waited on since before the sweep); (2) #132's one genuine
throughput angle is second-order: ~4.6 GB slab fits 5060 Ti ALONE -> frees 3060 entirely -> recovers ~3%
CUDA1 fixed toll + removes PCIe x4 constraint, simplifies architecture; (3) remaining ranking-as-throughput
door: session-adaptive pools (warm+allpool POC) could exceed static h ceiling — untested, not proposed now.
OWNER PACKAGE BASIS: build #132 for VRAM + land the -ot config win for throughput + RECORD THAT STATIC
EXPERT RANKING CANNOT EXCEED ~45% COVERAGE ON THIS MODEL AT ANY VRAM BUDGET — "that last sentence is the
campaign's real scientific result." Leader assembles package. Bank = §39.
=== END §39 ===

=== §39a: DERIVATION DOC COMMITTED (b69c55f8f) — DECISION BASIS FINAL ===
Both §39 nits applied and committed: b69c55f8f `docs: coverage-per-MiB derivation v2 (architect-validated)
— BUILD branch with re-scoped currency finding` (+299, doc-only, pre-existing M src/llama-cpp untouched).
Nit 1 fixed with rounding-slip correction (h-bar/f = 4.2392, Ratio = 4.3413 exact); Nit 2 break-even line
LEADS the robustness section (gate needs only h-bar >= 0.2174 at N=38; cleared by every measured h incl.
§20d-dead h_prefill = 0.238 => build case independent of decode-window quality). Doc = decision basis the
owner reads. Owner package DELIVERED (turn 1167): decisions (a) land 1.90x -ot config, (b) build #132 for
VRAM, (c) pending PR #134 / §21a / #153 continuation leg. KL isolation still RUNNING on rig. Bank = §39a.
=== END §39a ===

=== §40: KL ISOLATION YIELD (ARM_KLISOL_DONE) — INSTRUMENT CALIBRATED ===
4 disarmed-vs-disarmed pairs, build-e0 @ 4c2b9cb67 vs itself (stamp clean, provenance all 8 runs,
sha256 e9a0b4c1ffbd...; disarm proof 0 [HYDRA e1] lines all 8). Teacher-forced, identical flags,
head-600 corpus (88 chunks, sha 3cae5a0f02d4e1f8), ctx 512. KLD distribution: P1 0.000235±0.000044,
P2 0.000939±0.000119, P3 0.000467±0.000059, P4 0.000249±0.000024 (P4 = outlier policy: P2 sat 2.03x
the P1-P3 spread above nearest; P4 replicated P1, NOT P2 => tail event, not bimodal mode; nothing
discarded). median 0.000358, absolute spread 0.000704. ARMED prior 0.071563 = 76x (vs P2) to 304x
(vs P1) ABOVE this floor => the 0.0716 was ARMING EFFECT, not run-to-run nondeterminism. All pairs
>=10x below the 0.01 gate threshold => gate was ~1 order too coarse; instrument floor now measured.
Covariates: quiescence clean (baseline-only pre-run; no HOLD fires; zero run fails); during-run
non-rig spikes incl. P4-cmp 100% indexer yet P4 read LOWEST => load not assigning KLD. P2's only
distinctive covariate = background gh activity (unassigned cause, reported raw). [HYDRA e0] stats
lines MISSING all 8 (HYDRA_E0_STATS unset in env; KL3 dump inherited it) — stats-only counters,
numerics unaffected, covariate asymmetry vs KL3 disclosed. Harness bugs fixed in audit trail:
quiescence hold self-match (ps|grep) => pgrep -f; libcudart.so.13 path => LD_LIBRARY_PATH fix;
orphan full-corpus run killed pre-approval. Logs /tmp/opencode/armKLisol*.session.log,
klisol-P{1..4}.{dump,cmp}.log + samplers; runner armKLisol.sh. Rig FREE, lock released, no strays.
INTERPRETATION = ARCHITECT'S (worker raw, zero gloss). Forwarding for: KL leg closure ruling;
bearing on D0 (expected: none — different instrument, contention already excluded by prefill
paradox; mechanism still unattributed). Bank = §40.
=== END §40 ===

=== §41: ARCHITECT KL-ISOLATION RULING — LEG CLOSED, GATE KEPT, §36 AMENDED, KL3 CONFIG DIFF REQUIRED ===
Architect rulings on §40 yield (interpretation = architect's, per protocol):
Q1 KL LEG CLOSED on its authorized question (does the correctness instrument have resolution? YES,
~2 orders of magnitude). GATE 0.01 KEPT — "miscalibrated ~1 order" framing from §40 OVERTURNED:
0.01 sits 10.6x ABOVE the observed MAX floor (9.39e-4, correct practice at n=4 vs median) and 7.2x
below armed 0.0716 = well-placed with headroom AND resolution. Bonuses banked: dump PPL spread 8
runs = 0.031% (3.9108-3.9120); wall-time spread 4.7% (359-376s) = independent quiet-box run-to-run
timing variance, CORROBORATES the ±2.4% replication band used all session.
§36 AMENDMENT (header-retraction-stamp rule, same commit): KL FAIL 0.0716 "VACUOUS as run" was half
wrong — could not gate the mechanism, but 0.0716 = 76-304x floor = REAL SIGNAL of unknown origin,
remains open. New standing rule: a datum 2 orders above floor never gets labeled vacuous — label
the gate outcome, keep the datum.
Q2 ARMING-EFFECT ATTRIBUTION BLOCKED: KL3 (0.0716) vs isolation (4e-4) comparison rests on configs
being identical — KNOWN DIFFERENCE: HYDRA_E0_STATS inherited by KL3's dump, unset in all 8 isolation
runs ("stats-only, numerics unaffected" = ASSUMPTION not measurement; worse if KL3's cmp lacked it,
KL3's own two steps differed = the exact flaw that manufactures spurious KLD). REQUIRED before any
post-mortem entry: full config diff KL3 dump vs KL3 cmp vs isolation leg (corpus, sha, chunks, ctx,
every env var, flags). REGISTERED BRANCHES: (a) fully identical => arming perturbs numerics without
engaging = real correctness defect, enters post-mortem, implicates attach path (attach_device D2D
copy src0->data + weight backup run before any intercept); (b) configs differ => comparison VOID,
re-run one armed-vs-disarmed KL on isolation leg's exact corpus; (c) identical except stats
asymmetry => attribute there, prove by re-running KL3 pair with variable matched. Architect
prediction (b) or (c), registered 0-for-5 on session predictions (pattern named: accepting a
mechanism that fits before checking the config that would refute it).
Q3 D0 LEDGER TIGHTENS: numeric reproducibility (~4e-4 KLD, 0.03% PPL) EXCLUDES numeric drift as D0
mechanism — now: not MTP (log), not contention (prefill paradox), not numerics (this leg). 48.2%
swing looks MORE anomalous vs 4.7% wall-time spread. Still unattributed.
Q4 STANDING ITEMS: owner package +1 asset line (calibrated correctness instrument, floor ~4e-4,
validated 0.01 gate = prerequisite for ever shipping #132 — campaign product); #132 gate untouched;
OUTLIER STANDING PATTERN BANKED: P2-tail => P4 replicated P1, nothing discarded, tail not promoted
to mode; P2 gh-activity covariate stays UNASSIGNED (endorsed raw-reporting discipline).
KL3 CONFIG DIFF dispatched to rig worker (has KL3 + isolation logs in session ctx; box free, no GPU).
Bank = §41.
=== END §41 ===

=== §41a: RIG WORKER SESSION TERMINALLY DEAD — SUCCESSOR SPAWNED ===
Rig worker f4419282 hit non-retryable upstream 400 (reasoning encrypted_content not issued to this
caller, muse-spark via opencode zen) on its NEXT turn after ARM_KLISOL_DONE delivery; identical
error on retry => session state unrecoverable (not task-dependent). ARCHIVED. Successor spawned:
89d25210-b9eb-4e8a-b060-0b3c00005a90 (opencode / opencode-go/muse-spark-1.3-contributor, same
workspace, fresh session; roster = 6 again). Init prompt carries standing rules (raw yields, no
builds during rig, quiescence gate, provenance, outlier policy, no-vacuous-label rule) + KL3 CONFIG
DIFF task (ARM_KL3DIFF_DONE; no GPU needed). ROSTER UPDATE: rig = 89d25210. KL3-diff source note:
KL3-era logs may be under /tmp/opencode/e1-legs-*; if missing worker must say so, not infer.
=== END §41a ===

=== §42: KL3 CONFIG DIFF YIELD (ARM_KL3DIFF_DONE) — BRANCH (b) TRIGGERED, MATCHED PAIR DISPATCHED ===
Successor rig worker 89d25210 delivered exact 3-column diff (all sources present, zero inference;
kl-gate script byte-identical to commit 87e897d34; binary sha256 full e9a0b4c1...f0ff matches ×8 + KL3
prefix). DELTAS OF RECORD (no cause assigned): (1) CORPUS DIFFERS — KL3 = moe-lookahead-improvement-
plan.md 1954 lines sha aeafe3e9 79 chunks; isolation = wiki.test.raw head-600 sha 3cae5a0f 88 chunks.
(2) HYDRA_E0_STATS =1 effective BOTH KL3 steps (2 [HYDRA e0] lines each, dump AND cmp) vs never set
all 8 isolation runs — refutes the "KL3's own two steps differed" sub-hypothesis; KL3-vs-isolation
diff stands. (3) PIN+E1_DRYRUN on KL3 cmp only = the AUTHORIZED armed-vs-disarmed asymmetry (KL3 cmp
log: pins loaded 384 pins, engaged=0 armed=1, hits=0). (4) HYDRA_E1_COMPACT stripped in isolation,
not stripped+not set in KL3 (inert?). (5) LD_LIBRARY_PATH string differs 13.2.2 vs 13.2.1 (effective
values unlogged both). (6) [HYDRA e1]=0 all 10 logs. SOURCE SEMANTICS: ggml-cuda.cu:2004-2012 pin-
file load sets hydra_e0_on=true + armed=true; :2110 hydra_e0_active() per mul_mat_id.
BRANCH SELECTION per §41 pre-registration: configs differ (deltas 1+2+4+5) => (b) COMPARISON VOID;
valid number requires one armed-vs-disarmed pair on the isolation leg's exact corpus, env fully
matched except PIN+E1_DRYRUN on armed side. §40's "0.0716 was arming effect" remains UNPROVEN —
comparison was cross-corpus + cross-env. KL3's dump PPL 10.8072 vs isolation 3.9115 confirms the
corpora were different texts entirely (PPL gap = corpus gap, not model behavior).
DISPATCHED to rig worker: matched pair on wiki head-600 (HYDRA_E0_STATS unset BOTH sides; only
PIN+E1_DRYRUN on armed cmp; dump re-run fresh), tag ARM_KLARMEDPAIR_DONE. Ruling on the result =>
architect. Bank = §42.
=== END §42 ===

=== §43: MATCHED ARMED-PAIR YIELD (ARM_KLARMEDPAIR_DONE) — ARMING EFFECT MEASURED AT MATCHED CONFIG ===
Single pair per §42 design, isolation corpus exactly (wiki head-600 sha 3cae5a0f, 88 chunks, PPL
sanity dump 3.9114 = correct corpus), binary 4c2b9cb67 clean + full sha256 verified each run, env
attested identical except HYDRA_PIN_FILE + HYDRA_E1_DRYRUN=1 on armed side (parent env logged none;
LD_LIBRARY_PATH identical both steps). RESULT: ARMED cmp mean KLD 0.032906 ± 0.000579 (median
0.008325, max 3.397699 — heavy-tailed per-chunk), [HYDRA e1]=0, e0 lines 2 engaged=0 armed=1 hits=0,
pins line 384. VS disarmed floor 0.000235-0.000939 (median 0.000358): 35x-140x ABOVE FLOOR at
matched config with ZERO engagement. VS KL3 0.071563: ~half — remainder = corpus difference
(architect's to rule). Arm mode replicates KL3 cmp exactly (pins loaded, dryrun, engaged=0).
Artifacts: armKLarmedpair.{sh,session.log}, klarmedpair.{dump,cmp}.log + sidecars, base bin
RETAINED (11.1 GB). Rig FREE. Single pair per design, no replicate, nothing discarded, no unassigned
causes (both rc=0 first attempt). INTERPRETATION => ARCHITECT (this lands in registered branch (a)
territory: attach-path perturbation with no engagement — architect rules, not leader). Bank = §43.
=== END §43 ===

=== §44: ARCHITECT RULING — ARMING FACT CONFIRMED / MECHANISM OPEN; #132 GATE REDESIGNED; POST-MORTEM ORDERED ===
Q1 Branch (a) FACT CONFIRMED: armed 0.032906 = 92x floor median, armed MEDIAN 0.008325 = 23x floor
median => typical chunk perturbed, not tail. ARMING CHANGES NUMERICS WITHOUT ENGAGING. MECHANISM NOT
confirmed — leader's framing (attach_device D2D + weight backup) pre-attributed; two candidates with
different consequences: (i) WEIGHT MODIFICATION (correctness defect) vs (ii) ALLOCATION/SCHEDULING
ORDER (not a defect but breaks every armed-vs-disarmed A/B premise). DISCRIMINATOR ORDERED (one
leg): ARM WITH EMPTY PIN SET (env set, zero pins). KLD returns to floor => attach-dependent;
stays elevated => allocation path. Ordered as #132 PREREQUISITE, not E1 autopsy: if allocation-
order, ANY added device allocation perturbs numerics incl. #132 => the calibrated gate would fail
#132 spuriously.
Q2 KL3 CLOSED: 2.2x residual = corpus scale, recorded unverified-but-immaterial (E1 terminated, not
worth chasing). Amendment chain complete: §36 vacuous -> §41 real-signal-unknown-origin -> §43
arming-effect-matched-config.
Q3 #132 GATE CANNOT BE "armed ~ disarmed at floor": #132 splits one mul_mat_id into two nodes +
combine => summation order changes BY CONSTRUCTION, not bit-identical to stock, cannot be. THREE-WAY
DECOMPOSITION designed before code: (1) stock vs split-both-on-CPU => split/combine numeric cost
alone = THE GATE; (2) split-both-on-CPU vs split-hot-on-GPU => backend-assignment effect; (3) stock
vs full slab => end-to-end, must reconcile as ~ (1)+(2). Exceeds (1)+(2) => real defect; lands at
(1)+(2) => correct despite not matching stock. Without decomposition #132 would be gated against an
unachievable standard.
Q4 E1 POST-MORTEM: WRITE NOW, do not wait on open mechanism (that is how post-mortems never get
written). BUILDER DRAFTS (holds code detail), ARCHITECT REVIEWS. Sections: 1 what E1 was for
(mechanism-cost gate for C3, never throughput test); 2 what was built + surviving assets; 3 terminal
defect 1 reachability (h x residency x backend-assignment coupling); 4 terminal defect 2 capture
infeasibility (SYNC-JOIN = cudaStreamSynchronize); 5 correctness defect arming-perturbs-numerics at
engaged=0, mechanism OPEN, discriminator named; 6 why review missed them — premise never checked,
architect's own miss, reachability-precheck rule minted; 7 assets retained (event pools, ring
channels, fail-closed gates, calibrated KL instrument, proven...). [Ruling tail truncated in transit;
sections 8+/Q5 to be reconciled at next consult.] Bank = §44.
=== END §44 ===

=== §44a: BUILDER SESSION TERMINALLY DEAD — SUCCESSOR SPAWNED (2nd muse-spark session death) ===
Builder 2ac20e22 hit the SAME non-retryable 400 (reasoning encrypted_content not issued to caller)
on the post-mortem dispatch — identical signature to late rig worker f4419282 (§41a). PATTERN: long-
lived muse-spark/opencode sessions die on reasoning-state invalidation; retry futile (confirmed on
rig worker). ARCHIVED. Successor 7fe20b8a-e06d-457d-8761-ae117e6cea60 spawned (same provider/model,
fresh session; roster = 6: lead/architect/collector/summarizer/rig 89d25210/builder 7fe20b8a). Init
prompt: bank §§30-44 + fork code as context, standing rules (bank-cited claims, commit-only-on-
approval, stamp-after-commit), post-mortem task per §44 spec (draft only, no commit, mechanism
marked OPEN with discriminator in-flight). ROLES UNCHANGED otherwise.
=== END §44a ===

=== §45: EMPTY-PIN DISCRIMINATOR — FAIL-CLOSED BOUNDARY (LEG UNANSWERABLE AS DESIGNED) ===
Rig worker 89d25210: binary REFUSES zero-pin arming — GGML_ABORT at ggml-cuda.cu:2008 (via
hydra-pins.h:119 zero-count gate; parser skips # comments so header-only file parsed to 0 pins
cleanly, hit zero-count gate not parse error). rc=134 SIGABRT, wall 68s, abort during model load,
BEFORE any [HYDRA e0]/[HYDRA e1]/pins lines; no KLD produced. Verbatim: "HYDRA_PIN_FILE set but
zero pins loaded from /tmp/opencode/e0-pins-empty.txt" (file sha a49c28d2, 1 line header-only).
Everything else §43-identical (base bin REUSED + now hashed cb0ebec9 (first hash on record);
binary/corpus/LD/provenance all verified pre-run; quiescence clean). 0-byte-vs-header-only
difference: untested per stop rule; both parse to zero pins, same gate applies (source reading,
not run claim). CONSEQUENCE: the §44 discriminator (attach vs allocation path) is UNANSWERABLE by
empty-arm — condition unreachable at runtime. Fail-closed gate WORKED as designed (arming without
pins = config error). Artifacts: armKLemptypin.{sh,session.log}, klemptypin.cmp.log + sidecars,
empty pin file retained. Rig FREE. NEXT (leader): dispatch read-only SOURCE-FACT pass to worker —
in dryrun+pins arm mode, what executes (attach_device? D2D copies? weight backup? allocations?)
— so architect can redesign the discriminator from code facts, not conjecture. Bank = §45.
=== END §45 ===

=== §46: ARM-PATH SOURCE FACTS (ARM_ARMPATH_FACTS_DONE) — BOTH §44 CANDIDATES CONTRADICTED ===
Rig worker read-only pass (ggml-cuda.cu E0 block :1940-2090 + hook :2093-2130, hydra-e1.h 714 lines,
hydra-pins.h 139 lines; worktree M dirtiness flagged, nothing written). FACTS:
ARM IS LAZY — first mul_mat_id via call_once (cu:2031-33); nothing at process start/model load.
ARM-TIME WORK (hydra_e0_init cu:2002-2029): getenv + host file parse into std::vector + set flags
(:2010-11) + one fprintf + atexit(hydra_e0_dump). ZERO device calls.
attach_device NEVER runs at arm time (sole caller e1.h:541 = full-engage path; dryrun early-return
:519-524 precedes). No weight backups (all copies live inside attach_device :288-330). No device
allocations (all cudaMalloc/Event inside attach_device :261-334 + disarmed-only stock events :436-37;
E0 block has zero malloc/Memcpy/Event — grep-verified). attach_host = site-match only (NEVER fired in
KL legs — layer-31 host-resident, corroborated by logs: lookups=0), host-only, no CUDA.
PER-INVOCATION armed+no-match: call_once flag check + stock_begin immediate return (e1.h:401-03) +
wanted() cached bools + dim reads + match() strstr on node names (:485-97). ZERO data-pointer derefs,
zero counter writes.
Q4 SCHEDULER/ALLOCATOR: arm path issues ZERO CUDA/ggml/scheduler/allocator calls — no
malloc/free/Event/Memcpy/stream/MemGetInfo (fit_check sole-caller = attach_device e1.h:245), no pool
reservation, no backend assignment, no graph interaction. Persistent state = heap vectors + 3 statics
+ 1 atexit. Disarmed-only lazy event pool returns BEFORE it when armed (stock_begin :401-03).
CONSEQUENCE (facts-level): §44 candidate (i) WEIGHT MODIFICATION contradicted — attach_device never
executes in dryrun; §44 candidate (ii) ALLOCATION/SCHEDULING-ORDER contradicted — zero allocations or
scheduler calls at arm. The 0.0329 armed-vs-floor perturbation (§43) has NO remaining code-path
mechanism among the two registered candidates. Architect must rule: reopen mechanism ledger (third
candidate, e.g. host-timing-amplified GPU nondeterminism — note §40 P4 100%-load-lowest-KLD weakly
argues against) / require armed-pair REPLICATE (§43 was single-pair by design; its median 23x floor
argues systemic not tail) / or other. No interpretation banked. Bank = §46.
=== END §46 ===

=== §47: ARCHITECT RULING — PRIOR SHIFTS TO (iv) NOT SYSTEMIC; TWO REPLICATE LEGS ORDERED (IDLE-TIME); §5 REFRAMED ===
1. Candidate (iii) SELF-ELIMINATED by architect source check: ggml CPU mul_mat dynamic chunking
(ggml-cpu.c:1432-40, atomic_fetch_add) partitions the OUTPUT matrix into DISJOINT elements — timing
changes WHO computes a chunk, not HOW anything is summed => cannot change results. Combined with §46:
no known path by which the armed build produces different numbers. PRIOR SHIFTS TO (iv): the §43
result is not systemic. Architect also REFUTED the "median 23x argues systemic" argument: the median
is ACROSS 88 CHUNKS within ONE pair (spread across chunks, not across pairs); median-across-chunks
is not replication; n=1.
2. REPLICATE ORDERED — two legs, §43 protocol, scheduled as CHEAP CURIOSITY-CLOSING, NOT critical
path: (a) ARMED-vs-ARMED pair (better discriminator, no code change: if ~0.03, effect is a run-family
property, not arming => (iv) confirmed strongest form); (b) one more ARMED-vs-DISARMED pair (gives
§43 an n). Empty-pin gate NOT relaxed (do not relax a gate in a dead spike to enable a test).
NOT LOAD-BEARING: #132 immunized against this whole class by the three-way decomposition (each
comparison holds instrumentation constant on both sides) — run when rig otherwise idle, nothing waits.
3. POST-MORTEM §5 REFRAME (exact architect wording): "Unexplained measurement anomaly (open). A
single matched armed-vs-disarmed KL pair read 0.032906 mean (92x the measured floor median, 23x at
the chunk median) with zero engagement. Both registered mechanisms — weight modification via
attach_device, and allocation/scheduler perturbation — are eliminated by source facts (§46); a third,
thread-timing-dependent reduction order, is eliminated by ggml's CPU chunking being disjoint-output.
No known mechanism remains. n=1; replication ordered. Leading candidate is that the result is not
systemic. Not established as a defect in E1 code." NOT a correctness defect — no evidence of
incorrect computation; asserting one would misdirect #132 design.
4. POST-MORTEM: send for review as written (builder applies §5 reframe first, then architect
reviews). [Ruling tail truncated ~296 chars in transit; reconcile at next consult.] Bank = §47.
=== END §47 ===

=== §48: POST-MORTEM REVIEW — REQUEST_CHANGES (4 must-fix, 2 should-add, 2 lines), COMMIT AUTHORIZED ON COMPLETION ===
Architect reviewed docs/analysis/e1-postmortem.md (builder successor draft, uncommitted): structure
holds, line refs check out, §5 substance correct. MUST FIX: (1) L28 verdict bands misquoted —
registered bands are <11.5 GO / 11.5-34.5 CONDITIONAL / >34.5 STOP (§30 A4, re-confirmed §34/§35);
L27 keeps both pairs labelled original(36.3/12.1) vs registered(34.5/11.5), verdict list quotes ONLY
registered; (2) L74-vs-L118 contradiction — attach_device PRESUMES device-resident weights BY DESIGN
(why host-resident site has no viable engaged path) AND never executed in dryrun; (3) L78 corrupted
token exacta× in the central-structural-result sentence; (4) L118 §5 pasted as literal quotation —
set as body text. SHOULD ADD: rules-minted section (reachability-precheck + stamp-after-commit +
no-builds-during-rig + no-attribution-without-refuting-check + floor-before-threshold + exact-
allocation-lines + quiescence-over-CPU%); consolidated #132 implications list (not bit-identical by
construction => three-way decomposition IS the gate; no intercept/no per-token transfer; payoff =
VRAM at matched coverage, ~45% ceiling). WORTH A LINE: L19 h=0.4207-vs-0.39 reconcile/cite both
(b69c55f8f reproducible); cost-accounting sentence (~800 lines, 3 review rounds, rig sessions).
LEAD MAY COMMIT WITHOUT RE-REVIEW once applied. Builder applying all eight now. Bank = §48.
=== END §48 ===

=== §49: POST-MORTEM COMMITTED (architect pre-authorized) ===
Builder applied all 8 review items; lead spot-verified (bands: 36.3/12.1 labelled ORIGINAL + 34.5/11.5
REGISTERED quoted in verdict list; exacta× gone; §5 body text; attach_device presumption + never-
executed; §§8 rules-minted + 9 #132-implications present; h-reconcile + cost accounting) and
COMMITTED per §48 authorization without re-review. Commit sha recorded in git log (docs: E1
post-mortem (architect-reviewed)). Replicate legs still in flight (ARM_KLREPL_DONE). E1 line now has
its closed record: design closed, code halted, post-mortem committed, remaining open item = the §5
anomaly (n=1) + owner decisions (a)(b)(c). Bank = §49.
=== END §49 ===

=== §50: REPLICATE YIELD (ARM_KLREPL_DONE) — (iv) REFUTED; ANOMALY IS DETERMINISTIC ===
Two legs, §47 order, rig 89d25210, all rc=0 first attempt, provenance/quiescence clean:
LEG 1 ARMED-vs-ARMED (armed dump + armed cmp vs armed base): mean KLD 0.000131 ± 0.000027, median
0.000000, max 0.402 (heavy tail, mean tiny). PPL 3.9248 (corpus gate pass). Armed runs agree with
armed runs AT FLOOR.
LEG 2 ARMED-vs-DISARMED (armed cmp vs reused §43 base, sha cb0ebec9 verified pre-run): mean KLD
0.032902 ± 0.000579 — matches §43's 0.032906 to 0.000004 (5 significant figures). Perfectly
reproducible.
PATTERN: disarmed~disarmed (4e-4, §40); armed~armed (1.3e-4); armed-vs-disarmed = 0.0329 BOTH TIMES
deterministically. => §47 (iv) "not systemic" REFUTED — the anomaly is a DETERMINISTIC, REPRODUCIBLE
numeric difference between armed and disarmed computation families, with every registered code-path
mechanism still eliminated (§46). Additional datum: armed dump PPL 3.9248 vs disarmed 3.9114 — the
families differ in PPL too. Same base-bin byte size both families (11,144,961,364 B; stated not
interpreted). New base klrepl-AA.kld-base.bin retained; §43 base untouched. Rig FREE. One candidate
NOT yet examined (leader note, not banked interpretation): disarmed-only lazy event pool (e1.h:434-
440 cudaEventCreate) runs in DISARMED stock_begin but returns immediately when armed — the families
DO differ in device-side bookkeeping, direction opposite to naive expectation. INTERPRETATION =>
ARCHITECT. Bank = §50.
=== END §50 ===

=== §51: ARCHITECT RULING — CONFIRMED DEFECT (INSTRUMENT STATE ALTERS COMPUTATION); PRIORITIES RESHUFFLED ===
§50 pattern ruled: deterministic, systematic, NOT artifact. (iv) refuted => REQUALIFIED as confirmed
defect: same commit 4c2b9cb67, env-only difference, KLD 0.032902/0.032906 (5 s.f.) vs within-family
floors 4e-4 (disarmed) / 1.3e-4 (armed); PPL shift 0.0134 = 10x disarmed family spread.
1. CANDIDATE REFRAMED: event objects cannot change arithmetic; what can: (i) different kernel
selected (mul_mat_id dispatches MMVQ->MMQ->MMF->fallback, different accumulation precision), (ii)
different backend per node, (iii) different ubatch splitting; (iv) nondeterministic atomics EXCLUDED
(both families self-consistent at floor). Live candidate = DISPATCH-PATH or BACKEND-ASSIGNMENT
divergence between families. Event pool relevant as CAUSE of dispatch divergence (events exist =>
needs_sync differs => capture/dispatch differs), not as arithmetic effect.
DISCRIMINATOR (no code change): GGML_SCHED_DEBUG=2 (ggml-backend.cpp:1858 => print_assignments :997)
both families, DIFF the dumps. Differ => mechanism localized in the diff. Identical => inside a
kernel => next step per-layer KLD bisection.
SECOND LEG: PRISTINE vs disarmed vs armed — which family deviates from pristine is UNKNOWN. NOTE:
pristine-build.log exists but no build-pristine* tree found in fork — binary existence to confirm;
if missing, builder builds pristine FIRST (no rig session active => no-build rule respected), then
rig runs three-family legs.
2. §5 SECOND REWRITE (post-mortem shipped wording superseded): "Confirmed defect: instrument state
alters computation (mechanism open). Two builds of commit 4c2b9cb67, differing only in env state,
compute measurably different logits: armed-vs-disarmed KLD 0.032902 / 0.032906 (reproducible to
5 s.f.), against within-family floors of 4e-4 (disarmed) and 1.3e-4 (armed), with a PPL shift of
0.0134 = 10x the disarmed family's own spread. Both registered mechanisms are eliminated by source
facts (§46), as is thread-timing reduction order. The live candidate is kernel-dispatch or
backend-assignment divergence between the families; discriminator ordered. Which family deviates
from pristine is not yet established." [SUPERSEDED in §51] stamp on old §5. §7 strengthened: the KL
instrument caught a real defect NO other gate in this campaign would have seen = strongest
retention argument.
3. §44 AMENDED BY ARCHITECT (self-withdrawal): #132 "immunized by constant instrumentation" TOO
STRONG, WITHDRAWN — instrument STATE shifts results 80x gate floor; #132 comparisons change runtime
state (buffer assignment = state-like). Decomposition STAYS, gains a CONTROL: every comparison point
carries its OWN same-state floor leg (run that exact config twice, confirm floor) — gate threshold
per its own floor, not the global 4e-4. Converts premise from assumption to measurement (+1 leg per
comparison point).
4. PRIORITY: LOAD-BEARING, PROMOTED. "If a no-op env flag moves logits by 0.033 KLD and PPL by 0.34%,
no A/B in this codebase is trustworthy until we know why." BLOCKS issue B (correctness gate) + D
acceptance criteria. Does NOT block C (load-time split) or F (pin-set generation) — parallel. Land
the config win first regardless (owner decision (a), still pending). Bank = §51.
=== END §51 ===

=== §52: PRISTINE BINARY DELIVERED + POST-MORTEM AMENDED/COMMITTED ===
Builder 7fe20b8a: pristine llama-perplexity BUILT (§29 shelf artifact tree /tmp/opencode/pristine-src,
HEAD 86af0c9af clean detached, NO E0/E1 hooks) — sha256 302e5a64f79cfd7cf40dda4da8d2993293d101e56bbc
5d3476d7cbd8e6f389e9. Post-mortem amended: old §5 stamped [SUPERSEDED in §51], operative §5 =
confirmed-defect wording verbatim, §7 retention sentence added; lead verified structure and COMMITTED
bea932f69 @ 14:35:30+07. NEXT: rig sched-diff discriminator in flight (ARM_SCHEDDIFF_DONE); pristine
three-family leg (pristine vs disarmed vs armed) dispatches after rig frees — establishes WHICH family
deviates from pristine. Bank = §52.
=== END §52 ===

=== §53: RIG SESSION #2 DEAD (503 overload @ 234K ctx) — SUCCESSOR #3 + SCHED-DIFF ROOT CAUSE ===
Rig worker 89d25210 first errored 503 service_overloaded (RETRYABLE, unlike the fatal 400s) mid-turn
after context spiked to 234K reading the sched-diff dumps; session poisoned (new sends bounce the
same error). ARCHIVED per lesson (4th muse-spark session mortality this session: f4419282, 2ac20e22,
9b1b3bfa, 89d25210). Successor 8f3d94a4 spawned (fresh session) with TWO sequenced tasks:
(1) SCHED-DIFF RE-RUN: predecessor's runs COMPLETED (logs on disk, PPL 3.9114) but contained NO
scheduler output. LEAD ROOT-CAUSED from source: print_assignments emits via GGML_LOG_DEBUG
(ggml-backend.cpp:1000-1030), suppressed at default verbosity — fix = add --verbose flag to both
runs (disclosed covariate, no compute effect). Yield = assignment dumps + diff.
(2) PRISTINE THREE-FAMILY LEG queued: pristine binary confirmed present (§52), which-family-deviates
question, three dump runs + two compares (pristine vs disarmed base, pristine vs armed base).
ALSO learned: muse-spark 503 = transient overload, retryable, BUT sessions with >200K ctx are
prone; keep worker turns lean (files on disk, never paste dumps). Bank = §53.
=== END §53 ===

=== §53a: OPCODE/PROVIDER OUTAGE — RIG FAILOVER TO OMP HARNESS ===
The opencode provider went DOWN entirely ("Timed out refreshing OpenCode after 120000ms") — root
cause of both 503s (service_overloaded on old session, backend_unavailable on fresh spawn). muse-
spark/zen unreachable. FAILOVER: rig tasks moved to OMP harness (available) — rig worker s4 =
0f69b49a-7dcc-4037-a0cb-2b51b44075df on omp/opencode-go/glm-5.3-flash (full-access mode), carrying
both sequenced tasks: (1) sched-diff re-run with --verbose fix (ARM_SCHEDDIFF_DONE), (2) pristine
three-family leg (ARM_PRISTINE_DONE; pristine binary ready from §52). NOTE for fleet design: the
no-opencode fallback path works because every init prompt is bank-self-sufficient. Builder
(7fe20b8a, opencode/muse) is IDLE and unaffected while idle — will fail over if needed. Bank = §53a.
=== END §53a ===

=== §54: SCHED-DIFF BLOCKED — NEW DEFECT MANIFESTATION: ARMED ENV HANGS THE PROCESS ===
Lead ran the sched-diff discriminator directly (3 harness deaths made workers impractical; omp
worker died mid-task: OMP RPC exit 1). RESULT: DISCRIMINATOR BLOCKED by a NEW manifestation of the
§51 confirmed defect. ISOLATION MATRIX (deterministic, multiple attempts):
- DISARMED + --verbose + GGML_SCHED_DEBUG=2 => WORKS: 670,051-line dump (sched assignments present:
  "## SPLIT #1: CUDA0 # 13 inputs", per-node lines), base bin 4.9GB, PPL 3.9114. (omp worker, 597s)
- ARMED (pins+dryrun) + --verbose + GGML_SCHED_DEBUG=2 => HANG: 3 attempts (bg job 540s rc=0;
  bg job killed at 120s+; foreground 35s rc=124). /proc/io evidence: banner ONLY (wchar 211 bytes =
  common_init lines) then ZERO writes for minutes while process spins (57% CPU, D/l state, 31GB RSS
  loaded); base bin NEVER written (0 bytes); exit code inconsistent (0 or timeout-kill).
- ARMED + --verbose + NO scheddebug => HANG identically (rc=124, 0 bytes). => interaction is
  ARMED x --verbose (scheddebug not required).
- ARMED + non-verbose (no scheddebug) => WORKS (§43/§50: full logs, KLDs, base bins).
- DISARMED + non-verbose => WORKS (§40, all floor legs).
MEANING: the armed env deterministically HANGS the process when verbose logging is enabled — hang
occurs AFTER banner (~0.4s, model load begins) with spin-wait (busy CPU, no I/O, D state). The
instrument state does not merely shift numerics — under verbose logging it deadlocks execution.
SCHED-DIFF DISCRIMINATOR DEADLOCKED: per-node assignment capture requires GGML_LOG_DEBUG (verbose)
which the armed state cannot survive. Note: DIS dump alone still yields the DISARMED half of the
comparison (670K lines preserved: klscheddiff-v-DIS.dump.log). Evidence files preserved
(klscheddiff-v-ARM{,2}.* 0-byte, armverb-notest.kld.log 0-byte). Rig FREE (GPUs 1MiB, no procs;
one benign bash wrapper remains). ROUTE => ARCHITECT: (1) why does armed x verbose deadlock —
candidates: log I/O timing shifts the lazy pins-load/first-mul_mat_id race; atexit handler
interaction; stream/sync state under slower graph build; (2) alternative discriminator that does
not need armed+verbose (e.g. per-layer KLD bisection using non-verbose armed runs which WORK);
(3) is the hang itself the strongest datum yet for the defect ledger? Bank = §54.
=== END §54 ===

=== §55: ARCHITECT RULING — UNIFYING HYPOTHESIS (call_once DEADLOCK); SCHED-DIFF DROPPED; SEQUENCE ORDERED ===
1. PRIMARY HYPOTHESIS (registered so it can fail): arm is LAZY (§46) — first mul_mat_id fires
hydra_e0_init via call_once ON THE COMPUTE PATH: thread A takes once-flag, calls log path; --verbose
gives the log real backpressure (670K lines in the working case) so the write BLOCKS; other compute
threads block on the once-flag or SPIN in ggml_barrier, never yielding => log never drains =>
deadlock. Matches every symptom: 57% CPU zero I/O (spinning barriers + blocked write), D/l state,
banner via pipe but 0 bytes via file (buffered, unflushed), base bin never written, inconsistent
exits. Non-verbose works: log call completes cheaply, once-flag releases.
INSTRUMENT: gdb -p <pid> -batch -ex "thread apply all bt" during hang. REGISTERED PREDICTED
SIGNATURE: one thread inside hydra_init_once_fn in write/lock; >=1 in pthread_once/__gthread_once;
remainder in ggml_barrier. If backtrace shows otherwise (CUDA sync, atexit) hypothesis is WRONG,
follow the actual stack. (Architect notes: flagged call_once-on-hot-path as perf concern in original
review — it is a LIVENESS bug.)
2. SCHED-DIFF DROPPED — static argument: backend assignment lives in ggml_backend_sched_split_graph
which has no hydra knowledge; a bool CANNOT change backend assignment. Candidate (2) from §51
eliminated on inspection; by elimination the divergence = KERNEL DISPATCH inside
ggml_cuda_mul_mat_id (MMVQ/MMQ/MMF/fallback differ in accumulation precision). REPLACEMENT
DISCRIMINATOR (only if needed): dispatch-branch histogram — count branch per invocation, emit at
teardown via E0 stats channel, diff armed vs disarmed; counters not log lines = deadlock-proof;
names the kernel if counts differ.
3. HANG = SEPARATE DEFECT from §5 (shared origin: arm work on compute path; different failure mode/
severity/evidence). §5 keeps §51 wording; hang gets its own numbered finding + OWN ISSUE (--repo
explicit). ARCHITECTURAL POINT: lazy arming on the compute path is the design error — ARM EAGERLY
AT MODEL-LOAD TIME, off compute threads: removes deadlock by construction, plausibly upstream of
the numeric divergence (removes serialization point inside graph execution).
4. EFFICIENT SEQUENCE: (1) gdb backtrace confirm/refute; (2) fix by eager arm at load; (3) re-run
armed-vs-disarmed KL pair — if 0.0329 collapses to floor, BOTH defects close together and ISSUE B
UNBLOCKS DIRECTLY, no histogram needed. Histogram only if it does not. NOTE: free design validation
for #132 — it arms at load by construction, never had this exposure.
5. PRIORITY: hang files as own issue but does NOT escalate above B. Still blocking issue B; not
blocking C (load-time split), F (pin set), or the config-win landing — TOP UNSHIPPED ITEM should not
wait behind any of this. AMENDMENTS: §51 candidate (2) => eliminated by static argument; §51
discriminator sched-diff => SUPERSEDED by dispatch-branch histogram. Bank = §55.
=== END §55 ===

=== §55a: GDB BLOCKED (no root); /proc CENSUS PARTIAL; PROCEEDING TO FIX (intervention = confirmation) ===
ptrace blocked (yama ptrace_scope=1, no sudo); gdb attach + gdb-under-launch both unusable.
/proc per-thread census of hung process (13 min in): 10 threads futex_do_wait (S) — CONSISTENT with
threads queued on the once-flag/mutex; 4 poll_schedule_timeout; 1 folio_wait_bit_common (D, page
I/O); ZERO threads spinning in user space — the ggml_barrier-spin portion of the predicted signature
did NOT show; futex cluster DID. wchar crawled 101->227 bytes over 13 min => effectively frozen.
PARTIAL evidence, not conclusive. DECISION (leader, per architect sequence): proceed to STEP 2 (fix)
without gdb confirmation — the intervention test is STRONGER confirmation anyway: eager-arm fix ->
re-run armed-vs-disarmed KL pair; if 0.0329 collapses to floor both defects close and issue B
unblocks (§55 step 3). Dispatching fix to builder: move hydra_e0_init out of the mul_mat_id hot path
(call_once at first invocation) to EAGER arm at model-load time (init only reads env + parses pins
file — no tensor dependency — safe at load). New stamp required (stamp-after-commit). NOTE: post-fix
the instrument changes => all subsequent gate legs (incl. #132 three-way decomposition §44) run
POST-fix builds for instrument coherence; §43/§50 numbers remain banked as pre-fix record.
=== END §55a ===

=== §56: HANG FILED AS OWN ISSUE (#155); SUMMARY REBUILD #5 DISPATCHED ===
Architect §55 point-3 ruling executed: hang filed as its own issue --repo ddvnguyen/llama.cpp =>
https://github.com/ddvnguyen/llama.cpp/issues/155 (review-finding label; full §54 evidence matrix +
/proc census + registered hypothesis + eager-arm fix direction; blast-radius bounded note). Numeric
defect issue HELD until KL re-run verdict (may close with fix per §55 sequence).
Summary rebuild #5 dispatched to summarizer ee9633ba (folding §§51-55a onto rebuild #4 base);
write-to-file protocol; validation cap 8000 tokens.
=== END §56 ===

=== §57: SUMMARY REBUILD #5 COMMITTED (6817 tokens); /tmp EDQUOT — EVIDENCE RELOCATED ===
Rebuild #5 folded §§51-55a onto rebuild #4; committed to orchestration.md, tokens_estimate=6817
(within 8000 cap). /tmp hit user QUOTA (EDQUOT, "13G free but writes blocked") — ROOT CAUSE: our own
retained artifacts (11.1GB klarmedpair + 4.9GB DIS base + 136MB sched dump). EVIDENCE RELOCATED to
.evidence-e1/ in worktree (NVMe): klarmedpair.kld-base.bin, klscheddiff-v-DIS.{kld-base.bin,dump.log},
gdb-probe{,2}.kld-base.bin (0-byte), klscheddiff-v-ARM{,2}.dump.log (0-byte hang evidence). /tmp now
28G free. RULE MINTED: large instrument artifacts do NOT live in /tmp long-term — /tmp has user
quota; staging area for evidence = .evidence-e1/ (gitignored path, verify). Retention: klarmedpair +
0-byte pair kept until defect campaign closes.
=== END §57 ===

=== §58: EAGER-ARM FIX DELIVERED (1b0ac08bb); DECISIVE KL RE-RUN LAUNCHED (lead-run) ===
Builder 7fe20b8a delivered: commit 1b0ac08bb (e0/measurement-foundation, +31/-9); NEW STAMP
sha=1b0ac08bb tree=clean; binaries build-e0-eager/bin/ (perplexity sha256 9aef19b6...45c213ea71,
server ca511215...f94006eb3). FIX SHAPE: hydra_e0_arm_eager() at top of ggml_backend_cuda_init
(post device-validation); relaxed-atomic flags REPLACE call_once — hot path = one relaxed load,
ZERO futex potential; init once at backend init, identical env/abort/flags/dump semantics.
KL runner prepared /mnt/WorkDisk/tmp-eager/armKLrereager.sh (NOT run by builder). Build notes:
TMPDIR=/mnt/WorkDisk/tmp-eager (quota workaround); CUDA 13.2.1 needs explicit -lcudart -lcublas
exe-link flags (13.2.2 does not). LEAD RUNS the decisive pair directly (§55 step 3, airtight
protocol: NEW BINARY BOTH LEGS, env-only difference): leg-1 disarmed dump (no HYDRA env) -> new
base bin on WorkDisk; leg-2 armed cmp (PIN_FILE+DRYRUN) vs leg-1 base. VERDICT RULE: mean KLD
collapses to ~4e-4 floor => BOTH defects close, issue B unblocks, histogram NOT needed; stays
~0.033 => kernel-dispatch divergence confirmed, dispatch-branch histogram becomes the discriminator.
=== END §58 ===
