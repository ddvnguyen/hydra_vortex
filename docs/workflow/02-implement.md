# 2. Branch & implement

**Goal:** make the change on a branch, never on `main`.

1. **Branch:**
   - From a GitHub finding: `gh issue develop N --name fix/mN-Psev-seq`
     (e.g. `fix/m2-p2-060`).
   - Other work: `git checkout -b feat/<area>-<short>` (or `chore/…`, `docs/…`,
     `perf/…`) off the latest `main`.
2. **Scope:** follow the milestone doc for the area —
   `docs/milestone-perf.md`, `docs/milestone-3-production.md`, etc. Respect the
   `## Language Decisions`, `## Critical Facts`, and `## Key Design Decisions` in
   `CLAUDE.md` (do not relitigate them).

> ### ⚠️ If you change a submodule (e.g. `src/llama-cpp`), push the fork FIRST
>
> The parent repo's submodule pointer is a commit SHA on the submodule's fork.
> If you bump that pointer to a commit that is **not yet pushed to the fork's
> remote**, the PR is unreviewable: no one can `git submodule update --init` to
> see the C++ changes, fresh clones fail, and the Deploy CI job breaks.
>
> **Order matters — always:**
> 1. Commit the submodule change **inside the submodule's working tree**
>    (e.g. `cd src/llama-cpp && git commit ...`).
> 2. **Push the submodule's branch to its remote** (e.g.
>    `git -C src/llama-cpp push ddvnguyen hydra-fork`).
> 3. **Verify the SHA is reachable from the public remote:**
>    `git ls-remote <fork-url> <branch> | grep <sha>` — must print a line.
> 4. **Then** bump the parent's submodule pointer (`git add src/llama-cpp` in
>    the parent) and create the PR.
>
> If you skip step 2, the PR will be flagged `review-finding` with severity P0
> (see PR #290 for a real example).
>
> **Cross-repo coordination.** If the C++ change adds new RPC opcodes, server
> endpoints, model-arch fixes, or anything else visible to Hydra, the full
> cross-repo flow (Hydra issue → fork issue → fork PR → submodule bump → Hydra
> PR with `Closes #N`) lives in `08-llama-fork.md`. This step (02) handles the
> push-before-PR reachability guard; step 8 handles the issue/PR coordination.
3. **Build / run while developing:** see `DevelopmentRunBook.md`
   ("Quick Start", "Running Tests", "llama-server build").
4. **Track sub-steps with todos**; keep exactly one in-progress.

→ Next: `03-test-verify.md`
