# Milestone 0 — Minimum Viable Test

## Goal
Prove the full streaming data path works end-to-end with real GPUs.
llama.cpp fork adds binary streaming endpoints → Agent pipes stream directly
to/from Store → no disk round-trip anywhere.

## What "Done" Means
Run `pytest tests/e2e/test_e2e.py` and see:
```
✅ GET /slots/0/state streams binary KV state from RTX llama-server
✅ PUT /slots/0/state restores binary KV state on P100 llama-server
✅ Store PUT 800 MB via RPC < 200ms
✅ Store GET 800 MB via RPC < 150ms (sendfile confirmed)
✅ Agent saves RTX slot → Store without touching disk
✅ Agent restores P100 slot ← Store without touching disk
✅ P100 continuation shows cache_n > 0
✅ All ops have trace_id in logs
```

## Prerequisites
- tmpfs mounted: `sudo bash infra/setup-ramdisk.sh`
- Both llama-servers running (patched binaries from M0.0)

---

## Task M0.0: Fork llama.cpp — Streaming State Endpoints
**Language:** C++
**Repo:** fork https://github.com/ggml-org/llama.cpp → branch `hydra-state-streaming`
**File:** `tools/server/server.cpp`

### M0.0.1: GET /slots/{id}/state
Stream full KV + SSM state as binary HTTP response body.

```cpp
svr.Get("/slots/(\\d+)/state", [&](const httplib::Request & req,
                                    httplib::Response & res) {
    const int id_slot = std::stoi(req.matches[1].str());

    server_slot * slot = nullptr;
    for (auto & s : slots) {
        if (s.id == id_slot) { slot = &s; break; }
    }
    if (!slot) { res.status = 404; return; }
    if (slot->is_processing()) { res.status = 409; return; }

    const size_t state_size = llama_state_seq_get_size(ctx, slot->seq_id);
    std::vector<uint8_t> buf(state_size);
    llama_state_seq_get_data(ctx, buf.data(), buf.size(), slot->seq_id);

    res.set_header("X-Hydra-N-Past",     std::to_string(slot->n_past));
    res.set_header("X-Hydra-State-Size", std::to_string(state_size));
    res.set_content(
        reinterpret_cast<const char *>(buf.data()),
        state_size,
        "application/octet-stream"
    );
});
```

### M0.0.2: PUT /slots/{id}/state
Accept binary KV + SSM state and restore to slot.

```cpp
svr.Put("/slots/(\\d+)/state", [&](const httplib::Request & req,
                                    httplib::Response & res) {
    const int id_slot = std::stoi(req.matches[1].str());

    server_slot * slot = nullptr;
    for (auto & s : slots) {
        if (s.id == id_slot) { slot = &s; break; }
    }
    if (!slot) { res.status = 404; return; }

    const auto * data = reinterpret_cast<const uint8_t *>(req.body.data());
    const size_t n_read = llama_state_seq_set_data(
        ctx, data, req.body.size(), slot->seq_id
    );

    if (n_read == 0) {
        res.status = 400;
        res.set_content("{\"error\":\"restore failed\"}", "application/json");
        return;
    }

    // n_past is embedded in restored state; read it back from slot
    json resp = {{"restored", true}, {"n_past", slot->n_past}, {"bytes", n_read}};
    res.set_content(resp.dump(), "application/json");
});
```

### M0.0.3: GET /slots/{id}/state/meta
Return slot metadata without serializing KV state.

```cpp
svr.Get("/slots/(\\d+)/state/meta", [&](const httplib::Request & req,
                                         httplib::Response & res) {
    const int id_slot = std::stoi(req.matches[1].str());

    server_slot * slot = nullptr;
    for (auto & s : slots) {
        if (s.id == id_slot) { slot = &s; break; }
    }
    if (!slot) { res.status = 404; return; }

    json meta = {
        {"slot_id",       id_slot},
        {"n_past",        slot->n_past},
        {"state_size",    llama_state_seq_get_size(ctx, slot->seq_id)},
        {"is_processing", slot->is_processing()},
    };
    res.set_content(meta.dump(), "application/json");
});
```

### M0.0.4: Build for both GPUs

```bash
# RTX (host) — Blackwell sm_120, cuBLAS required
cmake -B build-rtx -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON
cmake --build build-rtx --target llama-server -j4

# P100 (copy source + build inside VM) — Pascal sm_60
cmake -B build-p100 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_NATIVE=ON
cmake --build build-p100 --target llama-server -j4
```

### M0.0.5: Verify endpoints

