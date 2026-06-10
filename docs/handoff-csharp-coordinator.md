# Hydra Coordinator — C# Migration Handoff

**Status: COMPLETED** — merged via PR #203 (2026-06). The Python coordinator is removed;
Agent services are merged into Hydra.Core. Hydra.Core is the single C# binary handling
HTTP API, Store RPC, routing, and llama-server communication. This file is retained for
historical context of the transition.

**Original Date:** 2026-06-07
**Original Branch:** `fix/m0-p0-store-atomic` (commit `c20ff70`)

---

## What We Did (completed)

### Goal
Move coordinator logic from Python to C# Hydra.Store — making the Store the "single source of truth" (brain) of the system. Python layer becomes thin OpenAI-compatible HTTP proxy (or removed entirely).
**Outcome:** Full transition completed. Hydra.Core is the single binary. Python coordinator and Agent services are removed.

### Architecture (final, simplified in PR #203)
```
Hydra.Core/                     (single C# binary)
├── Models/
│   ├── WorkerConfig.cs         ← Name, LlamaUrl, LlamaRpcPort, WorkerType, etc.
│   └── WorkItem.cs             ← WorkItemState enum, WorkItem class
├── Repositories/
│   ├── IRepositories.cs        ← ISessionLedger, IWorkerTracker
│   └── RepositoriesImpl.cs     ← SessionLedger, WorkerTracker
├── Services/
│   ├── IServices.cs            ← IWorkerScheduler, IHealthMonitorService, ICompletionProxyService
│   ├── WorkerSchedulerService.cs ← Channel<WorkItem> dispatcher, 4 routing paths, state machine
│   ├── HealthMonitorService.cs ← BackgroundService, llama RPC health polling
│   ├── CompletionProxyService.cs ← HttpClient proxy to llama-server
│   └── Router.cs               ← Static: derive session, estimate tokens, pick workers
├── Controllers/
│   └── ApiControllers.cs       ← [ApiController]: CompletionsController, HealthController, SessionsController
├── Store/
│   ├── StorageEngine.cs         ← file I/O + sendfile
│   ├── StoreServer.cs           ← RPC dispatch + chunked ops
│   ├── ChunkEngine.cs           ← chunk splitting, SHA-256, DiffPlan
│   └── ChunkStore.cs            ← content-addressed storage, dedup, GC
├── Extensions/
│   └── ServiceExtensions.cs    ← services.AddCore(config) DI
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

## Current State (final)

### What works (all resolved in PR #203)
1. **Build**: `dotnet build` — 0W/0E
2. **Tests**: All C# tests pass
3. **Docker image**: `localhost/hydra-core:latest` builds successfully
4. **Health endpoint**: `:9000/health` responds
5. **Completion API**: `/v1/chat/completions` routes requests directly to llama-servers
6. **Worker config**: Configured via `WorkerConfig` C# class, loaded from env/file
7. **State ops**: Hydra.Core contacts llama-servers directly via RPC (ports 9503/9502)
8. **Agent services removed**: No more hydra-agent-rtx or hydra-agent-p100 containers
9. **Python coordinator removed**: All routing logic now in C#

---

## How to Continue (post-PR #203)

The transition is complete. For ongoing development:

### Hydra.Core build & test
```bash
# Build
dotnet build src/Hydra.sln -c Release

# Docker build
podman build -f infra/Dockerfile -t localhost/hydra-core:latest --target hydra-core .

# Run
podman run -d --pod=hydra-core --name=hydra-core --replace \
  --tmpfs=/mnt/llm-ram:size=30G \
  -e HYDRA_CORE_PORT=9000 \
  -e HYDRA_STORE_DIR=/mnt/llm-ram/store \
  localhost/hydra-core:latest
```

---

## Remaining TODO (post-PR #203)

| Priority | Task | Status |
|:---:|------|------|
| ✅ | Migrate coordinator logic to C# | Done (PR #203) |
| ✅ | Remove Python coordinator | Done (PR #203) |
| ✅ | Merge Agent services into Hydra.Core | Done (PR #203) |
| ✅ | Wire Prometheus metrics | Done |
| P1 | Add Langfuse OTEL integration | Pending for M5 |
| P1 | Session eviction loop | Pending |
| P3 | NVMe write-behind persistence | M3.1 |

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
services.AddCore(config)
  ├── SessionLedger : ISessionLedger (Singleton)
  ├── WorkerTracker : IWorkerTracker (Singleton)
  ├── CompletionProxyService : ICompletionProxyService (Singleton)
  ├── WorkerSchedulerService : IWorkerScheduler (Singleton)
  ├── HealthMonitorService : IHealthMonitorService (Singleton + HostedService)
  └── Controllers (AddControllers)
```

---

## Build & Test Commands (post-PR #203)

```bash
# C# build
dotnet build src/Hydra.sln -c Release

# C# tests
dotnet test src/core/Tests.Shared/
dotnet test src/core/Tests.Core/

# Docker build
podman build -f infra/Dockerfile -t localhost/hydra-core:latest --target hydra-core .

# System tests
pytest tests/system/
```
