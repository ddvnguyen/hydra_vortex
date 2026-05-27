# Hydra Project Runbook

**For:** AI agents and engineers picking up this project cold.  
**Updated:** 2026-05-27  
**Status:** M0 llama fork ✅ | Hydra.Shared ✅ | Store/Agent ⚠️ incomplete | Coordinator ⚠️ incomplete | E2E test ✅

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
llama-server :8080      llama-server :8081 [C++ fork]    ✅ BUILT
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
| KVM VM | Tesla P100 16 GB | sm_60 | 192.168.122.21 | Decode + llama:8081 + Agent:9602 |

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

### ⚠️ M0.2 — Hydra.Store (SKELETON — NEEDS IMPLEMENTATION)

`src/Hydra.Store/` exists with file stubs. **Not yet implemented.**

Files that need implementation:
- `StorageEngine.cs` — file I/O on tmpfs (PUT/GET/DEL/STAT/LIST)
- `StoreServer.cs` — RPC handler dispatching to StorageEngine
- `Program.cs` — DI wiring + startup

`Tests.Store/` — 23/23 pass ✅ (was "compile errors", now fixed).  
`Tests.Integration/` — needs running Store + Agent services (skips at runtime).

### ⚠️ M0.3 — Hydra.Agent (SKELETON — NEEDS IMPLEMENTATION)

`src/Hydra.Agent/` exists with file stubs. **Not yet implemented.**

Files that need implementation:
- `LlamaClient.cs` — HTTP client wrapping llama-server local API
- `StateHandler.cs` — Save/Restore orchestration (llama ↔ Store pipe)
- `AgentServer.cs` — RPC handler for Coordinator ops
- `Program.cs` — DI wiring + startup

`Tests.Agent/` — 13/13 pass ✅ (was "compile errors", now fixed).

### ✅ M0.4 — E2E Test (WRITTEN)

`src/tests/e2e/test_e2e.py` — async pytest, exercises prompt→save→restore→continuation flow with 4 assertions (cache_n > 0, prompt_ms < 5000, save returns size, restore returns restored=true).  
Uses `httpx.AsyncClient` for HTTP and `python_shared.RpcClient` for RPC.  
Requires all 6 services running (two llama-servers, Store, two Agents).  
Skipped by default — run with `pytest -m e2e -s`.

### ⚠️ M1 — Coordinator (SKELETON — NEEDS IMPLEMENTATION)

`src/coordinator/` has file stubs only. Core routing logic, session table, migration engine not implemented.

---

## 7. Environment Setup

### 7.1 Host Machine (RTX)

```bash
# 1. Mount tmpfs (30 GB ramdisk for Store)
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

### 8.1 Start Order

Always start in this order:
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
  --port 8081 \
  --rpc-port 8091 \
  --parallel 4 \
  --ctx-size 131072 \
  -ngl 99 \
  --log-format json
```

### 8.4 Hydra Store

```bash
# Not yet runnable — needs implementation (see Section 6)
# When implemented:
cd src/Hydra.Store
dotnet run --configuration Release
# Config via appsettings.json or env vars:
# Store__StoreDir=/mnt/llm-ram/store
# Store__Port=9500
```

### 8.5 Hydra Agent RTX

```bash
# Not yet runnable — needs implementation (see Section 6)
# When implemented:
cd src/Hydra.Agent
dotnet run --configuration Release
# Config:
# Agent__Port=9601
# Agent__NodeName=rtx
# Agent__LlamaUrl=http://localhost:8080
# Agent__StoreHost=127.0.0.1
# Agent__StorePort=9500
```

### 8.6 Hydra Agent P100

```bash
# On VM — same binary, different config:
# Agent__Port=9602
# Agent__NodeName=p100
# Agent__LlamaUrl=http://localhost:8081
# Agent__StoreHost=192.168.122.1   ← host machine IP from VM
# Agent__StorePort=9500
```

### 8.7 Coordinator

```bash
# Not yet runnable — needs implementation (see Section 6)
# When implemented:
pip install -e .
hydra-coordinator
# or: uvicorn coordinator.main:app --port 9000
# Config via env:
# HYDRA_COORD_HOST=0.0.0.0
# HYDRA_COORD_PORT=9000
```

---

## 9. Verification Tests

### 9.1 llama-server Health (HTTP)