```bash
# Start patched RTX server, send a prompt, then:

# 1. Get metadata (no serialization)
curl -s http://localhost:8080/slots/0/state/meta
# Expected: {"slot_id":0,"n_past":2968,"state_size":847003648,"is_processing":false}

# 2. Stream state out
curl -s http://localhost:8080/slots/0/state -o /tmp/kv_test.bin
ls -lh /tmp/kv_test.bin
# Expected: ~800 MB file

# 3. Stream state into P100
curl -s -X PUT http://192.168.122.21:8081/slots/0/state \
  -H "Content-Type: application/octet-stream" \
  --data-binary @/tmp/kv_test.bin
# Expected: {"restored":true,"n_past":2968}

# 4. Verify continuation (n_tokens MUST be > 2968)
curl -s http://192.168.122.21:8081/v1/chat/completions \
  -d '{"messages":[...original + response + new question...],"max_tokens":50}' \
  | python3 -c "import sys,json; t=json.load(sys.stdin)['timings']; print(f'cache_n={t[\"cache_n\"]}')"
# Expected: cache_n=2968
```

**M0.0 done when:** Step 4 shows `cache_n > 0` on P100 after streaming restore from RTX.

---

## Task M0.1: Shared RPC Library (C# — Hydra.Shared)
**Project:** `src/Hydra.Shared/Hydra.Shared.csproj`
**Framework:** .NET 8, NativeAOT-compatible

### M0.1.1: Protocol (`Protocol.cs`)
```csharp
// Request header: 16 bytes
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct RequestHeader(
    ushort Magic,       // 0x4859
    byte   Op,
    byte   Flags,
    ushort KeyLen,
    ulong  PayloadLen,
    ushort TraceLen
);

// Response header: 12 bytes
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct ResponseHeader(
    byte   Status,
    uint   MetaLen,    // only 3 bytes used (little-endian)
    ulong  PayloadLen
);

public static class Protocol {
    public const ushort MAGIC = 0x4859;
    public const int REQUEST_HEADER_SIZE  = 16;
    public const int RESPONSE_HEADER_SIZE = 12;

    public static RequestHeader ReadRequest(ReadOnlySpan<byte> buf) { ... }
    public static void WriteResponse(Span<byte> buf, byte status,
                                     int metaLen, ulong payloadLen) { ... }
}
```
- Use `BinaryPrimitives` for LE read/write
- `MemoryMarshal` for header overlay (zero-copy)
- **Lines:** ~60
- **Tests:** `Tests.Shared/ProtocolTests.cs`
  - Round-trip pack/unpack for all field combinations
  - Header sizes match spec (16 req, 12 res)
  - Magic mismatch throws `InvalidDataException`

### M0.1.2: RPC Server Base (`RpcServer.cs`)
```csharp
public abstract class RpcServer(string host, int port) {
    public async Task RunAsync(CancellationToken ct);
    protected abstract Task HandleAsync(OpCode op, string key,
        string traceId, long payloadLen,
        PipeReader reader, PipeWriter writer, CancellationToken ct);
}
```
- `System.IO.Pipelines` for all I/O
- One `Task` per connection via `TcpListener` + `AcceptTcpClientAsync`
- Invalid magic → log + close
- **Lines:** ~80
- **Tests:** `Tests.Shared/RpcServerTests.cs`
  - Start on random port, connect, send valid request, receive response
  - Invalid magic closes connection
  - Two sequential requests on same connection (persistent)
  - 10 concurrent connections

### M0.1.3: RPC Client (`RpcClient.cs`)
```csharp
public sealed class RpcClient(string host, int port) : IAsyncDisposable {
    public Task ConnectAsync(CancellationToken ct);
    public Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct);
    public IAsyncEnumerable<Memory<byte>> RequestStreamAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct);
}
```
- Reconnect with exponential backoff (3 attempts, 100ms/500ms/2s)
- **Lines:** ~80
- **Tests:** `Tests.Shared/RpcClientTests.cs`
  - Client ↔ server round-trip
  - Reconnect after server restart
  - Timeout propagation via CancellationToken

### M0.1.4: Logging (`HydraLogging.cs`)
```csharp
// Serilog with JSON formatter
// Every log scope binds: component, trace_id
// Output: {"ts":"...","level":"info","component":"store","trace_id":"abc","event":"put_done"}

public static class HydraLogging {
    public static ILogger CreateLogger(string component);
    public static IDisposable TraceScope(this ILogger log, string traceId);
}
```
- Serilog + Serilog.Sinks.Console (JSON)
- **Done when:** structured JSON with trace_id confirmed in stdout

---

## Task M0.2: Hydra Store (C# — Hydra.Store)
**Project:** `src/Hydra.Store/Hydra.Store.csproj`

