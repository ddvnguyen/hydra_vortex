using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using Hydra.Shared;

namespace Hydra.Core;

/// <summary>
/// #720 P1: ordered, memory-bounded stream over a chunked KV state blob.
///
/// Emits the blob's bytes in manifest chunk order. Each chunk is either
/// served from the local chunk cache (its data file verified present via
/// <see cref="LocalChunkCache.HasChunkData"/>) or pulled from a streaming
/// GET_CHUNKED store response. The store is told the verified local hashes,
/// so it returns exactly the non-local chunks; its frames
/// ([4B index LE][4B size LE][body]) arrive in manifest order
/// (DiffPlanWithInfo preserves manifest order).
///
/// Peak heap = one chunk body + one in-flight store frame — never the full
/// state. The old paths (AssembleFromChunksAsync / StateHandler buffer)
/// allocated the entire blob plus a per-chunk L1 probe with the wrong cache
/// key, and silently zero-filled regions whose cache files had been evicted
/// while their hashes remained listed. Here a chunk declared local that is
/// unreadable, and a store stream that skips or mis-sizes a chunk, both
/// fail loudly instead of corrupting the restore.
///
/// Single-reader contract: consumers (RpcClient's 64 KB body pump,
/// HttpContent's StreamContent) read sequentially.
/// </summary>
public sealed class OrderedKvStateStream : Stream
{
    private readonly List<ChunkRef> _chunks;
    private readonly bool[] _fromLocal;
    private readonly long _totalSize;
    private readonly LocalChunkCache? _cache;
    private readonly string? _cacheSid;
    private readonly ChannelReader<(int Index, byte[] Body)>? _storeFrames;
    private readonly CancellationTokenSource? _storeCts;
    private readonly int _localChunks;

    private int _nextChunk;
    private byte[]? _segment;
    private int _segmentPos;
    private bool _disposed;

    private OrderedKvStateStream(
        List<ChunkRef> chunks,
        long totalSize,
        bool[] fromLocal,
        int localChunks,
        LocalChunkCache? cache,
        string? cacheSid,
        ChannelReader<(int Index, byte[] Body)>? storeFrames,
        CancellationTokenSource? storeCts)
    {
        _chunks = chunks;
        _totalSize = totalSize;
        _fromLocal = fromLocal;
        _localChunks = localChunks;
        _cache = cache;
        _cacheSid = cacheSid;
        _storeFrames = storeFrames;
        _storeCts = storeCts;
    }

    /// <summary>Number of chunks served from the local cache (0..chunks.Count).</summary>
    public int LocalChunks => _localChunks;

