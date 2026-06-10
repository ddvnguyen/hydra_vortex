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

→ Next: `04-commit-pr.md`