### M0.2.1: Storage Engine (`StorageEngine.cs`)
```csharp
public sealed class StorageEngine(DirectoryInfo storeDir) {
    public Task PutAsync(string key, PipeReader source, long size, CancellationToken ct);
    public Task<FileInfo?> GetAsync(string key, CancellationToken ct);  // for SendFileAsync
    public Task<bool> DeleteAsync(string key, CancellationToken ct);
    public Task<StatResult?> StatAsync(string key, CancellationToken ct);
    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct);
}
```
- Path sanitization: reject keys containing `..` or starting with `/`
- **Tests:** `Tests.Store/StorageEngineTests.cs`
  - Put + get round-trip (1 KB, 10 MB)
  - Path traversal rejected
  - Stat returns correct size
  - List with prefix filter

### M0.2.2: Store RPC Server (`StoreServer.cs`)
```csharp
public sealed class StoreServer(StoreConfig cfg, StorageEngine engine)
    : RpcServer(cfg.Host, cfg.Port) {

    protected override async Task HandleAsync(OpCode op, ...) {
        switch (op) {
            case OpCode.Put:  await HandlePutAsync(...);  break;
            case OpCode.Get:  await HandleGetAsync(...);  break;
            case OpCode.Del:  await HandleDelAsync(...);  break;
            case OpCode.Stat: await HandleStatAsync(...); break;
            case OpCode.List: await HandleListAsync(...); break;
        }
    }

    private async Task HandleGetAsync(...) {
        var file = await engine.GetAsync(key, ct);
        if (file is null) { WriteNotFound(writer); return; }
        WriteResponseHeader(writer, StatusCode.Ok,
            meta: new { size = file.Length }, payloadLen: file.Length);
        // Zero-copy: Socket.SendFileAsync via transport
        await writer.FlushAsync(ct);
        await SendFileAsync(file, ct);
    }
}
```
- GET uses `Socket.SendFileAsync(filePath)` — zero-copy sendfile syscall
- PUT streams directly to file without buffering full 800 MB
- **Tests:** `Tests.Store/StoreServerTests.cs` (integration)
  - PUT 100 MB → GET → data matches, RSS doesn't spike
  - sendfile confirmed via `strace -e sendfile`
  - All five ops tested

### M0.2.3: Config + Debug Endpoint
```csharp
// appsettings.json
{
  "Store": {
    "Host": "0.0.0.0", "Port": 9500,
    "StoreDir": "/mnt/llm-ram/store",
    "MaxPayloadBytes": 4294967296,
    "DebugHttpPort": 9501
  }
}
```
- Minimal ASP.NET Core on :9501 with `GET /debug` → JSON stats
- **Done when:** `curl :9501/debug` returns file count + disk usage

---

## Task M0.3: Hydra Agent (C# — Hydra.Agent)
**Project:** `src/Hydra.Agent/Hydra.Agent.csproj`

### M0.3.1: Llama Client (`LlamaClient.cs`)
```csharp
public sealed class LlamaClient(HttpClient http, ILogger logger) {
    // Streaming state endpoints (M0.0 patches)
    public Task<Stream> GetStateAsync(int slotId, CancellationToken ct);
    public Task<RestoreResult> PutStateAsync(int slotId, Stream data, CancellationToken ct);
    public Task<SlotMeta> GetStateMetaAsync(int slotId, CancellationToken ct);

    // Standard endpoints (always available)
    public Task<bool> HealthAsync(CancellationToken ct);
    public Task<List<SlotInfo>> GetSlotsAsync(CancellationToken ct);
    public Task EraseSlotAsync(int slotId, CancellationToken ct);
    public Task<int?> FindIdleSlotAsync(CancellationToken ct);
}
```
- `HttpClient` with timeout via `CancellationToken`
- Streaming GET uses `HttpCompletionOption.ResponseHeadersRead`
- **Tests:** `Tests.Agent/LlamaClientTests.cs` (mock HttpMessageHandler)
  - GetState: mock 800 MB response, verify stream returned (not buffered)
  - PutState: verify binary body sent, response parsed
  - GetStateMeta: verify JSON parsed to SlotMeta
  - Health: true/false scenarios

### M0.3.2: State Handler (`StateHandler.cs`)
```csharp
public sealed class StateHandler(
    LlamaClient llama, RpcClient store, ILogger logger) {

    public async Task<SaveResult> SaveToStoreAsync(
            string sessionId, int slotId, string traceId, CancellationToken ct) {
        // 1. GET /slots/{slotId}/state → stream
        var stateStream = await llama.GetStateAsync(slotId, ct);
        // 2. RPC PUT "kv/{sessionId}" — pipe stream directly, no buffering
        await store.RequestAsync(OpCode.Put, $"kv/{sessionId}",
            stateStream, traceId, ct);
        return new SaveResult(...);
    }

    public async Task<RestoreResult> RestoreFromStoreAsync(
            string sessionId, int slotId, string traceId, CancellationToken ct) {
        // 1. RPC GET "kv/{sessionId}" → stream
        var storeStream = store.RequestStreamAsync(OpCode.Get,
            $"kv/{sessionId}", traceId, ct);
        // 2. PUT /slots/{slotId}/state — pipe stream directly
        var result = await llama.PutStateAsync(slotId, storeStream, ct);
        return new RestoreResult(...);
    }
}
```
- No disk I/O — stream piped directly llama ↔ store
- **Tests:** `Tests.Agent/StateHandlerTests.cs`
  - Save: mock llama stream + mock store → verify pipe called in order
  - Restore: mock store stream + mock llama → verify pipe called
  - Store NOT_FOUND → propagates as exception
  - Llama restore fails → exception with context

