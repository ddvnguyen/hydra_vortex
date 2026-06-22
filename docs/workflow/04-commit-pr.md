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

> ### ⚠️ If your PR bumps a submodule pointer (e.g. `src/llama-cpp`)
>
> Before opening the PR, **verify the pinned SHA is reachable from the
> submodule's public remote**. A dangling pointer makes the PR unreviewable
> (no one can fetch the C++ changes) and breaks `git clone --recurse-submodules`
> for anyone who tries to build the PR branch.
>
> ```bash
> # 1. Confirm the parent commit's pinned SHA
> SHA=$(git ls-tree HEAD src/llama-cpp | awk '{print $3}')
> echo "Pinned submodule SHA: $SHA"
>
> # 2. Confirm the SHA is on the fork's branch (the one the submodule tracks)
> URL=$(git config --file .gitmodules --get submodule.src/llama-cpp.url)
> BRANCH=$(git config --file .gitmodules --get submodule.src/llama-cpp.branch)
> if git ls-remote "$URL" "refs/heads/$BRANCH" | grep -q "$SHA"; then
>   echo "OK: $SHA is on $URL  $BRANCH"
> else
>   echo "DANGLING: $SHA is NOT on $URL  $BRANCH"
>   echo "→ Push the submodule's branch first (see 02-implement.md), then re-bump."
>   exit 1
> fi
> ```
>
> If the verification fails, **do not open the PR**. Push the submodule branch
> from inside the submodule's working tree (`git -C src/llama-cpp push ddvnguyen
> hydra-fork`), re-bump the parent, and re-verify. This is the exact failure mode
> that triggered review-finding P0 on PR #290.
>
> **Cross-repo coordination.** If this PR pairs with a fork PR in
> `ddvnguyen/llama.cpp` (i.e. the C++ change lives there first), the parent PR
> body must cross-link it. See `08-llama-fork.md` for the linking rules.

→ Next: `05-deploy.md` (if runtime/fork touched) else `07-issue-and-close.md`
