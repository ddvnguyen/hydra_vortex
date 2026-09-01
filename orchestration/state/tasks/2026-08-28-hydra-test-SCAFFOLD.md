# Hydra TEST Scaffold — 2026-08-28

Branch: `feat/hydra-test-p100` @ b2b21e343..d4c490753 (7 commits, no push)
Worktree: `/mnt/WorkDisk/Workplace/wt-hydra-test`
Gate: T2+N1+A1+L2, 11 owner answers applied (issue #708 comment 5449915490).

## Per-Step Completion

| Step | Deliverable | Status | Commit | File(s) |
|------|-------------|--------|--------|---------|
| 1 | `appsettings.Test.json` T2 per-instance | GREEN | 1b54f1e0f | `src/core/Hydra.Core/appsettings.Test.json` (26L), `src/core/Hydra.Core/Configuration/HydraTestConfig.cs` (34L), `src/core/Hydra.Core/Program.cs` (+6L) |
| 2 | `init-hydra-test.sql` | GREEN | 2acf4dcb0 | `infra/sql/init-hydra-test.sql` (36L) |
| 3 | `infra/docker-compose.hydra-test.yml` | GREEN | 24672e8b8 | `infra/docker-compose.hydra-test.yml` (329L) |
| 4 | `scripts/hydra-test/{up,down,status}.sh` | GREEN | a959af708 | `scripts/hydra-test/up.sh` (109L), `down.sh` (44L), `status.sh` (54L) — chmod +x, bash -n ok |
| 5 | `Tests.AgentWorkflow` xUnit | GREEN | 13824fd93 | `src/core/Tests.AgentWorkflow/` (csproj 22L, GlobalUsings 1L, HydraTestWorkflowTests.cs 150L) — `dotnet test --filter Workflow=HydraTest` SKIPPED (rig down, expected), would be green when rig up |
| 6 | `docs/hydra-test.md` + DevelopmentRunBook | GREEN | cdb922f8c | `docs/hydra-test.md` (122L), `DevelopmentRunBook.md` (+2L service map) |
| 7 | `infra/paseo-providers-hydra-test.yaml` | GREEN | d4c490753 | `infra/paseo-providers-hydra-test.yaml` (15L) |

## File List (15 files, 967 insertions)

- `src/core/Hydra.Core/appsettings.Test.json` — ConnectionStrings.Postgres=hydra_test, EngineStore dirs, Logging, Routing.AllowedInstanceIds=[A,B], HydraTest gate notes
- `src/core/Hydra.Core/Configuration/HydraTestConfig.cs` — IsTestInstance/ValidateIfTestInstance, HYDRA_INSTANCE=test + HYDRA_INSTANCE_ID=A|B validation
- `src/core/Hydra.Core/Program.cs` — gate call after Log.Logger init, prod no-op when HYDRA_INSTANCE unset
- `infra/sql/init-hydra-test.sql` — idempotent DO block with dblink fallback, not auto-mounted (up.sh applies it)
- `infra/docker-compose.hydra-test.yml` — 6 services (core-a/b, head-a/b, engine-a/b), +10000 ports, host network, x-podman.in_pod false, health checks, LD_LIBRARY_PATH=$HOME/hydra-min-test
- `scripts/hydra-test/up.sh` — idempotent up + DB init + 120s health poll + URL print
- `scripts/hydra-test/down.sh` — compose down + orphan rm + pgrep check for 18086/18087
- `scripts/hydra-test/status.sh` — podman inspect + curl per service, overall verdict
- `src/core/Tests.AgentWorkflow/Tests.AgentWorkflow.csproj` — xunit + Xunit.SkippableFact
- `src/core/Tests.AgentWorkflow/GlobalUsings.cs`
- `src/core/Tests.AgentWorkflow/HydraTestWorkflowTests.cs` — [Trait Workflow=HydraTest], 10 concurrent (5+5), asserts 200 + tokens>0 + no 5xx, prod :9000 metric snapshot skip-if-not-exposed
- `src/Hydra.sln` — added Tests.AgentWorkflow
- `docs/hydra-test.md` — 8 sections: Overview, Port plan, DB plan, Env-var matrix, Bring up/down, Paseo routing, VM hygiene, Blast-radius
- `DevelopmentRunBook.md` — appended TEST A/B rows to Service Map
- `infra/paseo-providers-hydra-test.yaml` — id hydra-test, openai-compat, base_url :19000, model minicpm5-1b, api_key not-required

## Diff Scope

```
15 files changed, 967 insertions(+)
git diff origin/epic/697-470-stabilization..HEAD --stat
```

Line counts: compose 329, test 150, doc 122, up.sh 109, others 36-54.

## Verification

- `dotnet build src/core/Hydra.Core` — succeeded (0 errors, 38 warnings pre-existing)
- `dotnet test src/core/Tests.AgentWorkflow --filter Workflow=HydraTest` — Skipped (rig not up, expected). Green when rig up.
- `HYDRA_HEAD_AUTH_TOKEN=dummy podman compose -f infra/docker-compose.hydra-test.yml config` — valid
- `bash -n` on all 3 scripts — ok
- No push, no PR, no prod compose touch, no #703 lane touch.

## Issues / Caveats

- `minicpm5-1b` not in current `infra/hydra-core/config/models.json` (has moe-35b/dens-27b aliases); provider yaml notes to check models.json, default may need updating when test model alias is finalized.
- `init-hydra-test.sql` CREATE DATABASE inside DO is Postgres-version-sensitive; up.sh has `\gexec` fallback path.
- Engine services use `ubuntu:22.04` placeholder + host bind `~/hydra-min-test`; real P100 engines may need nvidia device + CUDA_VISIBLE_DEVICES tuning per host.
- Workers configs `workers-test-a.json` / `workers-test-b.json` referenced in compose but not yet created (expected — lead to wire when engine topology is live).
- No `podman compose up` run per constraints; lead zero-trust-verifies on rig.

## Links

- `src/core/Hydra.Core/appsettings.Test.json:1`
- `src/core/Hydra.Core/Configuration/HydraTestConfig.cs:1`
- `infra/sql/init-hydra-test.sql:1`
- `infra/docker-compose.hydra-test.yml:1`
- `scripts/hydra-test/up.sh:1`
- `scripts/hydra-test/down.sh:1`
- `scripts/hydra-test/status.sh:1`
- `src/core/Tests.AgentWorkflow/HydraTestWorkflowTests.cs:1`
- `docs/hydra-test.md:1`
- `infra/paseo-providers-hydra-test.yaml:1`
