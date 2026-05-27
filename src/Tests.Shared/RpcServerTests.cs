using System.IO.Pipelines;
using System.Text;
using Hydra.Shared;

namespace Tests.Shared;

public class RpcServerTests : IAsyncLifetime
{
    private TestRpcServer? _server;
    private Task? _serverTask;

    public async Task InitializeAsync()
    {
        _server = new TestRpcServer(0);
        _serverTask = Task.Run(() => _server.RunAsync(CancellationToken.None));
        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task SendValidRequest_ReceivesResponse()
    {
        Assert.NotNull(_server);
        Assert.True(_server.IsRunning, "Server should be running");

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var response = await client.RequestAsync(
            OpCode.Put, "test-key", "hello"u8.ToArray(),
            "trace-123", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, response.Status);
        Assert.NotNull(response.Meta);
        Assert.Contains("Put", response.Meta);
    }

    [Fact]
    public async Task TwoSequentialRequests_SameConnection()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var r1 = await client.RequestAsync(
            OpCode.Stat, "key-1", ReadOnlyMemory<byte>.Empty,
            "trace-1", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, r1.Status);

        var r2 = await client.RequestAsync(
            OpCode.Get, "key-2", new byte[] { 1, 2, 3 },
            "trace-2", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, r2.Status);
        Assert.NotEmpty(r2.Payload);
        Assert.Equal(3, r2.Payload.Length);
    }

    [Fact]
    public async Task InvalidMagic_ClosesConnection()
    {
        Assert.NotNull(_server);

        using var tcpClient = new System.Net.Sockets.TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", _server.Port);
        var stream = tcpClient.GetStream();

        // Send header with wrong magic
        var buf = new byte[16];
        buf[0] = 0x00; buf[1] = 0x00; // magic = 0x0000 instead of 0x4859
        await stream.WriteAsync(buf);

        // Connection should be closed (read returns 0)
        var readBuf = new byte[1];
        var read = await stream.ReadAsync(readBuf);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task TenConcurrentConnections()
    {
        Assert.NotNull(_server);

        var tasks = Enumerable.Range(0, 10).Select(i =>
        {
            var client = new RpcClient("127.0.0.1", _server.Port);
            return client.RequestAsync(
                OpCode.List, $"conn-{i}", new byte[] { (byte)i },
                $"trace-{i}", CancellationToken.None);
        });

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal((byte)StatusCode.Ok, r.Status));
    }

    [Fact]
    public async Task LargePayload_RoundTrip()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = new byte[100_000];
        new Random(42).NextBytes(payload);

        var response = await client.RequestAsync(
            OpCode.Put, "large", payload,
            "trace-large", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, response.Status);
        Assert.Equal(payload.Length, response.Payload.Length);
        Assert.Equal(payload, response.Payload);
    }

    [Fact]
    public async Task HandlerCanReturnErrorStatus()
    {
        Assert.NotNull(_server);

        _server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            var meta = """{"error":"not allowed"}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Error, (uint)metaBytes.Length, 0, ct);
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var response = await client.RequestAsync(
            OpCode.Del, "secret", ReadOnlyMemory<byte>.Empty,
            "trace-error", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Error, response.Status);
        Assert.Contains("not allowed", response.Meta);
    }

    [Fact]
    public async Task EmptyPayload_And_EmptyMeta_Works()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var response = await client.RequestAsync(
            OpCode.Stat, "empty-test", ReadOnlyMemory<byte>.Empty,
            "trace-empty", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, response.Status);
    }

    [Fact]
    public async Task Server_RespectsCancellationToken()
    {
        Assert.NotNull(_server);

        _server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            await Task.Delay(5000, ct);
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RequestAsync(OpCode.Put, "timeout", ReadOnlyMemory<byte>.Empty, "trace-to", cts.Token));

        Assert.NotNull(ex);
    }
}
