# Hydra Coordinator — C# Migration Handoff

**Date:** 2026-06-07
**Branch:** `fix/m0-p0-store-atomic` (commit `c20ff70`)

---

## What We Did

### Goal
Move coordinator logic from Python to C# Hydra.Store — making the Store the "single source of truth" (brain) of the system. Python layer becomes thin OpenAI-compatible HTTP proxy (or removed entirely).

### Architecture
```
Hydra.Store/
├── Models/
│   ├── CoordinatorModels.cs    ← WorkerConfig, CoordinatorConfig, SessionEntry, NodeInfo, SlotInfo
│   └── WorkItem.cs             ← WorkItemState enum (18 states), WorkItem class
├── Repositories/
│   ├── IRepositories.cs        ← ISessionLedger, IWorkerTracker
│   └── RepositoriesImpl.cs     ← SessionLedger, WorkerTracker (ConcurrentDictionary + lock)
├── Services/
│   ├── IServices.cs            ← IWorkerScheduler, IHealthMonitorService, ICompletionProxyService
│   ├── WorkerSchedulerService.cs ← Channel<WorkItem> dispatcher, 4 routing paths, state machine
│   ├── HealthMonitorService.cs ← BackgroundService, agent RPC health polling every 20s
│   ├── CompletionProxyService.cs ← HttpClient proxy to llama-server
│   └── Router.cs               ← Static: derive session, estimate tokens, pick workers
├── Controllers/
│   └── CoordinatorControllers.cs ← [ApiController]: CompletionsController, HealthController, SessionsController
├── Extensions/
│   └── CoordinatorServiceExtensions.cs ← services.AddCoordinator(config) DI
└── Program.cs                  ← WebApplication.CreateSlimBuilder + DI wiring
```

### Python fixes (also in this branch)
| File | Change |
|------|--------|
| `session_table.py` | `slot_freed` flag instead of `slot_id=None` |
| `scheduler.py` | `finally` block, `_wait_for_wakeup` re-check, `id_slot` capture, background saves, cancel handling |
| `state_manager.py` | `slot_freed` check in `save_session` |
| `StateHandler.cs` (Agent) | Local cache re-fetch on chunk eviction |

### Tests
- **50 C# unit tests** pass (0W/0E build) — WorkerTracker, SessionLedger, Router, CoordinatorConfig, WorkItem
- **99 Python tests** pass (worktree, after `cp` to build tree)

---

## Current State

### What works
1. **Build**: `dotnet build` — 0W/0E
2. **Tests**: 50/50 C# tests pass
3. **Docker image**: `localhost/hydra-store:latest` builds successfully
4. **Coordinator starts**: Health endpoint at `:9000` responds `{"status":"..."}`
5. **Completion API**: `/v1/chat/completions` accepts requests, enqueues to Channel
6. **Hardcoded workers** in `CoordinatorModels.LoadWorkers()` fallback

### What doesn't work yet
1. **`HYDRA_COORD_WORKERS` env var not reaching container** — podman `-e` with JSON values fails silently. Workaround: hardcoded fallback in `LoadWorkers()`. Fix needed: use mounted config file or podman quadlet with proper env var escaping.

2. **Agents unstable** — agents were re-created during testing but may be missing. The pod `hydra-core` needs both agents running.

3. **Health monitor polling** — needs agents to be healthy before scheduler dispatches requests. The first request returned 499 after 60s timeout because both nodes were unhealthy.

4. **Health monitor logging** — no health poll output visible. May need Serilog config fix or the BackgroundService isn't starting properly.

5. **End-to-end not tested** — no successful completion request through the C# coordinator yet.

---

## How to Continue

### Fix the env var issue
The root problem: `HYDRA_COORD_WORKERS` env var is not reaching the container. Tests confirmed `workersJsonLen=0` despite `-e` flag.

**Fix options (try in order):**

1. **Use quadlet env file** — update `infra/quadlets/hydra-store.container` to include coordinator env vars:
```
EnvironmentFile=%h/.config/containers/systemd/hydra-store.env
```
And create `~/.config/containers/systemd/hydra-store.env` with:
```
HYDRA_COORD_ENABLED=true
HYDRA_COORD_PORT=9000
HYDRA_COORD_WORKERS=[...JSON...]
```

2. **Mount workers JSON as file** and set `HYDRA_COORD_CONFIG_FILE=/etc/hydra/workers.json`

3. **Keep hardcoded fallback** in `LoadWorkers()` — works for testing

### Verify agents are running
```bash
podman ps --filter name=agent
# Should show hydra-agent-rtx and hydra-agent-p100
```

