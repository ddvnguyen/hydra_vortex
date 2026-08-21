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
| PR #695 (`epic/591-rewrite-worker-scheduler`) | merge done, root cause of the collapse found and fixed, **not yet pushed** | Merging `main` pulled in #470's chunked-prefill streaming, which added new RPC paths (`EnginePrefillChunkedAsync`, `RequestStreamBodyAsync`) that `ScenarioRpcClient`'s mock didn't override — real TCP calls inside test scenarios, hence the "0/16 matched" collapse. Fixed in 2 commits (test-double adaptation + mock coverage for the new paths). Independently spot-verified: `DifferentialGateTests` + `AutoRouterTests` filter run in `/tmp/pr695-merge-work` → 0 failures. **Outstanding: 5 `GoldenTraceTests` scenarios drift** (`warm_affinity_on`, `warm_affinity_verify_on`, `combined`, `chunked_save`, `chunked_save_with_pushes`) — agent's read is this is legitimate re-baselining (the merge changed legacy scheduler behavior, so pre-merge goldens are stale), needs `HYDRA_HARNESS_REGEN=1` + review before merge, not blind acceptance. |
| `epic/610-server-hydra-extension` | rebased onto `hydra-fork` HEAD, **561 uncommitted lines pending review** | All 4 commits rebased clean onto `67ceb00bd`. C++ compiles; link fails (expected — needs hydra-fork's ggml-rpc submodule for a full build, can't verify standalone). Rebase left gaps (regex-based extraction dropped `process_single_task`, missed `hydra_apply_config`) — patched, high confidence per-region, but the fix diff needs review/squash decision before this is push-ready. No CI configured yet. |
| `epic/697-470-stabilization` | active (this branch) | Stabilization workstream per decision 001. |

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
