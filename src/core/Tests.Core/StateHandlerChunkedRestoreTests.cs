using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Hydra.Core;
using Hydra.Core.Caching;
using Hydra.Shared;
using Serilog;
using Tests.Core.Integration;
using Xunit;

namespace Tests.Core;

/// <summary>
/// #720 P1: StateHandler.RestoreFromStoreChunkedAsync — manifest-first,
/// streaming, no trailing STATE_META round-trip.
/// </summary>
public sealed class StateHandlerChunkedRestoreTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();

    public StateHandlerChunkedRestoreTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"hydra-shr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    private (LocalChunkCache Cache, StateHandler Handler, TestLlamaClient Llama, ManifestStoreClient Store) NewFixture(SlotMeta? meta = null)
    {
        var cache = new LocalChunkCache(new LocalFsChunkCache(_cacheDir, maxBytes: 16 * 1024 * 1024));
        var llama = new TestLlamaClient(meta);
        var store = new ManifestStoreClient();
        return (cache, new StateHandler(llama, store, cache, _log), llama, store);
    }

    private static byte[] ManifestJson(int nPast, long totalSize, params (int Index, string Hash, int Size)[] chunks)
    {
        var chunkJson = string.Join(",", chunks.Select(c =>
            $"{{\"index\":{c.Index},\"hash\":\"{c.Hash}\",\"size\":{c.Size}}}"));
        return Encoding.UTF8.GetBytes(
            $"{{\"n_past\":{nPast},\"total_size\":{totalSize},\"chunks\":[{chunkJson}]}}");
    }

    private static byte[] Body(int index, int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)(0xA0 + index);
        return data;
    }

    [Fact]
    public async Task MixedRestore_ManifestFirst_PutStreamed_NoTrailingMeta()
    {
        var (cache, handler, llama, store) = NewFixture();
        var sid = "sess-mixed";
        var bodies = new[] { Body(0, 128), Body(1, 256) };
        await cache.SaveChunkDataAsync(sid, "h0", bodies[0], CancellationToken.None);
        store.ManifestPayload = ManifestJson(99, bodies.Sum(b => (long)b.Length), (0, "h0", 128), (1, "h1", 256));
        store.FramesToServe.Add((1, bodies[1]));
        llama.PutStateResponder = (slotId, body) => Task.FromResult(new RestoreResult
        {
            Restored = true, NPast = 42, Bytes = body.Length, // 42 ≠ manifest's 99: proves PUT response wins
        });

        var result = await handler.RestoreFromStoreChunkedAsync(sid, 3, "trace-1", CancellationToken.None);

        Assert.True(result.Restored);
        Assert.Equal(42, result.NPast);        // from the PUT response, not STATE_META
        Assert.Equal(384, result.Size);
        // call order: GET_MANIFEST before GET_CHUNKED, key = kv/{sid}
        var calls = store.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(OpCode.GetManifest, calls[0].Op);
        Assert.Equal($"kv/{sid}", calls[0].Key);
        Assert.Equal(OpCode.GetChunked, calls[1].Op);
        Assert.Equal($"kv/{sid}", calls[1].Key);
        // known hashes = verified-local only
        var known = Encoding.UTF8.GetString(calls[1].Payload);
        Assert.Equal("""["h0"]""", known);
        // the old code made a trailing STATE_META round-trip here; the mixed
        // path must make ZERO meta calls.
        Assert.Equal(0, llama.MetaCallCount);
        // PUT carried the full blob bytes (local + store) with the declared length.
        var put = llama.PutStateCalls.Single();
        Assert.Equal(3, put.SlotId);
        Assert.Equal(384, put.DeclaredLen);
        Assert.Equal(bodies[0].Concat(bodies[1]), put.Body);
    }

    [Fact]
    public async Task AllLocalWarmSlot_SkipsPut_UsesMetaNPast()
    {
        var (cache, handler, llama, store) = NewFixture(meta: new SlotMeta { SlotId = 0, NPast = 7, StateSize = 384 });
        var sid = "sess-warm";
        var bodies = new[] { Body(0, 128), Body(1, 256) };
        await cache.SaveChunkDataAsync(sid, "h0", bodies[0], CancellationToken.None);
        await cache.SaveChunkDataAsync(sid, "h1", bodies[1], CancellationToken.None);
        store.ManifestPayload = ManifestJson(7, 384, (0, "h0", 128), (1, "h1", 256));

        var result = await handler.RestoreFromStoreChunkedAsync(sid, 0, "trace-2", CancellationToken.None);

        Assert.True(result.Restored);
        Assert.Equal(7, result.NPast);
        Assert.Equal(1, llama.MetaCallCount);  // warm check
        Assert.Empty(llama.PutStateCalls);     // no PUT on warm hit
        Assert.Equal(0, store.CallCount(OpCode.GetChunked));
    }

    [Fact]
    public async Task AllLocalColdSlot_PutsStreamedLocalChunks()
    {
        var (cache, handler, llama, store) = NewFixture(meta: new SlotMeta { SlotId = 0, NPast = 0 });
        var sid = "sess-cold";
        var bodies = new[] { Body(0, 128), Body(1, 256) };
        await cache.SaveChunkDataAsync(sid, "h0", bodies[0], CancellationToken.None);
        await cache.SaveChunkDataAsync(sid, "h1", bodies[1], CancellationToken.None);
        store.ManifestPayload = ManifestJson(0, 384, (0, "h0", 128), (1, "h1", 256));

        llama.PutStateResponder = (slotId, body) => Task.FromResult(new RestoreResult
        {
            Restored = true, NPast = 55, Bytes = body.Length,
        });

        var result = await handler.RestoreFromStoreChunkedAsync(sid, 0, "trace-3", CancellationToken.None);

        Assert.True(result.Restored);
        Assert.Equal(55, result.NPast);
        Assert.Equal(1, llama.MetaCallCount);   // warm check (negative)
        var put = llama.PutStateCalls.Single();
        Assert.Equal(bodies[0].Concat(bodies[1]), put.Body);
        Assert.Equal(0, store.CallCount(OpCode.GetChunked)); // nothing fetched
    }

    [Fact]
    public async Task PutFails_ReturnsFailure_NoThrow()
    {
        var (cache, handler, llama, store) = NewFixture();
        var sid = "sess-fail";
        await cache.SaveChunkDataAsync(sid, "h0", Body(0, 64), CancellationToken.None);
        store.ManifestPayload = ManifestJson(0, 64, (0, "h0", 64));
        llama.PutStateResponder = (slotId, body) => Task.FromResult(new RestoreResult
        {
            Restored = false, NPast = 0, Bytes = 0,
        });

        var result = await handler.RestoreFromStoreChunkedAsync(sid, 1, "trace-4", CancellationToken.None);

        Assert.False(result.Restored);
        Assert.Equal(0, result.NPast);
        Assert.Equal(0, result.Size);
    }

    [Fact]
    public async Task ManifestMissing_ReturnsFailure_NoLlamaCalls()
    {
        var (cache, handler, llama, store) = NewFixture();
        store.ManifestStatus = (byte)StatusCode.Error;
        store.ManifestMeta = "no such session";

        var result = await handler.RestoreFromStoreChunkedAsync("ghost", 0, "trace-5", CancellationToken.None);

        Assert.False(result.Restored);
        Assert.Equal(0, llama.MetaCallCount);
        Assert.Empty(llama.PutStateCalls);
        Assert.Equal(1, store.CallCount(OpCode.GetManifest));
        Assert.Equal(0, store.CallCount(OpCode.GetChunked));
    }

    [Fact]
    public async Task SlotSuffixedSessionId_StripsSlotForStoreKey()
    {
        var (cache, handler, llama, store) = NewFixture(meta: new SlotMeta { NPast = 0 });
        var sid = "sess-key";
        var body = Body(0, 64);
        await cache.SaveChunkDataAsync(sid, "h0", body, CancellationToken.None);
        store.ManifestPayload = ManifestJson(0, 64, (0, "h0", 64));
        llama.PutStateResponder = (s, b) => Task.FromResult(new RestoreResult { Restored = true, NPast = 9, Bytes = b.Length });

        var result = await handler.RestoreFromStoreChunkedAsync($"{sid}:2", 2, "trace-6", CancellationToken.None);

        Assert.True(result.Restored);
        Assert.Equal(OpCode.GetManifest, store.Calls[0].Op);
        Assert.Equal($"kv/{sid}", store.Calls[0].Key); // slot stripped
    }

    /// <summary>RpcClient fake serving a configured manifest payload and
    /// streaming configured GET_CHUNKED frames; records (op, key, payload).</summary>
    private sealed class ManifestStoreClient : RpcClient
    {
        private readonly List<(OpCode Op, string Key, byte[] Payload)> _calls = []; // ordered: the handler's store calls are sequential

        public byte ManifestStatus { get; set; } = (byte)StatusCode.Ok;
        public string? ManifestMeta { get; set; }
        public byte[]? ManifestPayload { get; set; }
        public List<(int Index, byte[] Body)> FramesToServe { get; } = [];

        public IReadOnlyList<(OpCode Op, string Key, byte[] Payload)> Calls => _calls.ToArray();
        public int CallCount(OpCode op) => _calls.Count(c => c.Op == op);

        public ManifestStoreClient() : base("test", 0) { }

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
        {
            _calls.Add((op, key, payload.ToArray()));
            return op switch
            {
                OpCode.GetManifest => Task.FromResult(new RpcResponse(ManifestStatus, ManifestMeta, ManifestPayload ?? [])),
                _ => Task.FromResult(new RpcResponse((byte)StatusCode.Ok, null, [])),
            };
        }

        public override Task<RpcResponse> RequestChunkedPayloadAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct,
            Action<long> onPayloadLen,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
            TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
        {
            _calls.Add((op, key, payload.ToArray()));
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
                onPayloadLen(wire.Length);
                var i = 0;
                while (i < wire.Length)
                {
                    var n = Math.Min(17, wire.Length - i); // split frames across calls
                    await onChunk(new ReadOnlyMemory<byte>(wire, i, n), ct);
                    i += n;
                }
                return new RpcResponse((byte)StatusCode.Ok, null, []);
            });
        }
    }
}
