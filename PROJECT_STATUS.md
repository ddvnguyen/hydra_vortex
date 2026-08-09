# Hydra — Project Status

> **Agent rule:** When code changes land (PR merged), update this file to reflect
> the new state. Keep milestones, verified facts, and implementation status in
> sync with the actual codebase. See `CLAUDE.md` for the full rule.

## Vision
High-throughput multi-GPU LLM inference system that manages KV cache state
across heterogeneous GPU nodes, enabling session migration without re-prefill.

## Architecture
```
		Clients (Cline, OpenWebUI, curl)
		OpenAI-compatible HTTP
					│ 
					▼
  ┌──────────────────────────────┐
  │  Hydra.Core :9000            │  C# / .NET 10
  │  Single binary: HTTP API     │  System.IO.Pipelines
  │  + Store RPC (:9500)         │  Socket.SendFileAsync
  │  + embedded Coordinator      │
  └──────┬─────────────┬─────────┘
         │ HTTP         │ HTTP
         ▼              ▼
  ┌──────────┐    ┌──────────┐
  │ Hydra    │    │ Hydra    │  Go node agent
  │ Head RTX │    │ Head P100│  4-service mgmt
  │ (container)  │ (VM systemd)
  │  │ llama :8080  │  │ llama :8086  C++ fork
  │  │ node_exp     │  │ node_exp
  │  │ nvidia_exp   │  │ nvidia_exp
  │  │ promtail     │  │ promtail
  │  │ RPC   │    │  │ RPC   │
  │  ▼       │    │  ▼       │
  └────┬─────┘    └────┬─────┘
       │ StateGet/Put  │
       └───────┬───────┘
               ▼
  ┌──────────────────────────────┐
  │  Store RPC :9500 + tmpfs     │
  │  KV state chunks             │
  │  Content-addressed (M2)      │
  │  /mnt/llm-ram/store/         │
  └──────────────────────────────┘
```

## Language Decisions (final)
| Component   | Language       | Reason                                            |
|-------------|----------------|---------------------------------------------------|
| Hydra.Core  | C# / .NET 10   | System.IO.Pipelines, Socket.SendFileAsync, team   |
| Hydra Head  | Go             | Single binary, process mgmt, OCI pull, 4-service  |
| llama-engine| C++ fork       | +3 streaming endpoints, COMBINED-mode filter      |

## Architecture Notes
Hydra.Core is a single C# binary with an embedded coordinator. It contacts
llama-engines directly via HTTP (through Hydra Head's process management). KV state ops use
binary RPC (StateGet/StatePut) directly to llama-engine's hydra RPC port (RTX :9503,
P100 :9502). Store RPC (Put/Get) is internal to Hydra.Core, backed by tmpfs.

Hydra Head is a Go node agent that manages 4 sub-services per GPU node:
llama-engine, node_exporter, nvidia_exporter, promtail. It handles binary deployment
via OCI registry (ghcr.io) with 2-layer YAML config.

## Current Implementation Status

