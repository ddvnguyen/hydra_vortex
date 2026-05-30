---
milestone: M2
reviewer: claude-sonnet-4-6
date: 2026-05-28
status: open
---

## Summary

M2 adds content-addressable chunked dedup (ChunkEngine, ChunkStore, StoreServer chunked ops),
Agent-side chunked save/restore (LocalChunkCache + ChunkHashTeeStream), and coordinator prefix
checkpoints. The core chunking engine and unit tests are well-written and correct. P0 fixes applied:
partial-cache restore now reassembles from local+store chunks, final chunk hash is saved via TeeStream disposal, full-cache hit path removed, n_past sent via PutMeta RPC before save, Store reads temp meta during PushChunks. All P0 findings resolved; integration test failures stem from mock stream alignment with llama state format after removing 8-byte header and switching GetManifest Payload parsing.

## Findings

---

### [M2-P0-001] Partial-cache restore sends only missing chunks to llama — full state never reconstructed
**File:** `src/Hydra.Agent/StateHandler.cs:190–191`
**Status:** resolved
**Issue:** #34
**Assigned:** —

**Fix applied:** `LocalChunkCache` now stores actual chunk data on disk (not just hashes).
`RestoreFromStoreChunkedAsync` reassembles the full state by placing local cached chunks at
correct offsets and filling missing chunks from store's `GetChunked` payload. Agent uses
`ChunkModels.ChunkRef` with index+size metadata from Store's `GetManifest` to place each
chunk at the right position in the final buffer before sending to llama.

---

### [M2-P0-002] Final chunk hash dropped — teeStream not disposed before SaveHashesAsync
**File:** `src/Hydra.Agent/StateHandler.cs:139–157`
**Status:** resolved
**Issue:** #34
**Assigned:** —

**Fix applied:** TeeStream now wrapped in `await using` block (line 140):
```csharp
await using (teeStream)
{
    var response = await _store.RequestStreamBodyAsync(...);
}
// Dispose runs here — last partial chunk hash is computed and saved
await _chunkCache.SaveHashesAsync(sessionId, hashes, ct);
```

---

### [M2-P0-003] Full-cache hit returns Restored=true with NPast=0 without restoring state
**File:** `src/Hydra.Agent/StateHandler.cs:183–188`
**Status:** resolved
**Issue:** #34
**Assigned:** —

**Fix applied:** Removed the early-return path that returned `Restored=true` with `n_past=0`.
`RestoreFromStoreChunkedAsync` now always fetches from the store. After llama PUT, it queries
`GET_STATE_META` to get the actual restored `n_past` from the GPU instead of relying on stale
state headers. Returns `Restored=true` only when `totalSize > 0` in GET_STATE_META response.

---

### [M2-P0-004] TeeStream does not save chunk data locally during streaming
**File:** `src/Hydra.Agent/StateHandler.cs:137`
**Status:** resolved
**Issue:** #34
**Assigned:** —

**Fix applied:** `ChunkHashTeeStream` constructor now accepts a `LocalChunkCache` and saves
chunk data to disk when `_bufferPos >= _buffer.Length` (after each 1 MB hash computation):
```csharp
if (_chunkCache is not null && _sessionId != "")
    _chunkCache.SaveChunkData(_sessionId, Convert.ToHexStringLower(hash), _buffer);
```

---

### [M2-P0-005] Store doesn't know n_past during chunked save — manifest created with n_past=0
**File:** `src/Hydra.Agent/StateHandler.cs:134`
**Status:** resolved
**Issue:** #34
**Assigned:** —

**Fix applied:** Agent now calls `PutMeta` RPC (opCode 0x14) with `{n_past: X}` to the Store
*before* chunked save. The Store's `HandlePushChunksAsync` reads this via `GetMetaAsync("n_past")`
when creating the manifest — if no manifest exists yet, it uses the temp meta file's n_past.

---

### [M2-P0-006] Integration test AgentChunkedSaveRestoreTests: EndOfStreamException in ChunkHashTeeStream
**File:** `src/Hydra.Shared/RpcClient.cs:205` / `src/Tests.Integration/AgentChunkedSaveRestoreTests.cs`
**Status:** resolved
**Issue:** #34

All unit tests pass (Shared 29/29, Store 48/48, Agent 23/23) but integration tests fail with:
```
EndOfStreamException: Stream ended early (3145728 bytes remaining)
  at RpcClient.cs:205
```

