 using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Serilog;

namespace Hydra.Agent;

public sealed record SaveResult(
    string SessionId,
    int SlotId,
    int NPast,
    long Size,
    long ElapsedMs
);

  public sealed record RestoreSessionResult(
    string SessionId,
    int SlotId,
    bool Restored,
    int NPast,
    long Size,
    long ElapsedMs
);


public sealed class StateHandler
{
    private readonly LlamaClient _llama;
    private readonly RpcClient _store;
    private readonly LocalChunkCache _chunkCache;
    private readonly ILogger _log;

    public StateHandler(LlamaClient llama, RpcClient store, LocalChunkCache chunkCache, ILogger log)
    {
        _llama = llama;
        _store = store;
        _chunkCache = chunkCache;
        _log = log;
    }

    public async Task<SaveResult> SaveToStoreAsync(
        string sessionId, int slotId, string traceId, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);
        var sw = ValueStopwatch.StartNew();

        var meta = await _llama.GetStateMetaAsync(slotId, ct);
        var (stateStream, contentLength) = await _llama.GetStateAsync(slotId, ct);

        _log.Information("Saving session {SessionId} slot {SlotId} state (meta_size={MetaSize}, body_len={BodyLen}) to store",
            sessionId, slotId, meta.StateSize, contentLength);

