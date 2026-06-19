## Summary

<!-- What does this PR do and why? -->

## Changes

<!-- Brief list of what changed -->

## Closes

<!-- Link every issue this PR resolves. Use one of these keywords per line:
     Closes #XX  |  Fixes #XX  |  Resolves #XX
     GitHub will auto-close the issue when this PR merges into main.
     If this PR does not address any open issue, write "N/A" here. -->

- Closes #

## Test plan

<!-- How was this tested? e.g. unit tests, integration tests, manual steps -->

- [ ] Unit tests pass (`dotnet test` / `pytest`)
- [ ] Integration tests pass
- [ ] System tests pass (if applicable)

## Submodule / fork changes (if applicable)

<!-- Skip this section if this PR does NOT touch src/llama-cpp or any other
     git submodule. If it does, every box must be checked. PRs with a
     dangling submodule SHA (parent commit points at a fork commit that is
     not yet pushed) are unreviewable and will be blocked at review. See
     docs/workflow/04-commit-pr.md for the verification command. -->

- [ ] If `src/llama-cpp` (or any submodule) was changed: the submodule's
      branch was pushed to its public remote **before** this PR was opened
- [ ] Pinned submodule SHA is verified reachable from the submodule's
      public remote: `git ls-remote <fork-url> <branch> | grep <sha>`
- [ ] If the C++ side was changed: the build was run locally
      (`cmake --build build_sm120 --target llama-engine`, etc.) and the
      `tools/server/server-task.h` / `server-rpc.h` / `server-context.cpp`
      diffs were reviewed against the existing struct layout
