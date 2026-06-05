# Hydra Project Runbook

**For:** AI agents and engineers picking up this project cold.  
**Updated:** 2026-05-28  
**Status:** M0 ✅ | Hydra.Shared ✅ | Store ✅ | Agent ✅ | Coordinator ✅ | M2 chunked dedup ✅ | Observability ✅ Prometheus+Grafana+Loki | System test ✅

---

## 1. What Is Hydra

Multi-GPU LLM inference system. Routes requests across RTX 5060 Ti (host) and Tesla P100 (KVM VM). Migrates 800 MB KV cache state between GPUs so a session prefilled on RTX can decode on P100 **without re-prefill** (which would take 12 minutes at 80K context).

**The only reason it works:** llama.cpp's `llama_state_seq_get_data` / `llama_state_seq_set_data` API can serialize and restore a slot's full KV cache. Cross-GPU restore is confirmed working (`cache_n=2964` verified in POC).

---

## 2. System Architecture

```
Client (curl / OpenWebUI / Cline)
  │  OpenAI-compatible HTTP
  ▼
Coordinator :9000       [Python/FastAPI]   ✅ M1 complete
  │ Hydra binary RPC           │ Hydra binary RPC
  ▼                            ▼
Agent RTX :9601         Agent P100 :9602   [C#/.NET 10]   ✅ M0 + M2 chunked
  │ HTTP local                 │ HTTP local
  ▼                            ▼
llama-server :8080      llama-server :8086 [C++ fork]      ✅ hydra-state-streaming
  │ Hydra binary RPC           │ Hydra binary RPC
  └──────────┬─────────────────┘
             ▼
           Store :9500                     [C#/.NET 10]   ✅ M0 + M2 chunked dedup
           /mnt/llm-ram/store/ (tmpfs)
```

**Critical rule:** All inter-service traffic uses **Hydra binary RPC** (see Section 5).  
HTTP only at two edges: Client → Coordinator, and Agent → local llama-server.

---

## 3. Hardware

| Machine | GPU | CUDA Arch | Address | Role |
|---|---|---|---|---|
| Host (i7-12700K, 64 GB) | RTX 5060 Ti 16 GB | sm_120 | localhost | Prefill + llama:8080 + Agent:9601 + Store:9500 + Coordinator:9000 |
| KVM VM | Tesla P100 16 GB | sm_60 | 192.168.122.21 | Decode + llama:8086 + Agent:9602 |

**Model:** `Darwin-36B-Opus-APEX-I-Balanced.gguf` (~25.5 GB, qwen35moe arch)  
**Model path:** `/mnt/SSD/` on host (mounted into llama container).

---

## 4. Repository Layout

```
hydra_vortex/
├── CLAUDE.md                    ← read first (AI handoff context)
├── PROJECT_PLAN.md              ← architecture + milestones
├── specs/rpc-protocol.md        ← binary wire format spec (authoritative)
├── docs/
│   ├── RUNBOOK.md               ← this file
│   ├── milestone-0-mvp.md       ← M0 task breakdown with code samples
│   └── hydra_llama_cpp_PLAN.md  ← llama fork implementation log
├── infra/
│   ├── docker-compose.hydra.yml ← Hydra core services (host networking)
│   └── docker-compose.infra.yml ← Observability stack + exporters
├── src/Hydra.sln              ← C# solution (all projects)
├── pyproject.toml               ← Python deps (coordinator + tests)
└── src/
    ├── llama-cpp/               ← git submodule, branch: hydra-state-streaming
    ├── Hydra.Shared/            ← C# RPC base lib [✅ 29 tests pass]
    ├── Hydra.Store/             ← C# tmpfs KV store + chunk engine [✅ 44 tests pass]
    ├── Hydra.Agent/             ← C# GPU sidecar + chunk cache [✅ 23 tests pass]
    ├── core/                    ← C# .NET — Hydra.Shared, Store, Agent
    │   ├── Tests.Shared/        ← [✅ 29 tests pass]
    │   ├── Tests.Store/         ← [✅ 44 tests pass incl. ChunkEngine + ChunkStore]
    │   ├── Tests.Agent/         ← [✅ 23 tests pass incl. LocalChunkCache]
    │   └── Tests.Integration/   ← [✅ 18 tests pass incl. chunked store + agent chunked]
    ├── coordinator/             ← Python FastAPI + prefix checkpoint [✅ 46 tests]
    │   └── lib/                 ← RPC client + logging shared lib
    ├── tests/                   ← Python system + unit tests
    └── llama-cpp/               ← C++ fork (submodule)
```

---

## 5. Hydra Binary RPC Wire Format

**Source of truth:** `specs/rpc-protocol.md`  
**C# implementation:** `src/core/Hydra.Shared/Protocol.cs`  
**C++ constants:** `src/llama-cpp/tools/server/server-rpc.h`

### Request Header (16 bytes, little-endian)
```
Offset  Size  Type     Field
0       2     uint16   magic = 0x4859 ("HY")
2       1     uint8    op — operation code
3       1     uint8    flags — 0x00=normal
4       2     uint16   key_len
6       8     uint64   payload_len
14      2     uint16   trace_len
```

### Request Body
```
[key_len bytes: key string]
[trace_len bytes: trace_id string]
[payload_len bytes: raw payload]
```

### Response Header (12 bytes, little-endian)
```
Offset  Size  Type     Field
0       1     uint8    status (0x00=OK, 0x01=NOT_FOUND, 0x02=ERROR, 0x04=BUSY)
1       3     uint24   meta_len (LE, 3 bytes only)
4       8     uint64   payload_len
```

### Response Body
```
[meta_len bytes: UTF-8 JSON metadata]
[payload_len bytes: raw payload]
```

