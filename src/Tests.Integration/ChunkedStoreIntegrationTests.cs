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
            Assert.Equal(expectedMissingSize, getResp.Payload.Length);
            Assert.Equal(expectedMissingSize, totalSize);
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
    public async Task SyncPlan_ReturnsOnlyMissingHashes()
    {
        var client = new RpcClient("127.0.0.1", _storeServer!.Port);
        await client.ConnectAsync(CancellationToken.None);
        try
        {
            var data = new byte[4 * 1024 * 1024];
            new Random(222).NextBytes(data);

            await client.RequestAsync(
                OpCode.PutChunked, "kv/syncplan-test", data,
                "trace-put", CancellationToken.None);

            var chunks = ChunkEngine.ChunkAndHash(data);
            var knownHashes = chunks.Take(2).Select(c => c.Hash).ToList();
            var knownJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(knownHashes);

            var resp = await client.RequestAsync(
                OpCode.SyncPlan, "kv/syncplan-test", knownJson,
                "trace-syncplan", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
            Assert.NotNull(resp.Meta);

            using var metaDoc = System.Text.Json.JsonDocument.Parse(resp.Meta);
            var missingCount = metaDoc.RootElement.GetProperty("missing_count").GetInt32();

            Assert.Equal(2, missingCount);
            Assert.NotEmpty(resp.Payload);
            var payloadStr = Encoding.UTF8.GetString(resp.Payload);
            Assert.Contains("missing_hashes", payloadStr);
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

            Assert.Equal(prefixData.Length, restoreResp.Payload.Length);
            Assert.True(restoreResp.Payload.AsSpan().SequenceEqual(prefixData),
                $"Prefix data mismatch: expected {prefixData.Length} bytes, got {restoreResp.Payload.Length}");
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
