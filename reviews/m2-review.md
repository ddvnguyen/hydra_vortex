---
milestone: M2
reviewer: claude-sonnet-4-6
date: 2026-05-28
status: open
---

## Summary

M2 adds content-addressable chunked dedup (ChunkEngine, ChunkStore, StoreServer chunked ops),
Agent-side chunked save/restore (LocalChunkCache + ChunkHashTeeStream), and coordinator prefix
checkpoints. The core chunking engine and unit tests are well-written and correct. Two P0
correctness bugs: partial-cache restore feeds incomplete data to llama, and the final chunk
hash is silently dropped before being saved. One P1 means the full-cache-hit path falsely
claims restore success with n_past=0. PUSH_CHUNKS and prefix checkpoints are incomplete paths.

## Findings

---

### [M2-P0-001] Partial-cache restore sends only missing chunks to llama — full state never reconstructed
**File:** `src/Hydra.Agent/StateHandler.cs:190–191`
**Status:** open
**Issue:** #16
**Assigned:** —

`GetChunked` returns only the chunks the client didn't report as known. `RestoreFromStoreChunkedAsync`
feeds that partial payload directly to `llama.PutStateAsync`:

```csharp
var dataStream = new MemoryStream(getResp.Payload);  // only MISSING chunks
var result = await _llama.PutStateAsync(slotId, dataStream, totalSize, ct);
```

If the client has 9/10 chunks cached, only 1 chunk (~1 MB) is in `getResp.Payload`. That 1 MB
is sent to llama as if it's the full 800 MB KV state, which will corrupt or reject the restore.
Only two extreme cases work correctly: no cache (full payload) and full cache (zero bytes,
early return). Any partial cache hit silently corrupts the session.

**Fix:** `LocalChunkCache` must store chunk *data* on disk (not just hashes), so the agent can
reassemble the full state from cached chunks + received missing chunks in correct index order.
Alternatively, drop client-side caching from `GetChunked` and use `GetChunked` as a pure
full-transfer op; keep SYNC_PLAN+PUSH_CHUNKS for the upload-dedup path only.

---

### [M2-P0-002] Final chunk hash dropped — teeStream not disposed before SaveHashesAsync
**File:** `src/Hydra.Agent/StateHandler.cs:121–141`
**Status:** open
**Issue:** #17
**Assigned:** —

`ChunkHashTeeStream` adds the final partial-chunk hash only in `Dispose`/`DisposeAsync`. The
stream is never explicitly disposed before hashes are saved:

```csharp
var teeStream = new ChunkHashTeeStream(stateStream, hashes, buffer);
var response = await _store.RequestStreamBodyAsync(..., teeStream, ...);
// teeStream not disposed — last partial chunk hash not yet in `hashes`
await _chunkCache.SaveHashesAsync(sessionId, hashes, ct);  // ← missing last hash
```

If total state size is not a multiple of 1 MB (virtually always), the saved hash list is 1
entry short. On next restore the store thinks the last chunk is missing and re-sends it;
the diff plan is wrong for every save.

**Fix:** Wrap in `await using var teeStream = new ChunkHashTeeStream(...)` and move
`SaveHashesAsync` after the `using` block so `DisposeAsync` runs first.

---

### [M2-P1-001] Full-cache hit returns Restored=true with NPast=0 without restoring state
**File:** `src/Hydra.Agent/StateHandler.cs:183–188`
**Status:** open
**Issue:** #18
**Assigned:** —

```csharp
if (totalSize == 0 && cachedHashes.Count > 0)
{
    return new RestoreSessionResult(sessionId, slotId, true, 0, ...);
    //                                                      ^--- n_past = 0
}
```

`LocalChunkCache` stores hashes, not chunk data. When all chunks are "cached," there is no
data to PUT to llama — the KV state was never actually restored to the GPU. The coordinator
receives `n_past=0` and `restored=true`, updates the session table accordingly, and the next
request's n_past guard sees 0 and won't protect against cache nuke.

The integration test `RestoreFromStoreChunked_WithLocalCache_SkipsTransfer` validates this
broken behavior by asserting that llama PUT is NOT called when chunks are locally cached.

**Fix:** Remove this early-return path until local chunk data storage is implemented (see
M2-P0-001). Until then, always fetch from the store on restore.

---

### [M2-P1-002] PUSH_CHUNKS never writes a manifest — flow is a dead end
**File:** `src/Hydra.Store/StoreServer.cs:311–349`
**Status:** open
**Issue:** #19
**Assigned:** —

`HandlePushChunksAsync` stores raw chunks by hash but never creates or updates a manifest for
the session key. There is no way to `GET_CHUNKED` a session uploaded via `PUSH_CHUNKS` — the
store has no chunk list for that key. The intended SYNC_PLAN → PUSH_CHUNKS flow is unusable
as a restore path.

**Fix:** `PUSH_CHUNKS` payload should include a manifest (or a separate `COMMIT_CHUNKS` opcode
should finalize the session after pushing). The client must send the ordered chunk list so the
store can write the manifest and enable subsequent `GET_CHUNKED`.

---

### [M2-P1-003] Prefix checkpoint save is non-chunked — coordinator never uses chunked path
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
**Issue:** #21
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
**Issue:** #22
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
**Issue:** #23
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
**Issue:** #24
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
**Issue:** #25
**Assigned:** —

`OpCode.PutChunked` (0x10) and `GetChunked` (0x11) mean "store-level chunk operations" in the
protocol, but the Agent maps them to "chunked save from llama" / "chunked restore to llama".
Any code that accidentally sends a Store `PutChunked` to an Agent port silently gets
`SaveStateChunked` behavior instead.

The coordinator currently only sends `SaveState`/`RestoreState` to agents so this causes no
failures today, but the opcode reuse is a maintenance hazard.

**Fix:** Assign dedicated opcodes in the 0x20 range (or extend the 0x20 block) for
`SaveStateChunked` and `RestoreStateChunked`.
