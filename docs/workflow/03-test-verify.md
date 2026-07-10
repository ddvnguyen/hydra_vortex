# 3. Test / verify (before any PR)

**Goal:** prove the change is green locally. Full commands: `DevelopmentRunBook.md`
"Running Tests".

1. **Always (unit):**
   - .NET: `dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/`
2. **If behaviour/runtime changed (E2E):**
   - System / E2E: `pytest tests/system` (mocked first; full-stack needs the live
      stack up — `cd infra && docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up -d`, see `DevelopmentRunBook.md`).
3. **All green is required before opening a PR.** If you cannot run a tier (e.g. the
   GPU stack isn't up), say so explicitly in the PR and note what was/wasn't verified.
4. Builds must be clean (`dotnet build src/Hydra.sln -c Release`); treat new warnings as
   review items.

> **When the user asks for "E2E verify" specifically:** deploy the current
> working-tree/branch code to the live environment (not a merged copy) and
> confirm the change behaves correctly — see `05-deploy.md` for the deploy
> commands and `06-monitoring.md` for what to check afterward. This is a
> verification step, not a merge step: do not run `gh pr merge` (or otherwise
> land the PR) as part of it. PR merges always need the user's explicit
> confirmation or request — see `04-commit-pr.md`.

→ Next: `04-commit-pr.md`
