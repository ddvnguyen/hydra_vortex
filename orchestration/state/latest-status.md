# latest-status — rolling handoff

This is the **ephemeral/session layer**. Update it at session boundaries — start of a
new agent session, end of a work block, when context shifts. Durable decisions that
outlast any single session belong in `docs/decisions/` instead of living here
indefinitely. This is the session-memory vs promoted-knowledge split: keep what's
current here, promote what's durable there.

---

## Current work: #712 prefix-KV reuse — DRAFT PR #732 open (2026-09-01)

- **P0 #712 DONE, awaiting review + CI + owner merge decision.** Branch
  `fix/712-prefix-reuse` (commit `f381f11`, base `epic/697-final-verify @ 56eb3f1`),
  **DRAFT PR #732** opened (`Closes #712`), per-turn TTFT table in PR body.
- Final A/B on test-a `:19000` → nodeA `:18086`: T1–T6 TTFT
  34.3 / 26.8 / 26.7 / 28.4 / 28.1 / 31.8 s — **all ≤ 1.10× A/B#4 baseline**
  (criterion ≤ 1.5× every turn; before: 34 / 99.5 / 100.1 / 136.6 / 197.8 / 234.5).
  Engine N_COMMON traces confirm delta-only prefills (8321 / 14530 / 20289 / 26048 /
  31357); 0 `applying hydra_config`, 0 `load_model`.
- Hermetic: Tests.Core 722/722 (717 base + 5 new), Tests.Shared 70/70, warnings
  unchanged (25 line-shifts only). Pre-existing flake noted in PR (MergedDecodeModelAliasTests,
  static ModelConfigLoader race under xunit parallelism, 1/5 full runs, 0/5 isolated).
- Evidence: `docs/hydra-test/evidence/712-prefix-reuse/` (README + 10 artifacts incl.
  intermediate v2/v3/v5 runs that exposed the save/restore race, the T4 health-cap
  anomaly, and the redundant evict save).
- Deployed image on test-a: `localhost/hydra-core:712-w1` = `c7e9d8d9886d` (v6).
  Container `hydra-infra_hydra-core-test-a_1`. test-b `:19001` untouched; prod `:9000` untouched.
- **Next:** PR #732 CI green → lead zero-trust review → ready for owner merge decision
  (epic→main close-out still gated on explicit owner confirm per AGENTS.md).

## Current branch(es) in flight

| Branch | Status | Notes |
|---|---|---|
| `fix/712-prefix-reuse` → DRAFT PR #732 | **done, awaiting CI + review** | #712 solo/cold prefix-KV reuse; see block above. |

| `epic/470-merged-decode` | deploy-hold, Phase 5 in progress | 37:1 fix:feat ratio. Contract frozen (see decision 001). |
| PR #695 (`epic/591-rewrite-worker-scheduler`) | **RE-BASELINED + PUSHED `027e9de29`** (fast-forward from `0706feef3`), status:`review` (2026-08-26) | Owner ruling Option A (V2-combined intended): 3 goldens re-baselined to V2 traces (combined=Put 4096 no StateGet; chunked_save* content-addressed). W2 `c92bedb0` produced; lead zero-trust re-ran -> GoldenTraceTests 3× green, DifferentialGateTests 3× green (0 drift), full Tests.Core **652/652** green. Drift-inventory comment on PR; OWNER-RULING paragraph for combined (supersedes legacy #635 fix4 in COMBINED mode). **Awaits owner CONFIRM to merge to `main`** (decision 6 — absolute gate). |
| `epic/610-server-hydra-extension` | superseded — old ref left alone, stale | Original branch (`14537a4`) predates the rebase; not force-pushed (user preferred a new branch over rewriting history on the existing ref). Left as-is for now; safe to delete once the new branch is confirmed working. |
| `epic/610-server-hydra-extension-rebased` (llama.cpp fork, `ddvnguyen` remote) | **PR #105 open against `hydra-fork`** — **DEFERRED (GPU phase)** | 4 rebased commits onto `hydra-fork` HEAD (`67ceb00bd`) + 1 fixup (`ecfbfda11`). Rig-validated 6/7 gates 2026-08-21; gate 7 (A/B toggle parity on P100 VM) blocked by VM SSH starvation. Per owner decision 2026-08-26: gate 7 + any live-rig validation deferred until owner schedules rig time. |
| `epic/697-470-stabilization` | active (this branch) | Stabilization workstream per decision 001. |

## Fleet model change — 2026-08-27 (owner directive)

- **Default lead/delegate model is now `opencode-go/glm-5.3-flash`.** Prior default
  `opencode-go/ox-alpha-free` is DEPRECATED — it now errors "Model not found" when
  prompted (both consultant relays `26a5ca56` and `6f8a1b9e` died of this).
- glm-5.3-flash known failure mode (per AGENTS.md): long multi-step turns truncate
  mid-tool-call → mitigate with micro-scoped single-action prompts + explicit
  "reply with only X, then stop".
