# Hydra Project Runbook

**For:** AI agents and engineers picking up this project cold.  
**Updated:** 2026-05-27  
**Status:** M0 llama fork ✅ | Hydra.Shared ✅ | Store ❇️ implemented | Agent ❇️ implemented | Coordinator ⚠️ skeleton | Observability ❇️ Prometheus+Grafana+Loki | E2E test ⏳

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
Coordinator :9000       [Python/FastAPI]   NOT YET BUILT (M1)
  │ Hydra binary RPC           │ Hydra binary RPC
  ▼                            ▼
Agent RTX :9601         Agent P100 :9602   [C#/.NET 10]   SKELETON EXISTS
  │ HTTP local                 │ HTTP local
  ▼                            ▼
llama-server :8080      llama-server :8086 [C++ fork]    ✅ BUILT
  │ Hydra binary RPC           │ Hydra binary RPC
  └──────────┬─────────────────┘
             ▼
           Store :9500                     [C#/.NET 10]   SKELETON EXISTS
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
**Model path:** assumed at `/mnt/llm-ram/` or local path — check actual location.

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
│   └── setup-ramdisk.sh         ← mount 30 GB tmpfs at /mnt/llm-ram
├── Hydra.sln                    ← C# solution (all projects)
├── pyproject.toml               ← Python deps (coordinator + tests)
└── src/
    ├── llama-cpp/               ← git submodule, branch: hydra-state-streaming
    ├── Hydra.Shared/            ← C# RPC base lib [✅ DONE, tests pass]
    ├── Hydra.Store/             ← C# tmpfs KV store [⚠️ SKELETON]
    ├── Hydra.Agent/             ← C# GPU sidecar [⚠️ SKELETON]
    ├── Tests.Shared/            ← [✅ 29 tests pass]
    ├── Tests.Store/             ← [⚠️ compile errors]
    ├── Tests.Agent/             ← [⚠️ compile errors]
    ├── Tests.Integration/       ← [⚠️ compile errors]
    ├── coordinator/             ← Python FastAPI [⚠️ SKELETON]
    └── python-shared/           ← Python RPC client lib [✅ DONE]
```

---

## 5. Hydra Binary RPC Wire Format

**Source of truth:** `specs/rpc-protocol.md`  
**C# implementation:** `src/Hydra.Shared/Protocol.cs`  
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
| Hex | Name | Used By |
|---|---|---|
| 0x01 | PUT | Store |
| 0x02 | GET | Store |
| 0x03 | DEL | Store |
| 0x04 | STAT | Store |
| 0x05 | LIST | Store |
| 0x20 | SAVE_STATE | Agent |
| 0x21 | RESTORE_STATE | Agent |
| 0x22 | SLOT_STATUS | Agent |
| 0x23 | SLOT_ERASE | Agent |
| 0x24 | NODE_HEALTH | Agent |
| 0x25 | COMPLETION | Agent |
| 0x30 | STATE_GET | llama direct |
| 0x31 | STATE_PUT | llama direct |
| 0x32 | STATE_META | llama direct |

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

`src/Hydra.Shared/` — C# base library used by Store and Agent.

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
dotnet test src/Tests.Shared/Tests.Shared.csproj
# Expected: Passed! - Failed: 0, Passed: 29
```

### ✅ M0.2 — Hydra.Store (COMPLETE, builds + 23 tests pass)

`src/Hydra.Store/` — full implementation.

| File | Status |
|---|---|
| `StorageEngine.cs` | ✅ PUT/GET/DEL/STAT/LIST + path traversal guard |
| `StoreServer.cs` | ✅ RPC dispatch + sendfile for GET + debug HTTP endpoint |
| `StoreMetrics.cs` | ✅ prometheus-net counters + histograms |
| `StoreConfig.cs` | ✅ appsettings binding |
| `StatResult.cs` | ✅ |
| `Program.cs` | ✅ DI wiring |

`Tests.Store/` — 23/23 pass ✅.

### ✅ M0.3 — Hydra.Agent (COMPLETE, builds + 13 tests pass)

`src/Hydra.Agent/` — full implementation.

| File | Status |
|---|---|
| `LlamaClient.cs` | ✅ GetState/PutState/GetStateMeta + HTTP health/slots/erase |
| `StateHandler.cs` | ✅ SaveToStore + RestoreFromStore (streamed, no 800 MB buffer) |
| `AgentServer.cs` | ✅ SAVE/RESTORE/SLOT_STATUS/NODE_HEALTH/SLOT_ERASE handlers |
| `AgentMetrics.cs` | ✅ prometheus-net counters + histograms |
| `AgentConfig.cs` | ✅ appsettings binding |
| `Program.cs` | ✅ DI wiring |

`Tests.Agent/` — 13/13 pass ✅.

**Known design issue (P2):** `RestoreFromStoreAsync` makes 2 Store GET requests — first one buffers  
the 800 MB payload to extract size from meta, then discards and re-streams via second GET.  
Fix: use OpCode.Stat to get size first, then single streaming GET.

### ✅ M0.4 — E2E Test (WRITTEN)

`tests/e2e/test_e2e.py` — async pytest: prompt→save→restore→continuation flow.  
4 assertions: choices present, save returns size, restore returns restored=true, cache_n > 0.  
Uses `httpx.AsyncClient` for HTTP and `python_shared.RpcClient` for RPC.  
Requires all 6 services running (two llama-servers, Store, two Agents).  
Skipped by default — run with `pytest -m e2e -s`.

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

`python_shared/rpc_client.py` — ✅ correct 16-byte wire format (`<HBBHqH`).

**Missing tests (P1):** No `test_health.py`, `test_proxy.py`, or integration test `test_coordinator_agent.py`.

---

## 7. Environment Setup

### 7.1 Host Machine (RTX)

```bash
# 0. Prerequisites: Docker + Docker Compose (for Hydra control plane + observability)
docker --version          # must be 24+
docker compose version

# 1. Mount tmpfs (30 GB ramdisk for Store)
# NOTE: Not needed when running Store via Docker Compose (tmpfs is managed by Docker)
sudo bash infra/setup-ramdisk.sh
# Verify: mountpoint -q /mnt/llm-ram && echo OK

# 2. Check .NET 10
dotnet --version   # must be 10.x

# 3. Check Python 3.14+
python3 --version  # must be 3.14+ (project uses 3.13+)

# 4. Install Python deps
pip install -e ".[dev]"
# or: uv sync

# 5. Build llama.cpp (RTX sm_120)
cd src/llama-cpp
cmake -B build-rtx -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON
cmake --build build-rtx --target llama-server -j4
# Binary: src/llama-cpp/build-rtx/bin/llama-server
```

### 7.2 P100 VM (192.168.122.21)

```bash
# 1. SSH in
ssh user@192.168.122.21

# 2. Copy source or build in VM
# Option A: build from shared source
cd /path/to/llama-cpp-source
cmake -B build-p100 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_NATIVE=ON
cmake --build build-p100 --target llama-server -j4

# Option B: copy binary from host (if compatible glibc)
scp src/llama-cpp/build-p100/bin/llama-server user@192.168.122.21:~/
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

### 8.0 Quick Start (Docker Compose)

The Hydra control plane (Store + Agent + Coordinator + observability) runs via Docker Compose.
llama-server runs natively on each GPU machine (not containerized).

```bash
# Start all Hydra services + Prometheus + Loki + Grafana
cd infra
docker compose up -d

# Verify:
curl -s http://localhost:9501/debug    # Store health
curl -s http://localhost:9611/debug    # Agent health
curl -s http://localhost:9000/health   # Coordinator health
curl -s http://localhost:9090/targets  # Prometheus targets
open http://localhost:3000             # Grafana (hydra dashboard)
```

### 8.1 Start Order (Native)

If running outside Docker, always start in this order:
1. **Store** (no dependencies)
2. **llama-server RTX** + **llama-server P100** (independent)
3. **Agent RTX** + **Agent P100** (need Store + llama running)
4. **Coordinator** (needs both Agents)

### 8.2 llama-server RTX

```bash
cd src/llama-cpp
./build-rtx/bin/llama-server \
  -m /path/to/Darwin-36B-Opus-APEX-I-Balanced.gguf \
  --port 8080 \
  --rpc-port 8090 \
  --parallel 4 \
  --ctx-size 131072 \
  -ngl 99 \
  --log-format json
```

**Flags:**
- `--rpc-port 8090` — Hydra binary RPC for KV state transfer (our addition)
- `--parallel 4` — 4 concurrent slots (continuous batching)
- `--ctx-size 131072` — 128K context window
- `-ngl 99` — offload all layers to GPU

### 8.3 llama-server P100

```bash
# On VM (192.168.122.21):
./llama-server \
  -m /path/to/Darwin-36B-Opus-APEX-I-Balanced.gguf \
  --port 8086 \
  --rpc-port 8091 \
  --parallel 4 \
  --ctx-size 131072 \
  -ngl 99 \
  --log-format json
```

### 8.4 Hydra Store (Docker Compose)

Store runs in Docker. See `8.0 Quick Start` for `docker compose up`.

For native debugging:
```bash
cd src/Hydra.Store
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
cd src/Hydra.Agent
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
dotnet test src/Tests.Shared/Tests.Shared.csproj -v m

# Full solution (will have errors in incomplete projects)
dotnet test Hydra.sln
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

`src/Hydra.Store/StorageEngine.cs` — PUT/GET/DEL/STAT/LIST with PipeReader streaming + sendfile.
`src/Hydra.Store/StoreServer.cs` — RPC dispatch for all 5 ops + debug HTTP endpoint.
`src/Tests.Store/` — 23 tests pass.

### ✅ Priority 3 — Hydra.Agent (M0.3) — COMPLETE

`src/Hydra.Agent/LlamaClient.cs` — HTTP client for llama-server state/health/slots/erase.
`src/Hydra.Agent/StateHandler.cs` — SaveToStore/RestoreFromStore with zero-copy streaming.
`src/Hydra.Agent/AgentServer.cs` — RPC dispatch for SAVE/RESTORE/SLOT_STATUS/NODE_HEALTH.
`src/Tests.Agent/` — 13 tests pass.

### Priority 4 — E2E Test (M0.4)

Write `tests/e2e/test_e2e.py`:
1. Send prompt to RTX llama-server → get response
2. RPC SAVE_STATE to RTX Agent → get n_past back
3. RPC RESTORE_STATE to P100 Agent (passes n_past internally)
4. Send continuation to P100 (n_tokens > n_past, include full prompt history)
5. Assert `cache_n > 0` in P100 response timings

```bash
# Run E2E (requires all services running):
pytest tests/e2e/test_e2e.py -v
```

### Priority 5 — Coordinator (M1)

File: `src/coordinator/` — implement session routing logic.  
Key constraint: **Every request must have n_tokens > session.n_past** or the KV cache dies.  
Session table must track: `{session_id → (node, slot_id, n_past)}`.

### Priority 6 — Observability (M3.2) — IMPLEMENTED

**Prometheus metrics** on each service:
- `Store` (:9501/metrics) — ops counter, bytes stored/sent, op duration histogram (`StoreMetrics.cs`)
- `Agent` (:9611/metrics) — save/restore count + duration histogram, slots idle gauge (`AgentMetrics.cs`)
- `Coordinator` (:9000/metrics) — request counter, cache hits, active sessions (`metrics.py`)

**Loki + Promtail** — Promtail scrapes Docker container logs → sends to Loki with `service` + `component` labels.
Serilog JSON output already includes `trace_id`, `component`, `source_context`.

**Grafana dashboard** (`infra/grafana/dashboards/hydra-dashboard.json`):
- Metric panels: request rate, active sessions, store ops/s, store bytes/s, save/restore p50/p95
- Logs panel: all service logs with label filters
- **Trace ID filter**: textbox variable `$trace_id` — enter trace ID to see all related logs in Grafana

**Docker Compose** (`infra/docker-compose.yml`):
- Builds and runs Store + Agent RTX + Coordinator + Loki + Promtail + Prometheus + Grafana
- llama-server runs natively on each GPU machine (not containerized)

```bash
cd infra && docker compose up -d
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
dotnet build Hydra.sln

# Build single project
dotnet build src/Hydra.Shared/Hydra.Shared.csproj

# Test (Shared — currently the only one that compiles clean)
dotnet test src/Tests.Shared/Tests.Shared.csproj

# Run Store
dotnet run --project src/Hydra.Store/Hydra.Store.csproj

# Run Agent
dotnet run --project src/Hydra.Agent/Hydra.Agent.csproj
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

### Implementing Store
```
src/Hydra.Store/StorageEngine.cs   ← file I/O
src/Hydra.Store/StoreServer.cs     ← RPC dispatch
src/Hydra.Store/StoreMetrics.cs    ← prometheus-net counters
src/Hydra.Store/Program.cs         ← DI + startup
src/Tests.Store/                   ← 23 tests
```

### Implementing Agent
```
src/Hydra.Agent/LlamaClient.cs     ← llama HTTP + RPC client
src/Hydra.Agent/StateHandler.cs    ← save/restore pipeline
src/Hydra.Agent/AgentServer.cs     ← RPC server + debug HTTP + metrics
src/Hydra.Agent/AgentMetrics.cs    ← prometheus-net counters
src/Hydra.Agent/Program.cs         ← DI + startup
src/Tests.Agent/                   ← 13 tests
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
infra/docker-compose.yml                          ← Hydra services + Loki + Promtail + Prometheus + Grafana
infra/prometheus/prometheus.yml                   ← scrape target config
infra/loki/loki-config.yml                        ← log storage config
infra/promtail/promtail-config.yml                ← Docker log scraping
infra/grafana/datasources/datasources.yml          ← datasource provisioning
infra/grafana/dashboards/hydra-dashboard.json      ← metrics + logs + trace_id filter panel
infra/grafana/dashboards/dashboard-providers.yml   ← auto-load dashboards
src/Hydra.Store/StoreMetrics.cs                   ← Store Prometheus metrics
src/Hydra.Agent/AgentMetrics.cs                   ← Agent Prometheus metrics
src/coordinator/metrics.py                        ← Coordinator Prometheus metrics
```

---

## 15. Milestones Completion Gate

| Milestone | Gate Condition |
|---|---|
| **M0** | `pytest tests/e2e/test_e2e.py` passes all 8 assertions; `cache_n > 0` on P100 |
| **M1** | `curl localhost:9000/v1/chat/completions` routes to correct GPU; session migrates automatically |
| **M2** | Store uses content-addressed chunks; repeated migrations skip duplicate data |
| **M3** | Grafana dashboard shows metrics + logs with trace_id filter; Langfuse traces per completion; model layer distribution |

---

## 16. Test Status Summary (2026-05-27)

| Test Suite | Status | Count |
|---|---|---|
| `Tests.Shared` | ✅ All pass | 29/29 |
| `Tests.Store` | ✅ All pass | 23/23 |
| `Tests.Agent` | ✅ All pass | 13/13 |
| `Tests.Integration` | ✅ All pass | 6/6 |
| `Python coordinator tests` | ✅ All pass | 42/42 |
| `E2E test_e2e.py` | ✅ Written, skipped by default (use `-m e2e`) | 1 test, 4 assertions |
| llama-server `build-check` | ✅ Compiles | CPU-only |

**Missing test coverage (P1):**
- `test_health.py` — HealthMonitor unit tests
- `test_proxy.py` — proxy SSE forwarding
- `tests/integration/test_coordinator_agent.py` — M1.4.1 with real Store + Agent
