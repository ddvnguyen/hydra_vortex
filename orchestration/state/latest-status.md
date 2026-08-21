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
| PR #695 (`epic/591-rewrite-worker-scheduler`) | pushed (`ccfd8601d`), 1 known-red test blocking green | Golden-trace review done by Claude directly (not delegated): re-ran each drifted scenario 3x in isolation to separate stale-baseline from flaky. 4/5 (`warm_affinity_on`, `warm_affinity_verify_on`, `combined`, `chunked_save`) were deterministic and legitimately explained by main's #470 landing after goldens were captured — re-baselined and pushed. `chunked_save_with_pushes` is a genuine non-deterministic op-ordering race (background Store push racing the main prefill call) — golden left untouched (test honestly fails) rather than masked by a lucky regen. Tracked as **#698**. PR not clean-green until #698 is fixed. |
| `epic/610-server-hydra-extension` | rebased + fixup reviewed and committed locally (`ecfbfda11`), **push blocked pending user go-ahead** | All 4 commits rebased clean onto `67ceb00bd`. Reviewed the 561-line post-rebase fix diff line-by-line (Claude directly, not delegated): confirmed it correctly restores generic (non-Hydra) dispatch code the regex-extraction over-deleted, no duplicate definitions between `server-context.cpp`/`hydra-server-context.cpp`, call sites match forward declarations. Committed as a labeled fixup commit. Pushing requires `--force` (history rewritten by rebase) — blocked by the auto-mode safety classifier, correctly so; asked user for explicit go-ahead, awaiting response. No CI configured yet. |
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
