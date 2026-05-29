---
milestone: M0
reviewer: claude-sonnet-4-6
date: 2026-05-28
status: open
---

## Summary

M0 covers the foundational data path: llama.cpp fork (3 streaming endpoints), Hydra.Shared
(RPC protocol + server + client), Hydra.Store (StorageEngine + StoreServer), and Hydra.Agent
(LlamaClient + StateHandler + AgentServer). Core architecture is sound — System.IO.Pipelines
used correctly, sendfile wired up, structured logging in place. Two correctness bugs in
RpcClient retry logic and one overflow lurking in ReadPayloadAsync. Performance overhead from
excessive flushes per response.

## Findings

---

### [M0-P1-001] RpcClient retry logic never uses the last delay
**File:** `src/Hydra.Shared/RpcClient.cs:149–186` (same pattern in all three catch blocks)
**Status:** open
**Issue:** #27
**Assigned:** —

The `when (attempts < RetryDelays.Length)` guard and the inner `if (attempts < RetryDelays.Length)`
re-check create an off-by-one. After `attempts++` inside the handler, the inner check throws on
`attempts == 3` before waiting `RetryDelays[2]` (2000 ms) or reconnecting. Effectively only
two retries fire; the third delay slot is dead code.

```csharp
catch (IOException) when (attempts < RetryDelays.Length)   // enters when attempts=0,1,2
{
    attempts++;                                              // now 1,2,3
    if (attempts < RetryDelays.Length)                       // true for 1,2; false for 3
        ...delay + reconnect...
    else
        throw;                                               // fires at attempts==3, no 2000ms wait
}
```

**Fix:** Remove the inner `if/else`; always delay and reconnect inside the `catch`, and rely only
on the `when` guard to limit entries.

---

### [M0-P1-002] ReadPayloadAsync truncates long to int for large payloads
**File:** `src/Hydra.Shared/RpcServer.cs:160`
**Status:** open
**Issue:** #27
**Assigned:** —

```csharp
var result = await reader.ReadAtLeastAsync((int)payloadLen, ct);
```

`payloadLen` is `long`. At 800 MB this is fine; at > 2 GB it overflows to a negative value,
causing `ArgumentOutOfRangeException`. The KV state for qwen35moe at 80K context is ~800 MB
today but could exceed 2 GB with larger models or contexts.

**Fix:** `ReadAtLeastAsync` accepts `int` — for large payloads, loop reading chunks rather than
reading the entire payload at once. Alternatively, only use `ReadPayloadAsync` for small known
payloads (JSON headers) and pipe large payloads directly.

---

### [M0-P2-001] _connections list has TOCTOU between Add and RemoveAll
**File:** `src/Hydra.Shared/RpcServer.cs:53–56`
**Status:** open
**Issue:** #27
**Assigned:** —

```csharp
_connections.Add(connTask);                            // on accept loop thread
_ = connTask.ContinueWith(_ => CleanConnections(), TaskScheduler.Default);

private void CleanConnections() => _connections.RemoveAll(t => t.IsCompleted);
```

`List<Task>` is not thread-safe. `CleanConnections` (via `ContinueWith` on a thread pool thread)
may run concurrently with `_connections.Add` in the accept loop, causing list corruption under
load.

**Fix:** Use `ConcurrentBag<Task>` or `ConcurrentDictionary<Task, byte>`, or lock `_connections`
in both paths.

---

### [M0-P2-002] Multiple flushes per response add unnecessary syscalls
**File:** `src/Hydra.Shared/RpcServer.cs:167–183`
**Status:** open
**Issue:** #27
**Assigned:** —

`WriteResponseHeaderAsync` flushes after the 12-byte header. `WriteMetaAsync` flushes after
meta JSON. Handlers then flush again after writing payload. For an 800 MB GET response this is
three `FlushAsync` calls before data flows. Each flush is a potential `write()` syscall boundary.

**Fix:** Only call `FlushAsync` once after the full response (header + meta + payload) is
written. The `PipeWriter` buffers internally until flushed.

---

### [M0-P2-003] StateHandler TOCTOU between GetStateMetaAsync and GetStateAsync
**File:** `src/Hydra.Agent/StateHandler.cs:46–48`
**Status:** open
**Issue:** #28
**Assigned:** —

```csharp
var meta = await _llama.GetStateMetaAsync(slotId, ct);   // GET /slots/0/state/meta
var stateStream = await _llama.GetStateAsync(slotId, ct); // GET /slots/0/state
var stateSize = meta.StateSize;
```

The meta (which reports state size) and the actual stream are fetched in two separate HTTP
calls. If a concurrent inference increments `n_past` between the two calls, `stateSize` will
not match the actual stream length, causing the store PUT to report an incorrect size or
`EndOfStreamException`.

**Fix:** Use the `Content-Length` header from the GET /state response directly (already set by
the llama fork as `X-Hydra-State-Size`), or hold a slot lock between meta and state fetches.

---

### [M0-P2-004] proxy.py appends non-standard SSE event after [DONE]
**File:** `src/coordinator/proxy.py:56`
**Status:** open
**Issue:** #28
**Assigned:** —

```python
yield f"data: {json.dumps({'hydra': ...})}\n\n"
```

This event is appended after llama-server has already sent `data: [DONE]`. SSE clients that
stop consuming on `[DONE]` (Cline, some OpenWebUI versions) will silently ignore it. Clients
that consume all events may try to parse it as a chat completion delta and fail.

**Fix:** Inject hydra metadata in the response HTTP headers (`X-Trace-Id`, `X-Hydra-Node`)
instead of an extra SSE event. The headers are already set on `StreamingResponse`.
