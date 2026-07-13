---
description: Spawn a hydra_vortex code reviewer for a PR/issue on a different provider than the implementer, then route the verdict. Analysis only — no edits. Use when a worker's PR is ready for review.
argument-hint: [PR number and/or issue number, e.g. "PR 42 issue 17"]
allowed-tools: Bash(paseo *) Bash(gh *) Bash(git *) Read Glob Grep
---

You are the hydra_vortex TEAM LEAD driving the REVIEW stage (charter PR
section). Charter: orchestration/LEAD_CHARTER.md.

## TARGET
$ARGUMENTS

## Context budget — hard rule
Keep THIS conversation lean (< 180K tokens). You orchestrate the review; you do
NOT review the code yourself and do NOT edit code:
- Read only the charter and the referenced issue.
- Do not read the diff here — the reviewer agent (separate context) does that.
- Push outcomes into issue/PR comments, not chat.

## Procedure
1. Resolve the PR number and its issue from the target above (use
   `gh pr list` / `gh issue list` if not fully specified). Identify the
   IMPLEMENTER's provider from the issue comments / worker names.
2. Confirm state: PR exists and issue is `status:review`. If not, relabel to
   `status:review` first.
3. SPAWN a reviewer on a provider DIFFERENT from the implementer, tier-1/2 per
   orchestration/providers.yaml:

   ```
   paseo run --provider <tier-1/2, != implementer> --detach --name rev-<N> \
     --env LEAD_ID=$PASEO_AGENT_ID --label role=lead-child \
     "Review PR #<X> for issue #<N>. Analysis only — no edits, no commits.
      Check against orchestration/ARCHITECTURE.md and the acceptance criteria in
      issue #<N>. Post your findings as a PR review comment. Verdict on the last
      line: 'APPROVE' or 'REQUEST_CHANGES' followed by a concrete numbered list.
      FINAL STEP: run orchestration/scripts/emit-event.sh DONE <N> rev-<N> na
      '<APPROVE|REQUEST_CHANGES>', then stop — this notifies the lead."
   ```

   Note: anything labeled `draft:needs-review` (tier-3 output) MUST pass this
   review before merge — no exceptions.
4. Record in an issue comment: reviewer name/ID and that review is in flight.
5. ROUTING (state what happens next; execute now only if the reviewer already
   finished — otherwise stop and let a supervise sweep pick it up):
   - REQUEST_CHANGES -> route the numbered list back to the implementing worker
     via `paseo send <impl-id>`; loop, max 3 rounds, then escalate with
     `ATTENTION:` to the user.
   - APPROVE -> merge the PR, relabel `status:deployed`, hand to deploy/monitor.
6. REPORT: reviewer name + ID, PR/issue numbers. Then GO IDLE — the reviewer
   wakes you via emit-event.sh when its verdict is in; no need to poll. To check
   manually: `paseo chat read hydra-events --since <cursor> --json`.

Do not block or loop; idle and react when the reviewer notifies you.
