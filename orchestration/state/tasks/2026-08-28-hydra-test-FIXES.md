# Hydra TEST — 11-issue fix pass 2026-08-28 ~17:00 ICT
Branch: `feat/hydra-test-p100` @ b9466d599..0074f337e (7 draft fix commits on top of 8 scaffold commits, no push)
Worktree: `/mnt/WorkDisk/Workplace/wt-hydra-test`
Base: `origin/epic/697-470-stabilization`

## Per-Fix Verdict

| # | Severity | Issue | Verdict | Commit | File(s):line |
|---|----------|-------|---------|--------|--------------|
| 1 | CRITICAL | Engine image `ubuntu:22.04` has no CUDA stack | **applied** | `b9466d599` | `infra/docker-compose.hydra-test.yml:244-245` `image: nvidia/cuda:12.9.0-runtime-ubuntu22.04` (12.9 = P100 CUDA per `CLAUDE.md: P100 sm_60 CUDA 12.9`; host RTX uses 13.2 but P100 VM is 12.9) |
| 2 | CRITICAL | No `runtime: nvidia` / device passthrough | **applied** | `b9466d599` | `infra/docker-compose.hydra-test.yml:248,293` `runtime: nvidia` for both engines; alt fallback `devices: [/dev/nvidia0, /dev/nvidiactl, /dev/nvidia-uvm]` documented but not needed — P100 VM has `nvidia` runtime per lead note, verify on rig if missing |
| 3 | CRITICAL | `CUDA_VISIBLE_DEVICES=0/1` on single-GPU VM | **applied** | `b9466d599` | `infra/docker-compose.hydra-test.yml:253,299` both now `CUDA_VISIBLE_DEVICES=0`; comment added at `:242-243` and `:288-289` "Single-GPU P100 VM; both engines share the device via mmap page-cache. This is single-GPU shared-VRAM P/D, not true 2-GPU P/D split." |
| 4 | CRITICAL | Engine model 35B-A3B exceeds 16 GB P100 VRAM | **applied** | `b9466d599` | `infra/docker-compose.hydra-test.yml:257,303` `--model /opt/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf` (sha `03b7472…7e8` per minifleet spec `MINIFLEET_MODEL_PATH=$HOME/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf`); bind-mount `${HOME}/hydra-min-test:/opt/hydra-min-test:ro` retained; file existence trusted per minifleet evidence (no direct P100 access from this host) |
| 5 | CRITICAL | `appsettings.Test.json` never loaded (missing `ASPNETCORE_ENVIRONMENT`) | **applied** | `69380f9fe` | `infra/docker-compose.hydra-test.yml:49,113` added `ASPNETCORE_ENVIRONMENT=Test` to both cores; verified `src/core/Hydra.Core/Program.cs:169` uses `WebApplication.CreateSlimBuilder(args)` which auto-loads `appsettings.{env}.json` — no custom builder needed |
| 6 | MEDIUM | `workers-test-a/b.json` referenced but not created | **applied** | `8006a9464` | `infra/hydra-core/config/workers-test-a.json:1` and `workers-test-b.json:1` — minimal 1-worker lists (copy of `infra/hydra-core/config/workers.json:1` shape, trimmed; `llama_url` → `http://localhost:18086/18087`, `rpc_port/llama_rpc_port` → `19513/19514`, `name` test-a/b, `gpu sm_60` preserved) |
| 7 | MEDIUM | `node-test-a/b.yaml` referenced but not created | **applied** | `fbe3c4308` | `infra/hydra-head/config/node-test-a.yaml:1` / `node-test-b.yaml:1` — copied from `infra/hydra-head/config/node-p100-mini.yaml:1`, rewrote `node.name` test-a/b, `llama.port` 18086/18087, `rpc_port` 19513/19514, `llama.params.model` `/opt/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf`, `n-gpu-layers` 16/8, `threads` 3, `ctx-size` 4096 per minifleet topology; `readiness.timeout_sec` 120 |
| 8 | MEDIUM | `localhost/hydra-core:latest` / `localhost/hydra-head:rtx` images not built | **applied** | `5559ef184` | `scripts/hydra-test/up.sh:55-82` idempotent `podman image exists` checks + `podman build -f infra/Dockerfile --target core -t localhost/hydra-core:latest .` and `podman build -f infra/hydra-head/Dockerfile.rtx -t localhost/hydra-head:rtx .` (note: `infra/Dockerfile:18` has only `target: core`; head uses `infra/hydra-head/Dockerfile.rtx:1` — task's `--target head` fallback documented in comments); builds are skipped if tag exists |
| 9 | MEDIUM | `init-hydra-test.sql` `DO $$` fails — `CREATE DATABASE` cannot run in txn | **applied** | `12ac23711` | `infra/sql/init-hydra-test.sql:1` replaced broken `DO $$ ... EXCEPTION WHEN duplicate_database` (36L) with 3-line doc pointing at `up.sh`'s `\gexec` path; `scripts/hydra-test/up.sh:38-63` now does single `\gexec` create + 5s verification loop (`SELECT 1 FROM pg_database WHERE datname='hydra_test'`) and `exit 1` with clear error if DB missing |
| 10 | LOW | `api_key: not-required` string is misleading | **applied** | `0074f337e` | `infra/paseo-providers-hydra-test.yaml:8` `api_key: ""` + comment "Hydra.Core doesn't require auth in test; empty api_key is fine." |
| 11 | LOW | Paseo provider `base_url: localhost:19000` breaks from host (P100 at 192.168.122.21) | **applied** | `0074f337e` | `infra/paseo-providers-hydra-test.yaml:8` `base_url: http://192.168.122.21:19000` + comment "P100 VM is at 192.168.122.21; Paseo agent on the host uses this URL. If the Paseo daemon runs INSIDE the P100 VM, change to http://localhost:19000." |

## Commit SHAs (per-step)

- Fix 1-4 (engine): `b9466d5999c299f2e5a0d6048d6366f52c05ae77` — `draft: fix(hydra-test): engine image, runtime, single-GPU and model (fixes 1-4)`
- Fix 5 (env): `69380f9fea3b491c0cac64ac9b57e23e4bc9b154` — `draft: fix(hydra-test): set ASPNETCORE_ENVIRONMENT=Test for core A/B (fix 5)`
- Fix 6 (workers): `8006a9464ba1982d46c2e61a88250082f1e53ce7` — `draft: fix(hydra-test): add workers-test-a/b.json per-instance configs (fix 6)`
- Fix 7 (nodes): `fbe3c43080045af5f1b2358964ddca30e6744639` — `draft: fix(hydra-test): add node-test-a/b.yaml per-instance head configs (fix 7)`
- Fix 8 (build): `5559ef18460a49f9c7e7958663fbe95d2299b369` — `draft: fix(hydra-test): idempotent podman build step for core/head images (fix 8)`
- Fix 9 (DB): `12ac23711119598a903d6a4b6d35e07c69fa6ba7` — `draft: fix(hydra-test): replace broken DO block with \gexec doc and add DB verify (fix 9)`
- Fix 10-11 (paseo): `0074f337e9b3f311d674e489eb897df5261dc75a` — `draft: fix(hydra-test): paseo provider api_key empty and P100 VM base_url (fixes 10-11)`

## Post-Fix Verification (re-run of lead's checks)

| Check | Result | Evidence |
|-------|--------|----------|
| `HYDRA_HEAD_AUTH_TOKEN=dummy podman compose -f infra/docker-compose.infra.yml -f infra/docker-compose.hydra-test.yml config` | **PASS** (exit 0, valid YAML, all refs resolve) | engines show `image: nvidia/cuda:12.9.0-runtime-ubuntu22.04` + `runtime: nvidia`, `CUDA_VISIBLE_DEVICES=0` both, `--model /opt/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf`, cores show `ASPNETCORE_ENVIRONMENT=Test`, healthchecks intact |
| `bash -n` on 3 scripts | **PASS** | `up.sh` ok, `down.sh` ok, `status.sh` ok |
| `dotnet build src/core/Hydra.Core` | **PASS** | 0 errors, 37 warnings (pre-existing, unchanged) |
| `dotnet test src/core/Tests.AgentWorkflow --filter Workflow=HydraTest` | **PASS (SKIP)** | `Skipped! - Failed: 0, Passed: 0, Skipped: 1` — rig down expected, not a failure |
| `git diff --stat origin/epic/697-470-stabilization..HEAD` | **PASS** | 21 files changed, 1297 insertions (+4 new config files vs scaffold's 15 files; all 11 fixes present, no prod `infra/docker-compose.hydra.yml` or prod `appsettings*.json` touched) |
| Scope discipline | **PASS** | `git diff -- infra/docker-compose.hydra.yml` empty, `git diff -- src/core/Hydra.Core/appsettings.json` empty |

## NEW Issues Discovered (none blocking, for lead awareness)

1. **Head image build dependency** — `infra/hydra-head/Dockerfile.rtx:56` `COPY bin/hydra-head` requires `bin/hydra-head` built from Go. `up.sh` now auto-builds it via `go build -o bin/hydra-head ./src/head/...` if missing (`$HOME/go-sdk/go/bin/go` per `CLAUDE.md: go is NOT in default PATH`). If the binary is stale, the `podman image exists` short-circuit will skip rebuild; bump by `podman rmi localhost/hydra-head:rtx` if needed. Not a new bug, but head build is heavier than core.

2. **Nvidia runtime fallback not exercised** — `runtime: nvidia` is the primary (per task). If the P100 VM's container runtime is `crun` without `nvidia` runtime patched, the lead may need to fall back to `devices: [/dev/nvidia0, /dev/nvidiactl, /dev/nvidia-uvm]` + `capabilities`. The compose is ready to swap; verify with `podman info | grep -i nvidia && podman run --rm --runtime nvidia nvidia/cuda:12.9.0-runtime-ubuntu22.04 nvidia-smi`.

3. **Model file existence assumed** — `/opt/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf` inside container maps to host `~/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf` (minifleet spec). No direct P100 host `ls` from this worktree; trusted per minifleet evidence. Lead to `ls -lh ~/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf` on VM before `up.sh`.

4. **Workers/Node configs are minimal stubs** — they parse and pass `CoordinatorConfig.Validate()` (checked `WorkerConfig: `rpm_port` >0, `llama_url` valid), but routing-level integration (which model alias maps to test engines, whether cores should use `HYDRA_COORD_MULTI_ENGINE_POLICY`) is not yet wired. The scaffold's `docs/hydra-test.md` already notes model alias `minicpm5-1b` vs `models.json` moemap; this is deferred to test-model alias finalization (#708 follow-up).

## Assumptions

- P100 CUDA is 12.9 (`nvidia/cuda:12.9.0-runtime-ubuntu22.04`); host RTX is 13.2 but P100 VM sm_60 needs 12.9 per `infra/hydra-head/config/node-p100*.yaml` context and `CLAUDE.md` hardware table.
- `~/hydra-min-test` on the P100 VM already contains both the `llama-engine` binary and `Qwen3.5-9B-Q4_K_M.gguf` (minifleet proof run); no re-download needed.
- `mmap` page-cache sharing is sufficient for single-GPU VRAM sharing (both engines `mmap` the same GGUF); host has enough page-cache for ~5.5 GB Q4 plus ~3 GB VRAM per engine (ngl 16+8).
- `ASPNETCORE_ENVIRONMENT=Test` + `CreateSlimBuilder` is sufficient; no custom `AddJsonFile` needed (verified `Program.cs:169`).
- `infra/Dockerfile` target `head` does not exist; head image is built from `infra/hydra-head/Dockerfile.rtx` — documented in `up.sh` comments per task's "or whatever the actual target name is" clause.

## Risks / Follow-up

- Do NOT push, Do NOT open PR, Do NOT `podman compose up` — per task constraints. Lead zero-trust verifies on rig.
- If `nvidia` runtime is missing on P100 VM, swap to device list (1-line compose edit) and re-verify `podman compose config`.
- If the P100 host stores the 9B model at `/mnt/SSD/models/Qwen3.5-9B-Q4_K_M.gguf` instead of `~/hydra-min-test/`, the container `--model` path should be `/models/Qwen3.5-9B-Q4_K_M.gguf` (already bind-mounted via `/mnt/SSD:/models:ro`). A 1-line fix can switch; current path matches minifleet's `$HOME` evidence.
