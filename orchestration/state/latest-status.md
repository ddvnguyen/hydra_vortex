# latest-status — rolling handoff

This is the **ephemeral/session layer**. Update it at session boundaries — start of a
new agent session, end of a work block, when context shifts. Durable decisions that
outlast any single session belong in `docs/decisions/` instead of living here
indefinitely. This is the session-memory vs promoted-knowledge split: keep what's
current here, promote what's durable there.

---

## Current branch(es) in flight

| Branch | Status | Notes |
|---|---|---|
| `epic/470-merged-decode` | deploy-hold, Phase 5 in progress | 37:1 fix:feat ratio. Contract frozen (see decision 001). |
| PR #695 (`epic/591-rewrite-worker-scheduler`) | pushed (`0706feef3`), **GoldenTraceTests green 3× (21/21)** — #698 fixed 2026-08-21 (harness NormalizeRpcTrace + re-baselined chunked_save_with_pushes.json, fast-forward ccfd8601d..0706feef3) | Fix verified independently 3× isolated; DifferentialGateTests still 3 drifts (combined/chunked_save pre-existing + chunked_save_with_pushes from re-baseline) — outside #698 scope, tracked separately. PR now awaits full CI green + user-confirmed merge to `main` per decision 001. |
| `epic/610-server-hydra-extension` | superseded — old ref left alone, stale | Original branch (`14537a4`) predates the rebase; not force-pushed (user preferred a new branch over rewriting history on the existing ref). Left as-is for now; safe to delete once the new branch is confirmed working. |
| `epic/610-server-hydra-extension-rebased` (llama.cpp fork, `ddvnguyen` remote) | **PR #105 open against `hydra-fork`** — **DEFERRED (GPU phase)** | 4 rebased commits onto `hydra-fork` HEAD (`67ceb00bd`) + 1 fixup (`ecfbfda11`). Rig-validated 6/7 gates 2026-08-21; gate 7 (A/B toggle parity on P100 VM) blocked by VM SSH starvation. Per owner decision 2026-08-26: gate 7 + any live-rig validation deferred until owner schedules rig time. |
| `epic/697-470-stabilization` | active (this branch) | Stabilization workstream per decision 001. |

## Track A resumed 2026-08-26 (lead `1abcd138`, ox-alpha-free)

Owner decisions locked via consultant (do not re-ask): PR #695 DifferentialGate drift =
REAL-BUT-INTENDED V2 behavior; resolution B-THEN-A — (B) Store-layer KV equivalence
proof for `combined` (+ chunked_save*), then (A) re-baseline 3 goldens
(HYDRA_HARNESS_REGEN=1 + 3x isolated, test files ONLY). Proof failure → CONFIRM_REQUIRED,
no re-baseline over unexplained difference. GPU items (#105 gate 7, live-rig validation)
deferred. Issue #703 lane explicitly out of scope.

Progress this session:

- **W1 `a6fadd23` spawned** (mimo-v2.5, build mode): KV-equivalence proof,
  worktree `/tmp/w1-equiv-proof` @ `0706feef3` (branch `w1-kv-equivalence`),
  deliverable EQUIVALENCE_FINDINGS.md w/ per-scenario verdict. Prior findings
  `/tmp/w1-diffgate/DIFFGATE_FINDINGS.md` wiped — worker re-derives from source.
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
