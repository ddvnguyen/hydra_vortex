using System.Net;
using System.Text;
using Hydra.Agent;
using Hydra.Shared;
using StoreConfig = Hydra.Store.StoreConfig;
using StoreServer = Hydra.Store.StoreServer;
using StorageEngine = Hydra.Store.StorageEngine;
using ChunkStore = Hydra.Store.ChunkStore;
using StoreMetadata = Hydra.Store.StoreMetadata;

namespace Tests.Integration;

/// <summary>
/// Tests the Agent's chunked save/restore methods against a real Store server.
/// M2.2.1: StateHandler chunked operations with LocalChunkCache integration.
/// </summary>
[Collection("SerializedPG")]
public sealed class AgentChunkedSaveRestoreTests : IAsyncLifetime
{
    private readonly DirectoryInfo _storeDir;
    private StoreServer? _storeServer;
    private StoreMetadata? _metadata;
    private Task? _storeServerTask;
    private readonly string _chunkCacheDir;

    public AgentChunkedSaveRestoreTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-agent-chunk-{Guid.NewGuid():N}"));
        _chunkCacheDir = Path.Combine(Path.GetTempPath(), $"hydra-cache-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync()
    {
        var cfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            StoreDir = _storeDir.FullName,
        };

        var connStr = Environment.GetEnvironmentVariable("HYDRA_STORE_PG_CONN")
            ?? "Host=localhost;Database=hydra_test;Username=hydra;Password=hydra";
        _metadata = new StoreMetadata(connStr);
        await _metadata.EnsureSchemaAsync(CancellationToken.None);
        await using var cleanConn = await _metadata.DataSource.OpenConnectionAsync();
        await using var cleanCmd = cleanConn.CreateCommand();
        cleanCmd.CommandText = "DELETE FROM session_chunks; DELETE FROM sessions; DELETE FROM chunks";
        await cleanCmd.ExecuteNonQueryAsync();

        var engine = new StorageEngine(_storeDir);
        var chunkStore = new ChunkStore(_storeDir);
        _storeServer = new StoreServer(cfg, engine, chunkStore, _metadata);
        _storeServerTask = Task.Run(() => _storeServer.RunAsync(CancellationToken.None));
        await Task.Delay(500);
    }

    public async Task DisposeAsync()
    {
        if (_storeServer is not null)
            await _storeServer.DisposeAsync();

        if (_metadata is not null)
        {
            await using var cleanConn = await _metadata.DataSource.OpenConnectionAsync();
            await using var cleanCmd = cleanConn.CreateCommand();
            cleanCmd.CommandText = "DELETE FROM session_chunks; DELETE FROM sessions; DELETE FROM chunks";
            await cleanCmd.ExecuteNonQueryAsync();
            await _metadata.DisposeAsync();
        }

        if (_storeDir.Exists)
            _storeDir.Delete(recursive: true);

        if (Directory.Exists(_chunkCacheDir))
            Directory.Delete(_chunkCacheDir, recursive: true);
    }