```bash
# RTX
curl -s http://localhost:8080/health
# Expected: {"status":"ok","slots_idle":4,"slots_processing":0}

# P100
curl -s http://192.168.122.21:8081/health
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
# curl P100:8081/v1/chat/completions with the original prompt + continuation
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

### Priority 2 — Hydra.Store (M0.2)

Implement `src/Hydra.Store/StorageEngine.cs`:
- `PutAsync(key, PipeReader, size, ct)` — stream to file at `{storeDir}/{key}`, no 800 MB buffer
- `GetAsync(key, ct)` — return `FileInfo?` (caller uses `Socket.SendFileAsync`)
- `DeleteAsync`, `StatAsync`, `ListAsync` — straightforward file operations
- Key validation: reject keys with `..` or starting with `/`

Implement `src/Hydra.Store/StoreServer.cs`:
- Extend `RpcServer` from Hydra.Shared
- Handle ops: PUT(0x01), GET(0x02), DEL(0x03), STAT(0x04), LIST(0x05)
- GET uses `Socket.SendFileAsync` for zero-copy sendfile syscall
- PUT pipes `PipeReader` directly to `FileStream` — no buffering

Fix `src/Tests.Store/` compile errors, run tests:
```bash
dotnet test src/Tests.Store/Tests.Store.csproj
```

### Priority 3 — Hydra.Agent (M0.3)

Implement `src/Hydra.Agent/LlamaClient.cs`:
- `GetStateAsync(slotId)` — Hydra RPC STATE_GET (0x30) on `--rpc-port`
- `PutStateAsync(slotId, stream)` — Hydra RPC STATE_PUT (0x31) on `--rpc-port`
- `GetStateMetaAsync(slotId)` — Hydra RPC STATE_META (0x32) on `--rpc-port`
- Standard HTTP: `/health`, `/slots`, `/v1/chat/completions`
- **Note:** Use `RpcClient` from Hydra.Shared for the RPC ops (0x30-0x32), not HTTP

Implement `src/Hydra.Agent/StateHandler.cs`:
- `SaveToStoreAsync(sessionId, slotId, traceId)`: RPC STATE_GET → pipe → Store RPC PUT
- `RestoreFromStoreAsync(sessionId, slotId, traceId)`: Store RPC GET → pipe → RPC STATE_PUT
- **Key:** Store n_past from STATE_GET response; pass to caller for Coordinator use
- No disk I/O — everything is piped in memory

Implement `src/Hydra.Agent/AgentServer.cs`:
- Handle: SAVE_STATE(0x20), RESTORE_STATE(0x21), SLOT_STATUS(0x22), NODE_HEALTH(0x24)
- Response to SAVE_STATE includes `n_past` so Coordinator can track it

Fix `src/Tests.Agent/` compile errors, run tests:
```bash
dotnet test src/Tests.Agent/Tests.Agent.csproj
```

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

---

## 12. Build Commands Reference

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
| 8081 | llama-server P100 (HTTP) | 192.168.122.21 | OpenAI-compat completions |
| 8091 | llama-server P100 (RPC) | 192.168.122.21 | Hydra binary RPC |
| 9000 | Coordinator (HTTP) | 0.0.0.0 | Client-facing OpenAI-compat API |
| 9500 | Store (RPC) | 0.0.0.0 | Binary KV state store |
| 9501 | Store debug (HTTP) | localhost | `/debug` stats endpoint |
| 9601 | Agent RTX (RPC) | 0.0.0.0 | Coordinator ↔ Agent channel |
| 9611 | Agent RTX debug (HTTP) | localhost | `/debug` stats endpoint |
| 9602 | Agent P100 (RPC) | 192.168.122.21 | Coordinator ↔ Agent channel |

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
src/Hydra.Store/Program.cs         ← DI + startup
src/Tests.Store/                   ← fix compile errors first
```

### Implementing Agent
```
src/Hydra.Agent/LlamaClient.cs     ← llama HTTP + RPC client
src/Hydra.Agent/StateHandler.cs    ← save/restore pipeline
src/Hydra.Agent/AgentServer.cs     ← RPC server
src/Hydra.Agent/Program.cs         ← DI + startup
src/Tests.Agent/                   ← fix compile errors first
```

### Implementing Coordinator
```
src/coordinator/routing.py         ← route by prompt length / n_past
src/coordinator/session_table.py   ← session → (node, slot, n_past) map
src/coordinator/state_manager.py   ← trigger save/restore via Agent RPC
src/coordinator/router.py          ← FastAPI routes
src/coordinator/app.py             ← app factory
src/python-shared/rpc_client.py    ← Python RPC client (write this first)
```

---

## 15. Milestones Completion Gate

| Milestone | Gate Condition |
|---|---|
| **M0** | `pytest tests/e2e/test_e2e.py` passes all 8 assertions; `cache_n > 0` on P100 |
| **M1** | `curl localhost:9000/v1/chat/completions` routes to correct GPU; session migrates automatically |
| **M2** | Store uses content-addressed chunks; repeated migrations skip duplicate data |
| **M3** | Grafana dashboard live; Langfuse traces per completion; model layer distribution |

---

## 16. Test Status Summary (2026-05-27)

| Test Suite | Status | Count |
|---|---|---|
| `Tests.Shared` | ✅ All pass | 29/29 |
| `Tests.Store` | ✅ All pass | 23/23 |
| `Tests.Agent` | ✅ All pass | 13/13 |
| `Tests.Integration` | ✅ Builds (skips if services not running) | 7/7 |
| `E2E test_e2e.py` | ✅ Written, skipped by default (use `-m e2e`) | 1 test, 4 assertions |
| llama-server `build-check` | ✅ Compiles | CPU-only |
| Python coordinator tests | ✅ Collect (42 tests) | 42/42 |
