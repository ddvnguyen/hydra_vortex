using System.Text;
using Hydra.Shared;
using StoreConfig = Hydra.Store.StoreConfig;
using StoreServer = Hydra.Store.StoreServer;
using ChunkStore = Hydra.Store.ChunkStore;
using StorageEngine = Hydra.Store.StorageEngine;
using ChunkEngine = Hydra.Store.ChunkEngine;

namespace Tests.Integration;

public sealed class ChunkedStoreIntegrationTests : IAsyncLifetime
{
    private readonly DirectoryInfo _storeDir;
    private StoreServer? _storeServer;
    private Task? _storeServerTask;

    public ChunkedStoreIntegrationTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-chunk-int-{Guid.NewGuid():N}"));
    }

    public async Task InitializeAsync()
    {
        var cfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            StoreDir = _storeDir.FullName,
        };

        var engine = new StorageEngine(_storeDir);
        var chunkStore = new ChunkStore(_storeDir);
        _storeServer = new StoreServer(cfg, engine, chunkStore);
        _storeServerTask = Task.Run(() => _storeServer.RunAsync(CancellationToken.None));
        await Task.Delay(500);
    }

    public async Task DisposeAsync()
    {
        if (_storeServer is not null)
            await _storeServer.DisposeAsync();

        if (_storeDir.Exists)
            _storeDir.Delete(recursive: true);
    }

    [Fact]
    public async Task PutChunked_NewSession_StoresAllChunks()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var data = new byte[10 * 1024 * 1024];
            new Random(42).NextBytes(data);

            var resp = await client.RequestAsync(
                OpCode.PutChunked, "kv/chunked-session", data,
                "trace-putchunked", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
            Assert.NotNull(resp.Meta);
            Assert.Contains("new_chunks", resp.Meta);

            using var doc = System.Text.Json.JsonDocument.Parse(resp.Meta);
            var newChunks = doc.RootElement.GetProperty("new_chunks").GetInt32();
            var totalChunks = doc.RootElement.GetProperty("total_chunks").GetInt32();
            var deduped = doc.RootElement.GetProperty("deduped_chunks").GetInt32();

            Assert.Equal(10, totalChunks);
            Assert.Equal(10, newChunks);
            Assert.Equal(0, deduped);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task PutChunked_Dedup_SecondSaveOnlyDelta()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var baseData = new byte[10 * 1024 * 1024];
            new Random(42).NextBytes(baseData);

            // First save: 10 MB, all new
            var resp1 = await client.RequestAsync(
                OpCode.PutChunked, "kv/dedup-session", baseData,
                "trace-dedup-1", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, resp1.Status);

            using var doc1 = System.Text.Json.JsonDocument.Parse(resp1.Meta!);
            var firstNew = doc1.RootElement.GetProperty("new_chunks").GetInt32();
            Assert.Equal(10, firstNew);

            // Second save: 10 MB + 1 MB new
            var secondData = new byte[11 * 1024 * 1024];
            new Random(42).NextBytes(secondData);

            var resp2 = await client.RequestAsync(
                OpCode.PutChunked, "kv/dedup-session", secondData,
                "trace-dedup-2", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, resp2.Status);

            using var doc2 = System.Text.Json.JsonDocument.Parse(resp2.Meta!);
            var secondNew = doc2.RootElement.GetProperty("new_chunks").GetInt32();
            var secondTotal = doc2.RootElement.GetProperty("total_chunks").GetInt32();
            var secondDeduped = doc2.RootElement.GetProperty("deduped_chunks").GetInt32();

            Assert.Equal(11, secondTotal);
            Assert.Equal(1, secondNew);
            Assert.Equal(10, secondDeduped);

            // Third save: identical to second — full dedup
            var resp3 = await client.RequestAsync(
                OpCode.PutChunked, "kv/dedup-session", secondData,
                "trace-dedup-3", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, resp3.Status);

            using var doc3 = System.Text.Json.JsonDocument.Parse(resp3.Meta!);
            var thirdNew = doc3.RootElement.GetProperty("new_chunks").GetInt32();
            var thirdDeduped = doc3.RootElement.GetProperty("deduped_chunks").GetInt32();

            Assert.Equal(0, thirdNew);
            Assert.Equal(11, thirdDeduped);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetChunked_WithKnownHashes_ReturnsOnlyMissing()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var data = new byte[5 * 1024 * 1024];
            new Random(99).NextBytes(data);

            await client.RequestAsync(
                OpCode.PutChunked, "kv/getchunked-test", data,
                "trace-put", CancellationToken.None);

            var chunks = ChunkEngine.ChunkAndHash(data);
            var knownHashes = chunks.Take(3).Select(c => c.Hash).ToList();
            var knownJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(knownHashes);

            var getResp = await client.RequestAsync(
                OpCode.GetChunked, "kv/getchunked-test", knownJson,
                "trace-getchunked", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, getResp.Status);
            Assert.NotNull(getResp.Meta);

            using var metaDoc = System.Text.Json.JsonDocument.Parse(getResp.Meta);
            var missingCount = metaDoc.RootElement.GetProperty("missing_count").GetInt32();
            var totalSize = metaDoc.RootElement.GetProperty("total_size").GetInt64();

            Assert.Equal(2, missingCount);
            Assert.True(totalSize > 0);

            var expectedMissingSize = chunks[3].Size + chunks[4].Size;
            // total_size (meta) is bodies-only; the wire payload also carries 8B of
            // framing ([index][size]) per chunk.
            Assert.Equal(expectedMissingSize, totalSize);
            Assert.Equal(expectedMissingSize + missingCount * 8, getResp.Payload.Length);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetChunked_AllChunksKnown_ReturnsEmptyPayload()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var data = new byte[3 * 1024 * 1024];
            new Random(111).NextBytes(data);

            await client.RequestAsync(
                OpCode.PutChunked, "kv/all-known", data,
                "trace-put", CancellationToken.None);

            var chunks = ChunkEngine.ChunkAndHash(data);
            var allHashes = chunks.Select(c => c.Hash).ToList();
            var knownJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(allHashes);

            var getResp = await client.RequestAsync(
                OpCode.GetChunked, "kv/all-known", knownJson,
                "trace-get-all", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, getResp.Status);

            using var metaDoc = System.Text.Json.JsonDocument.Parse(getResp.Meta!);
            var missingCount = metaDoc.RootElement.GetProperty("missing_count").GetInt32();
            var totalSize = metaDoc.RootElement.GetProperty("total_size").GetInt64();

            Assert.Equal(0, missingCount);
            Assert.Equal(0, totalSize);
            Assert.Empty(getResp.Payload);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetChunked_NonExistentSession_ReturnsNotFound()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var resp = await client.RequestAsync(
                OpCode.GetChunked, "kv/nonexistent", ReadOnlyMemory<byte>.Empty,
                "trace-get-nf", CancellationToken.None);

            Assert.Equal((byte)StatusCode.NotFound, resp.Status);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeltaSave_SyncPushManifest_RoundTrips()
    {
        // Exercise the exact wire sequence the Agent uses: SYNC_MISSING → PUSH_CHUNKS
        // (missing bodies, framed [4B size LE][body]) → PUT_MANIFEST (ordered list) →
        // GET_CHUNKED reassembles byte-identical state.
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var data = new byte[3 * 1024 * 1024 + 12345]; // 3 full + 1 partial chunk
            new Random(7).NextBytes(data);
            var chunks = ChunkEngine.ChunkAndHash(data); // List<ChunkRef>(Index,Hash,Size)

            // 1. SYNC_MISSING — fresh store lacks all.
            var hashesJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                chunks.Select(c => c.Hash).ToList());
            var sync = await client.RequestAsync(
                OpCode.SyncMissing, "kv/delta", hashesJson, "t-sync", CancellationToken.None);
            using (var d = System.Text.Json.JsonDocument.Parse(sync.Meta!))
                Assert.Equal(chunks.Count, d.RootElement.GetProperty("missing_count").GetInt32());

            // 2. PUSH_CHUNKS — frame every chunk body [4B size LE][body].
            using var push = new MemoryStream();
            foreach (var c in chunks)
            {
                var body = new byte[c.Size];
                Array.Copy(data, c.Index * ChunkEngine.CHUNK_SIZE, body, 0, c.Size);
                var sz = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(sz, body.Length);
                push.Write(sz); push.Write(body);
            }
            var pushResp = await client.RequestAsync(
                OpCode.PushChunks, "kv/delta", push.ToArray(), "t-push", CancellationToken.None);
            using (var d = System.Text.Json.JsonDocument.Parse(pushResp.Meta!))
                Assert.Equal(chunks.Count, d.RootElement.GetProperty("stored").GetInt32());

            // 3. PUT_MANIFEST — authoritative ordered list.
            var manifest = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                n_past = 1234,
                total_size = (long)data.Length,
                chunks = chunks.Select(c => new { index = c.Index, hash = c.Hash, size = c.Size }),
            });
            var mResp = await client.RequestAsync(
                OpCode.PutManifest, "kv/delta", manifest, "t-manifest", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, mResp.Status);

            // 4. GET_CHUNKED (empty known) → de-frame → byte-identical.
            var getResp = await client.RequestAsync(
                OpCode.GetChunked, "kv/delta", ReadOnlyMemory<byte>.Empty, "t-get", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, getResp.Status);
            Assert.Equal(data, ChunkedTestUtil.Reassemble(getResp.Payload));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task PutManifest_RejectsUnresidentChunks()
    {
        // Guard: a manifest referencing a chunk the store doesn't have must be refused,
        // not written (else restore reconstructs garbage).
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var manifest = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                n_past = 1,
                total_size = 1024L,
                chunks = new[] { new { index = 0, hash = new string('f', 64), size = 1024 } },
            });
            var resp = await client.RequestAsync(
                OpCode.PutManifest, "kv/bad", manifest, "t-bad", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Partial, resp.Status);

            // And no manifest should have been written → GET_MANIFEST not found.
            var getm = await client.RequestAsync(
                OpCode.GetManifest, "kv/bad", ReadOnlyMemory<byte>.Empty, "t-bad2", CancellationToken.None);
            Assert.NotEqual((byte)StatusCode.Ok, getm.Status);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task SyncMissing_ReturnsHashesTheStoreLacks()
    {
        // SYNC_MISSING is save-direction: given the hashes the client wants to store,
        // return the subset the global chunk index does NOT already have.
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            // Seed the store with one session's chunks so those hashes are resident.
            var data = new byte[4 * 1024 * 1024]; // 4 chunks
            new Random(222).NextBytes(data);
            await client.RequestAsync(
                OpCode.PutChunked, "kv/seed", data, "trace-seed", CancellationToken.None);

            var resident = ChunkEngine.ChunkAndHash(data).Select(c => c.Hash).ToList();
            // Mix resident hashes (store has) with two fabricated absent hashes.
            var absent = new[]
            {
                new string('a', 64),
                new string('b', 64),
            };
            var candidates = resident.Concat(absent).ToList();
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(candidates);

            var resp = await client.RequestAsync(
                OpCode.SyncMissing, "kv/seed", json, "trace-sync", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
            using var metaDoc = System.Text.Json.JsonDocument.Parse(resp.Meta!);
            Assert.Equal(2, metaDoc.RootElement.GetProperty("missing_count").GetInt32());

            using var payloadDoc = System.Text.Json.JsonDocument.Parse(resp.Payload);
            var missing = payloadDoc.RootElement.GetProperty("missing_hashes")
                .EnumerateArray().Select(e => e.GetString()).ToHashSet();
            Assert.Equal(absent.ToHashSet(), missing);   // exactly the absent ones
            foreach (var h in resident)
                Assert.DoesNotContain(h, missing);        // resident ones never reported
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task PrefixCheckpoint_SaveAndRestore_RoundTrips()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var prefixData = new byte[1 * 1024 * 1024]; // 1 MB
            new Random(333).NextBytes(prefixData);

            var saveResp = await client.RequestAsync(
                OpCode.PutChunked, "prefix/system_prompt", prefixData,
                "trace-prefix-save", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, saveResp.Status);

            using var saveDoc = System.Text.Json.JsonDocument.Parse(saveResp.Meta!);
            Assert.True(saveDoc.RootElement.GetProperty("new_chunks").GetInt32() > 0);

            var restoreResp = await client.RequestAsync(
                OpCode.GetChunked, "prefix/system_prompt", ReadOnlyMemory<byte>.Empty,
                "trace-prefix-restore", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, restoreResp.Status);

            var restored = ChunkedTestUtil.Reassemble(restoreResp.Payload);
            Assert.Equal(prefixData.Length, restored.Length);
            Assert.True(restored.AsSpan().SequenceEqual(prefixData),
                $"Prefix data mismatch: expected {prefixData.Length} bytes, got {restored.Length}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task PushChunks_StoresChunksIndividually()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var chunk1 = "chunk one data"u8.ToArray();
            var chunk2 = "chunk two payload here"u8.ToArray();

            var pushPayload = new byte[4 + chunk1.Length + 4 + chunk2.Length];
            var offset = 0;
            BitConverter.TryWriteBytes(pushPayload.AsSpan(offset, 4), chunk1.Length);
            offset += 4;
            chunk1.CopyTo(pushPayload, offset);
            offset += chunk1.Length;
            BitConverter.TryWriteBytes(pushPayload.AsSpan(offset, 4), chunk2.Length);
            offset += 4;
            chunk2.CopyTo(pushPayload, offset);

            var resp = await client.RequestAsync(
                OpCode.PushChunks, "kv/push-test", pushPayload,
                "trace-push", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);

            using var doc = System.Text.Json.JsonDocument.Parse(resp.Meta!);
            var stored = doc.RootElement.GetProperty("stored").GetInt32();
            Assert.Equal(2, stored);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
