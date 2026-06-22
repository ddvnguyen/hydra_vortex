---
description: Ralph-style autonomous task dispatch — fresh-context subagent per issue, isolated worktree, container build
---

You are dispatching one or more ralph-style tasks. The user gave you: `$ARGUMENTS`.

Parse `$ARGUMENTS` as a space-separated list of GitHub issue numbers in `ddvnguyen/hydra_vortex`. For each issue, dispatch a fresh-context subagent that works the full task lifecycle (branch → implement → test → PR) in an isolated git worktree and builds **inside a container** with bounded memory.

If `$ARGUMENTS` has multiple issues, dispatch all subagents in **parallel** (multiple `task` tool calls in the same message). If a single issue, dispatch one.

This pattern exists because:

- **Worktree per subagent** keeps git state isolated. Multiple subagents run in parallel without conflict, and the main repo's working tree is never modified.
- **Container build** isolates the .NET SDK + its memory from the host. Running `dotnet build`/`dotnet test` on the host can use 4-8 GB of RAM, which can starve the running Core container. **This caused the 2026-06-22 SIGSEGV cascade in Core 0.9.0** when a subagent ran `dotnet build` directly on the host. Container build with bounded memory (`--memory=6g/8g --cpus=4`) prevents the contention.

## Pre-flight (run once per dispatch session, not per issue)

```bash
# Resolve the repo root dynamically (works for any dev, any path)
REPO_ROOT=$(git rev-parse --show-toplevel) \
  || { echo "Not in a git repo. cd into the hydra_vortex repo root and try again."; exit 1; }
WORKSPACE_PARENT=$(dirname "$REPO_ROOT")
REPO_NAME=$(basename "$REPO_ROOT")

# Verify the build image exists; build it if not.
podman images --format '{{.Repository}}:{{.Tag}}' | grep -qE '^hydra-build:latest$' \
  || podman build -f "$REPO_ROOT/infra/Dockerfile.build" -t hydra-build:latest "$REPO_ROOT"
```

## Per-issue workflow

For each issue number `N` in `$ARGUMENTS`:

### 1. Read the issue

```bash
gh issue view N --repo ddvnguyen/hydra_vortex --json title,body,labels,milestone,state
```

Confirm `state` is `OPEN`. If not, skip this issue and tell the user.

If the issue has the `llama-fork` label, **stop and tell the user** — the C++ change must land on the fork first per `docs/workflow/08-llama-fork.md`. Do not dispatch directly.

### 2. Derive the branch name

From the title, extract the `[M?-P?-seq]` token (e.g. `[M-Perf-P1-336]`). Build a branch name like:

- `fix/<m-perf-p1-336>-<short-slug>` for bug fixes
- `feat/<m5-p1-107-a>-<short-slug>` for features

Match the convention in `docs/workflow/02-implement.md` (e.g. `fix/mN-Psev-seq`). Use lowercase, dash-separated slug derived from the title after the bracket.

### 3. Create the worktree (mandatory isolation)

```bash
WORKTREE="${WORKSPACE_PARENT}/${REPO_NAME}-N"
# If a stale worktree exists from a previous run, remove it first
git worktree remove --force "$WORKTREE" 2>/dev/null || true
git worktree add "$WORKTREE" main
```

The worktree at `$WORKTREE` is the isolated workspace for the subagent. **Do not** modify the main repo's working tree.

### 4. Dispatch the subagent

Use the `task` tool with `subagent_type: "general"`. Pass the spec below, filling in `N`, the branch name, the issue body, the worktree path, and `$REPO_ROOT`.

## Subagent prompt template

```
You are an autonomous coding agent operating with ralph-style fresh-context-per-task semantics. Execute one full task: fix issue <N> in ddvnguyen/hydra_vortex and open a PR.

## Required reading (in order)
1. The issue body: `gh issue view <N> --repo ddvnguyen/hydra_vortex --comments`
2. <REPO_ROOT>/CLAUDE.md (project context, language decisions, critical facts)
3. <REPO_ROOT>/docs/workflow/01-pickup.md
4. <REPO_ROOT>/docs/workflow/02-implement.md (cross-repo / submodule rules — this issue is C# only)
5. <REPO_ROOT>/docs/workflow/03-test-verify.md
6. <REPO_ROOT>/docs/workflow/04-commit-pr.md

## Isolation (mandatory)
The worktree is at <WORKTREE>. **cd into it for ALL work.** DO NOT switch the main repo's branch. DO NOT modify the main repo's working tree. DO NOT touch <REPO_ROOT> outside the worktree.

## Build & test in a container (mandatory)
Running dotnet on the host will starve the running Core container. Always use the hydra-build container with bounded memory.

```bash
# Restore + build
podman run --rm --memory=6g --cpus=4 \
  -v <WORKTREE>:/src \
  -w /src/src/core \
  hydra-build:latest \
  bash -c "dotnet restore src/Hydra.sln && dotnet build src/Hydra.sln -c Release"

