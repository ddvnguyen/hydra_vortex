# 4. Commit & PR

**Goal:** land the change; the `Closes #N` link is the only "tracking" needed.

1. **Commit** — conventional commits: `fix:` / `feat:` / `docs:` / `chore:` / `perf:`
   / `test:`. Reference the issue in the body. End every commit message with:
   ```
   Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
   ```
2. **PR:** `gh pr create --title "fix: [MN-Psev-seq] short title" --body "Closes #N"`.
   The `Closes #N` link ties the PR to the work item — **no manual board update**:
   built-in workflows move the item to **Done** when the PR merges / issue closes.
   Summarise what changed + which test tiers ran.
3. **CI / merge:** ensure CI green — `build-test` + `integration` + `system`
   (`gh pr checks`). Merge only when green and reviewed. CI failures auto-file
   `ci-failure` issues (auto-added to the board) — investigate, don't ignore.

→ Next: `05-deploy.md` (if runtime/fork touched) else `07-issue-and-close.md`