### M0.3.3: Agent RPC Server (`AgentServer.cs`)
```csharp
public sealed class AgentServer(AgentConfig cfg, StateHandler handler,
    LlamaClient llama, RpcClient store)
    : RpcServer(cfg.Host, cfg.Port) {

    protected override async Task HandleAsync(OpCode op, ...) {
        switch (op) {
            case OpCode.SaveState:    await HandleSaveStateAsync(...);    break;
            case OpCode.RestoreState: await HandleRestoreStateAsync(...); break;
            case OpCode.SlotStatus:   await HandleSlotStatusAsync(...);   break;
            case OpCode.NodeHealth:   await HandleNodeHealthAsync(...);   break;
        }
    }
}
```
- **Lines:** ~60

### M0.3.4: Config + Debug Endpoint
```json
{
  "Agent": {
    "Host": "0.0.0.0", "Port": 9601,
    "NodeName": "rtx",
    "LlamaUrl": "http://localhost:8080",
    "StoreHost": "127.0.0.1", "StorePort": 9500,
    "DebugHttpPort": 9611
  }
}
```

---

## Task M0.4: Integration + E2E

### M0.4.1: Store ↔ Agent Integration (`Tests.Integration/StoreAgentTests.cs`)
- Start real Store server on random port
- Start Agent with mock LlamaClient
- SAVE_STATE: mock llama returns 10 MB stream → Store has file
- RESTORE_STATE: Store has file → mock llama receives stream
- Verify trace_id appears in both service logs

### M0.4.2: E2E with Real llama-servers (`tests/e2e/test_e2e.py`)
**Python script** — runs against real patched llama-servers + C# services.

```python
async def test_full_migration():
    # 1. Send prompt to RTX (via llama-server directly)
    response = await send_completion(RTX_URL, PROMPT, max_tokens=50)

    # 2. Tell RTX Agent to save
    await rpc_call(RTX_AGENT, OpCode.SAVE_STATE, "test_session")

    # 3. Tell P100 Agent to restore
    await rpc_call(P100_AGENT, OpCode.RESTORE_STATE, "test_session")

    # 4. Send continuation to P100 (MUST have more tokens than cached)
    result = await send_completion(P100_URL, PROMPT + response + NEW_QUESTION)

    assert result["timings"]["cache_n"] > 0
    assert result["timings"]["prompt_ms"] < 5000  # not 27000ms
```

---

## Task Summary

| Task    | Lang | Component | Lines | Test File                   | Parallel? |
|---------|------|-----------|-------|-----------------------------|-----------|
| M0.0.1-3| C++  | llama fork| ~80   | manual curl                 | Start first|
| M0.1.1  | C#   | Shared    | 60    | ProtocolTests.cs            | After M0.0|
| M0.1.2  | C#   | Shared    | 80    | RpcServerTests.cs           | After M0.1.1|
| M0.1.3  | C#   | Shared    | 80    | RpcClientTests.cs           | After M0.1.1|
| M0.1.4  | C#   | Shared    | 30    | manual                      | Anytime   |
| M0.2.1  | C#   | Store     | 60    | StorageEngineTests.cs       | Parallel with M0.3|
| M0.2.2  | C#   | Store     | 100   | StoreServerTests.cs         | After M0.2.1|
| M0.2.3  | C#   | Store     | 20    | manual                      | After M0.2.2|
| M0.3.1  | C#   | Agent     | 80    | LlamaClientTests.cs         | Parallel with M0.2|
| M0.3.2  | C#   | Agent     | 80    | StateHandlerTests.cs        | After M0.3.1|
| M0.3.3  | C#   | Agent     | 60    | —                           | After M0.3.2|
| M0.4.1  | C#   | Integration| 60   | StoreAgentTests.cs          | After M0.2+M0.3|
| M0.4.2  | Python| E2E      | 60    | test_e2e.py                 | After all above|

**Dependency order:**
```
M0.0 (C++ llama fork) ──► M0.4.2 (E2E needs patched servers)
M0.1 (Shared lib)     ──► M0.2 and M0.3 (both depend on shared)
M0.2 and M0.3         run in parallel
M0.4.1                ──► M0.2 + M0.3 both done
M0.4.2                ──► everything done
```
