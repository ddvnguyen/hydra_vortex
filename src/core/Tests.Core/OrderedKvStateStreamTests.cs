using System.Buffers.Binary;
using System.Text;
using Hydra.Core;
using Hydra.Core.Caching;
using Hydra.Shared;

namespace Tests.Core;

/// <summary>
/// #720 P1: OrderedKvStateStream — manifest-ordered, memory-bounded stream
/// over a chunked KV blob (local L1 files + streaming GET_CHUNKED store frames).
/// </summary>
public sealed class OrderedKvStateStreamTests : IDisposable
{
    private readonly string _cacheDir;

    public OrderedKvStateStreamTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"hydra-okvs-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    /// <summary>Per-test L1 dir + facade (L1 only — no PG dependency).</summary>
    private (string Dir, LocalChunkCache Cache) NewCache()
    {
        var dir = Path.Combine(_cacheDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (dir, new LocalChunkCache(new LocalFsChunkCache(dir, maxBytes: 16 * 1024 * 1024)));
    }

    /// <summary>Chunk i = repeated byte (0xA0 + i), length size — distinct per chunk.</summary>
    private static byte[] Body(int index, int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)(0xA0 + index);
        return data;
    }

    /// <summary>Drains the stream in small slices (odd size exercises partial reads) and returns the bytes.</summary>
    private static async Task<byte[]> DrainAsync(Stream stream, int sliceSize = 5)
    {
        var outBuf = new MemoryStream();
        var slice = new byte[sliceSize];
        int n;
        while ((n = await stream.ReadAsync(slice.AsMemory(), CancellationToken.None)) > 0)
            outBuf.Write(slice, 0, n);
        return outBuf.ToArray();
    }

