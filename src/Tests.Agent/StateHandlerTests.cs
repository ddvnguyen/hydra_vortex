using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Agent;
using Hydra.Shared;

namespace Tests.Agent;

public sealed class StateHandlerTests : IAsyncLifetime
{
    private TestStoreServer? _storeServer;
    private Task? _storeServerTask;
    private static int _nextPort = 18000;
    private static readonly object _portLock = new();
    private int _port;

    public async Task InitializeAsync()
    {
        lock (_portLock)
        {
            _port = _nextPort++;
        }
        _storeServer = new TestStoreServer(_port);
        _storeServerTask = Task.Run(() => _storeServer.RunAsync(CancellationToken.None));
        while (_storeServer.Port == 0)
            await Task.Delay(10);
        await Task.Delay(100);
    }

    public async Task DisposeAsync()
    {
        if (_storeServer is not null)
            await _storeServer.DisposeAsync();
    }

    [Fact]
    public async Task SaveToStore_RoundTripsData()
    {
        Assert.NotNull(_storeServer);

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

            if (path.Contains("/state"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(stateData),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-agent");
        var handler = new StateHandler(llamaClient, storeClient, log);

        var result = await handler.SaveToStoreAsync("test-session", 0, "trace-save", CancellationToken.None);

        Assert.Equal("test-session", result.SessionId);
        Assert.Equal(0, result.SlotId);
        Assert.Equal(2968, result.NPast);
        Assert.Equal(50_000, result.Size);
        Assert.True(result.ElapsedMs >= 0); // sub-ms mock ops can legitimately round to 0

        // Verify data was stored correctly
        var getResp = await storeClient.RequestAsync(
            OpCode.Get, "kv/test-session", ReadOnlyMemory<byte>.Empty,
            "trace-verify", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, getResp.Status);
        Assert.Equal(stateData, getResp.Payload);

        await storeClient.DisposeAsync();
    }

    [Fact]
    public async Task RestoreFromStore_RoundTripsData()
    {
        Assert.NotNull(_storeServer);

        var stateData = new byte[50_000];
        new Random(99).NextBytes(stateData);

        // First, put data into store
        var preClient = new RpcClient("127.0.0.1", _port);
        await preClient.ConnectAsync(CancellationToken.None);
        await preClient.RequestAsync(
            OpCode.Put, "kv/restore-session", stateData,
            "trace-preput", CancellationToken.None);

        var restoredCalled = false;
        byte[]? receivedBody = null;

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state"))
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                restoredCalled = true;
                receivedBody = await request.Content!.ReadAsByteArrayAsync(ct);

                var resp = """{"restored":true,"n_past":2968,"bytes":50000}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resp, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-agent");
        var handler = new StateHandler(llamaClient, storeClient, log);

        var result = await handler.RestoreFromStoreAsync(
            "restore-session", 1, "trace-restore", CancellationToken.None);

        Assert.Equal("restore-session", result.SessionId);
        Assert.Equal(1, result.SlotId);
        Assert.True(result.Restored);
        Assert.Equal(2968, result.NPast);
        Assert.True(result.ElapsedMs > 0);

        Assert.True(restoredCalled);
        Assert.NotNull(receivedBody);
        Assert.Equal(stateData, receivedBody);

        await storeClient.DisposeAsync();
    }

    [Fact]
    public async Task RestoreFromStore_NotFound_Throws()
    {
        Assert.NotNull(_storeServer);

        var llamaHandler = new MockHttpHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-agent");
        var handler = new StateHandler(llamaClient, storeClient, log);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            handler.RestoreFromStoreAsync("nonexistent", 0, "trace-nf", CancellationToken.None));

        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains("not found", ex.Message);

        await storeClient.DisposeAsync();
    }

    [Fact]
    public async Task SaveToStore_LargeData_RoundTrips()
    {
        Assert.NotNull(_storeServer);

        var stateData = new byte[1_000_000];
        new Random(123).NextBytes(stateData);

        var llamaHandler = new MockHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.ToString();

            if (path.Contains("/state/meta"))
            {
                var meta = $$"""{"slot_id":0,"n_past":5000,"state_size":1000000,"is_processing":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(meta, Encoding.UTF8, "application/json"),
                };
            }

            if (path.Contains("/state"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(stateData),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var llamaClient = new LlamaClient(new HttpClient(llamaHandler), "http://localhost:8080");
        var storeClient = new RpcClient("127.0.0.1", _port);
        await storeClient.ConnectAsync(CancellationToken.None);

        var log = HydraLogging.CreateLogger("test-agent");
        var handler = new StateHandler(llamaClient, storeClient, log);

        var result = await handler.SaveToStoreAsync("large-session", 0, "trace-large", CancellationToken.None);

        Assert.Equal(1_000_000, result.Size);

        var getResp = await storeClient.RequestAsync(
            OpCode.Get, "kv/large-session", ReadOnlyMemory<byte>.Empty,
            "trace-verify", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, getResp.Status);
        Assert.Equal(stateData, getResp.Payload);

        await storeClient.DisposeAsync();
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

internal sealed class TestStoreServer : RpcServer
{
    private readonly Dictionary<string, byte[]> _store = new();

    public TestStoreServer(int port = 0)
        : base("127.0.0.1", port)
    {
    }

    protected override async Task HandleAsync(
        OpCode op, string key, string traceId, long payloadLen,
        PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
    {
        switch (op)
        {
            case OpCode.Put:
            {
                var payload = payloadLen > 0
                    ? await ReadPayloadAsync(reader, payloadLen, ct)
                    : [];
                _store[key] = payload;
                await WriteMetaOk(writer, $$"""{"stored":true}""", ct);
                break;
            }
            case OpCode.Get:
            {
                if (_store.TryGetValue(key, out var data))
                {
                    var meta = $$"""{"size":{{data.Length}}}""";
                    var metaBytes = Encoding.UTF8.GetBytes(meta);
                    await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                        (uint)metaBytes.Length, (ulong)data.Length, ct);
                    var mSpan = writer.GetSpan(metaBytes.Length);
                    metaBytes.CopyTo(mSpan);
                    writer.Advance(metaBytes.Length);
                    var pSpan = writer.GetSpan(data.Length);
                    data.AsSpan().CopyTo(pSpan);
                    writer.Advance(data.Length);
                    await writer.FlushAsync(ct);
                }
                else
                {
                    await WriteMetaError(writer, "not_found", StatusCode.NotFound, ct);
                }
                break;
            }
            case OpCode.Stat:
            {
                if (_store.TryGetValue(key, out var data))
                {
                    var meta = $$"""{"size":{{data.Length}}}""";
                    await WriteMetaOk(writer, meta, ct);
                }
                else
                {
                    await WriteMetaError(writer, "not_found", StatusCode.NotFound, ct);
                }
                break;
            }
            default:
                await WriteMetaError(writer, $"unsupported op: {op}", StatusCode.Error, ct);
                break;
        }
    }

    private static async Task WriteMetaOk(PipeWriter writer, string meta, CancellationToken ct)
    {
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
        var span = writer.GetSpan(metaBytes.Length);
        metaBytes.CopyTo(span);
        writer.Advance(metaBytes.Length);
        await writer.FlushAsync(ct);
    }

    private static async Task WriteMetaError(PipeWriter writer, string message, StatusCode status, CancellationToken ct)
    {
        var meta = $$"""{"error":"{{message}}"}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)status, (uint)metaBytes.Length, 0, ct);
        var span = writer.GetSpan(metaBytes.Length);
        metaBytes.CopyTo(span);
        writer.Advance(metaBytes.Length);
        await writer.FlushAsync(ct);
    }
}