- **New leader dispatched** for the P100 VM GPU-passthrough fix (Track A adjacent):
  root cause pre-diagnosed by consultant — host vfio binding correct
  (`08:00.0` @ `10de:15f8` on `vfio-pci`, clean IOMMU group), but domain
  `ubuntu26_server` has NO `<hostdev>` in its libvirt XML, so every boot comes up
  without the GPU. Fix = `virsh attach-device --persistent` + reboot + in-VM nvidia-smi.

## Track A resumed 2026-08-26 (lead `1abcd138`, ox-alpha-free)

Owner decisions locked via consultant (do not re-ask): PR #695 DifferentialGate drift =
REAL-BUT-INTENDED V2 behavior; resolution B-THEN-A — (B) Store-layer KV equivalence
proof for `combined` (+ chunked_save*), then (A) re-baseline 3 goldens
(HYDRA_HARNESS_REGEN=1 + 3x isolated, test files ONLY). Proof failure → CONFIRM_REQUIRED,
no re-baseline over unexplained difference. GPU items (#105 gate 7, live-rig validation)
deferred. Issue #703 lane explicitly out of scope.

Progress this session:

- **W1 `a6fadd23` DONE — verdict MIXED → owner decision 2 STOP triggered:**
  - `chunked_save` / `chunked_save_with_pushes` = **equivalent** (transport-only diff).
  - `combined` = **NOT-equivalent**: legacy persists POST-decode slot state
    (`StateGet(0)`→`Put(2048)`); V2 persists PRE-decode prefill blob
    (`Put(4096)`, `req.KvBlob` from PrefillRunner). Delta ≠ granularity.
  - Aggravator (lead-confirmed): legacy `#635 fix 4` comment explicitly calls
    persisting pre-decode KvBlob a regression it fixed; V2 re-introduces it
    deliberately for COMBINED (HydraConfigDelivered gate ≠ same freshness).
  - Lead zero-trust: re-ran DifferentialGateTests (10 MATCH/8 SKIP/3 DRIFT,
    drift set exact), ran V2TraceDiagnostic (pass), read all cited lines.
  - **NO re-baseline, NO W2 spawn, PR #695 untouched.** Findings:
    `orchestration/state/699-w1-equivalence-findings.md` +
    [#699 comment](https://github.com/ddvnguyen/hydra_vortex/issues/699#issuecomment-5425652942).
  - **CONFIRM_REQUIRED posted to owner** (options: all-3 re-baseline /
    chunked-only + design review of combined save semantics / require V2 code change).
- **#469/#641 CPU-verifiable parts DONE** (comments posted on both issues):
  - #469: fix via fork PR #60 (`8c8775c7a`), ancestor of fork HEAD `67ceb00bd`;
    checkpoint-before-final-token-decode invariant verified at HEAD (`server-context.cpp`
    ~L4458). Remaining: rig decode-quality validation → issue stays OPEN.
  - #641: fix `5d37b9f08` ancestor of fork HEAD;
    `server_should_rewind_to_checkpoint` + pinning test present at HEAD. Remaining:
    rig no-re-prefill latency check → issue stays OPEN.

## Documentation state

- `AGENTS.md` rewritten 2026-08-21 — was a stale/partly-fictional v2.1.1 "hermes fleet"
  contract (referenced files that don't exist, model constraint already overridden in
  practice). Now an accurate bootstrap pointing at the actual leader/Paseo-delegate
  model. See `docs/decisions/002-supersede-hermes-fleet-contract.md`.
- `docs/cookbook/` has 2 written recipes now (`reconcile-epic-branch.md`,
  `paseo-delegate-and-verify.md`), both derived from real work performed this session
  rather than speculative. 3 more still scaffolding-only (see cookbook README).

## Open questions

- Is `epic/610`'s conflict surface in `server-context.cpp` from upstream churn or from
  the Hydra-specific additions? Determines rebase difficulty.
- Does `HYDRA_EXT_MODE` A/B toggle in `epic/610` need the same differential-parity
  harness as #695, or is the mechanical extraction test sufficient?
- PR #666 merged with failing Build & Test — is branch protection actually enforced or
  is the check required-in-name-only?

## Next steps

1. **Verify #469** against current fork state — prerequisite for trusting #695 golden
   traces. Fix present at HEAD but untested (parity test CI-skipped).
2. **Verify #641** — fix already landed on `hydra-fork` (`5d37b9f08`,
   `server-checkpoint-policy.h`), issue just never got closed. Confirm it resolves the
   reported symptom, then close #641. Not blocked on either rewrite.
3. **Finish PR #695** — merge 113 commits of `main`, fix the 2 missing interface
   members, drive `warm_affinity_on` / `warm_affinity_verify_on` / `migration` to full
   parity match (cosmetic RPC-ordering fix, unrelated to #641).
4. **Rebase `epic/610`** onto current `hydra-fork`, configure CI, validate with
   differential-parity harness.
5. **Stand up cookbook/decisions structure** (done in this PR).
