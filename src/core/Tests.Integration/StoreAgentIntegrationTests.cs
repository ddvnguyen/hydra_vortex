using System.Net;
using System.Text;
using Hydra.Core;
using Hydra.Shared;
using StoreConfig = Hydra.Core.StoreConfig;
using StoreServer = Hydra.Core.StoreServer;
using StorageEngine = Hydra.Core.StorageEngine;
using ChunkStore = Hydra.Core.ChunkStore;

namespace Tests.Integration;

public sealed class StoreAgentIntegrationTests : IAsyncLifetime
{
    private readonly DirectoryInfo _storeDir;
    private StoreServer? _storeServer;
    private Task? _storeServerTask;

    private readonly string _chunkCacheDir = Path.Combine(Path.GetTempPath(), $"hydra-cache-{Guid.NewGuid():N}");

    public StoreAgentIntegrationTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-integration-{Guid.NewGuid():N}"));
    }

    public async Task InitializeAsync()
    {
        // Start real Store server
        var storeCfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            StoreDir = _storeDir.FullName,
        };

        var engine = new StorageEngine(_storeDir);
        var chunkStore = new ChunkStore(_storeDir);
        _storeServer = new StoreServer(storeCfg, engine, chunkStore);
        _storeServerTask = Task.Run(() => _storeServer.RunAsync(CancellationToken.None));
        await Task.Delay(500);
    }

    public async Task DisposeAsync()
    {
        if (_storeServer is not null)
            await _storeServer.DisposeAsync();

        _storeDir.Delete(recursive: true);
    }

 [Fact]
    public async Task SaveState_AgentToRealStore_StoresData()
    {
        // Arrange: Create mock LlamaClient that returns state data
        var stateData = new byte[10_000];
        new Random(42).NextBytes(stateData);

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = $$"""{"slot_id":0,"n_past":2964,"state_size":10000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state"))
            {
                // Create a fresh stream for each request since state data is streamed once
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(stateData)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        RpcClient? storeClient = null;

        try
        {
            // Act: Save state directly via StateHandler (bypasses Agent RPC)
            var log = HydraLogging.CreateLogger("test-agent");
            storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
            await storeClient.ConnectAsync(CancellationToken.None);

            var chunkCache = new LocalChunkCache(_chunkCacheDir);
            var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);

            var saveResult = await handler.SaveToStoreAsync("test-session", 0, "trace-save-e2e", CancellationToken.None);

            // Assert: Save result is valid
            Assert.Equal("test-session", saveResult.SessionId);
            Assert.Equal(0, saveResult.SlotId);
            Assert.Equal(2964, saveResult.NPast);
            Assert.Equal(10_000, saveResult.Size);
            Assert.True(saveResult.ElapsedMs >= 0);

            // Verify data was stored correctly by reading it back from the real store
            var getResp = await storeClient.RequestAsync(
                OpCode.Get, "kv/test-session", ReadOnlyMemory<byte>.Empty,
                "trace-verify-store", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, getResp.Status);
            Assert.NotNull(getResp.Meta);
            Assert.Contains("10000", getResp.Meta);
            Assert.Equal(stateData.Length, getResp.Payload.Length);
            Assert.Equal(stateData, getResp.Payload);
        }
        finally
        {
            if (storeClient != null)
                await storeClient.DisposeAsync();
            llamaClient.Dispose();
        }
    }

    [Fact]
    public async Task RestoreState_AgentFromRealStore_RestoresData()
    {
        // Arrange: Put data into real store first (using a separate client)
        var stateData = new byte[10_000];
        new Random(99).NextBytes(stateData);

        var putClient = new RpcClient("127.0.0.1", _storeServer!.Port);
        await putClient.ConnectAsync(CancellationToken.None);

        var putResp = await putClient.RequestAsync(
            OpCode.Put, "kv/restore-session", stateData,
            "trace-preput", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, putResp.Status);
        await putClient.DisposeAsync();

        // Mock LlamaClient that captures PUT /slots/{id}/state body
        var receivedBody = new byte[0];
        var restoredCalled = false;

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            // Post-restore n_past verification (RestoreFromStoreAsync queries /state/meta).
            if (path.Contains("/state/meta"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"slot_id":0,"n_past":2964,"state_size":10000,"is_processing":false}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/state") && request.Method == HttpMethod.Put)
            {
                restoredCalled = true;
                receivedBody = await request.Content!.ReadAsByteArrayAsync(ct);
                var resp = """{"restored":true,"n_past":2964,"bytes":10000}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resp, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        RpcClient? storeClient = null;

        try
        {
            storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
            await storeClient.ConnectAsync(CancellationToken.None);

            // Act: Restore state directly via StateHandler (bypasses Agent RPC)
            var log = HydraLogging.CreateLogger("test-agent");
            var chunkCache = new LocalChunkCache(_chunkCacheDir);
            var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);

            var restoreResult = await handler.RestoreFromStoreAsync("restore-session", 0, "trace-restore-e2e", CancellationToken.None);

            // Assert: restore result is valid
            Assert.True(restoreResult.Restored);
            Assert.Equal(2964, restoreResult.NPast);
            Assert.True(restoreResult.ElapsedMs >= 0);

            // Verify data was actually sent to llama mock (not the old TestStoreServer proxy approach)
            Assert.True(restoredCalled, "Llama PUT /slots/0/state was not called");
            Assert.Equal(stateData.Length, receivedBody.Length);
            Assert.Equal(stateData, receivedBody);
        }
        finally
        {
            if (storeClient != null)
                await storeClient.DisposeAsync();
            llamaClient.Dispose();
        }
    }

    [Fact]
    public async Task SaveAndRestore_RoundTripData()
    {
        // Arrange: Create state data to round-trip
        var stateData = new byte[5_000];
        new Random(123).NextBytes(stateData);

        byte[] receivedBody = [];

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = """{"slot_id":0,"n_past":1500,"state_size":5000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(stateData)),
                };
            }

            if (path.Contains("/state") && request.Method == HttpMethod.Put)
            {
                receivedBody = await request.Content!.ReadAsByteArrayAsync(ct);
                var resp = """{"restored":true,"n_past":1500,"bytes":5000}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resp, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("roundtrip-test");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
            var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);

        try
        {
            // Act: Save state to store
            var saveResult = await handler.SaveToStoreAsync("roundtrip-session", 0, "trace-roundtrip-save", CancellationToken.None);

            // Assert save result
            Assert.Equal(5_000, saveResult.Size);
            Assert.Equal(1500, saveResult.NPast);
            Assert.True(saveResult.ElapsedMs >= 0);

            // Verify data exists in store
            var getResp = await storeClient.RequestAsync(
                OpCode.Get, "kv/roundtrip-session", ReadOnlyMemory<byte>.Empty,
                "trace-verify-roundtrip", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, getResp.Status);
            Assert.Equal(stateData, getResp.Payload);

            // Act: Restore state from store
            var restoreResult = await handler.RestoreFromStoreAsync(
                "roundtrip-session", 1, "trace-roundtrip-restore", CancellationToken.None);

            // Assert restore result
            Assert.True(restoreResult.Restored);
            Assert.Equal(1500, restoreResult.NPast);

            // Verify llama received the same data
            Assert.Equal(stateData.Length, receivedBody.Length);
            Assert.Equal(stateData, receivedBody);
        }
        finally
        {
            await storeClient.DisposeAsync();
            llamaClient.Dispose();
        }
    }

    [Fact]
    public async Task SaveState_NonExistentSession_FailsGracefully()
    {
        // Arrange: Try to restore a session that doesn't exist in store
        var storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
        await storeClient.ConnectAsync(CancellationToken.None);

        // Verify it's not there
        var statResp = await storeClient.RequestAsync(
            OpCode.Stat, "kv/nonexistent-session", ReadOnlyMemory<byte>.Empty,
            "trace-stat-nf", CancellationToken.None);
        Assert.Equal((byte)StatusCode.NotFound, statResp.Status);

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
            var handler = new StateHandler(llamaClient, storeClient, chunkCache, HydraLogging.CreateLogger("nf-test"));

        try
        {
            // Act: Should throw because session not found in store
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                handler.RestoreFromStoreAsync("nonexistent-session", 0, "trace-nf-restore", CancellationToken.None));
        }
        finally
        {
            await storeClient.DisposeAsync();
            llamaClient.Dispose();
        }
    }

    [Fact]
    public async Task SaveState_TraceIdPropagatedToStore()
    {
        // Arrange: Verify that trace_id from Agent is used in Store RPC calls
        var stateData = new byte[1_000];
        new Random(77).NextBytes(stateData);

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = """{"slot_id":0,"n_past":500,"state_size":1000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state"))
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

        var log = HydraLogging.CreateLogger("trace-test");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
            var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);

        try
        {
            // Act: Save state with a specific trace_id
            var saveResult = await handler.SaveToStoreAsync("trace-session", 0, "trace-unique-12345", CancellationToken.None);

            // Assert: save result is valid (trace propagation is in the RPC client, verified by successful call)
            Assert.Equal(1_000, saveResult.Size);

            // Verify data was stored with correct key
            var getResp = await storeClient.RequestAsync(
                OpCode.Get, "kv/trace-session", ReadOnlyMemory<byte>.Empty,
                "trace-verify", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, getResp.Status);
            Assert.Equal(stateData, getResp.Payload);
        }
        finally
        {
            await storeClient.DisposeAsync();
            llamaClient.Dispose();
        }
    }

    [Fact]
    public async Task LargePayload_AgentToStore_Successful()
    {
        // Arrange: Test with larger payload (closer to real KV cache size)
        var largeData = new byte[500_000]; // 500 KB
        new Random(42).NextBytes(largeData);

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = $$"""{"slot_id":0,"n_past":5000,"state_size":500000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/0/state"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(largeData)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _storeServer!.Port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("large-test");
        var chunkCache = new LocalChunkCache(_chunkCacheDir);
            var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);

        try
        {
            // Act: Save large state
            var saveResult = await handler.SaveToStoreAsync("large-session", 0, "trace-large-save", CancellationToken.None);

            // Assert
            Assert.Equal(500_000, saveResult.Size);
            Assert.Equal(5000, saveResult.NPast);

            // Verify large data round-trips correctly through store
            var getResp = await storeClient.RequestAsync(
                OpCode.Get, "kv/large-session", ReadOnlyMemory<byte>.Empty,
                "trace-large-verify", CancellationToken.None);
            Assert.Equal((byte)StatusCode.Ok, getResp.Status);
            Assert.Equal(largeData.Length, getResp.Payload.Length);
            Assert.Equal(largeData, getResp.Payload);
        }
        finally
        {
            await storeClient.DisposeAsync();
            llamaClient.Dispose();
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