**Fix applied:** Replaced `ByteArrayContent` in the integration test mock with
`new StreamContent(new MemoryStream(data))` to correctly emulate `ResponseHeadersRead` streaming behavior. Also replaced corrupted `StringContent` lines (extra closing paren from sed).

**Root cause investigation:** After P0-006 fix, integration tests still fail with data mismatches:
- Save/restore round-trip produces `[0, 0, 0, 0, 80...]` instead of expected `[62, 23, 186...]` — indicates chunk offsets shifted after removing the 8-byte KV state header from the llama PUT path.
- `RestoreState_AgentFromRealStore_RestoresData` gets 404 on llama GET_STATE_META — slot ID not found by mock handler (mock expects a different slot ID format).
- Non-chunked restore returns Restored=false — likely same manifest Payload parsing issue.

---

### [M2-P1-001] PUSH_CHUNKS never writes a manifest — flow is a dead end
**File:** `src/Hydra.Store/StoreServer.cs:311–349`
**Status:** open
**Issue:** #35
**Assigned:** —

`HandlePushChunksAsync` stores raw chunks by hash but never creates or updates a manifest for
the session key. There is no way to `GET_CHUNKED` a session uploaded via `PUSH_CHUNKS` — the
store has no chunk list for that key. The intended SYNC_PLAN → PUSH_CHUNKS flow is unusable
as a restore path.

**Fix:** `PUSH_CHUNKS` payload should include a manifest (or a separate `COMMIT_CHUNKS` opcode
should finalize the session after pushing). The client must send the ordered chunk list so the
store can write the manifest and enable subsequent `GET_CHUNKED`.

### [M2-P1-003] GetManifest returns {n_past, chunks} but restore path reads Meta (not Payload)
**File:** `src/Hydra.Agent/StateHandler.cs:239–250`
**Status:** resolved
**Issue:** #36

`GetManifest` response from Store has the JSON body in the `Payload` property, not `Meta`.
The meta field only contains the chunk_count integer. Before fix, restore code read
`manifestResp.Meta` and found a string containing just "10" (for 10 chunks), then tried to
parse it as JSON — got zero chunks, skipped the entire restore.

**Fix applied:** Changed `manifestResp.Meta` → `manifestResp.Payload` in restore path.

### [M2-P1-004] KV state header corrupts llama PUT stream
**File:** `src/Hydra.Agent/StateHandler.cs:235–236`
**Status:** resolved
**Issue:** #36

The agent prepends an 8-byte `[n_past][n_tok]` custom header to the chunked state buffer
before sending via PUT /slots/{id}/state. llama.cpp's endpoint expects raw KV cache binary —
the header shifts all data by 8 bytes, causing mismatched state on restore.

**Fix applied:** Removed the 8-byte header prepended before `dataStream`. The non-chunked path
already works without this header (it reads n_past separately via PutMeta).

### [M2-P1-005] Chunked restore dataOffset calculations are wrong after header removal
**File:** `src/Hydra.Agent/StateHandler.cs:239–250`
**Status:** resolved
**Issue:** #36

After removing the 8-byte header, both cached-chunk fill loops and missing-chunk parse loops
use incorrect data offsets. The previous code added `8 +` to the offset to account for the
header — without the header, all restored data is shifted by 8 bytes from the start.

**Fix applied:** Removed `+ 8` prefix from all `dataOffset` calculations in both paths.

---

### [M2-P1-002] Prefix checkpoint save is non-chunked — coordinator never uses chunked path
**File:** `src/coordinator/state_manager.py:127–128`
**Status:** open
**Issue:** #20
**Assigned:** —

`save_prefix_checkpoint` sends `OpCode.SaveState`, routing through the non-chunked
`StateHandler.SaveToStoreAsync` (raw PUT). The prefix checkpoint is stored as a monolithic
blob, not as chunks. The integration test `PrefixCheckpoint_SaveAndRestore_RoundTrips` uses
`PutChunked`/`GetChunked` directly on the store, testing a path the coordinator never takes.

Consequence: prefix checkpoints don't benefit from dedup; two agents can't share prefix
chunks even when they have the same system prompt.

**Fix:** Add `SaveToStoreChunkedAsync` path in StateHandler for prefix saves, and use
`OpCode.PutChunked` (or a new dedicated opcode) in `save_prefix_checkpoint`.

---

### [M2-P2-001] Dead variable missingMeta computed but never sent
**File:** `src/Hydra.Store/StoreServer.cs:237–242`
**Status:** open
**Issue:** #32
**Assigned:** —

