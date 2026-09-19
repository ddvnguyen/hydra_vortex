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

### Model Config (models.json)
| Model Alias | Mode | GPUs | Status |
|-------------|------|------|--------|
| `moe-35b-solo` | SOLO | RTX 5060 Ti | ✅ Production |
| `moe-35b-pd` | P/D split | RTX prefill + P100 decode | ✅ Production |
| `dense-27b-combined` | COMBINED layer-split | RTX 5060 Ti + RTX 3060 | ⚠️ Swap fixed (PR #537); decode blocked by fork KV-restore (#78) — **UPDATE 2026-08-23 §15: gpu-burn confirms this same 3060 has hardware VRAM fault (FAIL 1520→22718 errors within 90s, 5060 Ti 0 errors), likely root blocker for this stability issue too per §12.6 suspicion (cross-ref #703)** |

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

### Merged-decode epic fixes (`epic/470-merged-decode`)
| Item | Status | Notes |
|------|--------|-------|
| #597 parallel + coalesced liveness probes | ✅ Landed | `0cd73cb3` + `2c0b6f7cd` — stale-worker probes no longer serialize; `_llamaClients` made concurrent-safe |
| #598/#599/#600 merged-decode helper extraction + fixes | ✅ Landed (PR #605) | shared merged-decode request-resolution + `GetOrCreateRpcClient` helpers; leftover bare block flattened |
| #609 `KvModelAlias` in merged-decode alias fallback | ✅ Landed | `ea10dc03c` — model-agnostic sessions keep Gate A match |
| #588 LiveRig budgets 4K-16K + concurrency=2 | ✅ Landed | `fec895c06`, `ef8a7adbb` — thinking-heavy model budgets + rig slot limit |
| #615 Store LRU sweep + evict-on-ENOSPC + eviction lock | ✅ Landed (CRITICAL) | `31c9d30ef`, `6a3e62f4f` — tmpfs can no longer fill 100% → KV save failures → no KV restore between turns; verified live: 0 save failures, restores working, Multiturn40kContext 13 min (was 28 min FAIL) |
| #616 merged-decode empty-content → HTTP proxy fallback | ✅ Landed | `16e537795` + QA `0ac1fd036` — buffered + streaming fallback paths |
| #617 migrate continuation re-enters KV restore | ✅ Landed | `16e537795` — StatePut status check + non-resident ledger |
| FIX-3 Dense27bMultiturn timing-budget test | ✅ Landed | `ea49169f` — baseline + 10 s per expected state transition |
| Ops: write-behind flush to SSD | ✅ Fixed | compose user 0:0 (rootless podman maps container root → host ddv = owner of ntfs-mounted `/mnt/SSD`); chunks flush to SSD backup, 0 errors |
| #470 open item 3: p100 cold-expert warm-up | ✅ Landed (config-only) | `no-mmap: true` on p100 (`node-p100.yaml`) — eager expert load kills the +7446-majflt first-decode tax; warm-up prefill not config-hookable (head readiness is sentinel-driven, no post-load hook), so `--no-mmap` chosen as the deterministic fix |
| #618/#619 follow-ups | 📌 Filed | FIX-3 hidden-load hole; store chunk dir byte cap |

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
| 4.x | Epic follow-up fixes (probes, KvModelAlias, empty-content fallback, Store LRU sweep, …) | ✅ Landed on `epic/470-merged-decode` |
| 5 | v3 segmented framing, validate-first, real SSE streaming | ▶ In progress |
| 6 | E2E soak on `pi/hydra/moe-35b-pd` | ⏳ Pending |

**⚠️ Deploy hold until Phase 5 lands.** `merged_decode` is advertised
unconditionally while the engine still rejects `prompt.messages` — the only shape
Hydra.Core sends — so any live node on this engine build fails every P/D chat
request. See `specs/rpc-protocol.md` for the v3 `0x43` contract.

### Per-Expert Backend Selection (fork epic #148)

E0 measurement foundation (pin-load arming + timing hooks + lookup counters +
engagement gate), built fresh on `epic/148-per-expert-backend-selection`
(86af0c9af); gather pilot superseded-by-design, not ported. Stage-1 pool
premise CONFIRMED (S1 27.2832 tok/s = 1.140×C, CPU%dec 673→510).

| Step | What | Status |
|------|------|--------|
| E0 | Pin-load, timing rings, lookup counters, gate | ▶ PR #152 open (3 commits, closes #142/#143/#144; #146 closed as moved) |
| E1 | One-site spike timing (paper before number) | ⏳ Design draft `docs/e1-spike-timing-design-DRAFT.md`, awaiting architect review |

Support scripts: `scripts/hydra-engagement-gate.sh`, `scripts/hydra-build-stamp.sh`.

## Leader Contract

- **ADR 0002 signed 2026-08-21 (Option A):** `ddvnguyen ↔ muse-spark-1.2-contributor` — standing until superseded, scoped to **llama.cpp baseline (2×RTX vanilla, `Qwopus3.6`)** running authority (build `--parallel 8`, `infra/llama-baseline/` compose, ctx `98304→65536` yarn `scale 4`, harness `dsh`/`pi` via `:8080`). Revocable via doc removal + this file update; merges still require explicit user confirmation per `CLAUDE.md §4`. Hermes fleet `v2.1.1` stays superseded (`f8b322c73`). No GPG/HMAC. Ref: `docs/decisions/0002-leader-contract.md`.
- **ADR 0003 signed 2026-09-13 (Option A):** `ddvnguyen ↔ baseline-flash-next-leader` — standing until superseded, scoped to the **`baseline-flash-next` track (Qwen3.8-Flash-Next / `qwen4exp`, #763)** running authority: fork/submodule on `ddvnguyen/llama.cpp baseline-flash-next`, RTX (`86;120`, CUDA 13.2.2) + P100 `sm_60` (CUDA 12.9) builds `--parallel 8`, `infra/llama-baseline/` params-arms + harnesses, bounded P100 RPC smoke. **Not** authority over Hydra production (`:8080`/`:8081`), P100 production engine (`:8086`/`:9502`) or `main` merges. Revocable via doc removal + this file update; merges still require explicit user confirmation per `CLAUDE.md §4`. No GPG/HMAC. Ref: `docs/decisions/0003-leader-contract-baseline-flash-next.md`.

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
| Stage-1 pool anchor (S1 -t16) | 27.2832 tok/s, 1.140×C, CPU%dec 510, VRAM 13699/11809 |
| BRIDGE B3 (E0 tree, disarmed) | ✅ 27.4374 tok/s (+0.21% vs anchor) — base CERTIFIED |
| Worker lease on mid-pipeline cancel | ✅ FinalizeAsync called at both exit points (PR #541). Before: BusySince climbed unbounded until coordinator restart |
| deploy-heads startup_failure | ✅ Root cause: caller workflow lacked `pull-requests: read` for the cross-repo reusable workflow's job-level `permissions` (PR #539) |
| Head supervision             | ✅ Event-driven (stdout sentinel readiness + exit-event liveness), no HTTP poll (issue #538) |
| Multiturn40kContext live rig (run #31405080406) | ✅ 13 min PASS (was 28 min FAIL) — KV restores working between turns |
| KV restore latency           | ✅ Working up to 7.5 s for large blobs; cold prefills bounded (~4 total in suite) |
| P100 cold-expert mmap tax    | ✅ Fixed (`no-mmap`, epic #470) — first decode prefill after cold start was 15.0 s / 29.6 s total (majflt 12952→20398, RSS +4.15 GB, Mapped 7.73 GB) vs 4.1 / 6.2 s warm; eager expert load at engine start removes the one-shot fault storm |
| Store LRU sweep              | ✅ L1 sweep heartbeat every 45 s (`chunk_cache_lru_sweep`) |
| Merged-decode result path    | ⚠️ Drops `reasoning_content` (engine bug, #616) — interim coordinator HTTP-proxy fallback; engine fix pending |
| RTX 3060 role                | ✅ Peer-only by design (mainline #481, slots=0) — COMBINED peer, not SOLO |
| RTX 3060 hardware stability  | ⚠️ Cold-boot-only fault (#701): Xid 13/31/43/109 fired once, in the first CUDA workload dispatched after a reboot; every subsequent test (incl. 180s/97k-iter soak and 2× 60s full-180W soaks) passed clean. Isolated to this card's PCI path (5060 Ti clean on identical driver/tests). Likely cold-start power-rail/link settling on the NVMe-adapter riser, not a persistent defect — unconfirmed without physical slot-swap isolation. `scripts/gpu-smoke-test.sh` added to catch this class of fault going forward |
| RTX 3060 hardware health (#701/#702) | ✅ Not a hardware fault — light/full-power GEMM/99%-VRAM soak (`scripts/gpu-smoke-test.sh`) all PASS, zero Xid, on real card — **UPDATE 2026-08-23 §15: superseded — gpu-burn standalone VRAM diagnostic (CUDA 13.2, 600s 90% 10608 MB) now FAILs on this same 3060 (1520→22718 errors escalating 0.2%→2.5%+ within 90s, 5060 Ti 0 errors, dual-GPU burn isolates to 3060), confirming hardware VRAM fault with silent bit-flip (no ECC). Earlier smoke test was not sensitive enough under sustained cuBLAS compare load.** |
| llama.cpp baseline (#703) 3060 crash | ⚠️ **PARTIALLY ROOT-CAUSED — two independent bugs**. (1) **Fixed**: the original crashing `src/llama-cpp/build/` directory had a mismatched CMake config (`CUDAToolkit_ROOT=13.2` vs `CMAKE_CUDA_COMPILER=13.2.2`'s nvcc), confirmed via `readelf -d`/`CMakeCache.txt`. Three from-scratch builds with internally-consistent CUDA (13.2, 13.2.1, 13.2.2 — verified via `/proc/<pid>/maps`) all survived 60-90 repeated-request load loops with zero Xid, at ~593-599 tok/s prefill / ~10.6 tok/s decode — **but only with `token_embd.weight`/`output.weight`/`output_norm.weight` CPU-overridden**. (2) **Still open, real bug**: with those three tensors placed on GPU instead (default placement, or forced onto a single GPU), the same verified-clean binary crashes reliably on the first substantial request (Xid 13/31/43). CPU-overriding these tensors is a **required workaround, not legacy caution** — it also explains why decode is capped at ~10.6 tok/s rather than the ~20-24+ tok/s expected: this model's 248,320-token vocabulary makes `output.weight`/`token_embd.weight` ~1GB+ matrices, and CPU-placing them means every decode step pays a CPU GEMV cost. `--split-mode row` remains unusable on this hardware pair (5060 Ti/Blackwell lacks split-buffer support) — unrelated, separate limitation. `ikawrakow/ik_llama.cpp` was cross-tested as an alternative engine: survives the crash loop on short prompts but **hangs indefinitely on realistic-length prompts** (one GPU pegged 100%, no response after 5 min) — not a viable substitute. One real, minor, separate upstream bug found: `ggml_cuda_kernel_can_use_pdl()` (`common.cuh:1592`) gates PDL by PTX-ISA version instead of device compute capability (worth reporting, doesn't affect this crash). (3) **RPC-transport test (`ggml-rpc-server` + `--rpc`/`-dev RPC0,CUDA0`, §11) also crashes**: same Xid 13-class 3060 CUDA fault (`misaligned address` / `illegal instruction` at `ggml_backend_cuda_synchronize`) reproduces — deferred to ~2-3 successful GPU-resident requests instead of crashing on request #1. So RPC is NOT a workaround; CPU-override of the three tensors stays required under any transport. The 3060 (sm_86) is the failing device in every case, pointing at a 3060-specific bad CUDA op during GPU-resident large-vocab decode, not a vanilla-vs-RPC transport difference. GPU-resident decode briefly hit ~20 tok/s (2x the 10.6 CPU-override rate) before dying — confirms the speed win is real. (4) **`GGML_CUDA_DISABLE_GRAPHS=1` has NO effect** (§11.6): tried on the 3060 rpc-server and on both processes; crash still reproduces at request 1-3 with the same Xid 13-class 3060 `ggml_backend_cuda_synchronize` fault. Bug is graph-independent — rules out the cheapest one-line fix and points at a genuine bad CUDA op / memory error on the 3060. Since Hydra's prod stack is also RPC-based (Head + ggml-RPC + Store, dense-27b-combined), the same 3060-side fault is the likely blocker for dense-27b-combined stability (#78 decode path) — not Hydra-specific code. Real next step: `compute-sanitizer --tool memcheck` on the 3060 for this exact repro. (5) **ROOT CAUSED via memcheck (§12) — now a named, fixable upstream op.** `compute-sanitizer --tool memcheck` on the 3060 rpc-server caught `Invalid __global__ read of size 4` in kernel `mul_mat_vec_q<(ggml_type)14>` = `mul_mat_vec_q<GGML_TYPE_Q6_K>` (the quantized GEMV, i.e. the per-decode `output.weight`/lm_head vocab projection, n_vocab=248,320), at a wild address (0xcd4004b6c, 17.1 GB past the nearest 2 MB allocation) — a corrupt weight-pointer/shard-sizing in the tensor-split/RPC `output.weight` shard on the 3060. The earlier Xid 13/illegal/launch-failure and the downstream `ggml_cuda_kernel_can_use_pdl` rope cascade are SECONDARY. This is why CPU-overriding `output.weight` is required and why it is transport-independent (vanilla tensor-split §10 and RPC §11 both hit it) and graphs-independent (§11.6): the bad GEMV is in the shared scheduler/tensor-split path, not Hydra-/RPC-specific. **Hydra impact: this is the likely root blocker for dense-27b-combined decode stability (#78)** — validate by forcing the lm_head off the 3060 peer in Hydra and confirming decode stabilizes. Fix lives upstream in `ggml_cuda_mul_mat_vec_q` shard sizing; CPU-override of `output.weight` on the 3060 remains the required workaround until then. Full story: `docs/investigations/703-3060-xid-crash.md`. **UPDATE 2026-08-23 §15: gpu-burn standalone VRAM diagnostic (CUDA 13.2, `COMPUTE=86`, `CUDA_VISIBLE_DEVICES=1` 3060 only, 600s 10min 90% 10608 MB, `compare.fatbin`) confirms hardware VRAM fault — 3060: FAIL 1520 errors at 0.2% → 22718 at 2.5% within 13-90s (escalating 0.2%→2.5%+), 5060 Ti: PASS 0 errors throughout (100% 692 iter, both GPUs 600s 5060 Ti 0 vs 3060 254 at 3.3%, isolates to 3060 specifically, no ECC so silent). This explains every failure mode across #703 (all 3 CUDA point releases, both tensor placements, both process architectures single-process 25,40 + 2-process RPC-split 21,44, all 4 kernel-bug hypotheses checked clean) — none were actual cause, hardware was. Cross-ref Hydra production `dense-27b-combined` (also uses this same 3060, `RTX 5060 Ti + RTX 3060` COMBINED `layer-split`, `⚠️ Swap fixed` but `decode blocked` #78) — this is likely root blocker for that stability issue too per §12.6 suspicion. |

## #703 post-slot-swap verdict (addendum 2026-08-23T23:15Z)

- **The RTX 3060 is NOT defective — the NVMe-to-PCIe-x16 adapter (bus `02:00.0`, electrically x4-limited) caused the silent compute corruption.** Moving the card to a real motherboard PCIe x16 slot (bus `07:00.0`) eliminated it: the corrected PR #702 GPU smoke test (`scripts/gpu-smoke-test/kernels`, exact GEMM-compare, 90s) went from **2774–2912 mismatches pre-swap** to **0 mismatches across 3 independent post-swap runs**; 5060 Ti control 0 both times. `pcie.link.width.current` stayed at 4 under load on the new slot (max 16), so lane width was never the cause — the adapter's electrical/power integrity was.
- This **overturns** the earlier §15 "hardware VRAM fault" / die/VRAM-intrinsic conclusion and the AER-based argument (AER only sees link-layer errors, not in-GPU power-induced bit-flips). The slot-swap isolation test (flagged in PR #702 as the open question) is now **conclusive: adapter-caused**.
- The §12 `mul_mat_vec_q<Q6_K>` GEMV out-of-bounds and §10 GPU-resident-output.tensor crash are SEPARATE real software bugs (reproducible on a verified-clean binary regardless of hardware) and remain valid/unresolved; they are not caused by, and not fixed by, the slot move.
- Action: do NOT file RMA or upstream issue yet (user's call, still pending). The card is usable on a clean slot.
- Post-swap solo crash-loop (Qwen3.5-9B-Q8_0, `CUDA_VISIBLE_DEVICES=1`, 5×10 multi-turn 6K→12K) also **PASS 50/50** (corrected harness; first run misfired on `Qwen3.5` `reasoning_content`, not a fault) — overturns the pre-swap 6:40 hang. Both decisive tests (smoke + crash-loop) now clean on the real x16 slot.
- #381 decode re-validation (POST-hardware-fix): RPC-split 21/44 + MTP `Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf` on clean x16 slot = **50/50 PASS, ~31 tok/s decode (MTP draft acc 0.62–0.67), ~776 tok/s prefill, 0 Xid** on both buses. Confirms the #381 best decode number (31.1 tok/s) under rigorous sustained 5×10 growing-context load. The §12 `mul_mat_vec_q<Q6_K>` GEMV OOB software bug did NOT trigger in this exact config (latent in other shapes); hardware question definitively answered — 3060 on clean slot holds #381 decode clean.
- Detail: `docs/investigations/740-results-report.md` (post-slot-swap rows + verdict section).

## #703 systematic test matrix + confirmed best config (addendum 2026-08-25, updated 2026-08-26)

- Post-slot-swap, a systematic params-file test matrix (`infra/llama-baseline/params/001-024*.yml`, 24 arms, run via `run-with-params.sh`) swept CUDA point releases (13.2/13.2.2/13.3), tensor-split ratios, CPU-override tensor sets, chat-template source, production-parity params, context ceilings (98K→128K→143K→160K), and ubatch sizes, each validated by a 40-request crash-loop plus real multi-turn sessions (`pi` CLI + `tests/bench/chat_multi_turn.py`).
- **Two confirmed best configs:**
  - **DEFAULT — Arm 017 (speed-optimized)**: 98K ctx (`rope-scale 3`), tensor_split 26/39, no CPU-override, ubatch 1024, prod-parity params (`cache-reuse:64`, `cache-prompt`, `prio-batch:1`, `context-shift`), native GGUF template, `draft-mtp`. Result: **10/10 multi-turn, 0 Xid, 36–39 tok/s decode, 236s total, VRAM 88%/74%**.
  - **ALTERNATIVE — Arm 024 (ctx-optimized)**: 143K ctx (`rope-scale 5`), everything else identical to 017. Result: **9/10 multi-turn, 0 Xid, 28–32 tok/s decode, VRAM 95.4%/79.6%**. The 143K ceiling is confirmed via arms 018-024: 128K passes (018, no override), 143K passes (024, no override — override is counter-productive as KV expands to fill freed space), 160K OOMs even with override (019).
- **Key findings from the full matrix:**
  - 26/39 tensor_split is optimal (3060 is decode bottleneck; shifting layers to 5060 Ti hurts — arm 021).
  - ubatch 1024 is optimal (512 and 2048 both worse — arms 022/023).
  - CPU-override is counter-productive at 143K (arms 020 vs 024: same VRAM, override = slower decode).
  - Chat-template mismatch was a major confound (010: 6/10 with 3.6 template → 013: 9/10 with native template).
  - cache_type_k=q8_0 is a hard constraint (never q4_0 for K-cache).
- `infra/llama-baseline/docker-compose.baseline.yml` / `Dockerfile.baseline` now pinned to 017's RPC-split topology with prod-parity params. 024 documented as alternative profile (change `--ctx-size 143360 --rope-scale 5` to switch).
- `run-with-params.sh` updated with crash-loop/multi-turn log separation (`-crashloop.log`/`-multiturn.log`) and prod-parity param mappings. `tests/bench/chat_multi_turn.py` extended with `--deterministic` mode for controlled multi-turn testing.
- Detail: `docs/investigations/740-results-report.md`, `infra/llama-baseline/params/README.md`.

## #703 arms 090-114 — concurrency-shape vs production-pin verdict (addendum 2026-09-06)

- Arms 090-105 established a 3-slot concurrent shape (`146176×3 ctx, kv_unified off, cache_ram 24576 MiB, UM on`, arm102) as the best concurrent-decode config found, alongside knob sweeps (tensor_split 25/40 and 30/35, ubatch 1024, cache_ram 8192, cache V-quant q5_0/q5_1, UM-off — arms 106-112) that all landed **statistically identical to arm102** (3-conc aggregate 95-102 tok/s band); an upstream llama.cpp v0.3.0 rebuild (arm114) also matched. None of these knobs move concurrent-decode throughput on this rig — arm102's `tensor_split 27,38` / `ubatch 512` / `cache_ram 24576` remains the defensible default for that shape. UM-off (arm112) deterministically OOMs — the shape requires `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`.
- **Head-to-head decision (arm111, 102-shape + `V=q5_1`, vs arm090, the current production pin)** under identical `multiturn-growth-test.sh` (10 turns, ~4000 new prompt tokens/turn, growing to ~33-34K depth): arm111 delivers genuine 2/3-way concurrent decode at 15-20 tok/s/session; arm090 (`parallel=1`) serializes concurrent sessions behind one slot at ~8-12 tok/s/session with 2× longer per-turn walls (same "overlap" flag reported by the harness in both cases, but arm090's is queue time-slicing, not real concurrent decode).
- **Verdict: choose by workload pattern, not a single winner.** Concurrent-growth workloads (2-3 simultaneous sessions actively growing past 30K context) → use the 102/111 shape. Rotational turn-taking (one session live, others idle, fast-return on resume) → **arm090 stays the pin** — its 18×-faster-idle-return design (validated 2026-08-29, 3-agent/6-turn production test, 0 evictions) is a different mechanism than 111's shape and is not invalidated by this test. `docker-compose.baseline.yml` DEFAULT PIN is unchanged (still arm090); no production cutover made — this is a documented option for a different use case, pending a decision on whether to add the 102/111 shape as a selectable second profile.
- Detail: `docs/investigations/740-results-report.md` (arms 106-114 + "Arm 111 vs 090" head-to-head section).

## #763 baseline-flash-next base swap onto GenerelSchwerz `qwen4exp-mtp` (addendum 2026-09-12)

- **Decision (user-approved, Option B):** rebase Hydra's fork commits onto `GenerelSchwerz/llama.cpp` branch `qwen4exp-mtp` to adopt its CUDA MoE expert cache + grouped MoE drafting/MTP — the productionized form of our #762 (MoE-cache) and #761 (MTP) work. Its published evidence shows Qwen3.8-Flash-Next at **47 tok/s decode on an RTX 5070 Ti 16 GB (`120a-real`)**, 48/48 grouped layers, 0 fallback. `beellama/main` was rejected (separate BeeLlama/KVarN lineage, not MoE/MTP).
- **Submodule `src/llama-cpp` pointer:** `1d3c4a8e3` → `efd26f235`, branch `fork/763-qwen4exp-mtp` on `ddvnguyen/llama.cpp`. Base `77b733d5c` (qwen4exp-mtp tip, superset of `moe-cache-drafting`); our 5 Hydra #747 commits cherry-picked on top with **zero conflicts**.
- **Our patches preserved:** `--parallel-ctx-threshold` admission gate (d06537485/0ed2ac31e/3ab0fdec8/1d3c4a8e3) + `cudaMemAdvise/prefetch` UM (ba2c46f65).
- **Verified:** host build (CUDA 13.2.2, `CMAKE_CUDA_ARCHITECTURES=86;120`) `llama-server` at `efd26f235`; `--help` exposes both `--parallel-ctx-threshold` and `--moe-expert-cache-size` / `--spec-draft-moe-expert-cache-size` / `--load-mode`.
- **NOT yet run:** no live-rig test (pending GPU availability). Their expert cache is **device-local** and explicitly does not support row/tensor-split sharding of a cached expert tensor — reconciliation with the separate COMBINED-OT expert-split engine is tracked under #763.
- **Note:** the uncommitted #762 `tools/moe-trace` port is stashed in the submodule (`stash@{0} 762-moe-trace-port-wip`) against the old base; it must be reapplied on the new branch.

### Fork PR target + lineage conflict (2026-09-12)

- **Fork PR target (per user):** `ddvnguyen/llama.cpp` branch **`baseline-flash-next`** (currently `cdd11021b`) — *not* `master`, *not* `hydra-fork`, *not* `fork/763-qwen4exp-mtp`. All future llama.cpp-side PRs base on `baseline-flash-next`.
- **No new authored llama.cpp changes** at that point: `efd26f235` is a replay (rebase) of the pre-existing 5 Hydra #747 commits onto the external `gs/qwen4exp-mtp` base; it adds no new diff of its own. (Reconciliation PR opened later — see "Resolution" below.)
- **Real conflict found (flagged, not resolved):** `cdd11021b` is **not** unrelated to us — its parent is our old Hydra chain (`1d3c4a8e3` → 5 × #747 commits), so our gate/UM patches are already on `baseline-flash-next`. `cdd11021b` adds one hand-rolled qwen4exp **NextN/MTP draft-head graph with shared-tensor borrowing** (329 lines in `qwen4exp.cpp`; 6 commits ahead of `5266f24da`). The `gs/qwen4exp-mtp` lineage my rebase sits on already contains its **own independent** qwen4exp NextN/MTP draft head (`788b21de6` "model: add the qwen4exp NextN/MTP draft head", `90f28fdf5` "llama: let an MTP draft borrow the target's embeddings and lm head"), i.e. two competing implementations of the same feature. Non-destructive `git merge-tree cdd11021b 77b733d5c` → **3 content conflicts**: `src/llama-model.h`, `src/models/models.h`, `src/models/qwen4exp.cpp`.
- **Divergence:** from merge-base `5266f24da`, `efd26f235` is **485** commits ahead (whole `gs/qwen4exp-mtp` delta + replayed Hydra), `cdd11021b` is **6** ahead. This is a lineage/base decision, not a small PR.

### Resolution: adopt gs lineage, supersede cdd11021b — PR #120 (2026-09-12)

- **Decision (per user: resolve and PR for review):** the two "competing" draft heads are the **same code** (identical `LLM_TENSOR_NEXTN_HC_HEAD_*` enum/names, `llama_layer_nextn.hc_head_*` fields, `graph`/`no_build_t`/`graph_mtp` shape, and draft graph). The only real difference is the embedding/LM-head **borrow mechanism**:
  - **gs:** model-load time, opt-in `{arch}.nextn_shared_target_tensors` GGUF key + `llama_model_params.model_shared`, resolved by `llama_model_loader::borrow_shared_tensor()`; wired via `--mtp-shared-embd` (convert) + `common/speculative.cpp` setting `mparams.model_shared = model_tgt`.
  - **cdd11021b:** graph-build time, qwen4exp-only `qwen4exp_shared_model(cparams.ctx_other, ...)`, with `token_embd` marked `TENSOR_NOT_REQUIRED` for `mtp_only`.
- **Primary basis = gs. Nothing ported from cdd11021b** — every cdd delta is already in gs or superseded. In particular cdd's `common/speculative.cpp` `is_mem_shared` gemma gate is **unnecessary** in gs: gs resets `cparams.ctx_other = nullptr` and only sets it for Gemma4Assistant/EAGLE3/DFlash (`src/llama-context.cpp:791-808`), so `llama_get_ctx_other(ctx_dft)` is null for qwen4exp and `is_mem_shared` is already false. cdd's gate only counteracts cdd's own `ctx_other` use for graph-time borrow.
- **Merge result:** `b6b34cc0f` (parents `efd26f235` + `cdd11021b`); tree **byte-identical** to `efd26f235` (`df72c2736`) → no unique cdd code lost, no cdd divergence retained.
- **PR:** **ddvnguyen/llama.cpp#120** (`base=baseline-flash-next`, `head=feat/763-reconcile-qwen4exp-mtp`), state OPEN/MERGEABLE, 486 commits / 406 files as a base adoption. Full rationale + uncertainties in the PR body. Not merged (human review).
- **Caveats for the reviewer (also in PR):** no end-to-end run (rig occupied); gs's load-time borrow path untested against Hydra GGUFs; a draft-only GGUF without `nextn_shared_target_tensors` is now refused at load rather than auto-borrowed (Hydra's current `Qwen3.8-27B-*-MTP.gguf` are self-contained, so unaffected).

### #763 E2E results — resolved tree (`efd26f235`) on the live rig (2026-09-12)

Production stopped by the human for the test window; both GPUs drained to 1 MiB; stack shut down clean afterwards (GPUs back to 1 MiB).

- **Production-shaped self-contained model — PASS.** `Qwen3.8-27B-UD-Q5_K_S.gguf` + `--spec-type draft-mtp`, dual-GPU RPC (`-dev RPC0,CUDA0 -ts 27,38`), UM on. Loaded clean. 400-token gen: prefill 134.7 tok/s, decode **32.57 tok/s**, **draft_n=533 / accepted=221 → 41.5%** acceptance, 178 verify steps; coherent output. **Finding:** this model is `general.architecture=qwen35`, not `qwen4exp` — production pin 130 uses the generic **same-file** MTP head, so it does **not** exercise the qwen4exp draft head or the borrow path.
- **qwen4exp shared-draft borrow + MTP — PASS (validates both flagged risks).** Real pairing: target `qwen3.8-flash-next-apex-mini` (qwen4exp, 48 blocks, n_embd 2560, no nextn tensors) + sidecar `mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf` (qwen4exp, 49 blocks, `nextn_shared_target_tensors=True`, ships no `token_embd`/`output`/`output_norm`).
  - **(a) load-time borrow: CONFIRMED.** Sidecar hard-refused standalone (`borrow_shared_tensor: … draft head without its own 'token_embd.weight'`), loads cleanly as `--spec-draft-model` against the target → `borrow_shared_tensor` resolved from `model_shared`. (INFO `"taken from the target model"` line not captured at verbosity 3; contrast is unambiguous.)
  - **(b) `is_mem_shared` false for qwen4exp / no double-borrow corruption: CONFIRMED.** **draft_n=97 / accepted=62 → 63.9%** acceptance, 33 verify steps (per-position 29+19+14=62), coherent correct output. Decode 5.72 tok/s (74 GB target on UM/28 GB VRAM — memory-bound, not a correctness signal). Load ~3m50s.
- **Correction:** initial reading of `common/speculative.cpp:3002` suggested `-md` loads the target path; a non-existent-`-md` probe proved the loader honors the draft path (resolves `mparams.path`). Not a bug.
- **Verdict: no PR-blocking finding.** Full detail: PR #120 comment (`#issuecomment-5646887049`). Production was restarted by the human on pin 130 after this.

## MoE look-ahead PR-A (consumer) — branch landed (addendum 2026-09-14)

- **Branch:** `feat/moe-lookahead-pra` pushed to `ddvnguyen/llama.cpp` (remote `hydra-fork`), tip **`ec23a599f`** on top of `cbb19d802` (consumer) and `137fa17bb` (spec `docs/moe-lookahead-design.md`). Diff: 3 commits, 14 files, +1105/-58.
- **PR opened:** **ddvnguyen/llama.cpp#126** - `base=feat/763-reconcile-qwen4exp-mtp`, `head=feat/moe-lookahead-pra`, state OPEN/MERGEABLE. The base **deviates from this file's `baseline-flash-next` fork-PR-target rule on purpose**: the branch sits on the `gs` `qwen4exp-mtp` lineage, and `baseline-flash-next` is still `cdd11021b` (an ancestor of that lineage, PR #120 unmerged), so targeting it directly would re-show all 408 of #120's files inside this PR. PR #123 stacks on `feat/763-reconcile-qwen4exp-mtp` the same way; GitHub retargets this to `baseline-flash-next` automatically when #120 merges.
- **Scope:** consumer half only - `ggml_backend_cuda_moe_prefetch_experts()` (+ `_tensor` variant) and the `--moe-lookahead N` gate. The producer (`GGML_OP_MOE_PREFETCH`, `llama-graph.cpp`) is PR-P1, out of scope.
- **Verified on this rig (RTX 5060 Ti `CUDA0` + RTX 3060 `CUDA1`):**
  - Full `tests/test-moe-cache` on CUDA1: **19/19** cases, `test-moe-cache: OK`, exit 0, 12 s.
  - `test-moe-cache --lookahead-prefetch-only` on CUDA0: `lookahead prefetch legacy layer OK` + `lookahead prefetch eviction guard OK`, exit 0.
  - CLI: `--help` lists `--moe-lookahead N` (env `LLAMA_ARG_MOE_LOOKAHEAD`); no-flag and `--moe-lookahead 0` load stock; `--moe-lookahead 8 --moe-expert-cache-size 84` loads (`model loaded` / `listening`); `--moe-lookahead 8` with cache-size 0 fails loud: `--moe-lookahead requires --moe-expert-cache-size > 0`.
  - Invariants by inspection: no `cudaStreamSynchronize` and no compute-stream wait on the prefetch path; pinned slots skipped; eids outside `[0, n_experts)` ignored; H2D batched via `cudaMemcpyBatchAsync` (`cudaMemcpySrcAccessOrderAny`, CUDART >= 12080); non-MoE-cached targets skipped with a one-shot warn; exported `ggml_backend_cuda_moe_prefetch_experts` signature unchanged; width 0 = zero extra work per decode step.
- **Build defect found (real, blocks fork links):** `/opt/software/cuda/13.2` - referenced by `docs/build-environment.md`, `docs/cuda-modules.md`, `docs/llama-bench-guide.md`, hydra `CLAUDE.md`, and `/etc/ld.so.conf.d/cuda-13-2.conf` - **does not exist**. Installed toolkits are `{12.9, 13.2.1, 13.2.2, 13.3.1}`. Linking against it fails with `undefined reference to <cuda*>@libcudart.so.13`. Use `-DCUDAToolkit_ROOT=/opt/software/cuda/13.2.1` and, on this box, `-DCMAKE_EXE_LINKER_FLAGS=-Wl,-rpath-link,/opt/software/cuda/13.2.1/lib64`. Note the `hydra-dev` ccache/LTO-off preset lives on the `hydra-fork` branch only; a plain `feat/*` clone still has upstream presets.
- **Eviction guard added (review finding, commit `ec23a599f`, 3 files +259/-96):** the prefetch path picked its victim by plain LRU, so a prediction could displace a demand-warm resident; only the **pin** guard existed - **on both the demand and speculative paths**. There was no demand-side warm-resident guard to reuse (contrary to the review's initial framing, subsequently accepted). Ported colibri's `PILOT_EVICT_GUARD`: a speculative fill may displace a resident only when that resident is not genuinely warm - protected at `>= 2` **demand** accesses AND clearly hotter than the prediction, by the 25% + 4-frequency hysteresis in LFRU score units (score from colibri `c/tier.h:40-43`). A blocked prediction is **dropped**, never forced in. Heat counts demand accesses only, so a speculation cannot inflate the score that decides its own displacement (colibri #490). Victim selection + install are now one shared core (`select_victim_locked` / `install_fill_locked`), removing 96 duplicated lines; the pre-existing focused test passed **unchanged**, which is the behavior-preservation proof (a "same scenario through both paths" test is now tautological). New case `lookahead prefetch eviction guard` (3-slot pool): a warm resident survives a prediction for a never-seen expert (dropped, no copy, no miss, no eviction, still a demand hit), while a once-demanded resident **is** evicted for it.
- **Open for PR-P1 (producer):** (1) `prefetch_legacy_layer` calls `acquire_legacy_cache(experts)` with `compute_stream=nullptr`, so if L+1's pool/device resource is not already installed the lease is empty and prefetch silently no-ops - agreed fix: preinstall all MoE-cached layer pools at model load when `--moe-lookahead > 0` and slots > 0, and **fail loudly at load** if the budget does not fit, making the no-op unreachable rather than silent; (2) on a failed `cudaMemcpyBatchAsync` the rollback clears the new booking but does not restore the displaced resident (the miss falls to the demand path) - acceptable now that the guard guarantees the displaced victim is a cold speculation; (3) the copy-batching split in `ggml_cuda_moe_cache_prefetch_locked` still mirrors the acquire/LRU ordering (the victim-selection/install core is now shared), so future `acquire` edits must be mirrored there; (4) PR-P1 review findings to answer: copy-completion dependency between the prefetch H2D and L+1's demand read (event-based only, no stream sync), execution-domain gating to MAIN decode, backend/buft gating for the op node, and graph-plan/fingerprint interactions.

## Single-GPU 5060 Ti expert-cache sweep (addendum 2026-09-14)

Method: byte-for-byte replication of the 3060 run (`single3060-warm.sh`), changing only `CUDA_VISIBLE_DEVICES=0` and the port; binary `build-gs-cuda1322/bin/llama-server`, model `qwen3.8-flash-next-apex-mini` (78 GB, 6 shards), `-c 8192`, 2 x 256-token generations, warm = 2nd request. Artifacts: `/tmp/opencode/early-router-5060ti/`.

| N (`--moe-expert-cache-size`) | cold t/s | warm t/s | h2d_bytes (GB) | status |
|---|---|---|---|---|
| 84  | 45.79 | 46.71 | 31.73 | OK |
| 112 | 48.24 | 49.18 | 25.00 | OK |
| 126 | 48.89 | 50.13 | 22.67 | OK |
| 133 | - | - | - | OOM at load (`cudaMalloc(69798400)` failed) |
| 140 | - | - | - | OOM at load (`cudaMalloc(66304000)` failed) |

- **5060 Ti headroom ceiling is between N=126 and N=132**; the 3060 caps at N=84 (N=88 OOM). Both are VRAM-bound, not a code limit.
- **Throughput saturates:** 84 -> 112 is +5.3%, 112 -> 126 is +1.9%, so the plateau is ~50.5 t/s and N=126 already delivers ~99% of it. Larger N mostly buys H2D traffic reduction (31.7 -> 22.7 GB per 256-token warm run, i.e. fewer cache misses).
- **5060 Ti vs 3060 at the 3060's N=84:** 46.71 vs 20.83 warm t/s = **2.24x**. Grouped decode is fully exercised on both (`covered=48`, `plan_compiles=2`, `calls=12240`, `fallback=0`, `prepare_error=0`).
- **Reporting trap:** the `moe-grouped-decode:` line appears 3x per run - the last one is an all-zeros **teardown** summary printed after `kill -TERM`. Use the first/second (real cumulative) line; `tail -1` yields zeros.

## MoE look-ahead PR-P1 (producer) — implemented, review-gated, NOT landable (addendum 2026-09-14)

- **Branch:** `feat/moe-lookahead-p1` pushed to `ddvnguyen/llama.cpp` (remote `hydra-fork`), tip **`4f681738b`** on base `ec23a599f`. 21 files, +464/-5, ASCII-only, `Assisted-by: Oh My Pi (deepseek-v4.1-flash)`.
- **PR opened:** **ddvnguyen/llama.cpp#127** - `base=feat/763-reconcile-qwen4exp-mtp`, `head=feat/moe-lookahead-p1`, same base deviation as #126. Title carries **NOT LANDABLE**; the body carries the full isolation matrix. Reviewer agent `516880c8-bb41-49a9-9843-fb15458817b6` notified.
- **Implemented:** `GGML_OP_MOE_PREFETCH` end to end (enum/ctor/name/symbol/CPU no-op/`ggml_get_n_tasks`/RPC op list/backend-ops skip list); `build_moe_lookahead()` (gate matmul on `ffn_gate_inp` then `ggml_argsort_top_k`, ending in `ggml_moe_prefetch`), gated on `--moe-lookahead > 0`, `ctx_type == DEFAULT`, decode rows only; call sites in `qwen4exp.cpp` and `qwen35moe.cpp`; reserve-time pool preinstall; debug recall instrument behind `GGML_CUDA_MOE_LOOKAHEAD_DEBUG`.
- **Blocker 1 - the producer changes model output (correctness).** Same prompt/flags, only `--moe-lookahead` differs: `3.0613` / md5 `fc1da5b9` (off) vs `2.9434` / md5 `8e0a9d10` (on). Deterministic across reruns. Isolation matrix, each row a separate build: prediction-only without the op -> `2.9434` / `8e0a9d10` (op contributes nothing); prediction reading a trunk tensor -> `3.1849`; gate `MUL_MAT` only, no argsort -> `3.1377`; neutral COPY node -> `3.0613` / `fc1da5b9` (**identical to baseline**), so the scheduler is not merely shape-sensitive; look-ahead 0 with `GGML_CUDA_DISABLE_GRAPHS=1` -> `3.0613` / `fc1da5b9`, so lost graph capture is not the cause. The trigger is the extra `MUL_MAT` reading `ffn_gate_inp`. Likely mechanism (unconfirmed): the same count-based proof that rejects the graph for the prefetch node (`graph=unproven(14)`) covers router tensors, so an extra reader perturbs it. **The byte-identical-logits reading recorded in the PR-A section above is refuted** - it came from a build where the producer never ran.
- **Blocker 2 - the feature is inert on this rig.** `acquire_legacy_cache()` installs a pool only while the target group's authority is `GGML_CUDA_MOE_GROUP_AUTHORITY_LEGACY` with admission open; here it is not, so `preinstall_legacy_pools()` fails for **all 144 targets** and every prediction is dropped (`found no installed pool ... prediction dropped`, once). This answers design question Q1 **negatively**. The PR-A open item "preinstall all pools at model load" is also wrong as written: the snapshot does not exist until after `sched_reserve()`.
- **Blocker 3 - throughput.** Decode 43.23 t/s (look-ahead off) vs 37.71 t/s (on; 37.57/37.61/37.68 across runs), about **-13%**. Cause: the ids readback synchronizes the stream, forcing `use_cuda_graph = false` for every graph containing the op.
- **Correctness work that stands on its own:** `ggml.c` must not count `GGML_OP_MOE_PREFETCH` sources in `use_counts` (otherwise the grouped-decode certificate rejects the graph and compute fails); `preinstall_legacy_pools()` must not return `0` when the snapshot is unpublished (indistinguishable from success, so the caller latched "ready" forever - now `-1`, caller latches only on a real result); the producer must target `ffn_up_exps`, since `ffn_gate_up_exps` is **null for every layer** of this model.
- **Verified:** `test-moe-cache` on the final code **OK**, exit 0, incl. `test_lookahead_prefetch_eviction_guard OK`. `ninja -C build llama-server test-moe-cache llama-perplexity` clean.
- **Open decision (reviewer):** pursue or drop the producer on this rig; if pursued, blocker 1 must be located in the fork's router identification / count-based proofs, not in the prediction math. `qwen35moe.cpp` is mechanical and unvalidated (no model on this rig) - reviewer to rule on whether it stays.
- **Reviewer decision (2026-09-14): STOP / park.** Blocker 3 is structural (a per-layer host ids readback forfeits CUDA-graph capture; same class as the upstream Metal slot-pool and 1080 Ti sync-point failures), blocker 2 leaves nothing to feed without surgery on `GGML_CUDA_MOE_GROUP_AUTHORITY`, blocker 1 is a numerics-contract violation living in the router census. Deep surgery in three independent subsystems is not justified by a gain bounded to the residual miss tail. PR #127 stays open, unmerged, as the experimental record; `qwen35moe.cpp` stays in the branch as part of it.
- **Design doc updated:** `docs/moe-lookahead-design.md` gains a "PR-P1 outcome (measured)" section with all three blockers, the isolation matrix, Q1 answered negatively, the refutation of the earlier byte-identical reading, the corrections (`ffn_gate_up_exps` null, preinstall point, Q3, Q4), and the future direction (the fork's early-router copy-worker plus device-to-host mailbox, `moe-cache.cu` around the copy-stream setup) if the track is ever re-approached. Commit **`495da1938`**, pushed to `hydra-fork`.
- **Fork-level finding for visibility (independent of this feature):** an extra `MUL_MAT` that reads a router weight (`ffn_gate_inp`) reclassifies the decode path and changes model output (PPL `3.0613` -> `3.1377` with nothing else added), while an extra neutral node is byte-identical. Any future graph-level feature that re-reads router tensors hits this.

## PR #126 (consumer) — CI status and consumer-only neutrality (addendum 2026-09-14)

- **CI:** `OPEN`, `MERGEABLE`, `mergeStateStatus=UNSTABLE`. 9 checks SUCCESS; 5 FAILURE: `cuda`, `hip`, `musa`, `cpu-x64-high-perf`, `ubuntu-22-hip-quality-check`. **The failing set is identical on #127**, so P1 adds no CI regression. **Verified attribution:** the base head `f6301d4` (PR #120's tip, before any look-ahead code existed) fails the identical five names - `cuda`, `hip`, `musa`, `cpu-x64-high-perf`, `ubuntu-22-hip-quality-check` - plus `macos`/`macos-latest-arm64`/`macos-latest-x64` and `openvino-windows-2022` (9 failures in total on that commit). All five failures on #126 are therefore pre-existing on the lineage, introduced by neither #126 nor #127.
- **Consumer-only neutrality re-verified on #126's own tip `ec23a599f`** (built in `impl-pra`, `--moe-lookahead 8` vs `0`, `-b 1 -ub 1 -c 256 --chunks 1`, same prompt): PPL **3.0613** both arms and all-logits md5 **`fc1da5b9e41d249e7da51791a934d561`** on both - byte-identical to the look-ahead-off baseline. Decode **43.29 t/s** (off) vs **43.52 t/s** (on), i.e. no cost. This is the acceptance-A claim and it holds: with no producer in the tree nothing calls the prefetch entries, so the knob is inert.
- **Also on that build:** `test-moe-cache` **OK** (exit 0) and `test-moe-cache --lookahead-prefetch-only` passes (`lookahead prefetch eviction guard OK`).
- **Owner decision (2026-09-14): HOLD the #126 merge until #120 merges**, then merge into `baseline-flash-next` after the automatic retarget. Rationale: merging now would land three look-ahead commits on `feat/763-reconcile-qwen4exp-mtp`, which is PR #120's own head branch, contaminating #120's diff with non-reconciliation commits. Standing merge pre-authorization is conditional on #120 landing. Watched by heartbeat `3efdd17a` (`watch-pr120-for-126`, agent target, 8-hour cadence, 30-day expiry): on #120 merge it confirms the retarget, checks that the failing set is still the pre-existing five, merges with `--merge` (the branch carries 3 separable commits), records the result and deletes itself. It carries a dedupe guard: if #126 is already MERGED it skips the merge and only deletes itself. Note for future verification: heartbeats are **not** enumerated by `list_schedules`/`inspect_schedule` (those cover new-agent schedules); a heartbeat id's existence is only checkable by the `create_heartbeat`/`delete_heartbeat` round trip. An earlier id (`0cd815f5`) was created, confirmed real by a successful `delete_heartbeat`, then deleted during that existence test.
- **Fork-level finding filed as its own issue:** **ddvnguyen/llama.cpp#128** - an extra `MUL_MAT` reading a router weight (`ffn_gate_inp`) changes model output while a neutral node is byte-identical, with the 7-row isolation matrix, the `use_counts`/`moe_candidate_discover_route` hypothesis and the `graph=unproven(14)` link. Independent of look-ahead.
- **Standing consult heartbeat deleted (owner decision, 2026-09-14):** heartbeat **`bcca67ae`** (`pr-a-30m-consult`, fired every 30 min asking this agent to re-consult the reviewer) is **deleted**; `delete_heartbeat` returned success, which is the only reliable existence check for heartbeats (they are not enumerated by `list_schedules`/`inspect_schedule` - confirmed again here, 15 schedules listed, none matching that id or name). Rationale: pure overhead on a parked, sequencing-gated track; the conditional merge is already autonomous under `3efdd17a`. **Accepted trade-off:** while #126 sits held, nothing now re-checks that its failing-check set stays the pre-existing five - `3efdd17a` re-verifies at retarget time and aborts the merge on any new failure, so a degraded lineage would be noticed only when #120 lands. Reviewer explicitly accepted that floor. No replacement cadence. If the track reopens (e.g. someone takes the early-router mailbox direction from the design doc) or #120 lands without `3efdd17a` firing, issue a one-shot prompt then.

## MoE look-ahead - prediction accuracy and per-GPU cost (addendum 2026-09-14)

- **Horizon: one MoE layer, never a token.** Layer L's post-attention state predicts layer L+1's selection for the same token, at each of the 47 layer transitions of this 48-layer model, so one prediction per layer per step. `--moe-lookahead N` is the number of experts predicted **per layer**, not a token count. There is no multi-token look-ahead anywhere in the producer.
- **Measured recall** (2 x 256-token decode, decode rows only, ground truth = each layer's own router selection from its real FFN input, 23,936 scored (step, layer) pairs). The model routes to **10** experts per layer per token:

| predicted width | recall (of predicted) | coverage (of the 10 used) |
| --- | --- | --- |
| 2 | 97.37% | 19.47% |
| 4 | 95.13% | 38.05% |
| 6 | 91.50% | 54.90% |
| 8 | 86.21% | 68.97% |
| 10 | 79.26% | 79.26% |

  Random overlap for 10 of 256 experts is 3.9% of the predicted width, so width 8 is about **22x chance**: the prediction is genuinely informative. RTX **3060** at width 8: **85.85% / 68.68%** - the same within numerical noise, so accuracy is device-independent.
- **Two measurement traps that had kept this unmeasured:** the committed debug instrument logs via `GGML_LOG_INFO`, which never reaches the server log at default verbosity (only `fprintf(stderr, ...)` did - the env gate was fine); and scoring inside `ggml_cuda_mul_mat_id_cached` only ever sees **`blk.47`**, because with `--moe-expert-cache-size 84` exactly one layer holds a legacy lease during decode and every other layer takes the `cache == nullptr` early return. Recall had to be scored in-graph instead.
- **Implication for the knob:** `--moe-lookahead 8` predicts fewer experts than the model uses (10), so it covers 69% of the actual selection; width 10 covers 79%. The prediction quality is not the weak part of the design - the blockers (inert consumer, CUDA-graph capture loss, output perturbation) are.
- **Cost per GPU, clean builds, look-ahead 8 vs 0 (2 x 256-token decode):** RTX 5060 Ti **43.23 -> 37.71 t/s (-12.8%)**; RTX 3060 **20.88 -> 18.05 t/s (-13.6%)**. The regression is the same on both devices and comes from the per-layer ids readback forcing CUDA graphs off, not from prediction compute (the sweep shows 35.97 t/s at width 10 with measurement nodes present, so width barely moves it).
- **Pool-install authority question settled (design invariant, not a bug):** `preinstall_legacy_pools` returning `-1` for all 144 targets is the design working, not a failure to patch. `acquire_legacy_cache` admits a new record only under `authority == LEGACY && !admission_closed`, which binds pool creation to a certified execution; an unauthenticated install would let a caller claim VRAM pools that certification never proved (the overcommit class `README.md:25` warns about). Layer L+1 is by definition not the authoritative group while L runs - its authority publishes when its own demand path runs, i.e. when a prefetch is already too late - so **cross-layer install is unreachable by design**. If the track reopens, the seam is authority publication at graph reserve/certification time (full layer inventory and slot budget known up front), which is a certification-contract change needing its own review. Design doc commit `29eb92ec4`.
- **Recorded in the design doc:** `docs/moe-lookahead-design.md` "Prediction accuracy (measured 2026-09-14)", commits **`c8caa07b6`**, **`4d7c6a26e`** (measured recall beside colibri's cited 71.6% PILOT reference on GLM-5.2, with the caveat that the models and expert counts differ so it is not like-for-like) and **`29eb92ec4`** (the Q1 authority ruling above) on `feat/moe-lookahead-p1`.
- **Caveat:** the accuracy runs add graph nodes to build the ground truth, so they are valid for recall only - not as output-equivalence runs (see blocker 1).

## Multi-turn agent workflow at 64K-80K on the APEX-I-Mini rig (addendum 2026-09-14)

- **Current ctx on this rig is 8192** for the APEX-I-Mini single-GPU path, on both cards. Serve config recovered verbatim from the live launcher scripts: `--split-mode layer -fit off -ngl 99 --n-cpu-moe 99 --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size N -c 8192 --parallel 1 --flash-attn on --jinja -t 6 --experimental-logs --load-mode none --decode-overlap --ple-prefetch`, port 18346 = 5060 Ti (CUDA0), 18336 = 3060 (CUDA1). The Hydra MoE profile `models.json` declares `n_ctx: 128000`, but that path is not running; the documented dual-GPU `--fit` variant is also 8192.
- **KV is not the constraint.** qwen4exp is hybrid: only **12 of 48 layers** hold a growing KV (GQA 2 KV heads x 256 + 128-wide indexer K) = **27,648 B/token at f16**; the other 36 are GDN linear-attention layers with a fixed ~112 MiB/slot recurrent state. So 80K is ~2.2 GiB and the native 262144 is 6.75 GiB f16 / 3.59 q8_0 / 1.90 q4_0. The binding constraint is VRAM for `--moe-expert-cache-size` slabs.
- **`-c 81920` (80K) works on both cards**, each verified with a full 8-turn growing-conversation agent session (client `infra/llama-baseline/multiturn-growth-test.sh`, prefix-cache reuse, 1 session x 8 turns x ~6,625 tokens/turn, 128 out/turn): zero `truncated = 1` and zero CUDA errors on both.

| GPU | cache N | VRAM after load / peak | resident at end | prefill t/s (6.6K -> 53K) | decode t/s (6.6K -> 53K) |
| --- | --- | --- | --- | --- | --- |
| RTX 5060 Ti | 84 | 14037 / 14557 of 16311 MiB | 53,044 tok | 271.8 -> 231.3 | 36.47 -> **28.26** |
| RTX 3060 | 42 | 10463 / 10931 of 12288 MiB | 53,075 tok | 84.0 -> 73.8 | 12.42 -> **10.52** |

- **3060 cache ceiling at 80K:** N=84 OOMs at load (`cudaMalloc(77414616 bytes) failed`), N=56 loads at 11651 MiB but OOMs on the first long request, **N=42 and N=28 run clean**. N=28 and N=42 measured the same throughput (10.91 vs 11.24 t/s at 14K depth), so at 80K the 3060 is depth-limited, not slab-limited - take **N=42** (more cache for free). Lowering N is the correct lever for the 3060, as suspected.
- **Decode depth tax on the 5060 Ti** (separate `max_tokens=64` probes at ctx 81920, empty-to-deep): 35.65 t/s at 1.7K prompt, 33.53 at 6.5K, 32.14 at 9.2K, 29.34 at 17.9K, 26.34 at 21.0K tokens; the session curve above extends the same trend to 53K.
- **Harnesses used:** `/mnt/WorkDisk/harness/multiturn-ctx/{mt.sh,depth.sh}` (durable, outside the repos) drive the client `infra/llama-baseline/multiturn-growth-test.sh <port> <sessions> <turns> <new_tok_per_turn> <out_tok_per_turn>` and a decode-vs-depth probe; the fork's own multi-turn options are `tests/bench/chat_multi_turn.py --deterministic --n-turns 10 --first-turn-tokens 5000 --growth-tokens 2000 --base-url ...` (10 turns, ~23K depth).
- **Caveat:** the token budget is nominal - the harness's synthetic filler tokenizes at ~1.7 tokens/word against its 1.3 assumption, so its "8000 tokens/turn" delivers ~6,625; a run squarely inside the 64K-80K band needs 10-11 turns at this turn size (measured prompt_tok at turn 8 = 53,044 of the 81,920 window).
- **Does look-ahead help on the 3060 at 80K? No.** A/B on the producer branch (`feat/moe-lookahead-p1` @ `29eb92ec4`, `impl-pra/build`), 3060, `-c 81920 -N 42`, identical requests per arm, separate server load per arm:

| depth | `--moe-lookahead 0` decode | `--moe-lookahead 8` decode | delta | prefill (both) |
| --- | --- | --- | --- | --- |
| 13,953 tok | 11.42 t/s | 10.51 t/s | **-8.0%** | 107.0 / 107.0 t/s |
| 47,516 tok | 9.78 t/s | 9.21 t/s | **-5.8%** | 94.6 / 94.7 t/s |

  Same sign and same class as the ctx-8192 measurements (5060 Ti 43.23 -> 37.71, 3060 20.88 -> 18.05 t/s). Prefill is untouched (look-ahead is decode-only), and the log again shows the inert consumer (`legacy cache authority`): every prediction is dropped, so this is pure cost with no benefit. The cost is structural - the per-layer ids readback forfeits CUDA graphs (`use_cuda_graph = false`); a deeper context does not change that, it only shrinks the graph's relative share. Caveat: the producer perturbs output (measurable blocker 1), so these runs compare throughput, not outputs.

## Look-ahead implementation vs colibri and FreeToken (2026-09-14)

Verdict: the **prediction** and the **paging** in `feat/moe-lookahead-p1` match the reference architecture. The **ids transport** is the defect - a synchronous device-to-host readback inside the decode step, which neither reference does, and which both costs throughput and forbids CUDA graph capture.

### Where the defect is, exactly (in-tree)

- `ggml/src/ggml-cuda/ggml-cuda.cu:3903-3908` (`ggml_cuda_moe_prefetch`): `cudaMemcpyAsync(..., cudaMemcpyDeviceToHost, ctx.stream())` into a pageable `std::vector`, then `cudaStreamSynchronize(ctx.stream())`. That is a full pipeline flush, 47 times per decode step.
- `ggml-cuda.cu:4561`: because of that sync, `ggml_cuda_graph_check_compability` sets `use_cuda_graph = false` for **any** graph containing `GGML_OP_MOE_PREFETCH` (precedent: `ggml_cuda_mul_mat_id_needs_sync` at the same decision point). Without the exclusion the run dies with `operation not permitted when stream is capturing`.
- The paging half is already correct and efficient: `moe-cache.cu:13420+` `ggml_cuda_moe_cache_prefetch_locked` enqueues every H2D on the cache's own `copy_stream` via `cudaMemcpyBatchAsync`, with `wait_for_compute=false`, no sync, slot reservation held under `cache->mu`, an LFRU eviction guard credited to colibri's `PILOT_EVICT_GUARD`, and rollback that leaves a failed miss to the demand path.

### Cost decomposition (3060, `-c 81920 -N 42`, identical requests, one load per arm)

| depth | look-ahead 0 | 0 + `GGML_CUDA_DISABLE_GRAPHS=1` | look-ahead 8 | capture loss | residual (readback + sync) |
| --- | --- | --- | --- | --- | --- |
| 13,953 tok | 11.42 t/s | 10.98 t/s | 10.51 t/s | -3.9% | **-4.3%** |
| 47,516 tok | 9.78 t/s | 9.47 t/s | 9.21 t/s | -3.2% | **-2.7%** |

So roughly half the penalty is lost graph capture and half is the in-step readback itself. Single sample per arm (separate loads); the direction is consistent at both depths. Note the recorded statement "disabling CUDA graphs on the baseline reproduces the baseline exactly" is a **numerics** result (blocker 1 isolation matrix, byte-identical logits), not a throughput result - it does not contradict this table.

### Reference comparison

| | routing signal | ids transport | movement primitive | CUDA graphs |
| --- | --- | --- | --- | --- |
| our P1 | re-run L+1's router on L's post-attention state, top-k width 8; 86.21% recall @ width 8 | **D2H memcpy + `cudaStreamSynchronize` in-step** | `cudaMemcpyBatchAsync` on the cache copy stream, LFRU guard, rollback to demand | **forced off** for every graph containing the op |
| colibri PILOT (GLM engine, `c/colibri.c:6418-6843`) | re-run the next layer's router on a stale hidden state; 71.6% top-8 one-layer-ahead recall, narrower K under real loads, eviction guard | prediction runs host-side (CPU engine); the pilot never crosses the device boundary | `posix_fadvise(WILLNEED)` by default, real `pread`/io_uring with `PILOT_REAL=1`, all on a dedicated pilot thread | GLM engine has no CUDA graphs at all; the DeepSeek-V4 sibling backend prefetches into a double-buffered VRAM bank on an aux stream (`dsv4_cuda_graph_begin/end`) |
| FreeToken (FlashML-org/FreeToken, arXiv:2608.16157) | **no predictor at all** (paper 6) | decode: none - router ids stay device-side and rewrite LRU indices in place; the only readback knob, `moe_prefill_hit_d2d`, is **off by default** | decode: zero-copy kernel gathers misses from pinned host banks; prefill: whole-layer double buffer on a dedicated `prefill_copy_stream` with events | decode path is captured in the CUDA graph |

### Conclusions

1. **The idea is not what is wrong.** colibri predicts one layer ahead the same way, and our recall (86.21% @ width 8) is higher than its 71.6%; its own docs record look-ahead losing on some hosts (`docs/tuning.md:193-194`) and an eviction guard that once dropped ~100% of speculations (`CHANGELOG.md:497`).
2. **The transport is wrong.** No reference synchronizes the compute stream for a prefetch decision: FreeToken's decode path never reads ids back to the host, and colibri's pilot never touches the device. Our per-layer flush is the anti-pattern both designs exist to avoid - and the fork already ships the correct primitive: `early_workspace` (`moe-cache.cu:4913-5140`) publishes predicted ids into a `cudaHostAllocMapped` mailbox from a device kernel (`moe_early_router_publish_copy`), has the copy worker poll it with `cuda::atomic_ref<..., thread_scope_system>`, and signals completion on the copy stream with `cuStreamWriteValue32` so the main stream waits with a capturable `cuStreamWaitValue32`.
3. **The regime is questionable.** FreeToken does no decode look-ahead at all; it looks ahead in **prefill**, where the transfer is routing-independent (whole layer, no signal) and no graph is at risk. That does not transfer to this rig: one expert slab is ~1.75 MiB (marginal VRAM ~84 MiB per +1 slot across 48 layers), so a full per-layer expert bank is ~0.9 GiB and FreeToken's 2xE staging requirement is ~1.8 GiB - against a ~3.5 GiB 3060 cache budget at N=42.
4. **Fix, if the track reopens:** move the ids to the mailbox and let the copy worker consume them; delete the `use_cuda_graph=false` rule; keep the paging as-is. That removes both the sync and the capture ban, because the paging is already async and advisory. It does **not** by itself make the feature win: blocker 2 (the consumer refuses all 144 pools under the authority invariant) and blocker 1 (the router-census perturbation changes logits) still stand, so this is a prerequisite, not a fix.

## Rig limits, placement inventory and instrument design (2026-09-14)

### Placement / pinning inventory (impl-pra @ `d6f995a74`)

- **Only-FFN placement already exists.** `--moe-expert-cache-size > 0` injects a `CUDA_MoE_Cached` buffer-type override ahead of user overrides (`src/llama.cpp:318-366`, `MOE_EXPS_PATTERN` at :350-352) and only `ffn_{up,down,gate,gate_up}_{,c}exps` match (`common/common.h:1157-1178`). It wins over `--cpu-moe` / `--n-cpu-moe`, which the server log itself warns is subsumed.
- **There is no pin/reserve concept for the always-needed part.** No per-tensor non-evictable flag and no VRAM reservation API. The only pin-like state is `slot_pin_count` inside the expert cache, transient for the GEMM lifetime (`moe-cache.cu:13204-13216`, `13403-13405`).
- **The largest always-needed tensor is not resident at all.** `per_layer_token_embd` (PLE) is created `TENSOR_READ_LAZY` (`src/models/qwen4exp.cpp:195-197`), 27,465 MiB, mmap'd and read row-by-row on every token (`src/llama-staged-input.cpp:39-133`); `--ple-prefetch` is the per-token prefetch hook.
- **The rig's `-ot per_layer_token_embd=CPU` is redundant and inert**: redundant because a `LAYER_INPUT` tensor already receives the CPU buft list (`src/llama-model.cpp:1508-1510`), inert because the lazy path returns before override matching (`llama-model-loader.cpp:1073-1104`).
- Consequence: "pin what is always needed, offload only FFN" needs a **new** reserve/pin mechanism at load time plus a policy that keeps those tensors out of every lazy/offload path. It is not a flag that exists today.

### Measured limit, 3060, `-c 81920 -N 42`, steady decode (256 tokens at 11.65 t/s = 85.85 ms/token)

- `nvidia-smi dmon` during decode: SM busy **88-100%**, DRAM util **54-55%**, power 87-102 W of ~170 W, pclk 2137 MHz (max), mclk 8301 MHz, gtemp 54-57 C. No dispatch gaps, no clock throttling.
- H2D volume for that request: `moe-grouped-decode ... h2d_banks=144573 h2d_bytes=91032985600` = 91.03 GB over 256 tokens = **355 MiB/token, about 4.1 GB/s** - far below a Gen4 x16 link's practical ceiling.
- The only wait metric the fork exposes (legacy cache path) is ~0.006 ms for the whole request.
- Therefore on this config the decode critical path is **GPU execution, not expert movement** - the offload machinery is not what limits the 3060. Consistent with the 5060 Ti being ~2.7x faster at the same config, which tracks its compute/memory ratio to the 3060.
- **What we cannot answer yet:** the 85.85 ms/token is not attributed. Every timing in the fork is host wall-clock `ggml_time_us()`; there is no `cudaEventElapsedTime`, no device-side clock, no attention/dense-vs-MoE split, no copy completion timestamp (enqueue only), no per-step graph-capture state, no PCIe utilisation.

### Instrument A - what limits our speed

Required, in order of value:

1. Per-layer phase timing on the device (or CUDA events around attention, MoE gather+GEMM, and the cache copy-wait) so each decode step reports its own breakdown. Nothing of the sort exists.
2. H2D **completion** timestamp against the time the slab is needed, so "did the copy finish before the demand path needed it" becomes answerable rather than inferred (today only `h2d_enqueue_ms` exists).
3. Per-step capture-state counter (captured replay vs re-dispatch) plus the `graph_update_required` rate; both are invisible beyond a global profile print.
4. PCIe utilisation sample, so "transfer-bound or not" does not require an external `dmon`.

### Instrument B - does the layer predict and load correctly

Already present: recall scoring (`ggml-cuda.cu:3289-3323`, gate `GGML_CUDA_MOE_LOOKAHEAD_DEBUG`), prefetch consumed vs evicted (`phase_prefetch_hits/misses/used`), `phase_prefetch_dropped`, prefetch H2D counts/bytes, per-request flush in `print_timings` (`tools/server/server-context.cpp:769-806`).

Gaps:

1. Recall is scorable only through the legacy cached MMID path, which on this rig is one layer per step (`blk.47`); every other layer runs certified-grouped and never surfaces ids to the host. Scoring must move outside that path.
2. `prefetch_dropped` is counted but never printed, and the install/drop warnings are `warn_once`, so drop rates are invisible under steady-state load.
3. Overlap correctness has no timestamp (instrument A item 2).
4. No counter separates "predicted but not yet copied", "copied but never used" and "used but not predicted" as three distinct populations; that separation is what turns an aggregate hit rate into a load-correctness verdict.

### Status

Both instruments and the pin/offload policy are **specified, not implemented**. Fork code changes need explicit owner approval and the look-ahead track stays parked; the reopening ruling is recorded on the producer branch as `d6f995a74`.

## Instruments A and B — measured, and kept in the tree (addendum 2026-09-14)

Both instruments were implemented on `feat/moe-lookahead-p1`, measured on the RTX 3060
(ctx 81920, cache 42, one 13,946-token prompt, 256 decode tokens, no look-ahead), and the
owner has asked that they stay in the tree rather than be reverted. Commits: `9d24345de`
(probes) and `570370183` (results section in `docs/moe-lookahead-design.md`), both on
`feat/moe-lookahead-p1`, pushed to `hydra-fork` and recorded on PR #127 (which is titled
NOT LANDABLE for the producer, not for these diagnostics).

Gates: `GGML_CUDA_MOE_PHASE_PROBE=1` (attribution) and `GGML_CUDA_MOE_LOOKAHEAD_DEBUG=1`
(recall off the lease). Both are inert with the env vars unset, and both turn themselves
off with a single stderr note if an event or a memcpy cannot be issued, which is what
happens under CUDA graph capture. An attribution needs `GGML_CUDA_DISABLE_GRAPHS=1`,
because replay steps never visit the host dispatch loop where the events are recorded.

Decode attribution (256 steps, 96.90 ms/step, 10.43 t/s; rows sum to 95.7 ms):

| bucket | ops | ms/step | share |
| --- | --- | --- | --- |
| ATTN | flash-attn, indexer, SSM/GDN, rope, softmax | 1.41 | 1.5% |
| MOE | `MUL_MAT_ID`, `MOE_PREFETCH`, `ARGSORT`, `TOP_K` | 67.18 | 69.3% |
| DENSE | every other matmul and elementwise op | 23.80 | 24.6% |
| OTHER | copy/cont/reshape/view/permute/transpose | 3.30 | 3.4% |
| PLE | `GET_ROWS` on `per_layer_token_embd` | 0.00 | 0.0% |

Dispatch state: `mode_legacy=3 mode_direct=253`. Prefill (28 ubatches, 4555 ms/ubatch):
ATTN 42.07, MOE 4005.38 (87.9%), DENSE 367.91, OTHER 11.49, PLE 0.00. Per-layer decode MoE
spreads over all layers (blk.00 2.91 ms, blk.24 1.12, blk.47 1.55). Device state in the same
window: SM 95-99%, DRAM controller 19-26%, 90-94 W, 55-56 C, 10,851 MiB resident.

- The expert H2D traffic is 337.6 MiB/token (90.62 GB in 256 tokens) = 3.71 GB/s. The link is
  **gen4 x4** (card is x16-capable, slot is wired x4), about 7.9 GB/s, so the traffic runs at
  ~47% of the ceiling — and the grouped telemetry reports `calls=ready=12240`,
  `ready_min=255`, i.e. every staged copy is complete before the op needs it.
- Conclusion: decode is MoE **execution**-bound on a saturated SM, not copy-bound. Prediction
  width, pinning or a device-side transport cannot move that, which closes the park.

Recall, scored off the legacy lease (instrument B), last 32 decode steps: 141 node
predictions, 1,410 used expert ids, 909 used-and-predicted, 501 used-not-predicted →
64.5% of the used experts were predicted, 80.6% of the predictions were used. Same order as
the in-graph measurement (86.2% of predicted / 69.0% coverage averaged over all layers).
The other two populations are structural zeros here (install refusals 0, prefetched slabs
evicted unused 0) because nothing is offered to the installer: the decode phase line reads
`ops=0` with `legacy cache authority`. Drop rates are now printed on the `moe-cache-phase`
line as `prefetch_dropped` and `evicted_prefetched_unused`.

Prefill is where the cache machinery actually runs, and the telemetry says it is mostly
wasted: 122,327 speculative installs, 4,720 ever used (3.9%), 114,496 evicted unused,
73.4 GiB of prefetch H2D and 3.8 GiB of ids D2H per request, against a 49.84% L1 hit rate
and no queue wait at all (copy_wait 0.29 ms over 423 events). Decode runs outside that
path (ops=9, the blk.47 lease) and on the grouped path instead. Those wasted installs were
invisible until `prefetch_dropped` and `evicted_prefetched_unused` were printed.

Independent finding: the rig's `-ot per_layer_token_embd=CPU` is redundant and inert —
layer-input tensors already land in the CPU buflist and the lazy path returns before user
override matching runs. PLE shows 0.00 ms/step above.

Known-bad, not caused by this work: `test-moe-cache` fails one assertion
(`tests/test-moe-cache.cpp:8348`, grouped-dispatch numerical equality, exit 1). It reproduces
identically and deterministically at the pre-instrumentation commit `d6f995a74`, so the
instrumentation adds no new failures; the suite was not green on this rig before it.

## MoE decode plan: issue #129, review verdicts, and the pin-share arm (2026-09-14)

Issue https://github.com/ddvnguyen/llama.cpp/issues/129 now carries the measured attribution,
the byte accounting and the tiered plan. A reviewer was asked to attack four claims; its
verdicts changed two numbers and the ordering.

Review verdicts, with what they mean for the plan:

- **MoE-bound: agree, with a caveat.** The 67.18 ms MoE bucket is not kernel-pure: the phase
  instrument opens events per run of same-(phase,layer) nodes, so host gaps land inside it.
  The legacy dispatch ops in decode contribute `op_cpu_ms=16.761` over 9 ops (1.86 ms/op) and
  `ids_d2h_ms=10.920` over 2 syncs, i.e. roughly 5-9 ms/step of the bucket. The conclusion
  survives because the 45 direct-dispatch layers (no id readback, no lease acquire) show
  ~1.2-1.4 ms per ~21.5 MiB on their own, the same 15-18 GB/s as the aggregate. A kernel-only
  number does not exist yet and is being produced with the repository's op-level perf harness.
- **Tier 0 already resident: agree.** Pin share is therefore Tier-1 scaffolding, not a Tier-0
  win: in decode the L1 slot pool only serves the 3 legacy ops/step (the 45 direct layers use
  staged banks), so there is nothing to protect until speculation shares the pool.
- **Preload L+1 already shipped: agree**, with a sharper reading of the cost. Under direct
  authority the producer installs 0 of 144 pools ("look-ahead could not install 144 of 144 MoE
  expert cache pools ... prefetch is inert"), so the measured -8..-13% of `--moe-lookahead` is
  the price of forcing `use_cuda_graph=false` plus the ids readback sync, not prefetch
  overhead. Fixing the transport removes the entire penalty; it does not by itself add
  throughput on this rig.
- **Ordering: disagreed.** The reviewer ranks raised M via the existing MTP/draft path ahead
  of split-K: the same work at M=512 costs 8.9 ms/token-equivalent against 96.9 ms at M=1, and
  that machinery is already present and certified in this fork, whereas split-K targets an
  efficiency ceiling not yet separated from booking overhead.
- **Falsified in my own issue text:** `--moe-expert-cache-prefetch`'s MMQ-at-M=1 suspicion (M=1
  already takes MMVQ: `ggml_cuda_moe_use_mmq` requires `n_tokens > 1`), and the acceptance
  table. The corrected link budget is `miss_fraction * 1.0817e9 B * tps <= 7.88e9`: 33% miss
  binds at ~22 t/s, so 30 t/s needs <=24% and 48 t/s needs <=15% (the earlier 74%/46% was
  wrong by ~3x). Both corrections are now in the issue.

Pin share ("Tier 0 pinning" implemented as reserved L1 slots), env-gated
`GGML_CUDA_MOE_CACHE_RESERVED`, default 0 = byte-identical behaviour: a slot joins the
reserve once the demand path has reused its expert twice, speculative installs then take only
unreserved slots and are counted in `prefetch_reserve_refused` when none remain. First arm at
RESERVED=24 measured `l1_reserved=0 prefetch_reserve_refused=0` in both phases while decode
showed `l1_hits=183 l1_misses=87 l1_evictions=0`, i.e. the reserve never engaged even though
promotion must have fired - the budget resolves to 0 at cache creation. Fix in flight; the
decode verdict (expected neutral: 0 evictions today) and the prefill verdict (expected
negative: 235 unique experts per ubatch against 42 slots) both depend on the corrected arm.

## Phase 2 hybrid split: CLOSED (2026-09-16)

Worktree `1q3ry0vb/baseline-flash-next`, branch `t-d1cf43b723`, build-only
`src/llama-cpp/build-hybrid-t2`, rig serial discipline, IDLE on exit.
Committed in submodule as `0fc51e039` (4 files: `common/arg.cpp`,
`src/llama.cpp`, `ggml/src/ggml-cuda/moe-cache.cu`, `.cuh`). NOT pushed.
Other worker's dirt (`CMakeLists.txt`, `src/CMakeLists.txt`,
`src/llama-context.cpp`) left untouched.

- **Phase C close-out.** Record-only hook (`GGML_CUDA_MOE_DEVICE_SPLIT=shadow`) dumped
  live x + plan ids + miss slices (`/tmp/shadow-rec/rec-*.bin`); x-liveness proven
  byte-identical across runs; standalone validator (`shadow-validate`, CPU vs plain-CUDA
  backends) FAILed with bounded deltas (`maxd_out` 0.039-0.095, `mean_out` 0.012-0.020 vs
  `maxout` 3.8-6.4) — mechanism: CUDA `__hmul2` fp16 scale products vs CPU fp32
  (`vecdotq.cuh:158,229,272`), amplified by x-outliers ~433. Accepted as bounded
  equivalence (CPU is the accurate side).
- **Phase D move (Approach A).** `GGML_CUDA_MOE_DEVICE_SPLIT=cpu` recomputes cache-miss
  expert rows on a persistent CPU backend (mutex-guarded, same graph as the validator)
  and overwrites the DOWN output rows in `finish_graph_group` after the
  `defer_completion` early return (capture-safe: never runs during capture, replay never
  reaches per-node finish) and before `finish_decode`, so the MIX consumer sees replaced
  rows via stream order. Staging stays on: any validation/compute failure skips the
  overwrite and GPU values stand (`cpu_replace_skip`). DIRECT/eager only — requires
  `GGML_CUDA_DISABLE_GRAPHS=1`; env off is a single bool (zero behavior change).
- **Gates, all PASS (cache 53, ctx 81920, no-graph, greedy-200 + PPL-15):**
  no-graph P0 baseline SERVER `decode_tps=11.05` / greedy `6ceeb608` / PPL `15.7524`;
  OFFCHECK (new binary, env off) byte-identical `6ceeb608` + PPL `15.7524` exact;
  CPUREPLACE (cpu on) greedy `6ceeb608` BYTE-IDENTICAL over 200 tokens through
  `cpu_replace_dispatches=9093 cpu_replace_rows=40142 cpu_replace_skip=0`, PPL `15.7524`
  exact, output coherent (no row-0 garbage). Graphs on/off do not change greedy output.
- **t/s report:** hybrid-cpu `5.89` vs no-graph P0 `11.05` vs 21.36 MTP ref vs 11.60
  with-graphs P0. The ~47% regression is expected and honest: CPU recompute sits on the
  critical path while staging still burns the full GPU GEMM (no savings yet). Phase E
  (skip staging for CPU-owned misses) is where the win comes from; Phase D de-risked it
  by proving the CPU path end-to-end correct.
- **Phase E: NO-GO (design-first, no implementation).** The demand "fill" is not an H2D
  memcpy but a zero-copy device gather kernel over PCIe (`moe_grouped_gather_decode`,
  ~0.55-0.66 ms/dispatch for ~9.9 MB at 4.41 misses/dispatch); skipping it keeps the
  1.74 ms/dispatch CPU recompute, nets ~7.0-7.6 t/s vs 11.05 P0, and converts the
  best-effort GPU fallback into silent corruption (no resupply path exists). The only
  hybrid that beats P0 skips GPU GEMM rows entirely (compaction + MIX renormalization)
  — scoped as a separate future epic, not this track. Track CLOSED.
