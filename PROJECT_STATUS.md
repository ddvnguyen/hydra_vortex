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
| `EngineConfigApplier` | ✅ Implemented | `src/core/Hydra.Core/Services/EngineConfigApplier.cs` |
| `0x40 EngineConfigure` RPC | ✅ Implemented | `src/core/Hydra.Shared/Protocol.cs` (OpCode 0x40) |
| `0x44 SET_EXPERT_MODE` | ✅ Implemented | COMBINED mode activation |
| `0x46 EnginePipelineAttach` | ✅ Implemented | PIPELINE mode activation |

### Model Config (models.json)
| Model Alias | Mode | GPUs | Status |
|-------------|------|------|--------|
| `moe-35b-solo` | SOLO | RTX 5060 Ti | ✅ Production |
| `moe-35b-pd` | P/D split | RTX prefill + P100 decode | ✅ Production |
| `dense-27b-combined` | COMBINED layer-split | RTX 5060 Ti + RTX 3060 | ✅ Config ready |

### AutoRouter Algorithm (4-step)
1. **STEP 0: Warm Affinity** — reuse existing KV session (highest priority)
2. **STEP 1: Candidate Filtering** — filter models by token count, context, health
3. **STEP 2: Hardware Feasibility** — match GPU requirements (VRAM, compute, capabilities)
4. **STEP 3: Swap-Cost Preference** — pick best model by quality tier and load time
5. **STEP 4: Build Worker Plan** — select head + peer/decode workers

### What's NOT Implemented (and not needed)
- **`ProfileSwitcher`** — NOT needed. llama-engine handles model switching internally.
  Hydra.Core sends config via 0x40 EngineConfigure; the engine decides when to reload.
- **`WorkerSchedulerService.SendEngineConfig`** — replaced by `EngineConfigApplier.ApplyAsync`
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

## Tech Stack Detail
| Concern          | Hydra.Core (C#)    |
|------------------|---------------------|
| Async runtime    | async/await + IOCP  |
| Binary protocol  | System.IO.Pipelines + BinaryPrimitives |
| HTTP server      | ASP.NET Core Kestrel|
| HTTP client      | HttpClient          |
| Logging          | Serilog (JSON)      |
| Config           | appsettings.json + models.json |
| Testing          | xUnit + Moq         |
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
│   └── tests/                   Python system/E2E tests
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
| n_tokens must be > n_past    | CRITICAL ⚠️  |
| AutoRouter routing           | ✅ 4-step algorithm |
| EngineConfig via 0x40        | ✅ Config push works |
| COMBINED mode (MoE)          | ✅ Expert-split verified |
| COMBINED mode (Dense)        | ✅ Layer-split config ready |
