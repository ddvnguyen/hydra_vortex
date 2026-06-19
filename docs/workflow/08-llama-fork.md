# 8. Cross-repo work — llama.cpp fork (when the change is in C++)

**Goal:** when a Hydra issue/PR requires a change inside the `src/llama-cpp`
submodule (i.e. the `ddvnguyen/llama.cpp` fork), produce **coordinated
artifacts in both repos** so that no work item gets lost between the parent
and the fork. This step is **required** whenever a Hydra-side change calls
for new RPC opcodes, a server endpoint, a model-architecture fix, or any
other C++ change in the submodule.

**Skim if:** the Hydra PR only consumes what the fork already exposes (no
C++ change). The existing `02-implement.md` push-before-PR guard still
applies, but you do not need this step.

## Why this is a separate step

`src/llama-cpp` is a **git submodule** of this repo, pointing at a
**private fork** of llama.cpp:

| | Parent repo | Fork |
|---|---|---|
| GitHub | `ddvnguyen/hydra_vortex` | `ddvnguyen/llama.cpp` |
| Default branch | `main` | `hydra-fork` (protected) |
| Tracked in `.gitmodules` | — | `branch = hydra-fork` |
| Issues | enabled | **enabled** (required by this workflow) |
| PRs | enabled | enabled |
| Project board | "Hydra Vortex" (v2) | (none) |

A change in the fork must land there **first**, and the parent repo
**then** bumps the submodule pointer. The work items in each repo must
link to each other so reviewers can see the full picture from either side.

## The two-repo flow

```
Hydra issue               Fork issue              Fork PR                Parent PR
  #N (M-Perf)              #M (llama.cpp)          hydra-fork ← branch    main ← branch
     │                        │                        │                     │
     ▼                        ▼                        ▼                     ▼
  pickup                    (file)               (open & merge)        (submodule bump
  (01)                                            before parent         + Hydra-side
                                                  PR opens)              changes)
     │                        │                        │                     │
     └──── branch & impl ─────┴──── branch & impl ─────┴──── branch & impl ─┘
                  (02)                       (08 fork)              (02)
```

1. **Hydra issue** — picked up from the Project board (step 1, `01-pickup.md`).
   When you discover the work needs a fork change, add the `llama-fork` label
   on the Hydra issue (use a one-off `gh label create` if it does not exist yet).
2. **Fork issue** — file on the fork, link it from the Hydra issue body.
3. **Fork PR** — implement on a feature branch, target `hydra-fork`, push,
   wait for fork CI, **merge the fork PR first** (or at minimum verify the
   SHA is reachable on `hydra-fork` per `02-implement.md`'s push-before-PR guard).
4. **Parent PR** — open the Hydra PR with the submodule pointer bumped to the
   fork's merge commit. Reference the fork PR and issue in the body. The
   parent PR's `Closes #N` closes the Hydra issue.

The fork PR **does not** have to be merged before the parent PR opens, but
its merge commit **must be reachable on `hydra-fork`** by the time the
parent PR merges. The push-before-PR guard in `02-implement.md` /
`04-commit-pr.md` enforces this.

## Fork-side commands

The fork's default branch is `hydra-fork` (the `.gitmodules` `branch =`
field is the source of truth). All work happens in `src/llama-cpp/`.

### File the fork issue (do this early — before coding)

```bash
# From the parent repo's working tree:
cd src/llama-cpp

# Sanity: confirm you are on hydra-fork, not a stale remote alias
git remote -v | grep ddvnguyen/llama.cpp
git fetch origin hydra-fork
git rev-parse origin/hydra-fork    # record this — your base

# Create a feature branch for the work
git checkout -b fork/hydra-291-llama-fork-workflow origin/hydra-fork
# (name it fork/hydra-<hydra-issue>-<short-slug> for cross-traceability)

# File the fork-side issue from the shell — issues are now enabled:
gh issue create --repo ddvnguyen/llama.cpp \
  --title "[fork/hydra-291] <short title>" \
  --label "hydra-fork" \
  --body "Tracks the C++ side of ddvnguyen/hydra_vortex#291.
...
"
# Note the new issue number M (e.g. ddvnguyen/llama.cpp#M).
```

Post the fork issue number on the **Hydra issue** as a comment so the
parent-side watchers can see the link without opening the fork:

```bash
gh issue comment 291 --body "Fork-side tracker: ddvnguyen/llama.cpp#M"
```

### Implement and open the fork PR