    [Fact]
    public async Task AllLocal_NoStoreCall_StreamsBlobInOrder()
    {
        var (dir, cache) = NewCache();
        var sid = "ses_local";
        var bodies = new[] { Body(0, 100), Body(1, 200), Body(2, 50) };
        var chunks = new List<ChunkRef>();
        for (var i = 0; i < bodies.Length; i++)
        {
            await cache.SaveChunkDataAsync(sid, $"h{i}", bodies[i], CancellationToken.None);
            chunks.Add(new ChunkRef(i, $"h{i}", bodies[i].Length));
        }
        var expected = bodies.SelectMany(b => b).ToArray();

        var store = new FakeChunkedStoreClient();
        using var stream = OrderedKvStateStream.Create(chunks, expected.Length, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        Assert.Equal(3, stream.LocalChunks);
        Assert.Equal(expected.Length, stream.Length);
        Assert.Equal(expected, await DrainAsync(stream));
        Assert.Equal(0, store.ChunkCallCount); // store never dialed
    }

    [Fact]
    public async Task MixedLocalAndStore_StreamsCorrectBlob_KnownHashesAreVerifiedLocalOnly()
    {
        var (dir, cache) = NewCache();
        var sid = "ses_mixed";
        var bodies = new[] { Body(0, 100), Body(1, 200), Body(2, 50) };
        // chunk 1 is NOT cached → store-sourced.
        await cache.SaveChunkDataAsync(sid, "h0", bodies[0], CancellationToken.None);
        await cache.SaveChunkDataAsync(sid, "h2", bodies[2], CancellationToken.None);
        var chunks = new List<ChunkRef>
        {
            new(0, "h0", bodies[0].Length),
            new(1, "h1", bodies[1].Length),
            new(2, "h2", bodies[2].Length),
        };
        var expected = bodies.SelectMany(b => b).ToArray();

        var store = new FakeChunkedStoreClient
        {
            BytesPerOnChunk = 13, // split frames across many onChunk invocations
            FramesToServe = { (1, bodies[1]) },
        };
        using var stream = OrderedKvStateStream.Create(chunks, expected.Length, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        Assert.Equal(2, stream.LocalChunks);
        Assert.Equal(expected, await DrainAsync(stream));

        Assert.Equal(1, store.ChunkCallCount);
        var known = Encoding.UTF8.GetString(store.GetChunkedPayloads.Single());
        Assert.Equal("""["h0","h2"]""", known); // only verified-local hashes
    }

    [Fact]
    public async Task ListedButEvictedChunk_IsStoreSourced_NotZeroFilled()
    {
        // The #720 bug: L1 eviction keeps the per-session hash list but drops
        // the data file. The stream must treat such a chunk as store-sourced.
        var (dir, cache) = NewCache();
        var sid = "ses_evicted";
        var bodies = new[] { Body(0, 100), Body(1, 200) };
        await cache.SaveChunkDataAsync(sid, "h0", bodies[0], CancellationToken.None);
        await cache.SaveChunkDataAsync(sid, "h1", bodies[1], CancellationToken.None);
        await cache.SaveHashesAsync(sid, ["h0", "h1"], CancellationToken.None); // the list survives eviction
        // Evict: drop ONLY the data files; the hash list stays.
        File.Delete(Path.Combine(dir, $"{sid}.h0"));
        File.Delete(Path.Combine(dir, $"{sid}.h1"));
        Assert.True(File.Exists(Path.Combine(dir, $"{sid}.chunks.json")));
        Assert.False(cache.HasChunkData(sid, "h0"));
        Assert.False(cache.HasChunkData(sid, "h1"));

        var chunks = new List<ChunkRef>
        {
            new(0, "h0", bodies[0].Length),
            new(1, "h1", bodies[1].Length),
        };
        var expected = bodies.SelectMany(b => b).ToArray();

        var store = new FakeChunkedStoreClient
        {
            BytesPerOnChunk = 7,
            FramesToServe = { (0, bodies[0]), (1, bodies[1]) },
        };
        using var stream = OrderedKvStateStream.Create(chunks, expected.Length, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        Assert.Equal(0, stream.LocalChunks);
        Assert.Equal(expected, await DrainAsync(stream)); // real bytes, not zeros
        var known = Encoding.UTF8.GetString(store.GetChunkedPayloads.Single());
        Assert.Equal("[]", known);
    }

    [Fact]
    public async Task OutOfOrderFrame_Throws()
    {
        var (dir, cache) = NewCache();
        var sid = "ses_ooo";
        var chunks = new List<ChunkRef>
        {
            new(0, "h0", 64),
            new(1, "h1", 64),
        };
        // Store delivers chunk 1 first — the manifest-order contract is broken.
        var store = new FakeChunkedStoreClient
        {
            FramesToServe = { (1, Body(1, 64)), (0, Body(0, 64)) },
        };
        using var stream = OrderedKvStateStream.Create(chunks, 128, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DrainAsync(stream));
        Assert.Contains("out of order", ex.Message);
    }

    [Fact]
    public async Task ShortBodyFrame_Throws()
    {
        var (dir, cache) = NewCache();
        var sid = "ses_short";
        var chunks = new List<ChunkRef> { new(0, "h0", 64) };
        var store = new FakeChunkedStoreClient
        {
            FramesToServe = { (0, new byte[16]) }, // wire size 16, manifest says 64
        };
        using var stream = OrderedKvStateStream.Create(chunks, 64, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DrainAsync(stream));
        Assert.Contains("size mismatch", ex.Message);
    }

    [Fact]
    public async Task StoreErrorStatus_ThrowsAtReadWithStoreReason()
    {
        var (dir, cache) = NewCache();
        var sid = "ses_err";
        var chunks = new List<ChunkRef> { new(0, "h0", 64) };
        var store = new FakeChunkedStoreClient
        {
            Status = (byte)StatusCode.Error,
            Meta = "session not found",
        };
        using var stream = OrderedKvStateStream.Create(chunks, 64, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DrainAsync(stream));
        Assert.Contains("GET_CHUNKED failed", ex.Message);
        Assert.Contains("session not found", ex.Message);
    }

    [Fact]
    public async Task CleanEarlyEof_Throws()
    {
        // Store reports OK but streams no frames at all (missing chunks).
        var (dir, cache) = NewCache();
        var sid = "ses_eof";
        var chunks = new List<ChunkRef> { new(0, "h0", 64) };
        var store = new FakeChunkedStoreClient();
        using var stream = OrderedKvStateStream.Create(chunks, 64, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DrainAsync(stream));
        Assert.Contains("ended before the chunk was delivered", ex.Message);
    }

    [Fact]
    public void NonContiguousManifest_ThrowsAtCreate()
    {
        var (dir, cache) = NewCache();
        var chunks = new List<ChunkRef>
        {
            new(0, "h0", 64),
            new(2, "h2", 64), // index 1 missing
        };
        var store = new FakeChunkedStoreClient();
        var ex = Assert.Throws<InvalidDataException>(() =>
            OrderedKvStateStream.Create(chunks, 128, cache, "s", store, "s.kv", "t", CancellationToken.None));
        Assert.Contains("order violated", ex.Message);
    }

    [Fact]
    public void SizeSumMismatch_ThrowsAtCreate()
    {
        var (dir, cache) = NewCache();
        var chunks = new List<ChunkRef> { new(0, "h0", 64) };
        var store = new FakeChunkedStoreClient();
        Assert.Throws<InvalidDataException>(() =>
            OrderedKvStateStream.Create(chunks, 999, cache, "s", store, "s.kv", "t", CancellationToken.None));
    }

    [Fact]
    public async Task EmptyChunks_ZeroLengthStream()
    {
        var (dir, cache) = NewCache();
        var store = new FakeChunkedStoreClient();
        using var stream = OrderedKvStateStream.Create(new List<ChunkRef>(), 0, cache, "s", store, "s.kv", "t", CancellationToken.None);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, await stream.ReadAsync(new byte[16], CancellationToken.None));
        Assert.Equal(0, store.ChunkCallCount);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightStoreFetch()
    {
        var (dir, cache) = NewCache();
        var sid = "ses_dispose";
        var local = Body(0, 32);
        await cache.SaveChunkDataAsync(sid, "h0", local, CancellationToken.None);
        var storeBody = Body(1, 64 * 1024); // large: many onChunk iterations
        var chunks = new List<ChunkRef>
        {
            new(0, "h0", 32),
            new(1, "h1", storeBody.Length),
        };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeChunkedStoreClient
        {
            BytesPerOnChunk = 4096,
            FramesToServe = { (1, storeBody) },
            Gate = gate.Task, // hold the fetch mid-flight so dispose has something to cancel
        };
        using var stream = OrderedKvStateStream.Create(chunks, 32 + storeBody.Length, cache, sid, store, $"{sid}.kv", "t1", CancellationToken.None);

        // Read only the local prefix, wait for the fetch to hit the gate,
        // then dispose — the in-flight store fetch must be cancelled
        // (no leaked producer, no blocked channel).
        var prefix = new byte[32];
        Assert.Equal(32, await stream.ReadAsync(prefix.AsMemory(), CancellationToken.None));
        await store.GateReached.WaitAsync(TimeSpan.FromSeconds(5));
        stream.Dispose();

        await store.FetchDone.WaitAsync(TimeSpan.FromSeconds(5)); // throws on timeout
        Assert.True(store.CancellationObserved, "dispose did not cancel the in-flight store fetch");
    }

    /// <summary>
    /// Fake store serving GET_CHUNKED as [4B idx LE][4B size LE][body] frames,
    /// split into configurable onChunk slice sizes, with payload recording.
    /// </summary>
    private sealed class FakeChunkedStoreClient : RpcClient
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(int Index, byte[] Body)> FramesToServe { get; } = [];
        public List<byte[]> GetChunkedPayloads { get; } = [];
        public int BytesPerOnChunk { get; set; } = 65536;
        public byte Status { get; set; } = (byte)StatusCode.Ok;
        public string? Meta { get; set; }
        public int ChunkCallCount { get; private set; }
        public Task FetchDone => _done.Task;
        public bool CancellationObserved { get; private set; }
        public Task? Gate { get; set; }
        public Task GateReached => _gateReached.Task;
        private readonly TaskCompletionSource _gateReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeChunkedStoreClient() : base("test", 0) { }

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
            => Task.FromResult(new RpcResponse((byte)StatusCode.Ok, null, []));

        public override Task<RpcResponse> RequestChunkedPayloadAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct,
            Action<long> onPayloadLen,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
            TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
        {
            ChunkCallCount++;
            GetChunkedPayloads.Add(payload.ToArray());

            var wire = new byte[FramesToServe.Sum(f => f.Body.Length + 8)];
            var off = 0;
            foreach (var (idx, body) in FramesToServe)
            {
                BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(off), idx);
                BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(off + 4), body.Length);
                body.CopyTo(wire, off + 8);
                off += body.Length + 8;
            }

            return Task.Run(async () =>
            {
                try
                {
                    if (Gate is not null)
                    {
                        _gateReached.TrySetResult();
                        await Gate.WaitAsync(ct);
                    }
                    onPayloadLen(wire.Length);
                    var i = 0;
                    while (i < wire.Length)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            CancellationObserved = true;
                            throw new OperationCanceledException(ct);
                        }
                        var n = Math.Min(BytesPerOnChunk, wire.Length - i);
                        await onChunk(new ReadOnlyMemory<byte>(wire, i, n), ct);
                        i += n;
                    }
                    return new RpcResponse(Status, Meta, []);
                }
                finally
                {
                    CancellationObserved |= ct.IsCancellationRequested;
                    _done.TrySetResult();
                }
            });
        }
    }
}
