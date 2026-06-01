using System.Net;
using System.Text;
using Hydra.Agent;
using Hydra.Store;
using Hydra.Shared;

namespace Tests.Agent;

public sealed class AgentHttpDebugTests : IAsyncLifetime
{
    private DirectoryInfo? _storeDir;
    private HttpClient? _httpClient;
    private int _debugPort;
    private CancellationTokenSource? _cts = new();
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
        var serverTask = Task.Run(() => _storeServer!.RunAsync(CancellationToken.None));
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

        // Create HttpClient after server setup
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Start debug endpoint on separate task
        var debugTask = Task.Run(async () =>
        {
            await agentServer.StartDebugEndpointAsync(_cts.Token);
        });

        // Give it time to start
        await Task.Delay(1000);
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

    public async Task DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            await Task.Delay(500);
        }
        finally
        {
            _httpClient?.Dispose();
            if (_storeRpcClient is not null)
                await _storeRpcClient.DisposeAsync();
            if (_storeServer is not null)
                await _storeServer.DisposeAsync();
            _cts?.Dispose();
            if (_storeDir is not null && _storeDir.Exists)
                _storeDir.Delete(recursive: true);
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
