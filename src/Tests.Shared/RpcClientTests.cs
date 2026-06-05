using System.Net.Sockets;
using Hydra.Shared;

namespace Tests.Shared;

public class RpcClientTests : IAsyncLifetime
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
    public async Task ClientServer_RoundTrip()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = new byte[] { 0xDE, 0xAD };
        var response = await client.RequestAsync(
            OpCode.Put, "rt", payload,
            "trace-rt", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, response.Status);
        Assert.NotNull(response.Meta);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, response.Payload);
    }

    [Fact]
    public async Task Client_BinaryPayload_RoundTrip()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = new byte[256];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        var response = await client.RequestAsync(
            OpCode.Put, "binary", payload,
            "trace-binary", CancellationToken.None);

        Assert.Equal(payload, response.Payload);
    }

    [Fact]
    public async Task Reconnect_AfterServerRestart()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var r1 = await client.RequestAsync(
            OpCode.Stat, "first", ReadOnlyMemory<byte>.Empty,
            "trace-r1", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, r1.Status);

        // Restart server on same port
        var port = _server.Port;
        await _server.DisposeAsync();
        _server = null;

        var server2 = new TestRpcServer(port);
        var server2Task = Task.Run(() => server2.RunAsync(CancellationToken.None));
        await Task.Delay(200);

        try
        {
            // Client should auto-reconnect
            var r2 = await client.RequestAsync(
                OpCode.Get, "second", new byte[] { 42 },
                "trace-r2", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, r2.Status);
            Assert.Equal([42], r2.Payload);
        }
        finally
        {
            await server2.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconnect_AfterServerRestart_ThreeAttempts()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        // First request works
        var r1 = await client.RequestAsync(
            OpCode.Stat, "alive", ReadOnlyMemory<byte>.Empty,
            "trace-alive", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, r1.Status);

        // Kill server it's connected to
        var oldPort = _server.Port;
        await _server.DisposeAsync();

        // Don't start a new one — connection should fail after retries
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestAsync(OpCode.Put, "dead", ReadOnlyMemory<byte>.Empty,
                "trace-dead", cts.Token));

        Assert.True(ex is IOException || ex is SocketException || ex is InvalidOperationException,
            $"Expected IO/Socket/InvalidOp exception, got: {ex.GetType().Name}");
    }

    [Fact]
    public async Task Timeout_PropagatesViaCancellationToken()
    {
        Assert.NotNull(_server);

        _server!.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            await Task.Delay(10_000, ct);
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RequestAsync(OpCode.Put, "timeout", ReadOnlyMemory<byte>.Empty,
                "trace-timeout", cts.Token));
    }

    [Fact]
    public async Task DisposedClient_ThrowsObjectDisposed()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);
        await client.DisposeAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestAsync(OpCode.Stat, "disposed", ReadOnlyMemory<byte>.Empty,
                "trace-disc", CancellationToken.None));

        Assert.True(ex is ObjectDisposedException || ex is NullReferenceException);
    }

    [Fact]
    public async Task RequestStreamAsync_StreamsPayload()
    {
        Assert.NotNull(_server);

        var server = _server!;
        var responsePayload = new byte[50_000];
        new Random(99).NextBytes(responsePayload);

        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            var metaBytes = """{"size":50000}"""u8.ToArray();
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, (ulong)responsePayload.Length, ct);

            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);

            // Stream payload in chunks
            var offset = 0;
            while (offset < responsePayload.Length)
            {
                var chunk = Math.Min(16384, responsePayload.Length - offset);
                var pSpan = writer.GetSpan(chunk);
                responsePayload.AsSpan(offset, chunk).CopyTo(pSpan);
                writer.Advance(chunk);
                await writer.FlushAsync(ct);
                offset += chunk;
            }
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var chunks = new List<byte>();
        await foreach (var chunk in client.RequestStreamAsync(
            OpCode.Get, "stream-me", ReadOnlyMemory<byte>.Empty,
            "trace-stream", CancellationToken.None))
        {
            chunks.AddRange(chunk);
        }

        Assert.Equal(responsePayload, chunks.ToArray());
    }

    [Fact]
    public async Task RequestStreamAsync_ThrowsOnError()
    {
        Assert.NotNull(_server);

        _server!.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            var meta = """{"error":"not_found"}""";
            var metaBytes = System.Text.Encoding.UTF8.GetBytes(meta);
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.NotFound,
                (uint)metaBytes.Length, 0, ct);
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var enumerable = client.RequestStreamAsync(
            OpCode.Get, "missing", ReadOnlyMemory<byte>.Empty,
            "trace-missing", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in enumerable) { }
        });
    }
}
