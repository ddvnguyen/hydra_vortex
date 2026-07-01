# Hydra Project Runbook

**For:** AI agents and engineers picking up this project cold.  
**Updated:** 2026-05-28  
**Status:** M0 ✅ | Hydra.Shared ✅ | Store ✅ | Hydra.Core ✅ | M2 chunked dedup ✅ | Observability ✅ Prometheus+Grafana+Loki | System test ✅ | PR #203 merged (Agent/Coordinator → Hydra.Core single binary)

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
Hydra.Core :9000       [C#/.NET 10]   ✅ single binary (HTTP API + Store + Router)
  │ HTTP completions          │ HTTP completions
  │ RPC StateGet/Put (:9503)  │ RPC StateGet/Put (:9502)
  ▼                           ▼
llama-server :8080      llama-server :8086 [C++ fork]      ✅ hydra-state-streaming

  /mnt/llm-ram/store/ (tmpfs, managed by Hydra.Core)
```

**Critical rule:** Hydra.Core contacts llama-servers directly via HTTP (completions) and
hydra binary RPC (state ops). No intermediate Agent layer. The Store runs embedded in
Hydra.Core — there is no separate Store container.

---

## 3. Hardware

| Machine | GPU | CUDA Arch | Address | Role |
|---|---|---|---|---|
| Host (i7-12700K, 64 GB) | RTX 5060 Ti 16 GB | sm_120 | localhost | Prefill + llama:8080 + Hydra.Core:9000 |
| KVM VM | Tesla P100 16 GB | sm_60 | 192.168.122.21 | Decode + llama:8086 |

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
│   ├── docker-compose.hydra.yml ← Hydra.Core (single C# binary, host networking)
│   └── docker-compose.infra.yml ← Observability stack + exporters
├── src/Hydra.sln              ← C# solution (all projects)
├── pyproject.toml               ← Python deps (system tests)
└── src/
    ├── llama-cpp/               ← git submodule, branch: hydra-state-streaming
    ├── Hydra.Shared/            ← C# RPC base lib [✅ tests pass]
    ├── Hydra.Core/              ← C# HTTP API + Store + Router [✅ tests pass]
    ├── core/                    ← C# .NET — Hydra.Shared, Core
    │   ├── Tests.Shared/        ← RPC round-trips
    │   └── Tests.Core/          ← Storage engine + ChunkEngine + ChunkStore + Router
    └── tests/                   ← Python system tests
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
| 0x20 | SAVE_STATE | Agent (retired) | M0 (raw; superseded by 0x26) |
| 0x21 | RESTORE_STATE | Agent (retired) | M0 (raw; superseded by 0x27) |
| 0x22 | SLOT_STATUS | Agent (retired) | M0 |
| 0x23 | SLOT_ERASE | Agent (retired) | M0 |
| 0x24 | NODE_HEALTH | Agent (retired) | M0 |
| 0x25 | (retired) | — | completions are HTTP-direct |
| 0x26 | SAVE_STATE_CHUNKED | Agent (retired) | M2 (was active default) |
| 0x27 | RESTORE_STATE_CHUNKED | Agent (retired) | M2 (was active default) |
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

`src/core/Hydra.Shared/` — C# base library used by Hydra.Core.

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

### ✅ M0.3 — Hydra.Agent (RETIRED — merged into Hydra.Core via PR #203)

The Agent service was a GPU sidecar that bridged Hydra RPC ↔ local llama-server HTTP.
As of PR #203, Hydra.Core contacts llama-servers directly via HTTP (completions) and
hydra RPC (StateGet/Put). The Agent containers (hydra-agent-rtx, hydra-agent-p100) no
longer exist. The C# code is retained in the repo for reference but `Hydra.Agent`
project and `Tests.Agent/` are removed from the build.

### ✅ M0.4 — System Test (COMPLETE)

`tests/system/test_system.py` — async pytest: prompt→save→restore→continuation flow.  
4 assertions: choices present, save returns size, restore returns restored=true, cache_n > 0.  
Uses `httpx.AsyncClient` for HTTP and RPC client for state operations.  
Requires Hydra.Core + both llama-servers running.  
Skipped by default — run with `pytest -m system -s`.

### ✅ M1 — Coordinator (RETIRED — migrated to Hydra.Core via PR #203)

The Python coordinator has been fully replaced. Routing, session tracking, health
monitoring, state management, and completion proxying are now C# code embedded in
Hydra.Core. The `src/coordinator/` directory, `hydra-coordinator` service, and
`HYDRA_COORD_*` environment variables are removed.

**For reference**, the original Python coordinator included:
| File | Description |
|---|---|
| `session_table.py` | LRU tracking, mark_evicted, get_sessions_on_node |
| `routing.py` | Session affinity → store_restore → long_prompt_rtx → least_loaded |
| `health.py` | Background poll every 10s; marks unhealthy after 3 failures |
| `state_manager.py` | save/restore/migrate/evict_lru via Agent RPC |
| `proxy.py` | Non-streaming + SSE streaming proxy to llama-server |
| `router.py` | POST /v1/chat/completions, GET /health, GET /status, etc. |
| `config.py` | pydantic-settings config |
| `metrics.py` | prometheus-client counters |

All this logic is now in Hydra.Core C# code.

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

**M2.2.1 — Chunked Transfer (now in Hydra.Core)** (originally in Hydra.Agent, merged via PR #203)
StateHandler.cs (in Hydra.Core) — `SaveToStoreChunkedAsync` / `RestoreFromStoreChunkedAsync` with `ChunkHashTeeStream`.
LocalChunkCache.cs (in Hydra.Core) — LRU session-based hash cache (JSON files), skips llama PUT if all chunks cached.
Hydra.Core contacts llama directly via RPC — no intermediate Agent layer.

**M2.3 — Prefix Checkpoint** (now in Hydra.Core C#)
Hydra.Core handles prefix checkpoint save/restore via C# code (was Python coordinator).

**M2 Test Counts:**
| Suite | Tests | Status |
|-------|-------|--------|
| `ChunkEngineTests` | ✅ All pass |
| `ChunkStoreTests` | ✅ All pass |
| `LocalChunkCacheTests` | ✅ All pass |
| `ChunkedStoreIntegrationTests` | ✅ All pass |
| `AgentChunkedSaveRestoreTests` | ✅ All pass |
| `test_prefix_checkpoint.py` | 4 | ✅ All pass |

```bash
# Run M2 tests (all in Tests.Core now)
dotnet test src/core/Tests.Core --filter "FullyQualifiedName~Chunk" -v m
pytest tests/coordinator/test_prefix_checkpoint.py -v
```

**Known gap:** (Historical) The old Python coordinator prefix checkpoint used raw `SaveState`/`RestoreState` (Agent RPC ops), not chunked `PutChunked`/`GetChunked`. With PR #203, this gap should be resolved since all logic is now in Hydra.Core C#.

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
#    Output: src/llama-cpp/build_sm120/bin/llama-engine  (gitignored, stays on filesystem)
cd src/llama-cpp
cmake -B build_sm120 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON
cmake --build build_sm120 --target llama-engine -j4
cd ../..

# 5. Build llama.cpp — P100 (sm_60)
#    Output: src/llama-cpp/build_sm60/bin/llama-engine  (rsynced to VM by scripts)
cd src/llama-cpp
cmake -B build_sm60 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_NATIVE=ON
cmake --build build_sm60 --target llama-engine -j4
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

Starts: Hydra.Core (single C# binary) + Observability (podman-compose), llama-server RTX
(container with `build_sm120/` volume), llama-server P100 (user systemd on VM), and
the OTel Collector gateway (Quadlet, joins the `infra-host` pod — see #363).

Manual verification:
```bash
curl -s http://localhost:9000/health    # Hydra.Core health
curl -s http://localhost:9501/metrics   # Hydra.Core Store metrics
curl -s http://localhost:9091/targets   # Prometheus scrape targets
open http://localhost:3000              # Grafana dashboard
```

### 8.1 Start Order (Native)

If running outside Docker, always start in this order:
1. **llama-server RTX** + **llama-server P100** (independent)
2. **Hydra.Core** (needs both llama-servers running)

### 8.2 llama-server RTX

Runs as a **container** via `infra/llama-rtx-node/docker-compose.yml`.
The container mounts `src/llama-cpp/build_sm120/` directly — no copy needed.

```bash
cd infra/llama-rtx-node
podman-compose up -d
# Connect to internal network so Hydra.Core can resolve "llama-cpp" hostname:
podman network connect hydra_default llama-cpp
```

Full launch args are in `infra/llama-rtx-node/docker-compose.yml`.  
Ports: HTTP `:8080`, Hydra RPC `:9503`

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

### 8.4 Hydra.Core (Docker Compose)

Hydra.Core is the single C# binary. See `8.0 Quick Start` for `docker compose up`.

For native debugging:
```bash
cd src/core/Hydra.Core
dotnet run --configuration Release
# Config via env vars:
# HYDRA_CORE_PORT=9000
# HYDRA_STORE_PORT=9500
# HYDRA_STORE_DIR=/mnt/llm-ram/store
# HYDRA_METRICS_PORT=9501
```

Metrics: `GET :9501/metrics`

### 8.5 Hydra.Agent (REMOVED — merged into Hydra.Core via PR #203)

The Agent service no longer exists. Hydra.Core contacts llama-servers directly.

---

### 8.8 Observability Stack

The Docker Compose setup includes:

| Service | Port | Purpose |
|---------|------|---------|
| Prometheus | 9090 | Metrics scraped from Hydra.Core/:9501, Hydra.Core/:9000 |
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
| **n_tokens > n_past is CRITICAL** | If the next completion request has fewer tokens than n_past, the KV cache is invalidated silently. Hydra.Core MUST guard this. |
| **n_past definition** | `slot.n_prompt_tokens_cache + slot.n_decoded` — NOT `llama_get_kv_cache_used_cells()` |
| **STATE_META after STATE_PUT gives n_past=0** | After restore, slot bookkeeping fields reset to 0. Get n_past from the STATE_GET response on the SOURCE machine. Never trust STATE_META after a PUT. |
| **SSM truncation is broken** | `--cache-prompt` is useless for qwen35moe. Delta export is impossible. Always use full KV state (800 MB). |
| **P100 prefill = 12 minutes at 80K** | RTX must handle all large prefills. Route by prompt length in Hydra.Core. |
| **RTX needs cuBLAS** | Build flag: `-DGGML_CUDA_FORCE_CUBLAS=ON`. Required for sm_120 (Blackwell). |
| **is_transferring in STATE_META** | Added to HTTP JSON (2026-05-27) and RPC JSON (2026-05-27). Both endpoints now return this field. |
| **STATE_META timeout** | Changed 1s → 5s (2026-05-27) for queue congestion margin. |
| **M2 mid-stream error** | `header_sent` flag + `close(fd)` prevents double-write on streaming failure (2026-05-27). |
| **RPC socket RCVTIMEO** | 120s inactivity timeout set on accepted connections (2026-05-27). |
| **M2 chunk size = 1 MB** | `ChunkEngine.CHUNK_SIZE = 1 << 20` (1,048,576 bytes). Last chunk may be smaller. |
| **SHA-256 dedup** | Chunks are content-addressed by SHA-256 hash. Same data → same hash → stored once. |
| **DiffPlan** | `DiffPlan(known_hashes)` returns only hashes NOT in the `known_hashes` set. Hydra.Core sends this to Store to fetch only missing chunks. |
| **Local chunk cache** | LRU cache of `session_id → [hash, hash, ...]` persisted as JSON. Loaded on startup. If all hashes known, llama PUT is skipped entirely. |
| **GC orphans** | `POST /debug/gc` on Hydra.Core:9501 removes chunks not referenced by any manifest. Safe to call periodically. |
| **M2.3 gap** | (Historical) Old coordinator prefix checkpoint bypassed chunked dedup. Should be resolved in Hydra.Core. |

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

### ✅ Priority 2.5 — Hydra.Store (M0.2) — COMPLETE (now merged into Hydra.Core)

Hydra.Store was merged into Hydra.Core via PR #203. StorageEngine, StoreServer, and chunk operations
are now part of the single binary.

### ✅ Priority 4 — System Test (M0.4) — COMPLETE

`tests/system/test_system.py` — prompt→save→restore→continuation flow.  
4 assertions: choices present, save returns size, restore returns restored=true, cache_n > 0.

```bash
# Run system test (requires all services running):
pytest tests/system/test_system.py -v -m system
```

### ✅ Priority 5 — Coordinator (M1) — COMPLETE (now migrated to Hydra.Core)

The Python coordinator was replaced by C# code in Hydra.Core via PR #203.
See Section 6 for historical reference of the orignal Python implementation.
See the M2.1 sections above for the current C# implementation in Hydra.Core.

### ✅ Priority 7 — Observability (M3.2) — IMPLEMENTED

**Prometheus metrics** on Hydra.Core:
- `:9501/metrics` — Store ops counter, bytes stored/sent, op duration histogram (`StoreMetrics.cs`)
- `:9000/metrics` — Request counter, cache hits, active sessions

**Loki + OTel Collector** — per-service direct push to the OTel Collector
gateway (Quadlet `infra-otel-collector.container`, joined to the `infra-host`
pod, port 4318). See #363 for the full design. Pipeline:
`Hydra.Core / Hydra.Head / per-child llama-server / node_exporter /
nvidia_exporter` push OTLP/HTTP to the collector → the collector's
`transform/log_labels` processor maps OTel resource attributes to Loki
stream labels (`component` ← `service.name`, `node` ←
`service.instance.id`, `level` ← `severity_text`) → Loki.

(Promtail and `container-log-shipper` were removed in #363. The
old host-side `infra-promtail` Quadlet and the in-container
`promtail` binary inside the hydra-head image are both gone. The
new pipeline is per-service push; the only log scraper left in
the system is the OTel Collector, which is a forwarder, not a
parser.)

For local forensic reads (when Loki or the collector is
unreachable), hydra-head still writes a plain-text copy of its
own log records to `os.Stdout` (visible via `journalctl -u
hydra-head` on P100 and `podman logs hydra-system_head-rtx_1`
on RTX).

**Grafana dashboard** (`infra/grafana/dashboards/hydra-dashboard.json`):
- Metric panels: request rate, active sessions, store ops/s, store bytes/s, save/restore p50/p95
- Logs panel: all service logs with label filters
- **Trace ID filter**: textbox variable `$trace_id` — enter trace ID to see all related logs in Grafana

**Docker Compose** (`infra/docker-compose.hydra.yml` + `infra/docker-compose.infra.yml`):
- `docker-compose.hydra.yml`: Hydra.Core (single C# binary, host networking)
- `docker-compose.infra.yml`: Loki + Promtail + Prometheus + Grafana
- llama-server runs natively on each GPU machine (not containerized)

```bash
cd infra && docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up -d
curl -s localhost:9501/metrics | head
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

# Build a single service
docker compose build hydra-core
docker compose up -d hydra-core

# View logs
docker compose logs -f hydra-core

# Filter logs by trace_id
docker compose logs hydra-core | grep "abc123"

# Check Prometheus targets
curl -s localhost:9090/targets | jq '.data.activeTargets[].labels'

# Stop everything
docker compose down

# Stop + delete volumes (lose store data + Grafana state)
docker compose down -v
```

**Build architecture:** A single `infra/Dockerfile` builds Hydra.Core with one `.NET SDK` stage.

### C++ (llama.cpp fork)

```bash
cd src/llama-cpp

# CPU-only (fast compile, for syntax checks)
cmake -B build-check -DGGML_CPU_ONLY=ON -G Ninja
cmake --build build-check --target llama-engine

# RTX (host) — Blackwell sm_120
cmake -B build-rtx -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON
cmake --build build-rtx --target llama-engine -j4

# P100 (VM) — Pascal sm_60
cmake -B build-p100 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_NATIVE=ON
cmake --build build-p100 --target llama-engine -j4
```

### C# (.NET 10)

```bash
# Build all
dotnet build src/Hydra.sln

# Build single project
dotnet build src/core/Hydra.Shared/Hydra.Shared.csproj

# Test
dotnet test src/core/Tests.Shared/Tests.Shared.csproj
dotnet test src/core/Tests.Core/Tests.Core.csproj

# Run Hydra.Core
dotnet run --project src/core/Hydra.Core/Hydra.Core.csproj
```

### Python (System Tests)

```bash
# Install deps
pip install -e ".[dev]"

# Run system tests
pytest tests/system -v
```

---

## 13. Ports Reference

| Port | Service | Host | Notes |
|---|---|---|---|
| 8080 | llama-server RTX (HTTP) | localhost | OpenAI-compat completions |
| 9503 | llama-server RTX (RPC) | localhost | Hydra binary RPC (STATE_GET/PUT/META) |
| 8086 | llama-server P100 (HTTP) | 192.168.122.21 | OpenAI-compat completions |
| 9502 | llama-server P100 (RPC) | 192.168.122.21 | Hydra binary RPC |
| 9000 | Hydra.Core (HTTP) | 0.0.0.0 | Client-facing OpenAI-compat API |
| 9500 | Hydra.Core (RPC) | 0.0.0.0 | Internal Store RPC |
| 9501 | Hydra.Core (HTTP) | localhost | `/metrics` endpoint |
| 3000 | Grafana (HTTP) | localhost | Pre-provisioned dashboards |
| 9090 | Prometheus (HTTP) | localhost | Metrics from Hydra.Core |
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

### Implementing Hydra.Core (Store + Router)
```
src/core/Hydra.Core/StorageEngine.cs       ← file I/O
src/core/Hydra.Core/StoreServer.cs         ← RPC dispatch + chunked ops
src/core/Hydra.Core/StoreMetrics.cs        ← prometheus-net counters + chunk metrics
src/core/Hydra.Core/StoreConfig.cs         ← config
src/core/Hydra.Core/Program.cs             ← DI + startup
src/core/Hydra.Core/Router.cs              ← request routing
src/core/Hydra.Core/WorkerConfig.cs        ← worker configuration
src/core/Tests.Core/                       ← Core tests (Store + Chunk + Router)
```

### Implementing Store Chunked Dedup (M2)
```
src/core/Hydra.Core/ChunkEngine.cs     ← chunk splitting, SHA-256, DiffPlan
src/core/Hydra.Core/ChunkModels.cs     ← ChunkRef, Manifest, result models
src/core/Hydra.Core/ChunkStore.cs      ← content-addressed storage, dedup, GC
src/core/Tests.Core/ChunkEngineTests.cs   ← chunk tests
src/core/Tests.Core/ChunkStoreTests.cs    ← chunk store tests
```

### Implementing Observability
```
infra/Dockerfile                                  ← Multi-target: hydra-core (one SDK pull)
infra/docker-compose.hydra.yml                     ← Hydra.Core service (host networking, in pod_hydra-system)
infra/docker-compose.infra.yml                     ← Observability stack + exporters
infra/prometheus/prometheus.yml                   ← scrape target config (network_mode: host)
infra/loki/loki-config.yml                        ← log storage config
infra/promtail/promtail-rtx.yml                   ← in-container promtail config (used by hydra-head-rtx + P100)
infra/grafana/datasources/datasources.yml          ← datasource provisioning
infra/grafana/dashboards/hydra-dashboard.json      ← metrics + logs + trace_id filter panel
infra/grafana/dashboards/dashboard-providers.yml   ← auto-load dashboards

# Node exporter + nvidia_gpu_exporter are NOT in the repo —
# they're pulled by hydra-head at startup (binaries pinned in
# src/head/internal/config/{global,node-p100}.yaml). They run as
# children of hydra-head, NOT as host-side systemd services.

# The host-side promtail systemd service (infra-promtail) was
# removed in commit 5f2c231. promtail-rtx.yml is the only
# promtail config; the in-container promtail (port 9080) ships
# logs from all host containers via the directly-mounted podman
# socket (userns=host, no socat proxy).

src/core/Hydra.Core/StoreMetrics.cs                   ← Hydra.Core Store Prometheus metrics
src/core/Hydra.Core/HydraMetrics.cs                   ← Hydra.Core API metrics
```

---

## 15. Milestones Completion Gate

| Milestone | Gate Condition |
|---|---|
| **M0** | `pytest tests/system/test_system.py` passes all 8 assertions; `cache_n > 0` on P100 |
| **M1** | `curl localhost:9000/v1/chat/completions` routes to correct GPU; session migrates automatically |
| **M2** | `dotnet test src/core/Tests.Core --filter Chunk` + `pytest tests/system`; Store deduplicates repeated saves; Hydra.Core skips llama PUT when chunks cached |
| **M3** | Grafana dashboard shows metrics + logs with trace_id filter; NVMe write-behind persistence |

---

## 16. Test Status Summary (2026-06)

| Test Suite | Status | Count |
|---|---|---|
| `Tests.Shared` | ✅ All pass | RPC protocol |
| `Tests.Core` (Store + Chunk + Router) | ✅ All pass | Core tests |
| `System test_system.py` | ✅ Written, skipped by default (use `-m system`) | 1 test, 4 assertions |
| llama-server `build-check` | ✅ Compiles | CPU-only |

**M2 test commands:**
```bash
dotnet test src/core/Tests.Core --filter "FullyQualifiedName~Chunk" -v m
```

> **Retired suites** (removed via PR #203): `Tests.Agent/` (23 tests), `Tests.Integration/`
> (18 tests), `tests/coordinator/` (46 tests). Agent and Coordinator logic is now in Hydra.Core.