### Routing & Model Selection
| Component | Status | Location |
|-----------|--------|----------|
| `AutoRouter` | ✅ Implemented (PR #443) | `src/core/Hydra.Core/Services/AutoRouter.cs` |
| `ModelConfigLoader` | ✅ Implemented | `src/core/Hydra.Core/Services/ModelConfigLoader.cs` |
| `models.json` config | ✅ Implemented | `infra/hydra-core/config/models.json` |
| `MultiEngineRouter` | ⚠️ Obsolete (kept for backward compat) | `src/core/Hydra.Core/Services/MultiEngineRouter.cs` |
| `Router.PickBest*` | ⚠️ Obsolete (replaced by AutoRouter) | `src/core/Hydra.Core/Services/Router.cs` |

### Engine Config Push
| Component | Status | Location |
|-----------|--------|----------|
| `hydra_config` on PREFILL | ✅ Implemented (#481 Phase 2b, #487) | `src/core/Hydra.Core/Services/WorkerSchedulerService.cs:1209` |
| `EngineConfigApplier` | ❌ Deleted (PR #488) | — superseded by the `hydra_config` PREFILL path above |
| `0x40 EngineConfigure` RPC | ✅ Implemented | `src/core/Hydra.Shared/Protocol.cs` (OpCode 0x40) |
| `0x44 SET_EXPERT_MODE` | ✅ Implemented | COMBINED mode activation |
| `0x46 EnginePipelineAttach` | ✅ Implemented | PIPELINE mode activation |

### Scheduler Rewrite Epic (#591)
Event-driven, slot-bounded rewrite of `WorkerSchedulerService` (event-loop executor +
fluent-DSL state machine + differential parity harness). Branch: `epic/591-rewrite-worker-scheduler`.
| Component | Status | Location |
|-----------|--------|----------|
| `Hydra.StateMachine` DSL framework | ✅ Implemented (epic #591, 23 unit tests) | `src/core/Hydra.StateMachine/` |
| `Tests.StateMachine` | ✅ Implemented | `src/core/Tests.StateMachine/` |
| Differential/contract harness (WP0) | ✅ Implemented (21 golden scenarios, lease invariants, route matrix; 500/500 Tests.Core green) | `src/core/Tests.Core/Harness/` |
| `Hydra.Core.Scheduling` executor core (WP1) | ✅ Implemented (SlotPool, PriorityWaiterQueue, MailboxExecutor, RpcConnectionPool, TimerWheel, OffloadPool; 75 tests) | `src/core/Hydra.Core.Scheduling/` + `src/core/Tests.Core.Scheduling/` |
| `WorkerSchedulerV2` (WP2, SOLID) | ✅ Implemented — separate class on `IWorkerScheduler`; DI A/B toggle `HYDRA_SCHEDULER_IMPL=legacy\|v2` (default legacy); DSL machine + phase handlers + lease-managed concurrency; 13 tests | `src/core/Hydra.Core/Services/SchedulerV2/` + `src/core/Tests.Core/SchedulerV2Tests/` |
| v2 behavior parity | ⏳ In progress (WP3 — scoped to **hydra model rules**, not legacy byte-parity; legacy-mode `cold_atomic_http` excluded by contract) | — |
| v2 hydra-model rule evaluation | ✅ Implemented — classifier + route planner validated against the rules of models and GPU workers (atomic/prefill split, COMBINED capability, warm affinity, capacity); 8 tests | `src/core/Tests.Core/SchedulerV2Tests/V2HydraModelRuleTests.cs` |
| Differential gate (WP3) | ✅ Implemented — runs the catalog against v2 via `V2ScenarioDriver`, diffs vs legacy goldens, prints parity matrix (legacy-mode scenarios skipped) | `src/core/Tests.Core/Harness/DifferentialGateTests.cs`, `IScenarioDriver.cs`, `V2ScenarioDriver.cs` |
| Hydra.Core v2 integration (toggle + strangler swap) | ⏳ Planned (WP2/WP3) | — |

### Model Config (models.json)
| Model Alias | Mode | GPUs | Status |
|-------------|------|------|--------|
| `moe-35b-solo` | SOLO | RTX 5060 Ti | ✅ Production |
| `moe-35b-pd` | P/D split | RTX prefill + P100 decode | ✅ Production |
| `dense-27b-combined` | COMBINED layer-split | RTX 5060 Ti + RTX 3060 | ⚠️ Swap fixed (PR #537); decode blocked by fork KV-restore (#78) |

### AutoRouter Algorithm (4-step)
1. **STEP 0: Warm Affinity** — reuse existing KV session (highest priority)
2. **STEP 1: Candidate Filtering** — filter models by token count, context, health
3. **STEP 2: Hardware Feasibility** — match GPU requirements (VRAM, compute, capabilities)
4. **STEP 3: Swap-Cost Preference** — pick best model by quality tier and load time
5. **STEP 4: Build Worker Plan** — select head + peer/decode workers

### What's NOT Implemented (and not needed)
- **`ProfileSwitcher`** — NOT needed. llama-engine handles model switching internally.
  Hydra.Core sends config via 0x40 EngineConfigure; the engine decides when to reload.
- **`WorkerSchedulerService.SendEngineConfig`** — replaced by the `hydra_config` dict
  injected into the PREFILL request body (`WorkerSchedulerService.cs:1209`, #481 Phase 2b)
- **`HydraEngineClient.SetEngineConfigAsync`** — replaced by `EngineConfigureAsync`

## Worker Node Model

Each GPU node is managed by Hydra Head with these characteristics:

| Node | Mode | HW | Sub-services |
|------|------|------|-------------|
| RTX | container | 5060 Ti 16 GB sm_120 | llama + promtail (host exporters stay in infra-host pod) |
| RTX 3060 | container | 3060 12 GB sm_86 | COMBINED peer (ggml-RPC :9504) |
| P100 | VM systemd | Tesla P100 16 GB sm_60 | llama + node_exporter + nvidia_exporter + promtail |

Coordinator worker config:
| Field | Default | Meaning |
|---|---|---|
| `worker_type` | `3` | `3`=both, `2`=decode-only, `1`=prefill-only |
| `prefill_priority` | `1` | Lower preferred for prefill |
| `decode_priority` | `1` | Lower preferred for decode |
| `decode_speed_tps` | `30.0` | Estimated decode tok/s |
| `combined_capable` | `false` | Whether worker can participate in COMBINED mode |

**Run modes** (`HYDRA_COORD_RUN_MODE`):
- `fast` (default) — session affinity; one GPU handles both prefill and decode per session
- `concurrency` — P/D disaggregation: prefill on RTX, KV saved to Store, decode on P100

**COMBINED engine mode** (5060 Ti + 3060):
- `COMBINED-OT` (expert-split, MoE default) — expert tensors route to peer via RPC
- `COMBINED-static` (layer-split, Dense profile) — layer-split across GPUs
- Switch: `bash scripts/set-profile.sh {moe|dense}`

See `docs/architecture.md` for the 4-tier routing algorithm and session lifecycle detail.

### Hydra Head supervision (event-driven, no HTTP polling)
Hydra Head is the parent of llama-engine, so it supervises from the child's own
signals instead of HTTP-polling it:

| Signal | Source | Mechanism |
|--------|--------|-----------|
| Liveness | child exit event | `cmd.Wait()` → backoff restart (long-standing) |
| Readiness | child stdout sentinel | `childWriter` onLine hook matches lifecycle lines (`server is listening on` / `router server is listening on` / `model loaded`) → `StateReady` |
| Miss-deadline | readiness timeout | no sentinel within `readiness.timeout_sec` → `StateSuspect` (started, not ready); **no restart** on timeout — slow model load is legitimate, crashes come via the exit event |

The periodic HTTP `/slots` health poll was removed (issue #538). The head never
wakes llama-engine with health probes. `/status` returns 503 until llama is
READY (real readiness gate for `wait-for-head.sh`); `/health` stays 200 on
liveness for the pod healthcheck and deploy scripts, reporting `ready` in the
body.

## Tech Stack Detail
| Concern          | Hydra.Core (C#)    |
|------------------|---------------------|
| Async runtime    | async/await + IOCP  |
| Binary protocol  | System.IO.Pipelines + BinaryPrimitives |
| HTTP server      | ASP.NET Core Kestrel|
| HTTP client      | HttpClient          |
| Logging          | Serilog (JSON)      |
| Config           | appsettings.json + models.json |
| Testing          | xUnit + Moq + Aspire.Testing |
| Metrics          | prometheus-net      |
| Tracing          | OpenTelemetry       |
| Zero-copy I/O    | Socket.SendFileAsync|
| Deployment       | NativeAOT binary    |

## Project Structure
All source code lives under `src/`.
```
├── CLAUDE.md                    # agent instructions (single source of truth)
├── PROJECT_STATUS.md            # this file — milestones, implementation status
├── docs/architecture.md         # architecture reference (this doc's detail layer)
├── docs/diagrams.md             # Mermaid diagrams for all major flows
├── specs/rpc-protocol.md        # binary wire format + opcode reference
├── pyproject.toml
├── src/Hydra.sln
│
├── src/
│   ├── Hydra.Shared/            C# — protocol, RPC base, shared types
│   │   ├── Protocol.cs          wire format, header pack/unpack, OpCode/StatusCode enums
│   │   ├── RpcServer.cs         base TCP RPC server (System.IO.Pipelines)
│   │   ├── RpcClient.cs         TCP RPC client (reconnect, stream body)
│   │   ├── ChunkModels.cs       ChunkRef record (index, hash, size)
│   │   ├── AsyncEnumerableStream.cs  IAsyncEnumerable<byte[]> → Stream adapter
│   │   └── HydraLogging.cs      Serilog setup, trace scope helpers
│   │
│   ├── Hydra.Core/              C# — single binary: store + coordinator + session mgmt
│   │   ├── Services/
│   │   │   ├── AutoRouter.cs           model selection (4-step algorithm)
│   │   │   ├── EngineConfigApplier.cs  push EngineConfig to engine via 0x40
│   │   │   ├── ModelConfigLoader.cs    load models.json + gpu-specs
│   │   │   ├── WorkerSchedulerService.cs  request dispatch + slot management
│   │   │   ├── MultiEngineRouter.cs    [Obsolete] legacy multi-engine routing
│   │   │   └── Router.cs              [Obsolete] legacy routing (replaced by AutoRouter)
│   │   ├── Models/
│   │   │   ├── EngineConfig.cs         stock-params engine config (sent via 0x40)
│   │   │   ├── ModelConfig.cs          models.json POCOs
│   │   │   ├── GpuSpec.cs              GPU hardware specs + capability bitmask
│   │   │   └── CoordinatorModels.cs    WorkerConfig, WorkItem, etc.
│   │   ├── Controllers/
│   │   │   └── CoordinatorControllers.cs  /v1/chat/completions, /v1/models
│   │   ├── StorageEngine.cs     raw file I/O on tmpfs (PUT/GET/DEL/STAT/LIST)
│   │   ├── ChunkEngine.cs       1 MB chunk + SHA-256 hash pipeline
│   │   ├── ChunkStore.cs        content-addressed chunk storage + manifest management
│   │   ├── StoreServer.cs       RPC handlers (PUT_CHUNKED, GET_CHUNKED, GET_MANIFEST …)
│   │   └── Program.cs
│   │
│   ├── Tests.Shared/            xUnit — Protocol, RpcClient, RpcServer
│   ├── Tests.Core/              xUnit — AutoRouter, EngineConfig, ModelConfig, etc.
│   ├── Tests.E2E/               xUnit — Hermetic E2E (Aspire + fake engine, Tier 1)
│   ├── Tests.LiveRig/           xUnit — Live-rig tests (Tier 2, SkippableFact-gated)
│   ├── Tests.EngineParity/      xUnit — HTTP/RPC parity (Tier 3, SkippableFact-gated)
│   ├── Tests.AgentWorkload/     xUnit — CLI-driven agent workload (Tier 4, opt-in)
│   │
│   ├── llama-cpp/               git submodule — hydra fork (sm_120 + sm_60)
│   │
│   ├── head/                    Go — Hydra Head node agent
│   │   ├── main.go               entry point
│   │   ├── go.mod / go.sum
│   │   └── internal/
│   │       ├── api/               HTTP API (/status, /health, /restart, /update)
│   │       ├── config/            YAML loading, 2-layer merge, CLI args
│   │       ├── health/            idle/busy mode health checker
│   │       ├── process/           4-service lifecycle manager
│   │       └── registry/          OCI registry pull via crane
│   │
│   └── tests/                   Python bench/stress tooling (out of scope for #518)
│
├── infra/
│   ├── hydra-core/config/
│   │   ├── models.json          model definitions + routing rules + engine defaults
│   │   ├── workers.json         worker configs (RTX + RTX 3060, MoE profiles)
│   │   └── workers-27b.json     worker configs (Dense 27B COMBINED profile)
│   ├── hydra-head/               Hydra Head deploy configs
│   │   ├── Dockerfile.rtx         RTX container build
│   │   ├── hydra-head.service     P100 systemd unit
│   │   └── config/
│   │       ├── global.yaml        shared params (models, infra endpoints)
│   │       ├── node-rtx.yaml      RTX-specific (router mode, services: disabled)
│   │       └── node-p100.yaml     P100-specific (model, services: all enabled)
│   ├── docker-compose.hydra.yml  Hydra.Core container
│   ├── docker-compose.infra.yml   Infra/observability stack
│   ├── quadlets/                  systemd quadlet units (infra-host pod, services)
│   ├── prometheus/                scrape configs + alerts
│   └── promtail/                  log pipeline configs
├── specs/                       protocol & service specs
└── docs/                        milestone docs + architecture + diagrams
```

## Milestones

| MS           | Name                           | Scope                                                       | Status   |
|--------------|--------------------------------|-------------------------------------------------------------|----------|
| M0           | MVP Test                       | llama fork + Store + Agent + system verify                  | ✅ done   |
| M1           | Core System                    | Coordinator + routing + session + migration                 | ✅ done   |
| M2           | Advanced                       | Chunked dedup + prefix checkpoints                          | ✅ done   |
| Phase 0      | Stabilize                      | Green CI/CD, restore obs, rebase local onto remote          | ✅ done   |
| M-Perf       | Heterogeneous Performance      | spec-decode → P/D streaming → pipeline (Tier-1)            | ✅ done   |
| Llama-Engine | P/D split mix-quant            | RTX precise prefill / P100 quant decode, worker policy, pipelined prefill, dynamic quant swap | ▶ now    |
| M3           | Persistence & Real Obs         | NVMe write-behind persistence (**C# re-spec**) + obs harden | Production (later) |
| M4           | Model Management & Multi-Modal  | model distribution, dynamic load, vision/embed/audio        | Production (later) |
| M5           | LLM Obs & Agentic              | Langfuse tracing, A/B testing, agentic system               | Production (later) |

## CI/CD Structure

| Check | When | Required for merge |
|-------|------|--------------------|
| `Build & Test` (ci.yml) | every push/PR | ✅ |
| `E2E (hermetic)` (e2e-hermetic.yml) | **manual only** (`gh workflow run e2e-hermetic.yml --ref <pr-branch>`) | ✅ required status check |
| System Tests (test-system.yml, LiveRig) | manual / deploy-heads | opt-in |

Notes:
- Hermetic E2E boots the full Aspire stack + Postgres and takes ~1–6 min; it was removed
  from push/PR CI to stop it from slowing/flaking the shared runner. It remains a
  mandatory merge gate via the required `E2E (hermetic)` check (reported on the PR
  head when run manually).
- Tests.Core integration tests are hermetic: the scheduler fixtures stub
  `LlamaClientFactory`, so they never dial the live engine
  (`localhost:8080` / `192.168.122.21:8086`). Live-boundary tests live in
  Tests.LiveRig. This fixed a 30-min teardown hang where the tests hit the
  production rig.

### Llama-Engine Sub-phases (v4 Design — Issue #397)

| Phase | What | Status |
|-------|------|--------|
| Phase 0 | Scheduler feasibility check | ✅ Done (COMBINED mode works) |
| Phase 1 | Config migration (drop `--ggml-rpc-port`) | ✅ Done |
| Phase 2 | E2E test scripts (COMBINED correctness) | ✅ Done |
| Phase 3 | Config migration (add peer_endpoint, default_model, default_split) | ✅ Done |
| Phase 4 | Hydra.Core orchestration (AutoRouter + EngineConfigApplier) | ✅ Done (PR #443) |
| Phase 5 | Profiling run + perf baseline | ⏳ Pending |
| Phase 6 | AF_UNIX dispatch test (optional) | ⏳ Pending (only if Phase 5 shows wire bottleneck) |

### Merged P/D DECODE (Issue #470)

Replaces the blind `STATE_PUT` (0x31) + HTTP-decode pair with a single validated
`DECODE` (0x43). The old pair had **no point at which the engine confirmed the KV
matched the resident model** before generating — the gap behind #469.

| Phase | What | Status |
|-------|------|--------|
| 1 | GGUF-derived model identity getters; `model_hash` removed | ✅ Merged (fork #63) |
| 2 | Framed `0x43` + `HTTP /v1/decode/{id}` | ✅ Merged (fork #64) |
| 3 | GGUF identity in Store + `CrossModelGuard` | ✅ Merged (#489) |
| 3b | DECODE dynamic model-swap before KV restore | ✅ Merged (fork #65) |
| 4 | Coordinator merged-decode path | ✅ Merged (#492) |
| R1 | Same-node fallback observability + COMBINED routing fix | ✅ Merged (#493) |
| 5 | v3 segmented framing, validate-first, real SSE streaming | ▶ In progress |
| 6 | E2E soak on `pi/hydra/moe-35b-pd` | ⏳ Pending |

**⚠️ Deploy hold until Phase 5 lands.** `merged_decode` is advertised
unconditionally while the engine still rejects `prompt.messages` — the only shape
Hydra.Core sends — so any live node on this engine build fails every P/D chat
request. See `specs/rpc-protocol.md` for the v3 `0x43` contract.

## Verified Facts
| Fact                         | Value        |
|------------------------------|--------------|
| RTX 5060 Ti decode           | ~200 tok/s   |
| RTX 3060 decode              | ~60 tok/s    |
| P100 prefill                 | 110 tok/s    |
| P100 decode                  | 28 tok/s     |
| Cross-GPU restore            | ✅ confirmed  |
| cache_n after restore        | 2964 / 2968  |
| KV state at 60-80K           | ~800 MB      |
| n_tokens must be > n_past    | CRITICAL ⚠️ — engine-owned under #470 (see below) |
| PREFILL appends last-position logits | ✅ decode samples without a re-prefill pass |
| Restored logits are per-slot | ⚠️ `llama_get_logits()` is context-wide; a concurrent slot clobbers it |
| Only P/D cross-node has restored logits | COMBINED / warm / cold have none → 1-token trick still required |
| Core cannot compute a token delta | No tokenizer — engine runs `get_common_prefix` |
| AutoRouter routing           | ✅ 4-step algorithm |
| EngineConfig via 0x40        | ✅ Config push works |
| COMBINED mode (MoE)          | ✅ Expert-split verified |
| COMBINED mode (Dense)        | ⚠️ Layer-split swap works (PR #537); decode KV-restore blocked (#78) |
| rpc_servers reachability     | ✅ Coordinator translates worker names → reachable host:port (PR #537). Before: `rtx3060:9504` unresolvable → peer never registered → whole model on CUDA0 → OOM → rollback |
| P100 binary                  | ✅ llama-engine `6d00536` (build 9670) — switched from llama-server (was RPC-dead in router mode, #577). Boots Q5_K-Balanced, Hydra RPC :9502 up |
| Merged DECODE prompt shape   | ✅ Coordinator sends bare messages array; engine now wraps it (fork PR #77). Before: `prompt_obj["n_predict"]` threw type_error on the array, silently swallowed by the RPC worker → connection leak → 180s coordinator timeout |
| `/state/meta` model identity | ✅ Engine now returns tokenizer/model_name/quant/caps (fork PR #77); Gate A requires them |
| Worker lease on mid-pipeline cancel | ✅ FinalizeAsync called at both exit points (PR #541). Before: BusySince climbed unbounded until coordinator restart |
| deploy-heads startup_failure | ✅ Root cause: caller workflow lacked `pull-requests: read` for the cross-repo reusable workflow's job-level `permissions` (PR #539) |
| Head supervision             | ✅ Event-driven (stdout sentinel readiness + exit-event liveness), no HTTP poll (issue #538) |
