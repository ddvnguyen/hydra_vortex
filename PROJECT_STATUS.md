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

## Leader Contract

- **ADR 0002 signed 2026-08-21 (Option A):** `ddvnguyen ↔ muse-spark-1.2-contributor` — standing until superseded, scoped to **llama.cpp baseline (2×RTX vanilla)** running authority (build `--parallel 8`, `infra/llama-baseline/` compose, ctx `98304→65536` yarn `scale 4`, harness `dsh`/`pi` via `:8080`). Revocable via doc removal + this file update; merges still require explicit user confirmation per `CLAUDE.md §4`. Hermes fleet `v2.1.1` stays superseded (`f8b322c73`). No GPG/HMAC. Ref: `docs/decisions/0002-leader-contract.md`.

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
