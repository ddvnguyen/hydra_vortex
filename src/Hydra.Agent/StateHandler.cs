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
        var stateStream = await _llama.GetStateAsync(slotId, ct);
        var stateSize = meta.StateSize;

        _log.Information("Saving session {SessionId} slot {SlotId} state (size={Size}) to store",
            sessionId, slotId, stateSize);

        await _store.RequestStreamBodyAsync(
            OpCode.Put, $"kv/{sessionId}", stateStream, stateSize,
            traceId, ct);

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Saved session {SessionId} slot {SlotId} to store in {Elapsed}ms",
            sessionId, slotId, elapsed);

        return new SaveResult(sessionId, slotId, meta.NPast, stateSize, elapsed);
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

        await _llama.PutStateAsync(slotId, asyncStream, longContentLength, ct);

        // Query llama for actual n_past after restore (not from PutState response).
        var meta = await _llama.GetStateMetaAsync(slotId, ct);

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Restored session {SessionId} to slot {SlotId} in {Elapsed}ms",
            sessionId, slotId, elapsed);

        return new RestoreSessionResult(sessionId, slotId, true, meta.NPast, longContentLength, elapsed);
    }

    public async Task<SaveResult> SaveToStoreChunkedAsync(
        string sessionId, int slotId, string traceId, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);
        var sw = ValueStopwatch.StartNew();

        var meta = await _llama.GetStateMetaAsync(slotId, ct);
        var stateStream = await _llama.GetStateAsync(slotId, ct);
        var stateSize = meta.StateSize;

        _log.Information("Saving session {SessionId} slot {SlotId} state chunked (size={Size})",
            sessionId, slotId, stateSize);

        var hashes = new List<string>();
        var buffer = new byte[1024 * 1024];
        var storeKey = $"kv/{sessionId}";
        int deduped = 0;
        int total = 0;

        // Send n_past to Store so it can update the manifest after PushChunks.
        var nPastPayload = JsonSerializer.SerializeToUtf8Bytes(new { n_past = meta.NPast });
        await _store.RequestAsync(OpCode.PutMeta, storeKey, nPastPayload, traceId, ct);

        // Configure TeeStream to also save chunk data locally during streaming.
        var teeStream = new ChunkHashTeeStream(stateStream, hashes, buffer, _chunkCache, sessionId);

        {
            await using (teeStream)
            {
                var response = await _store.RequestStreamBodyAsync(
                    OpCode.PutChunked, storeKey, teeStream, stateSize,
                    traceId, ct);

                if (response.Meta is not null)
                {
                    using var doc = JsonDocument.Parse(response.Meta);
                    if (doc.RootElement.TryGetProperty("deduped_chunks", out var d))
                        deduped = d.GetInt32();
                    if (doc.RootElement.TryGetProperty("total_chunks", out var t))
                        total = t.GetInt32();
                }
            }
        }

        await _chunkCache.SaveHashesAsync(sessionId, hashes, ct);
        _chunkCache.EvictLRU();

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Saved chunked session {SessionId} slot {SlotId} in {Elapsed}ms (chunks={Total}, deduped={Deduped})",
            sessionId, slotId, elapsed, total, deduped);

        return new SaveResult(sessionId, slotId, meta.NPast, stateSize, elapsed);
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

        int nPast = 0;
        List<ChunkRef> allChunks = [];
        if (manifestResp.Meta is not null)
        {
            using var doc = JsonDocument.Parse(manifestResp.Meta);
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

        // Allocate buffer for the complete KV state (without header).
        var kvBuffer = new byte[totalSize];

        // Fill local cached chunks first.
        foreach (var chunk in allChunks)
        {
            if (cachedHashes.Contains(chunk.Hash))
            {
                var chunkData = await _chunkCache.GetChunkDataAsync(sessionId, chunk.Hash, ct);
                if (chunkData is not null && chunkData.Length == chunk.Size)
                {
                    int dataOffset = chunk.Index * ChunkEngine.ChunkSize;
                    Array.Copy(chunkData, 0, kvBuffer, dataOffset, chunkData.Length);
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
            if (dataOffset + chunkSize > kvBuffer.Length)
            {
                _log.Warning("Chunk overflow for session {SessionId}: offset={Offset} + size={Size} > total={Total}",
                    sessionId, dataOffset, chunkSize, kvBuffer.Length);
                storeOffset += chunkSize;
                continue;
            }

            Array.Copy(storePayload, storeOffset, kvBuffer, dataOffset, chunkSize);
            storeOffset += chunkSize;
        }

        // Prepend llama state header: [4B n_past][4B n_tok=0] + KV cache data.
        var completeState = new byte[8 + kvBuffer.Length];
        var nPastBytes = BitConverter.GetBytes(nPast);
        Buffer.BlockCopy(nPastBytes, 0, completeState, 0, 4);
        var nTokBytes = BitConverter.GetBytes(0);
        Buffer.BlockCopy(nTokBytes, 0, completeState, 4, 4);
        Buffer.BlockCopy(kvBuffer, 0, completeState, 8, kvBuffer.Length);

        var dataStream = new MemoryStream(completeState);
        await _llama.PutStateAsync(slotId, dataStream, (long)completeState.Length, ct);

      // Query llama for actual n_past after restore (not from header or manifest).
        var postRestoreMeta = await _llama.GetStateMetaAsync(slotId, ct);

        _log.Information("Restored session {SessionId} slot {SlotId} in {Elapsed}ms",
            sessionId, slotId, sw.ElapsedMilliseconds);

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