# Tests (--network=host so integration tests can reach the running PG on 127.0.0.1:5432)
podman run --rm --memory=8g --cpus=4 --network=host \
  -v <WORKTREE>:/src \
  -w /src/src/core \
  hydra-build:latest \
  bash -c "dotnet test src/core/Tests.Shared/ --settings src/Hydra.runsettings && dotnet test src/core/Tests.Core/ --settings src/Hydra.runsettings"
```

Both must be 0 errors / 0 unexpected failures before PR. The pre-existing test failure `Tests.Core.CoordinatorConfigTests.LoadWorkers_ProductionConfigFile_LoadsBothWorkersWithCorrectModelFields` is known — note it but do NOT fix it in this PR.

## Self-review before PR (mandatory, ~2 min)

Before committing, re-read your own diff (`git diff <base>..HEAD`) and check for:

- **Silent errors** — exceptions caught and ignored, return values discarded, `_log.Error` followed by `return`, `Log.Warning` without escalation
- **Missing error handling** — null deref, missing `using` for `IDisposable`, resource leaks, un-awaited `Task` (fire-and-forget)
- **Dead code / debug prints** — `Console.WriteLine` in production paths, `Console.WriteLine("[DEBUG]..."`, commented-out blocks, `# TODO` that should be issues
- **Missed edge cases** — empty input, null keys, large payloads, concurrent access, the exact failure mode the issue was trying to fix
- **Metric naming** — counters end in `_total`, gauges are bytes/int with a unit suffix where ambiguous (`_bytes`, `_seconds`, `_count`)
- **Comment quality** — every public API has a one-line summary, no commented-out code, no unrelated `# TODO` pollution
- **Push-before-PR** — if you touched `src/llama-cpp`, the fork SHA is reachable on `origin/hydra-fork` per `02-implement.md`

Fix anything obvious, then commit. The dispatcher's external review will catch the rest.

## Task
<insert the issue body / fix scope here, verbatim or summarised>

## Branch
```bash
cd <WORKTREE>
git checkout -b <branch-name>
# OR, if available in this env: gh issue develop <N> --name <branch-name>
```

## Commit & PR
- Commit message: `fix: [<issue-id>] <short description>` (or `feat:` / `docs:` / `chore:` / `perf:` / `test:` per the change)
- End with `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- Push: `git push -u origin <branch-name>`
- Open PR: `gh pr create --title "fix: [<issue-id>] <short>" --body "Closes #<N>"` — body must include a Test Plan section ticking each tier with actual numbers
- Verify: `gh pr checks` should show green for all checks before declaring done. If the self-hosted runner is offline, that's a pre-existing infrastructure issue (note it, do not block the PR).

## Do NOT
- Do NOT run dotnet on the host (always use the container with bounded memory)
- Do NOT touch the src/llama-cpp submodule
- Do NOT modify workers.json, docker-compose*.yml, or any infra
- Do NOT deploy (the live system is already on the latest version; the subagent's job is code only)
- Do NOT spawn further subagents
- Do NOT switch the main repo's branch

## Return
Your final message must contain:
- PR URL (`https://github.com/ddvnguyen/hydra_vortex/pull/<N>`)
- 3-5 line summary of the actual change
- Build result (0 errors, N new warnings)
- Test result (X/Y passing, including the pre-existing failure)
- Any open questions, risks, or deviations from the issue's proposed scope

If you hit ambiguity you cannot resolve from the code/docs, **stop and report** what you hit and the trade-off you're considering — do not invent requirements.
```

## After all subagents complete

1. **Aggregate results.** Each subagent returns a PR URL.
2. **Post a short comment on each issue** (e.g. `gh issue comment <N> --body "PR opened: <url>"`).
3. **Run the Review and resolve cycle** (see below) for each PR opened.
4. **Clean up worktrees** the subagent (or fix subagent) didn't already clean up:
   ```bash
   git worktree remove --force "$WORKTREE"
   ```
5. **Report back to the user** with the PR URLs, branch names, and any flags (review iterations, build warnings, pre-existing failures).
6. **Do NOT merge.** Merging is a separate decision by the user.

## Review and resolve cycle

After each PR is opened, the dispatcher (you) reviews the PR. If findings exist, file them as `review-finding` issues and dispatch a fix subagent. Loop until the PR is clean, then mark it ready for review.

### For each PR opened by a subagent

#### 1. Read the PR

```bash
PR=<pr_number>
gh pr view "$PR" --repo ddvnguyen/hydra_vortex \
  --json title,body,files,additions,deletions,baseRefName,headRefName,mergeable,statusCheckRollup
gh pr diff "$PR" --repo ddvnguyen/hydra_vortex | head -400   # cap; read more if needed
gh pr checks "$PR" --repo ddvnguyen/hydra_vortex
```

#### 2. Review for issues

Check the diff + body + checks for:

- **Build & tests** — Did the subagent's container build + tests pass? Does CI pass? Are there new tests for new code? Is the test count reasonable (>= 1 test for the proposed fix, more for non-trivial changes)?
- **Code quality** — Obvious bugs, race conditions, silently-caught exceptions, ignored returns, missing error handling, resource leaks, unbounded loops, off-by-one
- **Design** — Does the change match the issue's proposed scope? Any scope creep? Any items from the issue body that the subagent missed or skipped?
- **Edge cases** — Empty input, null/missing keys, concurrent access, large payloads, error paths. **Especially:** the failure mode the issue was specifically trying to fix — is the fix actually triggered by that mode?
- **Conventions** — Commit message format, `Co-Authored-By` trailer, branch name matches `fix/mN-Psev-seq`, PR body has a Test Plan with actual numbers
- **Project-specific** — Per `docs/workflow/04-commit-pr.md`: `Closes #N` link, submodule reachability (if submodule touched), no infra changes, no deploy

#### 3. If issues: file findings + dispatch a fix subagent

For each finding, file a `review-finding` issue:

```bash
gh issue create --repo ddvnguyen/hydra_vortex \
  --title "[<seq>] <short description>" \
  --label "review-finding" \
  --body "Review finding from PR #$PR.

**File / line:** <path>:<line>

**Issue:** <what's wrong, with the code snippet>

**Suggested fix:** <how to address>"
```

The `seq` follows the project's P-sev-seq convention (e.g., `M-Perf-P1-001`). For multiple findings from the same review, number sequentially.

Then dispatch a **fix subagent** (one per PR, focused on all findings together — keep the cycle short). The fix subagent's spec is the same as the original subagent's spec, but with these adjustments:

- **Branch:** the same branch the PR is from. Re-create the worktree from the branch (the original worktree may have been kept around or pruned):
  ```bash
  git worktree add "$WORKSPACE_PARENT/${REPO_NAME}-${N}-fix" "$BRANCH_NAME"
  ```
- **Worktree:** the fix worktree above
- **Task scope:** the specific review findings (their issue numbers + descriptions), NOT the whole original issue body
- **Commit message:** `fix: address PR #<PR> review — <finding summary>` (or `… — N findings` if several)
- **PR body:** the fix subagent does NOT open a new PR. It pushes commits to the same branch. The existing PR is automatically updated.
- **Return:** just the new commit SHAs + a 2-line summary, NOT a new PR URL

#### 4. Re-review

After the fix subagent pushes:

```bash
gh pr diff "$PR" --repo ddvnguyen/hydra_vortex | head -400
gh pr checks "$PR" --repo ddvnguyen/hydra_vortex
```

If new findings appear, repeat from step 3. If the PR is clean (no findings, build green, tests green), continue to step 5.

#### 5. Mark ready and report

When the PR is clean:

```bash
gh pr ready "$PR" --repo ddvnguyen/hydra_vortex
```

Then report to the user with:
- PR URL
- Number of review-resolve iterations
- Final build/test result
- Any unaddressed risks or notes for the user

### When to stop the cycle

- **Stop on clean** — the PR has no findings, build is green, tests are green.
- **Stop on hard block** — the finding is outside the subagent's scope (cross-repo coordination, design discussion, requires user decision). Surface the finding to the user instead of dispatching a fix.
- **Cap iterations** — if more than 3 review-resolve cycles are needed on the same PR, the change is too large for a single subagent. Mark the PR as draft, surface to the user.