        await _store.RequestStreamBodyAsync(
            OpCode.Put, $"kv/{sessionId}", stateStream, contentLength,
            traceId, ct);

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Saved session {SessionId} slot {SlotId} to store in {Elapsed}ms",
            sessionId, slotId, elapsed);

        return new SaveResult(sessionId, slotId, meta.NPast, contentLength, elapsed);
    }

     public async Task<RestoreSessionResult> RestoreFromStoreAsync(
        string sessionId, int slotId, string traceId, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);
        var sw = ValueStopwatch.StartNew();

        var getResp = await _store.RequestAsync(
            OpCode.Get, $"kv/{sessionId}", ReadOnlyMemory<byte>.Empty,
            traceId, ct);

        if (getResp.Status != (byte)StatusCode.Ok)
        {
            _log.Error("Session {SessionId} not found in store", sessionId);
            throw new InvalidDataException(
                $"Session '{sessionId}' not found in store (status=0x{getResp.Status:X2})");
        }

        var longContentLength = 0L;
        if (getResp.Meta is not null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(getResp.Meta);
            if (doc.RootElement.TryGetProperty("size", out var sizeEl))
                longContentLength = sizeEl.GetInt64();
        }

        _log.Information("Restoring session {SessionId} to slot {SlotId} (size={Size})",
            sessionId, slotId, longContentLength);

        var storeStream = _store.RequestStreamAsync(
            OpCode.Get, $"kv/{sessionId}", ReadOnlyMemory<byte>.Empty,
            traceId, ct);

        var asyncStream = new AsyncEnumerableStream(storeStream, ct);

        var restoreResult = await _llama.PutStateAsync(slotId, asyncStream, longContentLength, ct);

        // Query llama for actual n_past after restore (not from PutState response).
        var meta = await _llama.GetStateMetaAsync(slotId, ct);

        if (!restoreResult.Restored || meta.StateSize == 0)
        {
            _log.Warning("LLAMA restore failed for session {SessionId}: result={Restored}, state_size={StateSize}",
                sessionId, restoreResult.Restored, meta.StateSize);
            return new RestoreSessionResult(sessionId, slotId, false, meta.NPast, longContentLength, sw.ElapsedMilliseconds);
        }

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Restored session {SessionId} to slot {SlotId} in {Elapsed}ms (n_past={NPast})",
            sessionId, slotId, elapsed, meta.NPast);

        return new RestoreSessionResult(sessionId, slotId, true, meta.NPast, longContentLength, elapsed);
    }

    public async Task<SaveResult> SaveToStoreChunkedAsync(
        string sessionId, int slotId, string traceId, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);
        var sw = ValueStopwatch.StartNew();

        var meta = await _llama.GetStateMetaAsync(slotId, ct);
        var (stateStream, contentLength) = await _llama.GetStateAsync(slotId, ct);

        _log.Information("Saving session {SessionId} slot {SlotId} state chunked (meta_size={MetaSize}, body_len={BodyLen})",
            sessionId, slotId, meta.StateSize, contentLength);

        var storeKey = $"kv/{sessionId}";

        // ── Pass 1: chunk the state locally — hash + cache every 1 MB block, build the
        //   full ordered chunk list. Bodies land in LocalChunkCache so we can push only
        //   the missing ones without re-reading the GPU state. ──────────────────────────
        var (chunks, totalSize) = await ChunkStateLocallyAsync(stateStream, sessionId, ct);
        var orderedHashes = chunks.Select(c => c.Hash).ToList();

        // ── Step 2: ask the Store which of these chunks it does NOT already have. ───────
        var missing = await SyncMissingAsync(storeKey, orderedHashes, traceId, ct);

        // ── Step 3: push only the missing chunk bodies (batched, memory-bounded). ───────
        var pushed = await PushMissingChunksAsync(storeKey, sessionId, missing, traceId, ct);

        // ── Step 4: write the authoritative ordered manifest (validates residency). ─────
        await PutManifestAsync(storeKey, meta.NPast, totalSize, chunks, traceId, ct);

        await _chunkCache.SaveHashesAsync(sessionId, orderedHashes, ct);
        _chunkCache.EvictLRU();

        var elapsed = sw.ElapsedMilliseconds;
        var deduped = chunks.Count - missing.Count;
        _log.Information("Saved chunked session {SessionId} slot {SlotId} in {Elapsed}ms " +
            "(chunks={Total}, pushed={Pushed}, deduped={Deduped}, bytes_total={Total_Bytes})",
            sessionId, slotId, elapsed, chunks.Count, pushed, deduped, totalSize);

        return new SaveResult(sessionId, slotId, meta.NPast, totalSize, elapsed);
    }

    // Read the GPU state stream in fixed 1 MB blocks; hash each block (SHA-256, hex-lower
    // — matches the Store's ChunkEngine), cache its body locally, and record an ordered
    // ChunkRef. Index i ⇒ bytes [i*ChunkSize, …], which is exactly how restore reassembles.
    private async Task<(List<ChunkRef> chunks, long totalSize)> ChunkStateLocallyAsync(
        Stream stream, string sessionId, CancellationToken ct)
    {
        var chunks = new List<ChunkRef>();
        var buffer = new byte[ChunkEngine.ChunkSize];
        long totalSize = 0;
        int index = 0;

        while (true)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), ct);
                if (read == 0) break;
                filled += read;
            }
            if (filled == 0) break;

            var slice = buffer.AsSpan(0, filled);
            var hash = Convert.ToHexStringLower(SHA256.HashData(slice));
            await _chunkCache.SaveChunkDataAsync(sessionId, hash, slice.ToArray(), ct);
            chunks.Add(new ChunkRef(index, hash, filled));
            totalSize += filled;
            index++;

            if (filled < buffer.Length) break; // short read ⇒ final chunk
        }

        return (chunks, totalSize);
    }

    // SYNC_MISSING (0x12): send the full ordered hash list, get back the subset the Store
    // lacks (distinct). Empty/duplicate-only states return no missing.
    private async Task<List<string>> SyncMissingAsync(
        string storeKey, List<string> hashes, string traceId, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(hashes);
        var resp = await _store.RequestAsync(OpCode.SyncMissing, storeKey, payload, traceId, ct);
        if (resp.Status != (byte)StatusCode.Ok)
            throw new InvalidDataException($"SYNC_MISSING failed (status=0x{resp.Status:X2})");

        var missing = new List<string>();
        if (resp.Payload is { Length: > 0 })
        {
            using var doc = JsonDocument.Parse(resp.Payload);
            if (doc.RootElement.TryGetProperty("missing_hashes", out var arr))
                foreach (var h in arr.EnumerateArray())
                {
                    var s = h.GetString();
                    if (!string.IsNullOrEmpty(s)) missing.Add(s);
                }
        }
        return missing;
    }

    // PUSH_CHUNKS (0x13): upload only the missing chunk bodies, framed [4B size LE][body],
    // batched so peak memory is bounded regardless of total state size.
    private async Task<int> PushMissingChunksAsync(
        string storeKey, string sessionId, List<string> missing, string traceId, CancellationToken ct)
    {
        if (missing.Count == 0) return 0;

        const int BatchBytes = 32 * 1024 * 1024;
        using var batch = new MemoryStream();
        int pushed = 0;

        async Task FlushAsync()
        {
            if (batch.Length == 0) return;
            await _store.RequestAsync(OpCode.PushChunks, storeKey, batch.ToArray(), traceId, ct);
            batch.SetLength(0);
        }

        var header = new byte[4];
        foreach (var hash in missing)
        {
            var body = await _chunkCache.GetChunkDataAsync(sessionId, hash, ct);
            if (body is null || body.Length == 0) continue;

            BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            batch.Write(header);
            batch.Write(body);
            pushed++;

            if (batch.Length >= BatchBytes) await FlushAsync();
        }
        await FlushAsync();
        return pushed;
    }

    // PUT_MANIFEST (0x15): write the authoritative ordered manifest. The Store refuses if
    // any referenced chunk is not resident, so a partial push can never corrupt restore.
    private async Task PutManifestAsync(
        string storeKey, int nPast, long totalSize, List<ChunkRef> chunks,
        string traceId, CancellationToken ct)
    {
        var manifest = new
        {
            n_past = nPast,
            total_size = totalSize,
            chunks = chunks.Select(c => new { index = c.Index, hash = c.Hash, size = c.Size }),
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var resp = await _store.RequestAsync(OpCode.PutManifest, storeKey, payload, traceId, ct);
        if (resp.Status != (byte)StatusCode.Ok)
            throw new InvalidDataException(
                $"PUT_MANIFEST failed (status=0x{resp.Status:X2}): {resp.Meta}");
    }

    public async Task<RestoreSessionResult> RestoreFromStoreChunkedAsync(
        string sessionId, int slotId, string traceId, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);
        var sw = ValueStopwatch.StartNew();

        var cachedHashes = await _chunkCache.LoadHashesAsync(sessionId, ct);
        var storeKey = $"kv/{sessionId}";

        _log.Information("Restoring chunked session {SessionId} to slot {SlotId} (cached_chunks={Cached})",
            sessionId, slotId, cachedHashes.Count);

        // Request missing chunks from store with known hashes.
        var clientHashesJson = JsonSerializer.Serialize(cachedHashes);
        var getResp = await _store.RequestAsync(
            OpCode.GetChunked, storeKey, Encoding.UTF8.GetBytes(clientHashesJson),
            traceId, ct);

        if (getResp.Status != (byte)StatusCode.Ok)
        {
            _log.Error("Session {SessionId} not found in store", sessionId);
            throw new InvalidDataException(
                $"Session '{sessionId}' not found in store (status=0x{getResp.Status:X2})");
        }

        var totalSize = 0L;
        int missingCount = 0;
        if (getResp.Meta is not null)
        {
            using var doc = JsonDocument.Parse(getResp.Meta);
            if (doc.RootElement.TryGetProperty("total_size", out var s))
                totalSize = s.GetInt64();
            if (doc.RootElement.TryGetProperty("missing_count", out var m))
                missingCount = m.GetInt32();
        }

          if (getResp.Meta is null)
        {
            _log.Error("GetChunked returned OK but no manifest metadata for session {SessionId}",
                sessionId);
            return new RestoreSessionResult(sessionId, slotId, false, 0, 0, sw.ElapsedMilliseconds);
        }

        if (totalSize == 0 || missingCount == 0)
        {
            // Full cache hit — all chunks deduped, nothing to fetch from store.
            // Verify we actually have state in llama (n_past > 0).
            var restoreMeta = await _llama.GetStateMetaAsync(slotId, ct);
            if (restoreMeta.NPast > 0)
            {
                _log.Information("Full cache hit for session {SessionId} — using existing llama state (n_past={NPast})",
                    sessionId, restoreMeta.NPast);
                return new RestoreSessionResult(sessionId, slotId, true, restoreMeta.NPast, restoreMeta.StateSize, sw.ElapsedMilliseconds);
            }

            _log.Warning("No state data to restore for session {SessionId} (total_size={Size}, missing={Missing})",
                sessionId, totalSize, missingCount);
            return new RestoreSessionResult(sessionId, slotId, false, 0, 0, sw.ElapsedMilliseconds);
        }

        // Partial cache hit — fetch manifest and reassemble state from local + store chunks.
        var manifestResp = await _store.RequestAsync(
            OpCode.GetManifest, storeKey, ReadOnlyMemory<byte>.Empty,
            traceId, ct);

        if (manifestResp.Status != (byte)StatusCode.Ok || manifestResp.Payload is null || manifestResp.Payload.Length == 0)
        {
            _log.Error("GetManifest failed for session {SessionId}, status={Status}",
                sessionId, manifestResp.Status);
            return new RestoreSessionResult(sessionId, slotId, false, 0, 0, sw.ElapsedMilliseconds);
        }

        int nPast = 0;
        List<ChunkRef> allChunks = [];
        {
            using var doc = JsonDocument.Parse(manifestResp.Payload);
            if (doc.RootElement.TryGetProperty("n_past", out var npEl))
                nPast = npEl.GetInt32();

            // Extract chunk list from manifest payload.
            var chunksNode = doc.RootElement.GetProperty("chunks");
            foreach (var chunk in chunksNode.EnumerateArray())
            {
                allChunks.Add(new ChunkRef(
                    Index: chunk.GetProperty("index").GetInt32(),
                    Hash: chunk.GetProperty("hash").GetString() ?? "",
                    Size: chunk.GetProperty("size").GetInt32()
                ));
            }
        }

        // Allocate buffer for the full KV state (no custom header — llama expects raw binary).
        var completeState = new byte[totalSize];

        // Fill local cached chunks directly into completeState.
        foreach (var chunk in allChunks)
        {
            if (cachedHashes.Contains(chunk.Hash))
            {
                var chunkData = await _chunkCache.GetChunkDataAsync(sessionId, chunk.Hash, ct);
                if (chunkData is not null && chunkData.Length == chunk.Size)
                {
                   int dataOffset = chunk.Index * ChunkEngine.ChunkSize;
                    Array.Copy(chunkData, 0, completeState, dataOffset, chunkData.Length);
                }
            }
        }

        // Parse missing chunks from GetChunked payload: [4B index][4B size][chunk data]...
        var storePayload = getResp.Payload;
        int storeOffset = 0;

        while (storeOffset < storePayload.Length)
        {
            if (storeOffset + 8 > storePayload.Length)
                break;

            var header = storePayload.AsSpan(storeOffset, 8);
            int chunkIndex = BitConverter.ToInt32(header.Slice(0, 4).ToArray());
            int chunkSize = BitConverter.ToInt32(header.Slice(4, 4).ToArray());
            storeOffset += 8;

            if (chunkIndex < 0 || chunkSize <= 0 || storeOffset + chunkSize > storePayload.Length)
                break;

            int dataOffset = chunkIndex * ChunkEngine.ChunkSize;
            if (dataOffset + chunkSize > completeState.Length)
            {
                _log.Warning("Chunk overflow for session {SessionId}: offset={Offset} + size={Size} > total={Total}",
                    sessionId, dataOffset, chunkSize, completeState.Length);
                storeOffset += chunkSize;
                continue;
            }

            Array.Copy(storePayload, storeOffset, completeState, dataOffset, chunkSize);
            storeOffset += chunkSize;
        }

        using var dataStream = new MemoryStream(completeState);
        var restoreResult = await _llama.PutStateAsync(slotId, dataStream, (long)completeState.Length, ct);

        // Query llama for actual n_past after restore (not from header or manifest).
        var postRestoreMeta = await _llama.GetStateMetaAsync(slotId, ct);

        if (!restoreResult.Restored || postRestoreMeta.StateSize == 0)
        {
            _log.Warning("LLAMA restore failed for session {SessionId}: result={Restored}, n_past={NPast}",
                sessionId, restoreResult.Restored, postRestoreMeta.NPast);
            return new RestoreSessionResult(sessionId, slotId, false, 0, 0, sw.ElapsedMilliseconds);
        }

        _log.Information("Restored session {SessionId} slot {SlotId} in {Elapsed}ms (n_past={NPast})",
            sessionId, slotId, sw.ElapsedMilliseconds, postRestoreMeta.NPast);

        return new RestoreSessionResult(sessionId, slotId, true, postRestoreMeta.NPast, totalSize, sw.ElapsedMilliseconds);
    }

    internal sealed class ChunkHashTeeStream : Stream
    {
    private readonly Stream _inner;
    private readonly List<string> _hashes;
    private readonly byte[] _buffer;
    private int _bufferPos;
    private LocalChunkCache? _chunkCache;
    private string _sessionId = "";


    public ChunkHashTeeStream(Stream inner, List<string> hashes, byte[] buffer)
        : this(inner, hashes, buffer, null!, string.Empty)
    {
    }

    public ChunkHashTeeStream(Stream inner, List<string> hashes, byte[] buffer,
        LocalChunkCache? chunkCache, string sessionId)
    {
        _inner = inner;
        _hashes = hashes;
        _buffer = buffer;
        _chunkCache = chunkCache;
        _sessionId = sessionId;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
            ProcessRead(buffer, offset, read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (read > 0)
            ProcessRead(buffer, offset, read);
        return read;
    }

    private void ProcessRead(byte[] buffer, int offset, int count)
    {
        var remaining = count;
        var bufOffset = offset;

        while (remaining > 0)
        {
            var space = _buffer.Length - _bufferPos;
            var toCopy = Math.Min(remaining, space);
            Array.Copy(buffer, bufOffset, _buffer, _bufferPos, toCopy);
            _bufferPos += toCopy;
            bufOffset += toCopy;
            remaining -= toCopy;

            if (_bufferPos >= _buffer.Length)
            {
                var hash = SHA256.HashData(_buffer.AsSpan(0, _buffer.Length));
                _hashes.Add(Convert.ToHexStringLower(hash));
                // Save chunk data locally for future partial-cache restore.
                if (_chunkCache is not null && _sessionId != "")
                    _chunkCache.SaveChunkData(_sessionId, Convert.ToHexStringLower(hash), _buffer);
                _bufferPos = 0;
            }
        }
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

      protected override void Dispose(bool disposing)
    {
        if (disposing && _bufferPos > 0)
        {
            var hash = SHA256.HashData(_buffer.AsSpan(0, _bufferPos));
            _hashes.Add(Convert.ToHexStringLower(hash));
            // Save partial final chunk data locally for future restore.
            if (_chunkCache is not null && _sessionId != "")
                _chunkCache.SaveChunkData(_sessionId, Convert.ToHexStringLower(hash), _buffer.AsSpan(0, _bufferPos).ToArray());
        }
        _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_bufferPos > 0)
        {
            var hash = SHA256.HashData(_buffer.AsSpan(0, _bufferPos));
            _hashes.Add(Convert.ToHexStringLower(hash));
            // Save partial final chunk data locally for future restore.
            if (_chunkCache is not null && _sessionId != "")
                await _chunkCache.SaveChunkDataAsync(_sessionId, Convert.ToHexStringLower(hash), _buffer.AsSpan(0, _bufferPos).ToArray(), CancellationToken.None);
        }
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

internal struct ValueStopwatch
{
    private readonly long _start;

    private ValueStopwatch(long start) => _start = start;

    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    public long ElapsedMilliseconds
    {
        get
        {
            var elapsed = Stopwatch.GetTimestamp() - _start;
            return elapsed * 1000 / Stopwatch.Frequency;
        }
    }
}

}