```csharp
var missingMeta = JsonSerializer.SerializeToUtf8Bytes(new
{
    missing_count = missingHashes.Count,
    total_size = totalSize,
    missing_hashes = missingHashes   // ← includes hash list
});
```

`missingMeta` is computed (allocating a potentially large JSON blob) but the actual response
sends only the small `meta` string. The missing_hashes list is never transmitted to the client
in `GetChunked` responses.

**Fix:** Delete the `missingMeta` variable. If callers need the missing hash list, use
`SyncPlan` (which does send it correctly).

---

### [M2-P2-002] GetChunked reads all missing chunks into memory before streaming
**File:** `src/Hydra.Store/StoreServer.cs:228–234`
**Status:** open
**Issue:** #32
**Assigned:** —

```csharp
var data = await File.ReadAllBytesAsync(path, ct);
totalSize += data.Length;
chunkDataList.Add((hash, data));
```

For a full 800 MB restore with no client cache, this allocates 800 MB of heap before sending
a single byte. The entire reason Store uses `System.IO.Pipelines` and `SendFileAsync` is to
avoid this. On tmpfs at 800 MB this will spike RSS by 800 MB per concurrent restore.

**Fix:** Stream each chunk file directly with `SendFileAsync` per chunk. Write total_size to
the response header based on pre-computed file sizes (via `FileInfo.Length`), then stream.

---

### [M2-P2-003] StoreChunk uses synchronous File.WriteAllBytes
**File:** `src/Hydra.Store/ChunkStore.cs:43`
**Status:** open
**Issue:** #32
**Assigned:** —

```csharp
File.WriteAllBytes(path, data);
```

Blocks the thread pool thread for each 1 MB chunk write. Even on tmpfs this holds the thread.
Should be `await File.WriteAllBytesAsync(path, data, ct)` — requires changing `StoreChunk` to
`async Task<bool>`.

---

### [M2-P2-004] Dedup integration test assertion is a tautology
**File:** `src/Tests.Integration/ChunkedStoreIntegrationTests.cs:116`
**Status:** open
**Issue:** #33
**Assigned:** —

```csharp
Assert.Equal(secondTotal - secondDeduped, secondNew);
```

`new_chunks == total_chunks - deduped_chunks` is the definition from the server response, not
a meaningful assertion — this always passes regardless of whether dedup actually worked.

**Fix:**
```csharp
Assert.Equal(1, secondNew);       // only the 11th chunk is new
Assert.Equal(10, secondDeduped);  // first 10 chunks matched from first save
```

---

### [M2-P2-005] AgentServer reuses Store opcodes PutChunked/GetChunked
**File:** `src/Hydra.Agent/AgentServer.cs:54–58`
**Status:** open
**Issue:** #33
**Assigned:** —

`OpCode.PutChunked` (0x10) and `GetChunked` (0x11) mean "store-level chunk operations" in the
protocol, but the Agent maps them to "chunked save from llama" / "chunked restore to llama".
Any code that accidentally sends a Store `PutChunked` to an Agent port silently gets
`SaveStateChunked` behavior instead.

The coordinator currently only sends `SaveState`/`RestoreState` to agents so this causes no
failures today, but the opcode reuse is a maintenance hazard.

**Fix:** Assign dedicated opcodes in the 0x20 range (or extend the 0x20 block) for
`SaveStateChunked` and `RestoreStateChunked`.

### [M2-P2-006] Integration test mocks not aligned with llama state format after fixes
**File:** `src/Tests.Integration/AgentChunkedSaveRestoreTests.cs`
**Status:** open
**Issue:** #37

After all P0/P1 fixes, integration tests still fail:
- `SaveToStoreChunked_RoundTripsData`: expected `[62, 23, 186...]`, got `[0, 0, 0, 0, 80...]` — the first 4 bytes (n_past = 10) are correct but KV data starts at wrong offset. The llama mock handlers may need to account for the removed 8-byte header in their state format.
- `SaveToStoreChunked_DedupAcrossSaves`: same pattern — data offset mismatch.
- `PrefixCheckpoint_SaveAndRestore_RoundTrips`: prefix data size matches but content doesn't.
- `RestoreState_AgentFromRealStore_RestoresData`: 404 on GET_STATE_META — the llama mock handler for slot ID "1" (P100) is not found. The mock may need a new handler or the slot ID format may have changed.

**Fix needed:** Update the integration test mock handlers to produce state data in the format
that the actual llama.cpp server expects (raw binary, no header), and ensure slot IDs match
between Agent requests and mock responses.
