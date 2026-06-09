using Hydra.Core;
using System.Text;

namespace Tests.Core;

public sealed class StoreHttpDebugTests : IAsyncLifetime
{
    private DirectoryInfo? _storeDir;
    private StoreServer? _server;
    private HttpClient? _httpClient;
    private int _debugPort;
    private CancellationTokenSource? _cts;

    public async Task InitializeAsync()
    {
        // Use a unique port per test to avoid conflicts
        _debugPort = 19500 + (Guid.NewGuid().GetHashCode() % 200);
        
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-store-http-test-{Guid.NewGuid():N}"));

        var engine = new StorageEngine(_storeDir);
        var chunkStore = new ChunkStore(_storeDir);
        var cfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            DebugHttpPort = _debugPort,
            StoreDir = _storeDir.FullName,
        };

        _server = new StoreServer(cfg, engine, chunkStore);
        
        _cts = new CancellationTokenSource();
        
        // Start the debug endpoint task (blocks until cancelled)
        var serverTask = Task.Run(() => _server.StartDebugEndpointAsync(_cts.Token));
        
        // Wait for HTTP server to start - poll the port
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

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            await Task.Delay(1000); // Wait for server to shut down
        }
        finally
        {
            _httpClient?.Dispose();
            if (_server is not null)
                await _server.DisposeAsync();
            _cts?.Dispose();
            if (_storeDir is not null && _storeDir.Exists)
                _storeDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GET_DevVersion_ReturnsOk()
    {
        Assert.NotNull(_httpClient);
        
        var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort}/version");
        
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("hydra-store", body);
    }

    [Fact]
    public async Task POST_DevGc_ReturnsOk()
    {
        Assert.NotNull(_httpClient);
        
        // Valid JSON with empty keep_sessions array
        var jsonBody = "{\"keep_sessions\":[]}";
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        
        var response = await _httpClient.PostAsync($"http://127.0.0.1:{_debugPort}/debug/gc", content);
        
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("chunks_removed", body);
    }

    [Fact]
    public async Task POST_DevGc_MissingKeepSessions_ReturnsOk()
    {
        Assert.NotNull(_httpClient);
        
        // JSON without keep_sessions field - body will be null, KeepSessions stays empty HashSet
        var jsonBody = "{}";
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        
        var response = await _httpClient.PostAsync($"http://127.0.0.1:{_debugPort}/debug/gc", content);
        
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("chunks_removed", body);
    }

    [Fact]
    public async Task GET_DevDebug_ReturnsOk()
    {
        Assert.NotNull(_httpClient);
        
        var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort}/debug");
        
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("raw", body);
        Assert.Contains("chunks", body);
    }

    [Fact]
    public async Task GET_DevDebug_UnknownPath_ReturnsNotFound()
    {
        Assert.NotNull(_httpClient);
        
        var response = await _httpClient.GetAsync($"http://127.0.0.1:{_debugPort}/unknown");
        
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
