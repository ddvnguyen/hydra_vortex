using System.IO.Pipelines;
using System.Net.Sockets;
using Hydra.Shared;

namespace Tests.Core;

/// <summary>
/// #470: RequestChunkedAsync must drop the connection on ANY mid-payload
/// IOException (e.g. the coordinator's onChunk hitting ENOSPC on the L1
/// tmpfs write). Pre-#470 the exception propagated WITHOUT dropping the
/// socket, leaving a half-consumed frame on the wire; the caller's retry
/// then misread the leftover bytes as a 12-byte response header →
/// ValidatePayloadLen → DropConnection → engine EPIPE →
/// prefill_rpc_error_exhausted (decode_node=-, tokens_out=0).
/// </summary>
public sealed class RequestChunkedDropConnectionTests : IAsyncLifetime
{
    private TestStreamingServer? _server;
    private Task? _serverTask;

    public async Task InitializeAsync()
    {
        _server = new TestStreamingServer(0);
        _serverTask = Task.Run(() => _server.RunAsync(CancellationToken.None));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_server.Port == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(_server.Port != 0, "server did not bind a port");
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task ChunkedRequest_OnChunkIoError_DropsConnectionAndRethrows()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        // The server streams a 256 KB payload; the client aborts on the very
        // first chunk with an IOException, simulating the ENOSPC L1 save
        // failing inside onChunk (the exact pre-#470 live-failure path).
        var ioError = await Assert.ThrowsAsync<IOException>(() =>
            client.RequestChunkedPayloadAsync(
                OpCode.Put, "kv.session", new byte[] { 1 }, "trace-enospc",
                CancellationToken.None,
                onPayloadLen: _ => { },
                onChunk: (_, _) => throw new IOException("ENOSPC on L1 tmpfs write")));
        Assert.Contains("ENOSPC", ioError.Message);

        // The desynced socket must be gone: the follow-up request reconnects
        // and completes cleanly against a SECOND server connection. Pre-#470
        // this request read the leftover frame bytes as a header and died.
        var resp = await client.RequestAsync(
            OpCode.Put, "after", new byte[] { 2 }, "trace-after", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Equal(2, _server.ConnectionCount);
    }

    /// <summary>Minimal streaming RPC server (mirrors Tests.Shared's
    /// TestRpcServer) that answers every request with a 256 KB payload.</summary>
    private sealed class TestStreamingServer : RpcServer
    {
        private int _connectionCount;

        public int ConnectionCount => _connectionCount;

        public TestStreamingServer(int port = 0)
            : base("127.0.0.1", port)
        {
        }

        protected override void OnConnectionAccepted()
            => Interlocked.Increment(ref _connectionCount);

        protected override async Task HandleAsync(
            OpCode op, string key, string traceId, long payloadLen,
            PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
        {
            if (payloadLen > 0)
                await ReadPayloadAsync(reader, payloadLen, ct);
            await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, 256 * 1024, ct);
            var payload = new byte[64 * 1024];
            for (int i = 0; i < 4; i++)
                await WritePayloadAsync(writer, payload, ct);
        }
    }
}