    /// <summary>
    /// Build the stream. Starts the store fetch (if any chunk is non-local)
    /// immediately so it overlaps the caller's remaining setup; dispose
    /// cancels a fetch the caller never drains.
    /// </summary>
    public static OrderedKvStateStream Create(
        List<ChunkRef> chunks,
        long totalSize,
        LocalChunkCache? cache,
        string? cacheSid,
        RpcClient store,
        string storeKey,
        string traceId,
        CancellationToken ct)
    {
        // The store streams frames in manifest order and the blob offsets are
        // index-contiguous (chunk i occupies [i*ChunkSize, i*ChunkSize+size)),
        // so manifest order must equal index order. Fail loud on anything else
        // rather than emit a misaligned blob.
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Index != i)
                throw new InvalidDataException(
                    $"manifest chunk order violated at position {i}: index {chunks[i].Index} — refusing to stream");
            if (chunks[i].Size < 0)
                throw new InvalidDataException(
                    $"manifest chunk {i} has negative size {chunks[i].Size} — refusing to stream");
        }
        var sum = 0L;
        foreach (var c in chunks) sum += c.Size;
        if (sum != totalSize)
            throw new InvalidDataException(
                $"manifest total_size {totalSize} != sum of chunk sizes {sum} — refusing to stream");

        // Up-front local decision: a chunk is local iff its data file is
        // present right now. The hash list alone is not enough — L1 evicts
        // data files while keeping the per-session hash list, and serving a
        // listed-but-evicted chunk from "local" would zero-fill it.
        var fromLocal = new bool[chunks.Count];
        var localHashes = new List<string>();
        var seen = new HashSet<string>();
        var localCount = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (cache is not null && cacheSid is not null && cache.HasChunkData(cacheSid, chunk.Hash))
            {
                fromLocal[i] = true;
                localCount++;
                if (seen.Add(chunk.Hash))
                    localHashes.Add(chunk.Hash);
            }
        }

        ChannelReader<(int Index, byte[] Body)>? storeFrames = null;
        CancellationTokenSource? storeCts = null;
        if (localCount < chunks.Count)
        {
            var channel = Channel.CreateBounded<(int Index, byte[] Body)>(
                new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait });
            storeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var knownJson = JsonSerializer.SerializeToUtf8Bytes(localHashes);
            var maxChunk = chunks.Max(c => c.Size);
            _ = Task.Run(() => FetchStoreFramesAsync(
                store, storeKey, knownJson, traceId, storeCts.Token, channel.Writer, maxChunk));
            storeFrames = channel.Reader;
        }

        return new OrderedKvStateStream(chunks, totalSize, fromLocal, localCount, cache, cacheSid, storeFrames, storeCts);
    }

    /// <summary>
    /// Stream the store's GET_CHUNKED payload, parse its frames, and hand
    /// complete bodies to the channel. All failures land in the channel so
    /// the consumer observes them at read time (this task never faults).
    /// </summary>
    private static async Task FetchStoreFramesAsync(
        RpcClient store,
        string storeKey,
        byte[] knownHashesJson,
        string traceId,
        CancellationToken fetchCt,
        ChannelWriter<(int Index, byte[] Body)> frames,
        int maxChunkSize)
    {
        var acc = new byte[Math.Max(8 + 65536, maxChunkSize + 8)];
        var accLen = 0;
        try
        {
            var resp = await store.RequestChunkedPayloadAsync(
                OpCode.GetChunked, storeKey, knownHashesJson, traceId, fetchCt,
                onPayloadLen: _ => { },
                onChunk: async (mem, token) =>
                {
                    if (accLen + mem.Length > acc.Length)
                        Array.Resize(ref acc, Math.Max(acc.Length * 2, accLen + mem.Length));
                    mem.Span.CopyTo(acc.AsSpan(accLen, mem.Length));
                    accLen += mem.Length;

                    while (accLen >= 8)
                    {
                        var idx = BinaryPrimitives.ReadInt32LittleEndian(acc.AsSpan()[..4]);
                        var size = BinaryPrimitives.ReadInt32LittleEndian(acc.AsSpan()[4..8]);
                        if (idx < 0 || size <= 0 || accLen < 8 + size)
                            break;
                        var body = new byte[size];
                        Buffer.BlockCopy(acc, 8, body, 0, size);
                        Buffer.BlockCopy(acc, 8 + size, acc, 0, accLen - 8 - size);
                        accLen -= 8 + size;
                        await frames.WriteAsync((idx, body), token);
                    }
                },
                requestTimeoutOverride: null,
                payloadIdleBudget: null);

            if (resp.Status != (byte)StatusCode.Ok)
                throw new InvalidDataException(
                    $"GET_CHUNKED failed for {storeKey}: status=0x{resp.Status:X2} meta={resp.Meta ?? "(none)"}");
            if (accLen != 0)
                throw new InvalidDataException(
                    $"GET_CHUNKED stream for {storeKey} ended with {accLen} bytes of a partial frame");
        }
        catch (Exception ex)
        {
            frames.Complete(ex);
            return;
        }
        frames.Complete();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var written = 0;
        while (written < buffer.Length)
        {
            if (_segment is null || _segmentPos >= _segment.Length)
            {
                var ci = _nextChunk;
                if (ci >= _chunks.Count)
                    break; // blob complete
                var chunk = _chunks[ci];
                _nextChunk = ci + 1;
                _segment = _fromLocal[ci]
                    ? await ReadLocalChunkAsync(chunk, cancellationToken)
                    : await ReadStoreChunkAsync(chunk, cancellationToken);
                _segmentPos = 0;
            }
            // Re-derive the output span after the await (spans cannot cross
            // async boundaries).
            var outSpan = buffer.Span[written..];
            var n = Math.Min(outSpan.Length, _segment.Length - _segmentPos);
            _segment.AsSpan(_segmentPos, n).CopyTo(outSpan);
            written += n;
            _segmentPos += n;
        }
        return written;
    }

    private async Task<byte[]> ReadLocalChunkAsync(ChunkRef chunk, CancellationToken ct)
    {
        var data = await _cache!.GetChunkDataAsync(_cacheSid!, chunk.Hash, ct);
        if (data is null || data.Length != chunk.Size)
            throw new InvalidDataException(
                $"local chunk {chunk.Index} (hash {ShortHash(chunk.Hash)}) unreadable or wrong size " +
                $"({data?.Length ?? -1} != {chunk.Size}) — refusing to stream state with a hole");
        return data;
    }

    private async Task<byte[]> ReadStoreChunkAsync(ChunkRef chunk, CancellationToken ct)
    {
        if (_storeFrames is null)
            throw new InvalidDataException($"chunk {chunk.Index} marked store-sourced but no store fetch is running");
        (int Index, byte[] Body) frame;
        try
        {
            frame = await _storeFrames.ReadAsync(ct);
        }
        catch (ChannelClosedException ex)
        {
            // ex.InnerException is the producer's failure (store error,
            // malformed frame) or null for a clean early EOF.
            throw new InvalidDataException(
                ex.InnerException is null
                    ? $"store stream for chunk {chunk.Index} ended before the chunk was delivered"
                    : $"store stream for chunk {chunk.Index} failed: {ex.InnerException.Message}",
                ex.InnerException ?? ex);
        }
        var (idx, body) = frame;
        if (idx != chunk.Index)
            throw new InvalidDataException(
                $"store frame out of order: expected chunk {chunk.Index}, got {idx} — refusing to stream state with a hole");
        if (body.Length != chunk.Size)
            throw new InvalidDataException(
                $"store chunk {chunk.Index} size mismatch: {body.Length} != {chunk.Size}");
        return body;
    }

    private static string ShortHash(string hash) => hash.Length <= 8 ? hash : hash[..8];

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), default).AsTask().GetAwaiter().GetResult();

    public override long Length => _totalSize;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // Abort an in-flight store fetch the consumer never drained; the
            // producer reports the cancellation into the channel (no-op once
            // completed) and the task ends without an unobserved exception.
            _storeCts?.Cancel();
            _storeCts?.Dispose();
            _segment = null;
        }
        base.Dispose(disposing);
    }
}
