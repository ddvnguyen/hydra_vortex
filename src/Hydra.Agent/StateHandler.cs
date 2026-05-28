using System.Diagnostics;
using System.Security.Cryptography;
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

        var result = await _llama.PutStateAsync(slotId, asyncStream, longContentLength, ct);

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Restored session {SessionId} to slot {SlotId} in {Elapsed}ms",
            sessionId, slotId, elapsed);

        return new RestoreSessionResult(sessionId, slotId, result.Restored, result.NPast, elapsed);
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
        var deduped = 0;
        var total = 0;

        {
            await using var teeStream = new ChunkHashTeeStream(stateStream, hashes, buffer);
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

        // Always get full state from store — client-side delta restore is not yet implemented
        var getResp = await _store.RequestAsync(
            OpCode.GetChunked, storeKey, ReadOnlyMemory<byte>.Empty,
            traceId, ct);

        if (getResp.Status != (byte)StatusCode.Ok)
        {
            _log.Error("Session {SessionId} not found in store", sessionId);
            throw new InvalidDataException(
                $"Session '{sessionId}' not found in store (status=0x{getResp.Status:X2})");
        }

        var totalSize = 0L;
        if (getResp.Meta is not null)
        {
            using var doc = JsonDocument.Parse(getResp.Meta);
            if (doc.RootElement.TryGetProperty("total_size", out var s))
                totalSize = s.GetInt64();
        }

        var dataStream = new MemoryStream(getResp.Payload);
        var result = await _llama.PutStateAsync(slotId, dataStream, totalSize, ct);

        if (getResp.Meta is not null)
        {
            using var doc = JsonDocument.Parse(getResp.Meta);
            if (doc.RootElement.TryGetProperty("missing_count", out var mc))
                _log.Information("Restored chunked session {SessionId} (missing={Missing})",
                    sessionId, mc.GetInt32());
        }

        var elapsed = sw.ElapsedMilliseconds;
        _log.Information("Restored session {SessionId} to slot {SlotId} in {Elapsed}ms",
            sessionId, slotId, elapsed);

        return new RestoreSessionResult(sessionId, slotId, result.Restored, result.NPast, elapsed);
    }
}

internal sealed class ChunkHashTeeStream : Stream
{
    private readonly Stream _inner;
    private readonly List<string> _hashes;
    private readonly byte[] _buffer;
    private int _bufferPos;

    public ChunkHashTeeStream(Stream inner, List<string> hashes, byte[] buffer)
    {
        _inner = inner;
        _hashes = hashes;
        _buffer = buffer;
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
            ComputeHashesForBuffer(buffer, offset, read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (read > 0)
            ComputeHashesForBuffer(buffer, offset, read);
        return read;
    }

    private void ComputeHashesForBuffer(byte[] buffer, int offset, int count)
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
