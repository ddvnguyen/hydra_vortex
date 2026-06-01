# 4. Commit & PR

**Goal:** land the change and link it across GitHub ↔ Plane.

1. **Commit** — conventional commits: `fix:` / `feat:` / `docs:` / `chore:` / `perf:`
   / `test:`. Reference the issue / Plane item in the body. End every commit message
   with:
   ```
   Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
   ```
2. **PR:** `gh pr create --title "fix: [MN-Psev-seq] short title" --body "Closes #N"`
   (omit `Closes` if there's no issue). Summarise what changed + how it was verified
   (which test tiers ran).
3. **Cross-link (manual bridge — no native sync):**
   - Paste the **PR URL into the Plane work item** (comment or description).
   - Reference the **Plane work item in the PR body**.
4. **CI / merge:** ensure CI is green — `build-test` + `integration` + `system`
   (`gh pr checks`). Merge only when green and reviewed. CI failures auto-file issues
   (`ci-failure`) — investigate, don't ignore.

→ Next: `05-deploy.md` (if runtime/fork touched) else `07-issue-and-plane.md`
