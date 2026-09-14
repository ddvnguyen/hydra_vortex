using System.Net.Sockets;

namespace Tests.Core.Scheduling;

/// <summary>
/// Pins the contract of the pooled RPC connection used by the worker scheduler
/// rewrite (epic #591): K connections with one in-flight request each, round-robin
/// dispatch, requestId-keyed response correlation, per-request timeout, caller
/// cancellation, and fault/error propagation. The transport is a fake
/// <see cref="RpcClient"/> (its <c>RequestAsync</c> is virtual) driven by
/// completion gates — fully deterministic, no sleeps.
/// </summary>
public sealed class RpcConnectionPoolTests
{
    [Fact]
    public async Task Factory_Is_Invoked_Exactly_Once_Per_Slot()
    {
        var factoryCalls = 0;
        await using var pool = new RpcConnectionPool(3, () =>
        {
            factoryCalls++;
            return new FakeRpcClient(gated: false);
        });

        Assert.Equal(3, factoryCalls);
        Assert.Equal(3, pool.Size);
    }

    [Fact]
    public void Constructor_Rejects_Invalid_Arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RpcConnectionPool(0, () => new FakeRpcClient()));
        Assert.Throws<ArgumentNullException>(() => new RpcConnectionPool(1, null!));
    }

    [Fact]
    public async Task Round_Robin_Distributes_Across_Connections()
    {
        var clients = new[] { new FakeRpcClient(gated: false), new FakeRpcClient(gated: false), new FakeRpcClient(gated: false) };
        var index = 0;
        await using var pool = new RpcConnectionPool(3, () => clients[index++]);

        await pool.SendAsync(OpCode.Get, "k1", ReadOnlyMemory<byte>.Empty, "t1");
        await pool.SendAsync(OpCode.Get, "k2", ReadOnlyMemory<byte>.Empty, "t2");
        await pool.SendAsync(OpCode.Get, "k3", ReadOnlyMemory<byte>.Empty, "t3");

        // Strict rotation: k1→client0, k2→client1, k3→client2.
        Assert.Equal(1, clients[0].CallCount);
        Assert.Equal(1, clients[1].CallCount);
        Assert.Equal(1, clients[2].CallCount);
        Assert.Equal("k1", clients[0].LastKey);
        Assert.Equal("k2", clients[1].LastKey);
        Assert.Equal("k3", clients[2].LastKey);
        Assert.Equal("t1", clients[0].LastTraceId);
    }

    [Fact]
    public async Task Freed_Connection_Is_Reused_Round_Robin()
    {
        var clients = new[] { new FakeRpcClient(gated: false), new FakeRpcClient(gated: false) };
        var index = 0;
        await using var pool = new RpcConnectionPool(2, () => clients[index++]);

        await pool.SendAsync(OpCode.Get, "k1", ReadOnlyMemory<byte>.Empty, "t1"); // slot 0
        await pool.SendAsync(OpCode.Get, "k2", ReadOnlyMemory<byte>.Empty, "t2"); // slot 1
        await pool.SendAsync(OpCode.Get, "k3", ReadOnlyMemory<byte>.Empty, "t3"); // slot 0 again (reused)
        await pool.SendAsync(OpCode.Get, "k4", ReadOnlyMemory<byte>.Empty, "t4"); // slot 1 again

        Assert.Equal(new[] { "k1", "k3" }, clients[0].Keys.ToArray());
        Assert.Equal(new[] { "k2", "k4" }, clients[1].Keys.ToArray());
    }

    [Fact]
    public async Task One_In_Flight_Per_Connection()
    {
        var client = new FakeRpcClient();
        await using var pool = new RpcConnectionPool(1, () => client);

        var first = pool.SendAsync(OpCode.Get, "k1", ReadOnlyMemory<byte>.Empty, "t1");
        Assert.Equal(1, client.CallCount);
        Assert.Equal(1, pool.InFlight);

        var second = pool.SendAsync(OpCode.Get, "k2", ReadOnlyMemory<byte>.Empty, "t2");
        Assert.Equal(1, client.CallCount);  // second request queued behind the busy connection
        Assert.Equal(1, pool.InFlight);     // only one request may be in flight
        Assert.False(second.IsCompleted);   // it cannot start until the first frees the slot

        client.Complete();
        await first;
        await second; // dispatched once the connection was freed
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Responses_Are_Correlated_When_Completed_Out_Of_Order()
    {
        var first = new FakeRpcClient();
        var second = new FakeRpcClient();
        var clients = new[] { first, second };
        var index = 0;
        await using var pool = new RpcConnectionPool(2, () => clients[index++]);

        var requestA = pool.SendAsync(OpCode.Get, "key-a", ReadOnlyMemory<byte>.Empty, "trace-a");
        var requestB = pool.SendAsync(OpCode.Get, "key-b", ReadOnlyMemory<byte>.Empty, "trace-b");
        Assert.Equal(2, pool.InFlight);

        // Complete the second connection first — its caller must still get its own response.
        second.Complete(new RpcResponse(1, "resp-b", [0x42]));
        var responseB = await requestB;
        Assert.Equal("resp-b", responseB.Meta);
        Assert.Equal([0x42], responseB.Payload);
        Assert.False(requestA.IsCompleted); // A is still in flight on its own connection
        Assert.Equal(1, pool.InFlight);

        first.Complete(new RpcResponse(1, "resp-a", [0x41]));
        var responseA = await requestA;
        Assert.Equal("resp-a", responseA.Meta);
        Assert.Equal([0x41], responseA.Payload);
    }

    [Fact]
    public async Task Per_Request_Timeout_Produces_TimeoutException()
    {
        var client = new FakeRpcClient();
        await using var pool = new RpcConnectionPool(1, () => client, requestTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => pool.SendAsync(OpCode.Get, "k", ReadOnlyMemory<byte>.Empty, "t"));
    }

    [Fact]
    public async Task Caller_Cancellation_Propagates()
    {
        var client = new FakeRpcClient();
        await using var pool = new RpcConnectionPool(1, () => client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.SendAsync(OpCode.Get, "k", ReadOnlyMemory<byte>.Empty, "t", cts.Token));
    }

    [Fact]
    public async Task Transport_Exception_Faults_The_Request_And_Slot_Is_Reusable()
    {
        var calls = 0;
        var client = new FakeRpcClient(gated: false, responseFactory: () =>
        {
            calls++;
            if (calls == 1)
                throw new IOException("connection reset");
            return new RpcResponse(0, "ok", []);
        });
        await using var pool = new RpcConnectionPool(1, () => client);

        var ex = await Assert.ThrowsAsync<IOException>(() => pool.SendAsync(OpCode.Get, "k1", ReadOnlyMemory<byte>.Empty, "t1"));
        Assert.Equal("connection reset", ex.Message);

        // The connection slot was released despite the failure: a follow-up works.
        var response = await pool.SendAsync(OpCode.Get, "k2", ReadOnlyMemory<byte>.Empty, "t2");
        Assert.Equal("ok", response.Meta);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Dispose_Faults_Pending_Requests_And_Rejects_New_Sends()
    {
        var client = new FakeRpcClient();
        var pool = new RpcConnectionPool(1, () => client);

        var pending = pool.SendAsync(OpCode.Get, "k", ReadOnlyMemory<byte>.Empty, "t");
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.SendAsync(OpCode.Get, "k", ReadOnlyMemory<byte>.Empty, "t"));
        await pool.DisposeAsync(); // idempotent
    }

    /// <summary>Deterministic stand-in for the real TCP transport. Two modes:
    /// <c>gated</c> — each request awaits a completion gate (tests drive
    /// <see cref="Complete"/>/<see cref="Fail"/>); ungated — the response factory
    /// runs immediately. Cancellation is honored unless disabled.</summary>
    private sealed class FakeRpcClient : RpcClient
    {
        private readonly Func<RpcResponse> _responseFactory;
        private readonly bool _gated;
        private readonly bool _honorCancellation;
        private readonly TaskCompletionSource<RpcResponse> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeRpcClient(bool gated = true, bool honorCancellation = true, Func<RpcResponse>? responseFactory = null)
            : base("fake-host", 0)
        {
            _gated = gated;
            _honorCancellation = honorCancellation;
            _responseFactory = responseFactory ?? (() => new RpcResponse(0, null, []));
        }

        public int CallCount { get; private set; }
        public OpCode? LastOp { get; private set; }
        public string? LastKey { get; private set; }
        public string? LastTraceId { get; private set; }
        public List<string> Keys { get; } = new();

        public override async Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct)
        {
            CallCount++;
            LastOp = op;
            LastKey = key;
            LastTraceId = traceId;
            Keys.Add(key);

            if (_gated)
            {
                // The gate's completed value IS the response (requestId correlation
                // is validated by completing requests out of order below).
                return _honorCancellation
                    ? await _gate.Task.WaitAsync(ct).ConfigureAwait(false)
                    : await _gate.Task.ConfigureAwait(false);
            }

            return _responseFactory();
        }

        public void Complete(RpcResponse response) => _gate.TrySetResult(response);

        public void Complete() => _gate.TrySetResult(_responseFactory());

        public void Fail(Exception exception) => _gate.TrySetException(exception);
    }
}