```bash
# ... edit / build / test in src/llama-cpp ...
cd src/llama-cpp
git add -A
git commit -m "fork: <conventional commit message>

Implements the C++ side of ddvnguyen/hydra_vortex#291.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"

# PUSH FIRST (this is the order-matters rule from 02-implement.md):
git push -u ddvnguyen fork/hydra-291-llama-fork-workflow

# Open the PR targeting hydra-fork
gh pr create --repo ddvnguyen/llama.cpp \
  --base hydra-fork \
  --head fork/hydra-291-llama-fork-workflow \
  --title "fork: <short title> (hydra_vortex #291)" \
  --body "Implements the C++ side of ddvnguyen/hydra_vortex#291.

- Hydra parent issue: ddvnguyen/hydra_vortex#291
- Fork issue: ddvnguyen/llama.cpp#M
- Targets: hydra-fork

## What & why
...

## Test plan
- [ ] cmake --build build_sm120 --target llama-engine
- [ ] cmake --build build_sm60  --target llama-engine
- [ ] (any model-level checks)
"
```

Record the fork PR number (e.g. `ddvnguyen/llama.cpp#PR`).

## Parent-side commands

After the fork PR is **merged** (or at minimum its head SHA is reachable on
`hydra-fork`), bump the parent's submodule pointer.

```bash
# Back in the parent repo's working tree
cd ../..   # back to the hydra_vortex root

# Confirm the SHA the submodule will be pinned to is on hydra-fork:
SHA=$(git -C src/llama-cpp rev-parse origin/hydra-fork)
git ls-remote https://github.com/ddvnguyen/llama.cpp \
  refs/heads/hydra-fork | grep -q "$SHA" \
  || { echo "FATAL: $SHA is not on hydra-fork"; exit 1; }
echo "OK: $SHA is on hydra-fork"

# Stage the submodule pointer bump
git add src/llama-cpp
git diff --cached --submodule=log    # confirm the diff is the new SHA only
git commit -m "chore: bump src/llama-cpp submodule to $SHA (fork#PR: <title>)

Fork PR: ddvnguyen/llama.cpp#PR
Hydra issue: #291

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"

# Open the parent PR with explicit cross-links in the body
gh pr create \
  --title "feat: <hydra-side title> (closes #291)" \
  --body "Closes #291

### Cross-repo links
- Hydra issue: ddvnguyen/hydra_vortex#291
- Fork issue: ddvnguyen/llama.cpp#M
- Fork PR (merged): ddvnguyen/llama.cpp#PR

### Submodule bump
src/llama-cpp: <old-sha> → $SHA

### Verification
- [ ] Unit tests pass (dotnet test)
- [ ] Fork PR's build artifacts (sm120 / sm60) smoke-tested locally
- [ ] Pinned submodule SHA is reachable on hydra-fork:
      git ls-remote https://github.com/ddvnguyen/llama.cpp \\
        refs/heads/hydra-fork | grep $SHA
"
```

## Linking rules — quick reference

| On | Body / comment should reference | Format |
|---|---|---|
| Hydra issue | fork issue + (later) fork PR | `ddvnguyen/llama.cpp#M`, `ddvnguyen/llama.cpp#PR` |
| Fork issue | Hydra issue | `ddvnguyen/hydra_vortex#N` |
| Fork PR | Hydra issue + fork issue | `ddvnguyen/hydra_vortex#N`, `ddvnguyen/llama.cpp#M` |
| Hydra PR | Hydra issue + fork issue + fork PR | `Closes #N`, `ddvnguyen/llama.cpp#M`, `ddvnguyen/llama.cpp#PR` |

GitHub auto-renders cross-repo references — `ddvnguyen/llama.cpp#N` becomes
a link in the Hydra repo and vice versa, and the Project workflow treats
both as work items (the Hydra side is on the board; the fork side is
not, but its number is searchable from the Hydra side).

## What this step is **not**

- **Not a script** — the owner chose doc-only. The `gh` commands above are
  the canonical sequence; the agent runs them. If a future change adds a
  helper script (e.g. `scripts/llama-fork-handoff.sh`), this doc gets
  shortened to point at it.
- **Not a CI check** — there is no automated enforcement that a parent PR's
  submodule bump is paired with an open/merged fork PR. The owner explicitly
  chose doc + reviewer attention over a CI gate. If a future change adds a
  check (e.g. in `.github/workflows/ci.yml`), this doc gets a link to it.
- **Not a substitute for the push-before-PR guard.** That guard
  (`02-implement.md` / `04-commit-pr.md` / `05-deploy.md`) is a hard
  prerequisite for the parent PR. This step is the coordination wrapper
  around it.

## Cross-references

- `02-implement.md` — push-before-PR order; submodule branch tracking.
- `04-commit-pr.md` — pre-PR `git ls-remote` reachability check.
- `05-deploy.md` — step 3 ("Push the fork + bump submodule") is the
  parent-side deploy view of this same flow.
- `07-issue-and-close.md` — when closing out a fork-coordinated task,
  confirm the fork PR is merged (or its SHA is on `hydra-fork`) before
  closing the Hydra issue.

→ Next: `07-issue-and-close.md` (when the parent PR is merged) →
`06-monitoring.md` (if runtime touched) → back to `01-pickup.md`.