    [Fact]
    public async Task SaveToStoreChunked_RoundTripsData()
    {
        var stateData = new byte[50_000];
        new Random(42).NextBytes(stateData);

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = """{"slot_id":0,"n_past":2968,"state_size":50000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state") && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(stateData)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-agent-chunked");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
        var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);

        try
        {
            var result = await handler.SaveToStoreChunkedAsync(
                "chunked-session", 0, "trace-chunked-save", CancellationToken.None);

            Assert.Equal("chunked-session", result.SessionId);
            Assert.Equal(0, result.SlotId);
            Assert.Equal(2968, result.NPast);
            Assert.Equal(50_000, result.Size);
            Assert.True(result.ElapsedMs >= 0);

            // Verify data integrity via GetChunked (framed [index][size][body] — de-frame it).
            var verifyResp = await storeClient.RequestAsync(
                OpCode.GetChunked, "kv/chunked-session", ReadOnlyMemory<byte>.Empty,
                "trace-verify-chunked", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, verifyResp.Status);
            Assert.Equal(stateData, ChunkedTestUtil.Reassemble(verifyResp.Payload));
        }
        finally
        {
            await storeClient.DisposeAsync();
            llamaClient.Dispose();
        }
    }

    [Fact]
    public async Task SaveToStoreChunked_DedupAcrossSaves()
    {
        var baseData = new byte[3 * 1024 * 1024]; // 3 MB — 3 chunks
        new Random(77).NextBytes(baseData);

        var llamaHandler1 = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = $$"""{"slot_id":0,"n_past":5000,"state_size":{{baseData.Length}},"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state") && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(baseData)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient1 = new LlamaClient(new HttpClient(llamaHandler1), "http://localhost:8080");
        var storeClient1 = new RpcClient("127.0.0.1", _storeServer!.Port);
        await storeClient1.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-dedup");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
        var handler1 = new StateHandler(llamaClient1, storeClient1, chunkCache, log);

        try
        {
            // First save — all 3 chunks should be new
            var result1 = await handler1.SaveToStoreChunkedAsync(
                "dedup-session", 0, "trace-dedup-1", CancellationToken.None);
            Assert.Equal(baseData.Length, result1.Size);

            // Second save with a new instance (no local cache) — same data, all deduped
            var llamaHandler2 = new MockHttpHandler(async (request, ct) =>
            {
                var path = request.RequestUri!.ToString();

                if (path.Contains("/state/meta"))
                {
                    var meta = $$"""{"slot_id":1,"n_past":5000,"state_size":{{baseData.Length}},"is_processing":false}""";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                   Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                    };
                }

                if (path.Contains("/slots/1/state") && request.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new MemoryStream(baseData)),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var llamaClient2 = new LlamaClient(new HttpClient(llamaHandler2), "http://localhost:8080");
            var chunksDir2 = Path.Combine(Path.GetTempPath(), $"hydra-cache-{Guid.NewGuid():N}");
            var chunkCache2 = new LocalChunkCache(chunksDir2);
            var handler2 = new StateHandler(llamaClient2, storeClient1, chunkCache2, log);

            var result2 = await handler2.SaveToStoreChunkedAsync(
                "dedup-session", 1, "trace-dedup-2", CancellationToken.None);
            Assert.Equal(baseData.Length, result2.Size);

            // Verify data round-trips correctly via GetChunked (de-frame the response).
            var verifyResp = await storeClient1.RequestAsync(
                OpCode.GetChunked, "kv/dedup-session", ReadOnlyMemory<byte>.Empty,
                "trace-verify-dedup", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, verifyResp.Status);
            Assert.Equal(baseData, ChunkedTestUtil.Reassemble(verifyResp.Payload));

            // Clean up second cache
            if (Directory.Exists(chunksDir2))
                Directory.Delete(chunksDir2, recursive: true);

            llamaClient2.Dispose();
        }
        finally
        {
            await storeClient1.DisposeAsync();
            llamaClient1.Dispose();
        }
    }

    [Fact]
    public async Task RestoreFromStoreChunked_RestoresDataToLlama()
    {
        var stateData = new byte[50_000];
        new Random(123).NextBytes(stateData);

        // First save the data using chunked
        var llamaSaveHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = """{"slot_id":0,"n_past":1500,"state_size":50000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state") && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(stateData)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaSaveClient = new LlamaClient(new HttpClient(llamaSaveHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-restore-chunked");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
        var saveHandler = new StateHandler(llamaSaveClient, storeClient, chunkCache, log);

        await saveHandler.SaveToStoreChunkedAsync(
            "restore-chunked-session", 0, "trace-chunked-save", CancellationToken.None);

        // Now restore and verify data flows to llama
        byte[]? receivedBody = null;
        var restoreCalled = false;

        var llamaRestoreHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            // Post-restore n_past verification (GetStateMetaAsync).
            if (path.Contains("/state/meta"))
            {
                var meta = """{"slot_id":1,"n_past":1500,"state_size":50000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/state") && request.Method == HttpMethod.Put)
            {
                restoreCalled = true;
                receivedBody = await request.Content!.ReadAsByteArrayAsync(ct);
                var resp = """{"restored":true,"n_past":1500,"bytes":50000}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resp, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaRestoreClient = new LlamaClient(new HttpClient(llamaRestoreHandler), "http://localhost:8080");
        var restoreCache = new LocalChunkCache(_chunkCacheDir + "-restore");
        var restoreHandler = new StateHandler(llamaRestoreClient, storeClient, restoreCache, log);

        try
        {
            var result = await restoreHandler.RestoreFromStoreChunkedAsync(
                "restore-chunked-session", 1, "trace-chunked-restore", CancellationToken.None);

            Assert.True(result.Restored);
            Assert.Equal(1500, result.NPast);
            Assert.True(restoreCalled);
            Assert.NotNull(receivedBody);
            Assert.Equal(stateData, receivedBody);
        }
        finally
        {
            await storeClient.DisposeAsync();
            llamaSaveClient.Dispose();
            llamaRestoreClient.Dispose();

            if (Directory.Exists(_chunkCacheDir + "-restore"))
                Directory.Delete(_chunkCacheDir + "-restore", recursive: true);
        }
    }

    [Fact]
    public async Task RestoreFromStoreChunked_WithLocalCache_StillFetchesFullData()
    {
        var stateData = new byte[3 * 1024 * 1024]; // 3 MB
        new Random(200).NextBytes(stateData);

        // Save chunked first
        var llamaSaveHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = $$"""{"slot_id":0,"n_past":3000,"state_size":{{stateData.Length}},"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state") && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(stateData)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaSaveClient = new LlamaClient(new HttpClient(llamaSaveHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-cache");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
        var handler = new StateHandler(llamaSaveClient, storeClient, chunkCache, log);

        try
        {
            // Save — populates local cache with chunk hashes
            var saveResult = await handler.SaveToStoreChunkedAsync(
                "cached-session", 0, "trace-cache-save", CancellationToken.None);
            Assert.True(saveResult.Size > 0);

            // Restore — always fetches full state from store regardless of cache
            byte[]? receivedBody = null;

            var llamaRestoreHandler = new MockHttpHandler(async (request, ct) =>
            {
                var path = request.RequestUri!.ToString();
                // Post-restore n_past verification (GetStateMetaAsync).
                if (path.Contains("/state/meta"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"slot_id":1,"n_past":3000,"state_size":50000,"is_processing":false}""", Encoding.UTF8, "application/json"),
                    };
                }
                if (path.Contains("/state") && request.Method == HttpMethod.Put)
                {
                    receivedBody = await request.Content!.ReadAsByteArrayAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"restored":true,"n_past":3000,"bytes":50000}""", Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var llamaRestoreClient = new LlamaClient(new HttpClient(llamaRestoreHandler), "http://localhost:8080");
            // Cold cache (different node) → restore must fetch the full state from the
            // store and PUT it into llama. (A warm same-node cache would correctly
            // short-circuit via the full-cache-hit path — covered by the round-trip test.)
            var restoreCache = new LocalChunkCache(_chunkCacheDir + "-coldrestore");
            var restoreHandler = new StateHandler(llamaRestoreClient, storeClient, restoreCache, log);

            var restoreResult = await restoreHandler.RestoreFromStoreChunkedAsync(
                "cached-session", 1, "trace-cache-restore", CancellationToken.None);

            Assert.True(restoreResult.Restored);
            Assert.NotNull(receivedBody);
            Assert.Equal(stateData, receivedBody);   // full state reassembled byte-identical

            llamaRestoreClient.Dispose();
            if (Directory.Exists(_chunkCacheDir + "-coldrestore"))
                Directory.Delete(_chunkCacheDir + "-coldrestore", recursive: true);
        }
        finally
        {
            await storeClient.DisposeAsync();
            llamaSaveClient.Dispose();
        }
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
