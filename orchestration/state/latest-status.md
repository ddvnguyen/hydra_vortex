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
| PR #695 (`epic/591-rewrite-worker-scheduler`) | in progress, CI failing | 3 of 16 parity scenarios unmatched. Blocks #641 resolution. |
| `epic/610-server-hydra-extension` | needs rebase | 45 commits behind `hydra-fork`, moderate-high conflict in `server-context.cpp`. No CI configured yet. |
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
   traces.
2. **Finish PR #695** — fix Build & Test CI, drive `warm_affinity_on` /
   `warm_affinity_verify_on` / `migration` to full parity match (treat as closing #641).
3. **Rebase `epic/610`** onto current `hydra-fork`, configure CI, validate with
   differential-parity harness.
4. **Stand up cookbook/decisions structure** (done in this PR).