If missing:
```bash
podman run -d --pod=hydra-core --name=hydra-agent-rtx --replace \
  -e HYDRA_AGENT_HOST=0.0.0.0 -e HYDRA_AGENT_PORT=9601 \
  -e HYDRA_AGENT_NODE_NAME=rtx \
  -e HYDRA_AGENT_LLAMA_URL=http://localhost:8080 \
  -e HYDRA_AGENT_STORE_HOST=localhost -e HYDRA_AGENT_STORE_PORT=9500 \
  localhost/hydra-agent:latest

podman run -d --pod=hydra-core --name=hydra-agent-p100 --replace \
  -e HYDRA_AGENT_HOST=0.0.0.0 -e HYDRA_AGENT_PORT=9602 \
  -e HYDRA_AGENT_NODE_NAME=p100 \
  -e HYDRA_AGENT_LLAMA_URL=http://192.168.122.21:8086 \
  -e HYDRA_AGENT_STORE_HOST=localhost -e HYDRA_AGENT_STORE_PORT=9500 \
  localhost/hydra-agent:latest
```

### Deploy and test
```bash
# Build
podman build -f infra/Dockerfile -t localhost/hydra-store:latest --target store .

# Stop old, start new
podman rm -f hydra-store
podman run -d --pod=hydra-core --name=hydra-store --replace \
  --tmpfs=/mnt/llm-ram:size=30G \
  -e HYDRA_STORE_HOST=0.0.0.0 -e HYDRA_STORE_PORT=9500 \
  -e HYDRA_STORE_DIR=/mnt/llm-ram/store -e HYDRA_STORE_DEBUG_PORT=9501 \
  -e 'HYDRA_STORE_PG_CONN=Host=localhost;Database=hydra_store;Username=hydra;Password=hydra' \
  -e HYDRA_STORE_BACKUP_DIR=/mnt/SSD/hydra-backup \
  -e HYDRA_COORD_ENABLED=true -e HYDRA_COORD_PORT=9000 \
  localhost/hydra-store:latest

# Wait for health
sleep 30
curl http://localhost:9000/health
# Should show nodes healthy

# Test completion
curl -X POST http://localhost:9000/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"messages":[{"role":"user","content":"hi"}],"max_tokens":5,"temperature":0}'
```

### Debug health monitor
If nodes stay unhealthy, check:
```bash
podman logs hydra-store | grep -E 'health|error|fail|Rpc'
```
The health monitor polls agents via RPC on ports 9601/9602. If agents are unreachable, they stay unhealthy.

### Add logging
The Serilog `Console.Error.WriteLine("[BOOT]...")` pattern works. Add more boot markers around health monitor and scheduler initialization for visibility.

---

## Remaining TODO

| Priority | Task | File |
|:---:|------|------|
| P0 | Fix `HYDRA_COORD_WORKERS` env var | `CoordinatorModels.cs`, quadlet config |
| P0 | Test end-to-end completion | Integration test |
| P1 | Wire Prometheus metrics to state handlers | `WorkerSchedulerService.cs` |
| P1 | Add Langfuse OTEL integration | `Program.cs` + NuGet |
| P1 | Session eviction loop | `Program.cs` (currently missing after refactor) |
| P2 | Remove Python coordinator | Delete `src/coordinator/`, update infra |
| P2 | Port remaining 49 Python tests to C# | `Tests.Store/` |
| P3 | Move hardcoded workers to env | `CoordinatorModels.cs` |

---

## Key Code Paths

**Request lifecyle:**
```
POST /v1/chat/completions → CompletionsController → WorkerSchedulerService.SubmitAsync()
  → Channel.Writer.WriteAsync(item) → RunAsync consumer loop → ProcessAsync()
  → DispatchAsync() → Handlers → Finalize()
```

**Scheduler state machine:**
```
PENDING → RouteDecision → WaitingPrefill → ModelLoadPrefill → PrefixRestore
→ Prefill → SaveKv → SaveDone → MarkEvicted → PickDecode → WaitingDecode
→ ModelLoadDecode → RestoreKv → Decode → BgSave → DONE
```

**DI registration:**
```
services.AddCoordinator(config)
  ├── SessionLedger : ISessionLedger (Singleton)
  ├── WorkerTracker : IWorkerTracker (Singleton)
  ├── CompletionProxyService : ICompletionProxyService (Singleton)
  ├── WorkerSchedulerService : IWorkerScheduler (Singleton)
  ├── HealthMonitorService : IHealthMonitorService (Singleton + HostedService)
  └── Controllers (AddControllers)
```

---

## Build & Test Commands

```bash
# C# build
dotnet build src/core/Hydra.Store/Hydra.Store.csproj -c Release

# C# tests (50 tests)
dotnet test src/core/Tests.Store/Tests.Store.csproj --filter "WorkerTracker|SessionLedger|Router|CoordinatorConfig|WorkItem"

# Python tests (worktree)
python3 -m pytest tests/coordinator/ -x -q --ignore=tests/coordinator/test_health.py

# Docker build (from build tree)
podman build -f infra/Dockerfile -t localhost/hydra-store:latest --target store /mnt/WorkDisk/Workplace/hydra_vortex

# Sync worktree → build tree (Python files only)
cp src/coordinator/*.py /mnt/WorkDisk/Workplace/hydra_vortex/src/coordinator/
```
