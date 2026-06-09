using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hydra.Core;
using Hydra.Core;
using Hydra.Shared;
using StoreConfig = Hydra.Core.StoreConfig;
using StoreServer = Hydra.Core.StoreServer;
using StorageEngine = Hydra.Core.StorageEngine;
using StoreMetadata = Hydra.Core.StoreMetadata;

namespace Tests.Agent;

/// <summary>
/// Tests for AgentServer RPC handler opcodes.
/// These tests verify the binary protocol layer — that each opcode
/// is routed to the correct handler and returns properly formatted responses.
/// They use a real Store server and mock llama-client to isolate the RPC behavior.
/// </summary>
public sealed class AgentServerRpcTests : IAsyncLifetime
{
    private CancellationTokenSource? _cts;
    private StoreServer? _store;
    private StoreMetadata? _metadata;
    private Task? _storeTask;
    private AgentServer? _server;
    private Task? _serverTask;
    private static int _nextPort = 19000;
    private static readonly object _portLock = new();

    private readonly DirectoryInfo _chunkCacheDir = new(Path.Combine(Path.GetTempPath(), $"hydra-agg-rpc-{Guid.NewGuid():N}"));
    private DirectoryInfo _storeDir;

    public async Task InitializeAsync()
    {
        lock (_portLock)
        {
            _nextPort += 10;
        }

        // Create temp directory for Store server
        _storeDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"hydra-agg-store-{Guid.NewGuid():N}"));
        _storeDir.Create();

        // Start real Store server (not mock) — so RequestStreamAsync works properly
        var storePort = _nextPort + 1;
        var storeCfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = storePort,
            StoreDir = _storeDir.FullName,
        };

        var connStr = Environment.GetEnvironmentVariable("HYDRA_STORE_PG_CONN")
            ?? "Host=localhost;Database=hydra_store;Username=hydra;Password=hydra";
        _metadata = new StoreMetadata(connStr);
        await _metadata.EnsureSchemaAsync(CancellationToken.None);
        await using var cleanConn = await _metadata.DataSource.OpenConnectionAsync();
        await using var cleanCmd = cleanConn.CreateCommand();
        cleanCmd.CommandText = "DELETE FROM session_chunks; DELETE FROM sessions; DELETE FROM chunks";
        await cleanCmd.ExecuteNonQueryAsync();

        var engine = new StorageEngine(_storeDir);
        _store = new StoreServer(storeCfg, engine, new ChunkStore(_storeDir), _metadata);
        _cts = new CancellationTokenSource();
        _storeTask = Task.Run(() => _store.RunAsync(_cts.Token));

        // Wait for StoreServer port to be ready before connecting
        while (_store.Port == 0)
            await Task.Delay(10);

        // Start AgentServer with mocked llama-client
        var agentCfg = new AgentConfig
        {
            Host = "127.0.0.1",
            Port = _nextPort,
            DebugHttpPort = _nextPort + 100,
            NodeName = "test-agent-rpc",
            LlamaUrl = "http://localhost:8080",
            StoreHost = "127.0.0.1",
            StorePort = storePort,
        };

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = """{"slot_id":0,"n_past":2964,"state_size":50000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/slots/") && path.Contains("?action=erase"))
            {
                // Consume request body to avoid hanging the connection
                if (request.Content != null) await request.Content.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.Contains("/health"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.Contains("/slots/0/state") && request.Method == HttpMethod.Get)
            {
                var data = new byte[50000];
                new Random(42).NextBytes(data);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(data)),
                };
            }

            if (request.Method == HttpMethod.Put && path.Contains("/slots/0/state"))
            {
                // Consume the request body to avoid hanging the connection
                var body = await request.Content!.ReadAsStreamAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"restored\":true,\"n_past\":2964,\"bytes\":50000}"),
                };
            }

            if (path.Contains("/slots"))
            {
                var slots = """[{"id":0,"n_past":2964,"is_processing":false},{"id":1,"n_past":0,"is_processing":true}]""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(slots, Encoding.UTF8, "application/json"),
                };
            }


            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient(agentCfg.StoreHost, agentCfg.StorePort);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("agent-rpc-test");
        var chunkCache = new LocalChunkCache(_chunkCacheDir.FullName);
        var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);

        _server = new AgentServer(agentCfg, handler, llamaClient, log);
        _serverTask = Task.Run(() => _server.RunAsync(_cts.Token));

        // Wait for AgentServer port to be ready before tests connect
        while (_server.Port == 0)
            await Task.Delay(10);
    }

    public async Task DisposeAsync()
    {
        // Cancel tasks first to stop the servers
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        // Wait for background tasks to complete
        if (_serverTask is not null && !_serverTask.IsCompleted)
        {
            await Task.WhenAny(_serverTask, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        if (_storeTask is not null && !_storeTask.IsCompleted)
        {
            await Task.WhenAny(_storeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        // Dispose servers
        if (_server is not null)
            await _server.DisposeAsync();

        if (_store is not null)
            await _store.DisposeAsync();

        if (_metadata is not null)
            await _metadata.DisposeAsync();

        // Cleanup temp dirs
        try
        {
            if (_chunkCacheDir.Exists)
                _chunkCacheDir.Delete(recursive: true);
        }
        catch { /* Ignore cleanup failures */ }

        try
        {
            if (_storeDir.Exists)
                _storeDir.Delete(recursive: true);
        }
        catch { /* Ignore cleanup failures */ }
    }

    [Fact]
    public async Task SaveState_ReturnsCorrectMetaFormat()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            var resp = await client.RequestAsync(
                OpCode.SaveState, "test-session:0", ReadOnlyMemory<byte>.Empty,
                "trace-save-rpc", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
            Assert.NotNull(resp.Meta);
            Assert.Contains("session_id", resp.Meta);
            Assert.Contains("slot_id", resp.Meta);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task SlotStatus_ReturnsSlotsInPayload()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            var resp = await client.RequestAsync(
                OpCode.SlotStatus, "test-session:0", ReadOnlyMemory<byte>.Empty,
                "trace-slot-status-rpc", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
            Assert.NotNull(resp.Payload);
            Assert.Contains("\"id\":0", Encoding.UTF8.GetString(resp.Payload));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task NodeHealth_ReturnsOk()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            var resp = await client.RequestAsync(
                OpCode.NodeHealth, "test-session:0", ReadOnlyMemory<byte>.Empty,
                "trace-health-rpc", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task SlotErase_ReturnsOk()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            var resp = await client.RequestAsync(
                OpCode.SlotErase, "0", ReadOnlyMemory<byte>.Empty,
                "trace-erase-rpc", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task SaveStateChunked_ReturnsCorrectMetaFormat()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            var resp = await client.RequestAsync(
                OpCode.SaveStateChunked, "chunked-session:0", ReadOnlyMemory<byte>.Empty,
                "trace-save-chunked-rpc", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
            Assert.NotNull(resp.Meta);
            Assert.Contains("session_id", resp.Meta);
            Assert.Contains("chunked", resp.Meta);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task RestoreStateChunked_ReturnsCorrectMetaFormat()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            // First save chunked so we have data to restore
            await client.RequestAsync(
                OpCode.SaveStateChunked, "restore-chunked:0", ReadOnlyMemory<byte>.Empty,
                "trace-restore-chunked-save", CancellationToken.None);

            var resp = await client.RequestAsync(
                OpCode.RestoreStateChunked, "restore-chunked:0", ReadOnlyMemory<byte>.Empty,
                "trace-restore-chunked-rpc", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, resp.Status);
            Assert.NotNull(resp.Meta);
            Assert.Contains("session_id", resp.Meta);
            Assert.Contains("restored", resp.Meta);
            Assert.Contains("chunked", resp.Meta);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetChunked_ReturnsStoreNotSupportedError()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            var resp = await client.RequestAsync(
                OpCode.GetChunked, "kv/test-key", ReadOnlyMemory<byte>.Empty,
                "trace-get-chunked-rpc", CancellationToken.None);

            Assert.NotEqual((byte)StatusCode.Ok, resp.Status);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task SaveState_ChunkedVsNormal_ResponseDiffer()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        try
        {
            var respNormal = await client.RequestAsync(
                OpCode.SaveState, "diff-session:0", ReadOnlyMemory<byte>.Empty,
                "trace-normal-rpc", CancellationToken.None);

            var respChunked = await client.RequestAsync(
                OpCode.SaveStateChunked, "diff-session:0", ReadOnlyMemory<byte>.Empty,
                "trace-chunked-rpc", CancellationToken.None);

            // Both should succeed but have different meta format
            Assert.Equal((byte)StatusCode.Ok, respNormal.Status);
            Assert.Equal((byte)StatusCode.Ok, respChunked.Status);
            Assert.NotNull(respNormal.Meta);
            Assert.NotNull(respChunked.Meta);
            Assert.Contains("chunked", respChunked.Meta);
        }
        finally
        {
            await client.DisposeAsync();
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
