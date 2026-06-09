using System.Net;
using System.Text;
using Hydra.Core;
using Hydra.Core;
using Hydra.Shared;

namespace Tests.Agent;

public sealed class AgentHttpDebugTests : IAsyncLifetime
{
    private DirectoryInfo? _storeDir;
    private HttpClient? _httpClient;
    private int _debugPort;
    private CancellationTokenSource? _cts = new();
    private Task? _serverTask;
    private RpcClient? _storeRpcClient;
    private StoreServer? _storeServer;

    public async Task InitializeAsync()
    {
        // Start a minimal StoreServer for RPC operations
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-agent-http-test-{Guid.NewGuid():N}"));
        var engine = new StorageEngine(_storeDir);
        var chunkStore = new ChunkStore(_storeDir);
        var storeCfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            DebugHttpPort = 0,
            StoreDir = _storeDir.FullName,
        };

        _storeServer = new StoreServer(storeCfg, engine, chunkStore);
        _serverTask = Task.Run(() => _storeServer!.RunAsync(_cts.Token));
        while (_storeServer!.Port == 0)
            await Task.Delay(10);

        // Create Llama mock that returns healthy + slots
        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/health"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok"),
                };

            if (path.Contains("/slots") || path.Contains("/state/meta"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"id":0,"n_past":0,"is_processing":false}]""", Encoding.UTF8, "application/json"),
                };

            if (path.Contains("/state"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[512]),
                };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        _storeRpcClient = new RpcClient("127.0.0.1", _storeServer.Port);
        await _storeRpcClient.ConnectAsync(ct: CancellationToken.None);

        // Create debug port
        _debugPort = 29701 + (Guid.NewGuid().GetHashCode() % 50);
        
        var agentCfg = new AgentConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            DebugHttpPort = _debugPort,
            NodeName = "test-agent",
            LlamaUrl = "http://localhost:8080",
            StoreHost = "127.0.0.1",
            StorePort = _storeServer.Port,
            SlotSavePath = _storeDir.FullName + "/slots",
            ChunkCacheDir = _storeDir.FullName + "/chunks",
        };

        var agentLog = HydraLogging.CreateLogger("test-agent");
        var stateHandler = new StateHandler(llamaClient, _storeRpcClient, null!, agentLog);
        
        var agentServer = new AgentServer(agentCfg, stateHandler, llamaClient, agentLog);

        // Start debug endpoint on separate task
        var debugTask = Task.Run(async () =>
        {
            await agentServer.StartDebugEndpointAsync(_cts.Token);
        });

        // Wait for debug port to become available via polling
        var retryCount = 0;
        while (retryCount < 50)
        {
            try
            {
                using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
                await testClient.GetAsync($"http://127.0.0.1:{_debugPort}/version");
                break; // Server is ready
            }
            catch (HttpRequestException)
            {
                retryCount++;
                await Task.Delay(50);
            }
        }

        if (retryCount >= 50)
        {
            throw new TimeoutException("Agent debug HTTP endpoint did not start within timeout");
        }

        // Create HttpClient after server is confirmed running
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    [Fact]
    public async Task GET_AgentVersion_ReturnsOk()
    {
        Assert.NotNull(_httpClient);
        Assert.NotNull(_storeServer);

        var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort}/version");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("hydra-agent", body);
    }

    [Fact]
    public async Task GET_AgentDebug_ReturnsOk()
    {
        Assert.NotNull(_httpClient);
        Assert.NotNull(_storeServer);

        var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort}/debug");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("status", body);
        Assert.Contains("node_name", body);
        Assert.Contains("uptime_s", body);
    }

    [Fact]
    public async Task GET_AgentDebug_UnknownPath_ReturnsNotFound()
    {
        Assert.NotNull(_httpClient);
        Assert.NotNull(_storeServer);

        var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort}/unknown");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_AgentDebug_LlamaUnreachable_Returns500()
    {
        Assert.NotNull(_httpClient);
        Assert.NotNull(_storeServer);

        // Create a new llama client that always returns 500
        var badLlamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("llama unreachable"),
            };
        });

        var badLlamaClient = new LlamaClient(new HttpClient(badLlamaHandler), "http://localhost:8080");

        // Create a new agent server with the bad llama client on a different port
        var agentLog = HydraLogging.CreateLogger("test-agent-bad-llama");
        var stateHandler = new StateHandler(badLlamaClient, _storeRpcClient!, null!, agentLog);

        var agentCfg = new AgentConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            DebugHttpPort = _debugPort + 5,
            NodeName = "test-agent-bad-llama",
            LlamaUrl = "http://localhost:8080",
            StoreHost = "127.0.0.1",
            StorePort = _storeServer.Port,
            SlotSavePath = _storeDir!.FullName + "/slots",
            ChunkCacheDir = _storeDir.FullName + "/chunks",
        };

        var agentServer = new AgentServer(agentCfg, stateHandler, badLlamaClient, agentLog);
        var testCts = new CancellationTokenSource();

        // Start debug endpoint on different port
        var debugTask = Task.Run(async () =>
        {
            await agentServer.StartDebugEndpointAsync(testCts.Token);
        });

        // Wait for debug port to become available via polling
        var retryCount = 0;
        while (retryCount < 50)
        {
            try
            {
                using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
                await testClient.GetAsync($"http://127.0.0.1:{_debugPort + 5}/version");
                break; // Server is ready
            }
            catch (HttpRequestException)
            {
                retryCount++;
                await Task.Delay(50);
            }
        }

        if (retryCount >= 50)
        {
            testCts.Cancel();
            try { await debugTask; } catch (OperationCanceledException) { }
            Assert.Fail("Debug endpoint did not start within timeout");
        }

        try
        {
            // The /debug endpoint should return 500 when llama-server is unreachable
            var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort + 5}/debug");

            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            testCts.Cancel();
            try { await debugTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task GET_AgentDebug_LlamaHealthOk_Slots500_Returns500()
    {
        Assert.NotNull(_httpClient);
        Assert.NotNull(_storeServer);

        // Llama /health returns 200 but /slots returns 500 (partial failure)
        var partialLlamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();
            if (path.Contains("/health"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok"),
                };
            // All other llama endpoints return 500
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("llama slots error"),
            };
        });

        var partialLlamaClient = new LlamaClient(new HttpClient(partialLlamaHandler), "http://localhost:8080");

        var agentLog = HydraLogging.CreateLogger("test-agent-partial-fail");
        var stateHandler = new StateHandler(partialLlamaClient, _storeRpcClient!, null!, agentLog);

        var agentCfg = new AgentConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            DebugHttpPort = _debugPort + 6,
            NodeName = "test-agent-partial-fail",
            LlamaUrl = "http://localhost:8080",
            StoreHost = "127.0.0.1",
            StorePort = _storeServer.Port,
            SlotSavePath = _storeDir!.FullName + "/slots",
            ChunkCacheDir = _storeDir.FullName + "/chunks",
        };

        var agentServer = new AgentServer(agentCfg, stateHandler, partialLlamaClient, agentLog);
        var testCts = new CancellationTokenSource();

        var debugTask = Task.Run(async () =>
        {
            await agentServer.StartDebugEndpointAsync(testCts.Token);
        });

        // Wait for debug port to become available via polling
        var retryCount = 0;
        while (retryCount < 50)
        {
            try
            {
                using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
                await testClient.GetAsync($"http://127.0.0.1:{_debugPort + 6}/version");
                break; // Server is ready
            }
            catch (HttpRequestException)
            {
                retryCount++;
                await Task.Delay(50);
            }
        }

        if (retryCount >= 50)
        {
            testCts.Cancel();
            try { await debugTask; } catch (OperationCanceledException) { }
            Assert.Fail("Debug endpoint did not start within timeout");
        }

        try
        {
            // /health returns 200 so isHealthy=true, but GetSlotsAsync throws 500 → debug endpoint 500
            var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort + 6}/debug");

            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            testCts.Cancel();
            try { await debugTask; } catch (OperationCanceledException) { }
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            _cts?.Cancel();

            // Await background tasks
            if (_serverTask is not null && !_serverTask.IsCompleted)
            {
                await Task.WhenAny(_serverTask, Task.Delay(TimeSpan.FromSeconds(2)));
            }
        }
        finally
        {
            try
            {
                _httpClient?.Dispose();
                if (_storeRpcClient is not null)
                    await _storeRpcClient.DisposeAsync();
                if (_storeServer is not null)
                    await _storeServer.DisposeAsync();
                _cts?.Dispose();
            }
            finally
            {
                if (_storeDir is not null && _storeDir.Exists)
                    try
                    {
                        _storeDir.Delete(recursive: true);
                    }
                    catch { /* Ignore cleanup failures */ }
            }
        }
    }

    // Minimal mock HTTP handler for LlamaClient
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
