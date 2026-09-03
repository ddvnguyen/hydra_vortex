using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using Hydra.Shared;

namespace Tests.Core;

/// <summary>
/// #712 M2: the per-request ceiling must be REAL for callers that pass
/// CancellationToken.None (12 store/engine call sites in
/// WorkerSchedulerService, incl. the chunked-PREFILL PutChunked store push).
/// Pre-fix, SendAndReceiveAsync / SendAndReceiveChunkedAsync did ALL I/O on
/// the CALLER token, so a request parked on a non-responding peer (the
/// observed ghost: coordinator read wedged on a stale fd while the live
/// stream was on a different one) held the shared _sync semaphore FOREVER.
/// The store push then clogged the chunk pipe and the engine PREFILL stalled
/// 120s+ until the socket timeout, killing every A/B run. These tests pin
/// the contract: ct=None + wedged peer ⇒ bounded by the ceiling, and the
/// semaphore is still usable afterwards (no ghost, no count corruption).
/// </summary>
public sealed class RpcClientCeilingTests : IAsyncLifetime
{
    private WedgeServer? _server;
    private Task? _serverTask;

    public async Task InitializeAsync()
    {
        _server = new WedgeServer(0);
        _serverTask = Task.Run(() => _server.RunAsync(CancellationToken.None));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_server.Port == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(_server.Port != 0, "server did not bind a port");
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            _server.Release(); // wake any handler parked in wedge mode
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task CallerCtNone_WedgedPeer_RequestBoundedByCeiling_SemaphoreStillUsable()
    {
        Assert.NotNull(_server);
        _server!.Wedge = true;

        // 2s ceiling (the default is 180s — too slow for a unit test; the
        // RpcClient ctor takes the per-client override).
        var client = new RpcClient("127.0.0.1", _server.Port, TimeSpan.FromSeconds(2));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.RequestAsync(OpCode.EngineInfo, "wedge-key", ReadOnlyMemory<byte>.Empty,
                "trace-wedge", CancellationToken.None));
        sw.Stop();
        Assert.Contains("timed out", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"ct=None request to a wedged peer must be bounded by the 2s ceiling, took {sw.Elapsed}");

        // The wedge request's finally must have released _sync AND dropped the
        // desynced connection: a follow-up request on the SAME client completes
        // normally against a fresh connection. Pre-fix this follow-up parked on
        // the ghost-held semaphore forever (the M2 stall).
        _server.Wedge = false;
        var resp = await client.RequestAsync(OpCode.EngineInfo, "recover-key",
            ReadOnlyMemory<byte>.Empty, "trace-recover", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Equal(2, _server.ConnectionCount);
    }

    [Fact]
    public async Task CallerCtNone_WedgedPeer_SecondWaiter_Bounded_AndSemaphoreRecoverable()
    {
        // The M2 topology exactly: a ghost holder parked on an unbounded read,
        // a second request (the store push) waiting behind it. Both must be
        // bounded by the ceiling, and the semaphore must be usable after.
        Assert.NotNull(_server);
        var server = _server!;
        server.Wedge = true;

        var client = new RpcClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));

        var first = client.RequestAsync(OpCode.EngineInfo, "ghost-key",
            ReadOnlyMemory<byte>.Empty, "trace-ghost", CancellationToken.None);
        await Task.Delay(300); // let 'first' acquire _sync and wedge on the read

        var second = client.RequestAsync(OpCode.EngineInfo, "waiter-key",
            ReadOnlyMemory<byte>.Empty, "trace-waiter", CancellationToken.None);

        var sw = Stopwatch.StartNew();
        // The waiter's timeout logs the holder (op + holding-elapsed) — the
        // permanent record that names the ghost — then drops the connection.
        var waiterEx = await Assert.ThrowsAsync<TimeoutException>(() => second);
        sw.Stop();
        Assert.Contains("timed out", waiterEx.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"waiter behind a wedged holder must be bounded by the ceiling, took {sw.Elapsed}");

        // The ghost's own read was cancelled by the waiter's DropConnection
        // (ODE) or by its own ceiling (TimeoutException) — either way bounded,
        // never a hang, and _sync released in its finally.
        await Assert.ThrowsAnyAsync<Exception>(() => first);

        // Semaphore still usable after both requests are gone.
        server.Wedge = false;
        var resp = await client.RequestAsync(OpCode.EngineInfo, "after-ghost",
            ReadOnlyMemory<byte>.Empty, "trace-after-ghost", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, resp.Status);
    }

    /// <summary>Wedge peer: consumes the request frame, then either never
    /// responds (wedge mode — the M2 ghost) or answers with an OK empty frame
    /// (respond mode — proves the client is healthy again).</summary>
    private sealed class WedgeServer : RpcServer
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectionCount;

        public volatile bool Wedge = true;
        public int ConnectionCount => _connectionCount;

        public WedgeServer(int port = 0)
            : base("127.0.0.1", port)
        {
        }

        /// <summary>Wake every handler parked in wedge mode (test teardown).</summary>
        public void Release() => _done.TrySetResult();

        protected override void OnConnectionAccepted()
            => Interlocked.Increment(ref _connectionCount);

        protected override async Task HandleAsync(
            OpCode op, string key, string traceId, long payloadLen,
            PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
        {
            if (payloadLen > 0)
                await ReadPayloadAsync(reader, payloadLen, ct);
            if (Wedge)
                await _done.Task; // parked forever: request consumed, no response
            else
                await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, 0, ct);
        }
    }
}