### Op Codes
| Hex | Name | Used By | Milestone |
|---|---|---|---|
| 0x01 | PUT | Store | M0 |
| 0x02 | GET | Store | M0 |
| 0x03 | DEL | Store | M0 |
| 0x04 | STAT | Store | M0 |
| 0x05 | LIST | Store | M0 |
| 0x10 | PUT_CHUNKED | Store | M2 |
| 0x11 | GET_CHUNKED | Store | M2 |
| 0x12 | SYNC_PLAN | Store | M2 (impl; unused — see #58) |
| 0x13 | PUSH_CHUNKS | Store | M2 (impl; unused — see #58) |
| 0x14 | PUT_META | Store | M2 |
| 0x20 | SAVE_STATE | Agent | M0 (raw; superseded by 0x26) |
| 0x21 | RESTORE_STATE | Agent | M0 (raw; superseded by 0x27) |
| 0x22 | SLOT_STATUS | Agent | M0 |
| 0x23 | SLOT_ERASE | Agent | M0 |
| 0x24 | NODE_HEALTH | Agent | M0 |
| 0x25 | (retired) | — | completions are HTTP-direct |
| 0x26 | SAVE_STATE_CHUNKED | Agent | M2 (active default) |
| 0x27 | RESTORE_STATE_CHUNKED | Agent | M2 (active default) |
| 0x30 | STATE_GET | llama direct | M0 |
| 0x31 | STATE_PUT | llama direct | M0 |
| 0x32 | STATE_META | llama direct | M0 |
| 0x33 | GET_MANIFEST | Store | M2 |

### Connection rules
- Persistent: client sends multiple sequential requests on one TCP connection
- No pipelining: wait for response before sending next request
- Bad magic → server closes connection immediately
- Error payload → always non-zero payload_len if error detail included

---

## 6. Current Implementation Status

### ✅ M0.0 — llama.cpp Fork (COMPLETE)

**Branch:** `src/llama-cpp` on `hydra-state-streaming`  
**Commits:**
1. `ee9eddba5` — M0: Hydra RPC listener (STATE_GET/PUT/META)
2. `1975b20e6` — M1: Task-queue routing (thread safety)
3. `69d49e0a4` — M1+M2: Async & zero-copy STATE_GET
4. `976d51396` — fix: is_transferring in RPC STATE_META + M2 close(fd) on stream error

**New files:**
- `tools/server/server-rpc.h` — wire-format constants
- `common/common.h` — added `int32_t rpc_port = 0`
- `common/arg.cpp` — added `--rpc-port PORT` flag + `LLAMA_ARG_RPC_PORT` env
- `tools/server/server-context.h` — added `start_rpc_server(int port)` declaration
- `tools/server/server-context.cpp` — +500 lines: RPC handlers, task cases, M2 streaming
- `tools/server/server-task.h` — 3 new task types + hydra_action struct + result struct
- `tools/server/server-task.cpp` — to_json() for hydra result (includes `is_transferring`)
- `tools/server/server.cpp` — HTTP `/slots/:id/state/meta` + `start_rpc_server()` call
- `tools/server/server-http.h/cpp` — added `put()` method
- `include/llama.h` — added `llama_state_seq_get_data_to_fd()`
- `src/llama-context.h/cpp` — `llama_io_write_socket` + `state_seq_get_data_to_fd()`

**Fixes applied (2026-05-27):**
1. `STATE_META` HTTP response JSON now includes `is_transferring` field
2. `STATE_META` timeout 1s → 5s (inference batch may congest queue)
3. M2 mid-stream error: `header_sent` flag prevents double-write; `close(fd)` signals truncation to client
4. `SO_RCVTIMEO` (120s) on accepted RPC sockets — prevents hung connections

### ✅ M0.1 — Hydra.Shared (COMPLETE, 29 tests pass)

`src/core/Hydra.Shared/` — C# base library used by Store and Agent.

| File | Purpose | Status |
|---|---|---|
| `Protocol.cs` | Wire format pack/unpack | ✅ |
| `RpcServer.cs` | Abstract TCP server base | ✅ |
| `RpcClient.cs` | TCP client with reconnect | ✅ |
| `RpcResponse.cs` | Response model | ✅ |
| `AsyncEnumerableStream.cs` | Stream adapter | ✅ |
| `HydraLogging.cs` | Serilog JSON logging | ✅ |

```bash
# Verify:
dotnet test src/core/Tests.Shared/Tests.Shared.csproj
# Expected: Passed! - Failed: 0, Passed: 29
```

### ✅ M0.2 — Hydra.Store (COMPLETE, builds + 23 tests pass)

`src/core/Hydra.Store/` — full implementation.

| File | Status |
|---|---|
| `StorageEngine.cs` | ✅ PUT/GET/DEL/STAT/LIST + path traversal guard |
| `ChunkEngine.cs` | ✅ Chunk splitting, SHA-256, DiffPlan (M2) |
| `ChunkStore.cs` | ✅ Content-addressed storage, dedup, GC, manifests (M2) |
| `ChunkModels.cs` | ✅ ChunkRef, Manifest, result models (M2) |
| `StoreServer.cs` | ✅ RPC dispatch + sendfile + chunked ops handlers (M2) |
| `StoreMetrics.cs` | ✅ prometheus-net counters + histograms + chunk metrics (M2) |
| `StoreConfig.cs` | ✅ appsettings binding |
| `StatResult.cs` | ✅ |
| `Program.cs` | ✅ DI wiring |

`Tests.Store/` — 44/44 pass ✅ (23 M0 + 21 M2).

### ✅ M0.3 — Hydra.Agent (COMPLETE, builds + 13 tests pass)

`src/core/Hydra.Agent/` — full implementation.

| File | Status |
|---|---|
| `LlamaClient.cs` | ✅ GetState/PutState/GetStateMeta + HTTP health/slots/erase |
| `StateHandler.cs` | ✅ SaveToStore + RestoreFromStore + chunked save/restore (M2) |
| `LocalChunkCache.cs` | ✅ LRU session chunk hash cache (M2) |
| `AgentServer.cs` | ✅ SAVE/RESTORE/SLOT_STATUS/NODE_HEALTH/SLOT_ERASE + chunked handlers (M2) |
| `AgentMetrics.cs` | ✅ prometheus-net counters + histograms |
| `AgentConfig.cs` | ✅ appsettings binding + chunk cache dir (M2) |
| `Program.cs` | ✅ DI wiring |

`Tests.Agent/` — 23/23 pass ✅ (13 M0 + 10 M2).

**Known design issue (P2):** `RestoreFromStoreAsync` makes 2 Store GET requests — first one buffers  
the 800 MB payload to extract size from meta, then discards and re-streams via second GET.  
Fix: use OpCode.Stat to get size first, then single streaming GET.

### ✅ M0.4 — System Test (COMPLETE)

`tests/system/test_system.py` — async pytest: prompt→save→restore→continuation flow.  
4 assertions: choices present, save returns size, restore returns restored=true, cache_n > 0.  
Uses `httpx.AsyncClient` for HTTP and `coordinator.lib.rpc_client.RpcClient` for RPC.  
Requires all 6 services running (two llama-servers, Store, two Agents).  
Skipped by default — run with `pytest -m system -s`.

### ✅ M1 — Coordinator (COMPLETE, 42 tests pass)

`src/coordinator/` — full implementation.

| File | Status |
|---|---|
| `session_table.py` | ✅ LRU tracking, mark_evicted, get_sessions_on_node |
| `routing.py` | ✅ Session affinity → store_restore → long_prompt_rtx → least_loaded |
| `health.py` | ✅ Background poll every 10s; marks unhealthy after 3 failures; polls immediately on start |
| `state_manager.py` | ✅ save/restore/migrate/evict_lru via Agent RPC |
| `proxy.py` | ✅ Non-streaming + SSE streaming proxy to llama-server |
| `router.py` | ✅ POST /v1/chat/completions, GET /health, GET /status, GET/DELETE /sessions, POST migrate |
| `app.py` | ✅ Single-instance singletons; lifespan only starts/stops HealthMonitor |
| `config.py` | ✅ pydantic-settings config |
| `metrics.py` | ✅ prometheus-client counters |
| `main.py` | ✅ uvicorn entry point |

`coordinator/lib/rpc_client.py` — ✅ correct 16-byte wire format (`<HBBHqH`).

**Missing tests (P1):** No `test_health.py`, `test_proxy.py`, or integration test `test_coordinator_agent.py`.

### ✅ M2 — Chunked Dedup & Prefix Checkpoints (COMPLETE)

**M2.1.1 — Chunk Engine**
`src/core/Hydra.Store/ChunkEngine.cs` — splits KV state into 1 MB chunks, SHA-256 hashing, `CreateManifest`/`LoadManifest`, `DiffPlan` (finds missing chunks).  
`src/core/Hydra.Store/ChunkModels.cs` — `ChunkRef`, `Manifest`, `ChunkedPutResult`, `ChunkedGetMeta`.

**M2.1.2 — ChunkStore**
`src/core/Hydra.Store/ChunkStore.cs` — content-addressed chunk storage (`chunks/{hash}.dat`), dedup (same hash stored once), manifest save/load (`manifests/{session_id}.json`), GC orphan cleanup, startup index rebuild, stats.

**M2.1.3 — Store Server Chunked Ops**
`src/core/Hydra.Store/StoreServer.cs` — 4 RPC handlers:
- `HandlePutChunkedAsync` (0x10) — stores 1 MB chunks with dedup
- `HandleGetChunkedAsync` (0x11) — returns only chunks client doesn't have
- `HandleSyncPlanAsync` (0x12) — diff plan (which hashes missing)
- `HandlePushChunksAsync` (0x13) — batch store from sync plan

**M2.2.1 — Agent Chunked Transfer**
`src/core/Hydra.Agent/StateHandler.cs` — `SaveToStoreChunkedAsync` / `RestoreFromStoreChunkedAsync` with `ChunkHashTeeStream`.
`src/core/Hydra.Agent/LocalChunkCache.cs` — LRU session-based hash cache (JSON files), skips llama PUT if all chunks cached.
`src/core/Hydra.Agent/AgentServer.cs` — `HandleSaveStateChunkedAsync` / `HandleRestoreStateChunkedAsync` handlers.

**M2.3 — Coordinator Prefix Checkpoint**
`src/coordinator/state_manager.py` — `save_prefix_checkpoint` / `restore_prefix_checkpoint`.
`src/coordinator/router.py` — `POST /prefix/{name}/save`, `POST /prefix/{name}/restore`.
`src/coordinator/config.py` — `prefix_checkpoint_name`, `prefix_checkpoint_enabled`.

**M2 Test Counts:**
| Suite | Tests | Status |
|-------|-------|--------|
| `ChunkEngineTests` | 10 | ✅ All pass |
| `ChunkStoreTests` | 11 | ✅ All pass |
| `LocalChunkCacheTests` | 10 | ✅ All pass |
| `ChunkedStoreIntegrationTests` | 8 | ✅ All pass |
| `AgentChunkedSaveRestoreTests` | 4 | ✅ All pass |
| `test_prefix_checkpoint.py` | 4 | ✅ All pass |

```bash
# Run M2 tests
dotnet test src/core/Tests.Store --filter "FullyQualifiedName~Chunk" -v m
dotnet test src/core/Tests.Agent --filter "FullyQualifiedName~ChunkCache" -v m
dotnet test src/core/Tests.Integration --filter "FullyQualifiedName~Chunked" -v m
pytest tests/coordinator/test_prefix_checkpoint.py -v
```

**Known gap:** Coordinator prefix checkpoint uses raw `SaveState`/`RestoreState` (Agent RPC ops), not chunked `PutChunked`/`GetChunked`. Content-addressed dedup is bypassed for prefix checkpoints. To fix: Agent should auto-upgrade to chunked path or coordinator should call chunked ops.

---

## 7. Environment Setup

### 7.1 Host Machine (RTX)

```bash
# 0. Prerequisites: podman + podman-compose
podman --version
podman-compose --version

# 1. Check .NET 10
dotnet --version   # must be 10.x

# 2. Check Python 3.13+
python3 --version

# 3. Install Python deps
pip install -e ".[dev]"

# 4. Build llama.cpp — RTX (sm_120, cuBLAS)
#    Output: src/llama-cpp/build_sm120/bin/llama-server  (gitignored, stays on filesystem)
cd src/llama-cpp
cmake -B build_sm120 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON
cmake --build build_sm120 --target llama-server -j4
cd ../..

# 5. Build llama.cpp — P100 (sm_60)
#    Output: src/llama-cpp/build_sm60/bin/llama-server  (rsynced to VM by scripts)
cd src/llama-cpp
cmake -B build_sm60 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_NATIVE=ON
cmake --build build_sm60 --target llama-server -j4
cd ../..

# 6. One-time P100 VM setup (no sudo needed)
bash scripts/setup-p100.sh
```

### 7.2 P100 VM (192.168.122.21)

The P100 VM runs llama-server as a **user systemd service** — no `sudo` required.

```bash
# One-time setup (run from repo root on the host):
bash scripts/setup-p100.sh

# This installs:
#   ~/.config/systemd/user/llama-p100.service  (on the VM)
#   /usr/local/bin/promtail                    (on the VM, for log shipping)

# Day-to-day: managed by scripts/start-env.sh
# Manual control:
ssh hydra-p100 "systemctl --user status llama-p100"
ssh hydra-p100 "systemctl --user restart llama-p100"

# Logs:
ssh hydra-p100 "journalctl --user -u llama-p100 -f"
```

SSH alias `hydra-p100` must be configured in `~/.ssh/config`:
```
Host hydra-p100 192.168.122.21
  HostName 192.168.122.21
  User vm1
  IdentityFile ~/.ssh/vm_agent_01
  IdentitiesOnly yes
```

### 7.3 Submodule Check

```bash
cd /mnt/WorkDisk/Workplace/hydra_vortex
git submodule status
# Must show: src/llama-cpp on branch hydra-state-streaming
# If not:
git submodule update --init
cd src/llama-cpp && git checkout hydra-state-streaming
```

---

## 8. Running Services

### 8.0 Quick Start

```bash
# Start everything (idempotent, safe to re-run)
bash scripts/start-env.sh

# RTX only (if P100 VM unavailable)
bash scripts/start-env.sh --skip-p100
```

Starts: Store + Agents + Coordinator + Observability (podman-compose), llama-server RTX
(container with `build_sm120/` volume), llama-server P100 (user systemd on VM), and host
log shipping services (`container-log-shipper` + `promtail` — systemd --user).

Manual verification:
```bash
curl -s http://localhost:9501/debug    # Store health
curl -s http://localhost:9611/debug    # Agent RTX health
curl -s http://localhost:9000/health   # Coordinator health (healthy / degraded)
curl -s http://localhost:9091/targets  # Prometheus scrape targets
open http://localhost:3000             # Grafana dashboard
```

### 8.1 Start Order (Native)

If running outside Docker, always start in this order:
1. **Store** (no dependencies)
2. **llama-server RTX** + **llama-server P100** (independent)
3. **Agent RTX** + **Agent P100** (need Store + llama running)
4. **Coordinator** (needs both Agents)

### 8.2 llama-server RTX

Runs as a **container** via `infra/llama-rtx-node/docker-compose.yml`.
The container mounts `src/llama-cpp/build_sm120/` directly — no copy needed.

```bash
cd infra/llama-rtx-node
podman-compose up -d
# Connect to internal network so Agents can resolve "llama-cpp" hostname:
podman network connect hydra_default llama-cpp
```

Full launch args are in `infra/llama-rtx-node/docker-compose.yml`.  
Ports: HTTP `:8080`, Hydra RPC `:9501`

### 8.3 llama-server P100

Runs as a **user systemd service** on the P100 VM (no sudo required).

```bash
# Start
ssh hydra-p100 "systemctl --user start llama-p100"

# Status / logs
ssh hydra-p100 "systemctl --user status llama-p100"
ssh hydra-p100 "journalctl --user -u llama-p100 -f"

# Restart after binary update
ssh hydra-p100 "systemctl --user restart llama-p100"
```

Service file: `infra/systemd/llama-p100-user.service`  
Binary: deployed to VM via `rsync` from `src/llama-cpp/build_sm60/bin/llama-server`  
Ports: HTTP `:8086`, Hydra RPC `:9502`  
Model load time: ~90 seconds

### 8.4 Hydra Store (Docker Compose)

Store runs in Docker. See `8.0 Quick Start` for `docker compose up`.

For native debugging:
```bash
cd src/core/Hydra.Store
dotnet run --configuration Release
# Config via env vars:
# HYDRA_STORE_DIR=/mnt/llm-ram/store
# HYDRA_STORE_PORT=9500
# HYDRA_STORE_DEBUG_PORT=9501
```

Metrics: `GET :9501/metrics`

### 8.5 Hydra Agent RTX (Docker Compose)

Agent RTX runs in Docker. See `8.0 Quick Start`.

For native debugging:
```bash
cd src/core/Hydra.Agent
dotnet run --configuration Release
# Config:
# HYDRA_AGENT_PORT=9601
# HYDRA_AGENT_NODE_NAME=rtx
# Agent__LlamaUrl=http://localhost:8080
# Agent__StoreHost=127.0.0.1
# Agent__StorePort=9500
```

### 8.6 Hydra Agent P100 (Native, on VM)

```bash
# On VM — same binary, different config:
# HYDRA_AGENT_PORT=9602
# HYDRA_AGENT_NODE_NAME=p100
# Agent__LlamaUrl=http://localhost:8086
# Agent__StoreHost=192.168.122.1   ← host machine IP from VM
# Agent__StorePort=9500
```

### 8.7 Coordinator (Docker Compose)

Coordinator runs in Docker. See `8.0 Quick Start`.

For native debugging:
```bash
pip install -e ".[monitoring]"
hydra-coordinator
# or: uvicorn coordinator.main:app --port 9000
# Config via env:
# HYDRA_COORD_HOST=0.0.0.0
# HYDRA_COORD_PORT=9000
```

---

### 8.8 Observability Stack

The Docker Compose setup includes:

| Service | Port | Purpose |
|---------|------|---------|
| Prometheus | 9090 | Metrics scraped from Store/:9501, Agent/:9611, Coordinator/:9000 |
| Loki | 3100 | Log storage, ingested via Promtail scraping Docker container logs |
| Promtail | 9080 | Docker log scraper, sends to Loki with `service` + `component` labels |
| Grafana | 3000 | Dashboards for metrics + logs with trace_id filter |

```bash
# Prometheus targets
curl -s http://localhost:9090/targets | jq

# Loki readiness
curl -s http://localhost:3100/ready

# Grafana
open http://localhost:3000  # anonymous admin, no login
```

Serilog JSON logs include `trace_id`, `component`, `source_context` — Promtail scrapes them from Docker's json-file logs. Use the Grafana dashboard's "Trace ID" filter to see all logs for a single request.

---

## 9. Verification Tests

### 9.1 llama-server Health (HTTP)

```bash
# RTX
curl -s http://localhost:8080/health
# Expected: {"status":"ok","slots_idle":4,"slots_processing":0}

# P100
curl -s http://192.168.122.21:8086/health
```

### 9.2 Slot Metadata via HTTP (debug endpoint)

```bash
curl -s http://localhost:8080/slots/0/state/meta
# Expected: {"slot_id":0,"state_size":847003648,"n_past":0,"is_processing":false,"is_transferring":false}
# n_past, is_processing, and is_transferring now included in HTTP endpoint.
```

### 9.3 STATE_META via RPC (Python)

```python
import socket, struct, json

def hydra_meta(host, port, slot_id=0):
    s = socket.create_connection((host, port))
    key   = str(slot_id).encode()
    trace = b'runbook-test'
    # Request header: magic(2) op(1) flags(1) key_len(2) payload_len(8) trace_len(2)
    hdr = struct.pack('<HBBHqH', 0x4859, 0x32, 0, len(key), 0, len(trace))
    s.sendall(hdr + key + trace)
    # Response header: status(1) meta_len(3LE) payload_len(8)
    res_hdr = s.recv(12)
    status   = res_hdr[0]
    meta_len = res_hdr[1] | (res_hdr[2] << 8) | (res_hdr[3] << 16)
    meta = json.loads(s.recv(meta_len))
    s.close()
    return status, meta

status, meta = hydra_meta('localhost', 8090)
print(f"STATUS={status:#04x} META={meta}")
# Expected: STATUS=0x00 META={'slot_id': 0, 'n_past': 0, 'state_size': 847003648, 'is_processing': False, 'is_transferring': False}
```

### 9.4 STATE_GET → STATE_PUT Round-Trip (Python)

```python
import socket, struct, json

def state_get(host, rpc_port, slot_id=0):
    """Returns (n_past, state_bytes)"""
    s = socket.create_connection((host, rpc_port))
    key = str(slot_id).encode()
    trace = b'runbook-get'
    hdr = struct.pack('<HBBHqH', 0x4859, 0x30, 0, len(key), 0, len(trace))
    s.sendall(hdr + key + trace)

    res_hdr = s.recv(12)
    status = res_hdr[0]
    assert status == 0x00, f"STATE_GET failed: status={status:#04x}"
    meta_len = res_hdr[1] | (res_hdr[2] << 8) | (res_hdr[3] << 16)
    payload_len = struct.unpack_from('<Q', res_hdr, 4)[0]

    meta = json.loads(s.recv(meta_len))
    print(f"GET meta: {meta}")  # n_past, state_size

    data = b''
    while len(data) < payload_len:
        chunk = s.recv(min(65536, payload_len - len(data)))
        if not chunk: break
        data += chunk
    s.close()
    return meta['n_past'], data

def state_put(host, rpc_port, slot_id, data):
    """Returns restore metadata"""
    s = socket.create_connection((host, rpc_port))
    key = str(slot_id).encode()
    trace = b'runbook-put'
    hdr = struct.pack('<HBBHqH', 0x4859, 0x31, 0, len(key), len(data), len(trace))
    s.sendall(hdr + key + trace + data)

    res_hdr = s.recv(12)
    status = res_hdr[0]
    meta_len = res_hdr[1] | (res_hdr[2] << 8) | (res_hdr[3] << 16)
    meta = json.loads(s.recv(meta_len)) if meta_len else {}
    s.close()
    assert status == 0x00, f"STATE_PUT failed: {meta}"
    return meta

# Usage — run after sending a prompt to RTX:
n_past, kv_data = state_get('localhost', 8090, slot_id=0)
print(f"Got {len(kv_data)/1e6:.1f} MB, n_past={n_past}")

result = state_put('192.168.122.21', 8091, slot_id=0, data=kv_data)
print(f"Restored: {result}")

# Then verify on P100 (n_tokens MUST be > n_past):
# curl P100:8086/v1/chat/completions with the original prompt + continuation
```

### 9.5 C# Tests

```bash
# Hydra.Shared (should pass — 29 tests)
dotnet test src/core/Tests.Shared/Tests.Shared.csproj -v m

# Full solution (will have errors in incomplete projects)
dotnet test src/Hydra.sln
```

---

## 10. Critical Facts (Do Not Ignore)

| Fact | Detail |
|---|---|
| **n_tokens > n_past is CRITICAL** | If the next completion request has fewer tokens than n_past, the KV cache is invalidated silently. Coordinator MUST guard this. |
| **n_past definition** | `slot.n_prompt_tokens_cache + slot.n_decoded` — NOT `llama_get_kv_cache_used_cells()` |
| **STATE_META after STATE_PUT gives n_past=0** | After restore, slot bookkeeping fields reset to 0. Get n_past from the STATE_GET response on the SOURCE machine. Never trust STATE_META after a PUT. |
| **SSM truncation is broken** | `--cache-prompt` is useless for qwen35moe. Delta export is impossible. Always use full KV state (800 MB). |
| **P100 prefill = 12 minutes at 80K** | RTX must handle all large prefills. Route by prompt length in Coordinator. |
| **RTX needs cuBLAS** | Build flag: `-DGGML_CUDA_FORCE_CUBLAS=ON`. Required for sm_120 (Blackwell). |
| **is_transferring in STATE_META** | Added to HTTP JSON (2026-05-27) and RPC JSON (2026-05-27). Both endpoints now return this field. |
| **STATE_META timeout** | Changed 1s → 5s (2026-05-27) for queue congestion margin. |
| **M2 mid-stream error** | `header_sent` flag + `close(fd)` prevents double-write on streaming failure (2026-05-27). |
| **RPC socket RCVTIMEO** | 120s inactivity timeout set on accepted connections (2026-05-27). |
| **M2 chunk size = 1 MB** | `ChunkEngine.CHUNK_SIZE = 1 << 20` (1,048,576 bytes). Last chunk may be smaller. |
| **SHA-256 dedup** | Chunks are content-addressed by SHA-256 hash. Same data → same hash → stored once. |
| **DiffPlan** | `DiffPlan(known_hashes)` returns only hashes NOT in the `known_hashes` set. Agent sends this to Store to fetch only missing chunks. |
| **Agent local chunk cache** | LRU cache of `session_id → [hash, hash, ...]` persisted as JSON. Loaded on startup. If all hashes known, llama PUT is skipped entirely. |
| **GC orphans** | `POST /debug/gc` on Store:9501 removes chunks not referenced by any manifest. Safe to call periodically. |
| **M2.3 gap** | Coordinator prefix checkpoint uses raw `SaveState`/`RestoreState` (bypasses chunked dedup). True cross-session chunk sharing not auto-wired yet. |

---

## 11. What Needs To Be Built Next

### ✅ Priority 1 — Fixed Known Bugs in llama.cpp Fork (2026-05-27)

**Changes applied:**

| Bug | Fix | File |
|-----|-----|------|
| `is_transferring` missing from HTTP meta | Added `{"is_transferring", hr->is_transferring}` to JSON response | `server-context.cpp:4342` |
| `is_transferring` missing from RPC meta | Added `meta_j["is_transferring"] = res->is_transferring` to RPC handler | `server-context.cpp:5321` |
| RPC STATE_META timeout 1s | Changed `recv_with_timeout(..., 1000)` → `5000` | `server-context.cpp:5287` |
| M2 error double-write | Added `header_sent` flag to result struct; skip re-send; `close(fd)` on failure | `server-task.h`, `server-context.cpp` |
| Hung RPC connections | Added `SO_RCVTIMEO` (120s) on accepted sockets | `server-context.cpp:5323` |

Build verification:
```bash
cd src/llama-cpp
cmake -B build-check -DGGML_CPU_ONLY=ON -G Ninja && cmake --build build-check --target llama-server -j$(nproc)
```

### ✅ Priority 2 — Hydra.Store (M0.2) — COMPLETE

`src/core/Hydra.Store/StorageEngine.cs` — PUT/GET/DEL/STAT/LIST with PipeReader streaming + sendfile.
`src/core/Hydra.Store/StoreServer.cs` — RPC dispatch for all 5 ops + debug HTTP endpoint.
`src/core/Tests.Store/` — 23 tests pass.

### ✅ Priority 3 — Hydra.Agent (M0.3) — COMPLETE

`src/core/Hydra.Agent/LlamaClient.cs` — HTTP client for llama-server state/health/slots/erase.
`src/core/Hydra.Agent/StateHandler.cs` — SaveToStore/RestoreFromStore with zero-copy streaming.
`src/core/Hydra.Agent/AgentServer.cs` — RPC dispatch for SAVE/RESTORE/SLOT_STATUS/NODE_HEALTH.
`src/core/Tests.Agent/` — 13 tests pass.

### ✅ Priority 4 — System Test (M0.4) — COMPLETE

`tests/system/test_system.py` — prompt→save→restore→continuation flow.  
4 assertions: choices present, save returns size, restore returns restored=true, cache_n > 0.

```bash
# Run system test (requires all services running):
pytest tests/system/test_system.py -v -m system
```

### ✅ Priority 5 — Coordinator (M1) — COMPLETE

`src/coordinator/` — session routing, health monitoring, migration, proxy.  
42 tests pass. See Section 6 for details.

### ✅ Priority 6 — M2 Chunked Dedup & Prefix Checkpoints — COMPLETE

Store splits KV state into 1 MB content-addressed chunks. Repeated migrations only store delta.  
Agent caches chunk hashes locally, skips llama PUT if all chunks already present.  
Coordinator exposes prefix checkpoint HTTP endpoints.  
47 new tests across Store, Agent, Integration, and coordinator. See Section 6 for details.

### ✅ Priority 7 — Observability (M3.2) — IMPLEMENTED

**Prometheus metrics** on each service:
- `Store` (:9501/metrics) — ops counter, bytes stored/sent, op duration histogram (`StoreMetrics.cs`)
- `Agent` (:9611/metrics) — save/restore count + duration histogram, slots idle gauge (`AgentMetrics.cs`)
- `Coordinator` (:9000/metrics) — request counter, cache hits, active sessions (`metrics.py`)

**Loki + Promtail** — Promtail runs on the host (not in Docker). Pipeline:
`container-log-shipper` (host systemd --user) tails `podman logs -f` to
`/tmp/container-logs/<name>.log` → `promtail` (host systemd --user) scrapes files → Loki.
Labels: `container`, `component`, `node` mapped via pipeline stages.
Serilog JSON output already includes `trace_id`, `component`, `source_context`.

**Grafana dashboard** (`infra/grafana/dashboards/hydra-dashboard.json`):
- Metric panels: request rate, active sessions, store ops/s, store bytes/s, save/restore p50/p95
- Logs panel: all service logs with label filters
- **Trace ID filter**: textbox variable `$trace_id` — enter trace ID to see all related logs in Grafana

**Docker Compose** (`infra/docker-compose.hydra.yml` + `infra/docker-compose.infra.yml`):
- `docker-compose.hydra.yml`: Store + Agent RTX + Coordinator (host networking)
- `docker-compose.infra.yml`: Loki + Promtail + Prometheus + Grafana
- llama-server runs natively on each GPU machine (not containerized)

```bash
cd infra && docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up -d
curl -s localhost:9501/metrics | head
curl -s localhost:9611/metrics | head
curl -s localhost:9000/metrics | head
open http://localhost:3000
```

---

## 12. Build Commands Reference

### Docker Compose (Hydra control plane + Observability)

```bash
# Build and start all services
cd infra
docker compose up -d

# Rebuild after code changes
docker compose build
docker compose up -d

# Build a single service (avoids rebuilding everything)
docker compose build store
docker compose up -d store

# View logs
docker compose logs -f store agent-rtx coordinator

# Filter logs by trace_id
docker compose logs store | grep "abc123"

# Check Prometheus targets
curl -s localhost:9090/targets | jq '.data.activeTargets[].labels'

# Stop everything
docker compose down

# Stop + delete volumes (lose store data + Grafana state)
docker compose down -v
```

**Build architecture:** A single `infra/Dockerfile` builds all projects with one shared `.NET SDK` stage.
- Each service selects its target: `store`, `agent`, or `coordinator`
- .NET SDK is pulled **once** for all C# services (down from 3× to 1×)
- Python coordinator uses `python:3.13-slim`

### C++ (llama.cpp fork)

```bash
cd src/llama-cpp

# CPU-only (fast compile, for syntax checks)
cmake -B build-check -DGGML_CPU_ONLY=ON -G Ninja
cmake --build build-check --target llama-server

# RTX (host) — Blackwell sm_120
cmake -B build-rtx -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON
cmake --build build-rtx --target llama-server -j4

# P100 (VM) — Pascal sm_60
cmake -B build-p100 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_NATIVE=ON
cmake --build build-p100 --target llama-server -j4
```

### C# (.NET 10)

```bash
# Build all
dotnet build src/Hydra.sln

# Build single project
dotnet build src/core/Hydra.Shared/Hydra.Shared.csproj

# Test (Shared — currently the only one that compiles clean)
dotnet test src/core/Tests.Shared/Tests.Shared.csproj

# Run Store
dotnet run --project src/core/Hydra.Store/Hydra.Store.csproj

# Run Agent
dotnet run --project src/core/Hydra.Agent/Hydra.Agent.csproj
```

### Python (Coordinator)

```bash
# Install deps
pip install -e ".[dev]"

# Run coordinator
hydra-coordinator
# or: python -m coordinator.main

# Run tests
pytest tests/ -v
```

---

## 13. Ports Reference

| Port | Service | Host | Notes |
|---|---|---|---|
| 8080 | llama-server RTX (HTTP) | localhost | OpenAI-compat completions |
| 8090 | llama-server RTX (RPC) | localhost | Hydra binary RPC (STATE_GET/PUT/META) |
| 8086 | llama-server P100 (HTTP) | 192.168.122.21 | OpenAI-compat completions |
| 8091 | llama-server P100 (RPC) | 192.168.122.21 | Hydra binary RPC |
| 9000 | Coordinator (HTTP) | 0.0.0.0 | Client-facing OpenAI-compat API |
| 9500 | Store (RPC) | 0.0.0.0 | Binary KV state store |
| 9501 | Store debug (HTTP) | localhost | `/debug` stats endpoint |
| 9601 | Agent RTX (RPC) | 0.0.0.0 | Coordinator ↔ Agent channel |
| 9611 | Agent RTX debug (HTTP) | localhost | `/debug` stats endpoint |
| 9602 | Agent P100 (RPC) | 192.168.122.21 | Coordinator ↔ Agent channel |
| 3000 | Grafana (HTTP) | localhost | Pre-provisioned dashboards |
| 9090 | Prometheus (HTTP) | localhost | Metrics from Store/Agent/Coordinator |
| 3100 | Loki (HTTP) | localhost | Log storage for Grafana |

---

## 14. File Checklist — What To Touch for Each Task

### Fixing llama.cpp bugs
```
src/llama-cpp/tools/server/server-context.cpp   ← all RPC logic
src/llama-cpp/tools/server/server-task.h        ← task/result structs
src/llama-cpp/tools/server/server-task.cpp      ← to_json()
src/llama-cpp/tools/server/server-rpc.h         ← constants only
```

### Adding a new RPC op to llama
```
1. server-rpc.h                 — add HYDRA_OP_* constant
2. server-task.h                — add SERVER_TASK_TYPE_* + hydra_action fields + result fields
3. server-task.cpp              — add to_json() case
4. server-context.cpp           — add case in process_single_task + RPC handler function
5. specs/rpc-protocol.md        — document the op
```

### Implementing Store (M0)
```
src/core/Hydra.Store/StorageEngine.cs   ← file I/O
src/core/Hydra.Store/StoreServer.cs     ← RPC dispatch + chunked ops (M2)
src/core/Hydra.Store/StoreMetrics.cs    ← prometheus-net counters + chunk metrics (M2)
src/core/Hydra.Store/StoreConfig.cs     ← config
src/core/Hydra.Store/Program.cs         ← DI + startup
src/core/Tests.Store/                   ← 44 tests (23 M0 + 21 M2)
```

### Implementing Store Chunked Dedup (M2)
```
src/core/Hydra.Store/ChunkEngine.cs     ← chunk splitting, SHA-256, DiffPlan
src/core/Hydra.Store/ChunkModels.cs     ← ChunkRef, Manifest, result models
src/core/Hydra.Store/ChunkStore.cs      ← content-addressed storage, dedup, GC
src/core/Tests.Store/ChunkEngineTests.cs   ← 10 tests
src/core/Tests.Store/ChunkStoreTests.cs    ← 11 tests
```

### Implementing Agent (M0)
```
src/core/Hydra.Agent/LlamaClient.cs     ← llama HTTP + RPC client
src/core/Hydra.Agent/StateHandler.cs    ← save/restore pipeline + chunked (M2)
src/core/Hydra.Agent/LocalChunkCache.cs ← LRU chunk hash cache (M2)
src/core/Hydra.Agent/AgentServer.cs     ← RPC server + debug HTTP + metrics + chunked handlers (M2)
src/core/Hydra.Agent/AgentMetrics.cs    ← prometheus-net counters
src/core/Hydra.Agent/Program.cs         ← DI + startup
src/core/Hydra.Agent/AgentConfig.cs     ← config + chunk cache dir (M2)
src/core/Tests.Agent/                   ← 23 tests (13 M0 + 10 M2)
```

### Implementing Prefix Checkpoint (M2.3)
```
src/coordinator/state_manager.py   ← save/restore prefix checkpoint
src/coordinator/router.py          ← POST /prefix/{name}/save, /prefix/{name}/restore
src/coordinator/config.py          ← prefix_checkpoint_name, prefix_checkpoint_enabled
tests/coordinator/test_prefix_checkpoint.py ← 4 tests
```

### Implementing Coordinator
```
src/coordinator/main.py            ← uvicorn entry point
src/coordinator/app.py             ← FastAPI factory
src/coordinator/router.py          ← HTTP routes
src/coordinator/routing.py         ← route logic
src/coordinator/session_table.py   ← session tracking
src/coordinator/health.py          ← node health polling
src/coordinator/state_manager.py   ← save/restore orchestration
src/coordinator/proxy.py           ← llama-server proxy
src/coordinator/config.py          ← pydantic-settings
src/coordinator/metrics.py         ← prometheus_client counters
src/python-shared/                 ← shared libs (RPC client, logging)
```

### Implementing Observability
```
infra/Dockerfile                                  ← Multi-target: store, agent, coordinator (one SDK pull)
infra/docker-compose.hydra.yml                     ← Hydra core services (host networking)
infra/docker-compose.infra.yml                     ← Observability stack + exporters
infra/prometheus/prometheus.yml                   ← scrape target config (network_mode: host)
infra/loki/loki-config.yml                        ← log storage config
infra/promtail/promtail-config.yml                ← container promtail docker_sd_configs
infra/grafana/datasources/datasources.yml          ← datasource provisioning
infra/grafana/dashboards/hydra-dashboard.json      ← metrics + logs + trace_id filter panel
infra/grafana/dashboards/dashboard-providers.yml   ← auto-load dashboards

# promtail config lives in the repo: infra/promtail/promtail-config.yml
~/.config/systemd/user/promtail.service            ← host promtail systemd unit
~/.local/bin/promtail                              ← host promtail binary
~/.local/bin/container-log-shipper.sh              ← tails podman logs -f to /tmp/container-logs/
~/.config/systemd/user/container-log-shipper.service  ← log shipper systemd unit

src/core/Hydra.Store/StoreMetrics.cs                   ← Store Prometheus metrics
src/core/Hydra.Agent/AgentMetrics.cs                   ← Agent Prometheus metrics
src/coordinator/metrics.py                        ← Coordinator Prometheus metrics
```

---

## 15. Milestones Completion Gate

| Milestone | Gate Condition |
|---|---|
| **M0** | `pytest tests/system/test_system.py` passes all 8 assertions; `cache_n > 0` on P100 |
| **M1** | `curl localhost:9000/v1/chat/completions` routes to correct GPU; session migrates automatically |
| **M2** | `dotnet test src/core/Tests.Store --filter Chunk` (21 pass) + `dotnet test src/core/Tests.Integration --filter Chunked` (12 pass) + `pytest tests/coordinator/test_prefix_checkpoint.py -v` (4 pass); Store deduplicates repeated saves; agent skips llama PUT when chunks cached |
| **M3** | Grafana dashboard shows metrics + logs with trace_id filter; Langfuse traces per completion; model layer distribution |

---

## 16. Test Status Summary (2026-05-28)

| Test Suite | Status | Count |
|---|---|---|
| `Tests.Shared` | ✅ All pass | 29/29 |
| `Tests.Store` (M0 raw) | ✅ All pass | 23/23 |
| `Tests.Store` (M2 ChunkEngine + ChunkStore) | ✅ All pass | 21/21 |
| `Tests.Agent` (M0 raw) | ✅ All pass | 13/13 |
| `Tests.Agent` (M2 LocalChunkCache) | ✅ All pass | 10/10 |
| `Tests.Integration` (M0 raw) | ✅ All pass | 6/6 |
| `Tests.Integration` (M2 chunked store ops) | ✅ All pass | 8/8 |
| `Tests.Integration` (M2 agent chunked) | ✅ All pass | 4/4 |
| `Python coordinator tests` | ✅ All pass | 46/46 |
| `System test_system.py` | ✅ Written, skipped by default (use `-m system`) | 1 test, 4 assertions |
| llama-server `build-check` | ✅ Compiles | CPU-only |

**Total: 160 tests passing** (71 C# + 46 Python + 1 system + llama build-check)

**M2 test commands:**
```bash
dotnet test src/core/Tests.Store --filter "FullyQualifiedName~Chunk" -v m
dotnet test src/core/Tests.Agent --filter "FullyQualifiedName~ChunkCache" -v m
dotnet test src/core/Tests.Integration --filter "FullyQualifiedName~Chunked" -v m
pytest tests/coordinator/test_prefix_checkpoint.py -v
```
