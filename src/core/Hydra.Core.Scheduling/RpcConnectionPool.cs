using System.Collections.Concurrent;
using Hydra.Shared;

namespace Hydra.Core.Scheduling;

/// <summary>
/// A pool of <c>K</c> RPC connections with one in-flight request per connection.
/// Requests are admitted in FIFO order (a <see cref="SemaphoreSlim"/> gate), then
/// dispatched round-robin over whichever connection just became free.
///
/// <para>Each request gets a unique <c>requestId</c>; the response is correlated
/// back to the caller through a <see cref="TaskCompletionSource{RpcResponse}"/>
/// keyed by that id (a <see cref="ConcurrentDictionary{TKey,TValue}"/> per pool),
/// so responses may be completed out of order and the correct caller still
/// receives its own response.</para>
///
/// <para>Every request is bounded by <c>requestTimeout</c>: when it elapses the
/// caller receives <see cref="TimeoutException"/> (and the underlying client is
/// expected to observe the cancellation and drop its connection). Caller
/// cancellation surfaces as <see cref="TaskCanceledException"/>. Cancelling
/// completes the caller's task immediately, but the connection slot stays busy
/// until the transport notices — callers must not abandon in-flight requests.</para>
/// </summary>
public sealed class RpcConnectionPool : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RpcClient[] _clients;
    private readonly Queue<int> _free;
    private readonly SemaphoreSlim _connectionGate;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private long _nextRequestId;
    private int _inFlight;
    private int _disposed;

    /// <summary>Default per-request timeout when none is supplied.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a pool of <paramref name="size"/> connections, each produced by
    /// <paramref name="clientFactory"/> (called exactly once per slot, eagerly).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="clientFactory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The factory returned null.</exception>
    public RpcConnectionPool(int size, Func<RpcClient> clientFactory, TimeSpan? requestTimeout = null)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Pool size must be positive.");
        ArgumentNullException.ThrowIfNull(clientFactory);

        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _clients = new RpcClient[size];
        _free = new Queue<int>(size);
        for (var i = 0; i < size; i++)
        {
            _clients[i] = clientFactory()
                ?? throw new InvalidOperationException("Client factory returned null.");
            _free.Enqueue(i);
        }

        _connectionGate = new SemaphoreSlim(size, size);
    }

    /// <summary>The number of pooled connections.</summary>
    public int Size => _clients.Length;

    /// <summary>Approximate number of requests currently in flight.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Send one request over a pooled connection and await its response. If every
    /// connection is busy the call waits in FIFO order. The request is bounded by
    /// the pool's request timeout and by <paramref name="ct"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    /// <exception cref="TimeoutException">The request did not complete within the pool's timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<RpcResponse> SendAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        // Wait for a free connection (FIFO). Cancellation here is cheap: nothing
        // has been dispatched yet, so no slot bookkeeping is needed.
        await _connectionGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

        int slot;
        lock (_gate)
        {
            slot = _free.Dequeue(); // guaranteed non-empty: gate count == free slots
        }

        Interlocked.Increment(ref _inFlight);
        var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(completion);
        _pending[requestId] = pending;

        // Fail the caller's task immediately when the caller cancels — do not
        // depend on the transport noticing. The dispatch still runs and releases
        // the slot once the (cancelled) request completes.
        using var callerRegistration = ct.Register(() =>
        {
            if (_pending.TryRemove(requestId, out var p))
                p.Completion.TrySetCanceled(ct);
        });

        // Per-request timeout defense-in-depth: even a transport that ignores the
        // token cannot leave the caller waiting past the timeout.
        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(requestId, out var p))
                p.Completion.TrySetException(NewTimeout(op));
        });

        _ = DispatchAsync(slot, requestId, op, key, payload, traceId, timeoutCts.Token, ct, timeoutCts);
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Run the request on a specific connection and resolve its pending
    /// entry by requestId. Always completes the pending entry and releases the
    /// slot exactly once.</summary>
    private async Task DispatchAsync(
        int slot, long requestId, OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken token, CancellationToken callerCt, CancellationTokenSource timeoutCts)
    {
        try
        {
            var response = await _clients[slot].RequestAsync(op, key, payload, traceId, token).ConfigureAwait(false);
            CompletePending(requestId, response);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !callerCt.IsCancellationRequested)
        {
            // Pool timeout fired and the transport cancelled accordingly.
            CompletePending(requestId, NewTimeout(op));
        }
        catch (Exception ex)
        {
            CompletePending(requestId, ex);
        }
        finally
        {
            ReleaseSlot(slot);
        }
    }

    private void CompletePending(long requestId, RpcResponse response)
    {
        if (_pending.TryRemove(requestId, out var pending))
            pending.Completion.TrySetResult(response);
    }

    private void CompletePending(long requestId, Exception error)
    {
        if (_pending.TryRemove(requestId, out var pending))
            pending.Completion.TrySetException(error);
    }

    private void ReleaseSlot(int slot)
    {
        lock (_gate)
        {
            _free.Enqueue(slot);
        }

        _connectionGate.Release();
        Interlocked.Decrement(ref _inFlight);
    }

    private TimeoutException NewTimeout(OpCode op) =>
        new($"RPC {op} did not complete within the connection pool timeout of {_requestTimeout.TotalSeconds:F0}s");

    /// <summary>
    /// Dispose every pooled client. Pending requests are faulted with
    /// <see cref="ObjectDisposedException"/> so no caller is left hanging.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var disposed = new ObjectDisposedException(GetType().FullName);
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var pending))
                pending.Completion.TrySetException(disposed);
        }

        foreach (var client in _clients)
            await client.DisposeAsync().ConfigureAwait(false);

        _connectionGate.Dispose();
    }

    private sealed class PendingRequest
    {
        public PendingRequest(TaskCompletionSource<RpcResponse> completion) => Completion = completion;

        public TaskCompletionSource<RpcResponse> Completion { get; }
    }
}
