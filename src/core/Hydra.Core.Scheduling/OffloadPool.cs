namespace Hydra.Core.Scheduling;

/// <summary>
/// A pool of worker slots with bounded parallelism. Callers submit async work via
/// <see cref="SubmitAsync{T}(Func{Task{T}}, CancellationToken)"/>; at most
/// <see cref="MaxConcurrency"/> units of work run at once, the rest queue in FIFO
/// order. This is the generic "offload onto a bounded set of workers" primitive
/// (e.g. decode dispatch per GPU node).
///
/// <para>Two overloads control cancellation granularity: with the
/// <see cref="Func{Task{T}}"/> overload the token only cancels the wait for a
/// free worker; with the <see cref="Func{CancellationToken,Task{T}}"/> overload
/// the work itself receives a linked token (caller's token + pool shutdown) so it
/// can observe cancellation mid-flight.</para>
///
/// <para><see cref="Dispose"/> rejects new submissions with
/// <see cref="ObjectDisposedException"/>, cancels queued waits, and lets running
/// work finish (the pool does not wait for it).</para>
/// </summary>
public sealed class OffloadPool : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    /// <summary>Creates a pool that runs at most <paramref name="maxConcurrency"/> units concurrently.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrency"/> is not positive.</exception>
    public OffloadPool(int maxConcurrency)
    {
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be positive.");
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        MaxConcurrency = maxConcurrency;
    }

    /// <summary>The maximum number of concurrently running work items.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Approximate number of work items currently running.</summary>
    public int ActiveCount => MaxConcurrency - _gate.CurrentCount;

    /// <summary>
    /// Submit <paramref name="work"/> for execution on a pooled worker. The
    /// returned task completes when <paramref name="work"/> completes (faulting
    /// with its exception). <paramref name="ct"/> cancels only the wait for a free
    /// worker; it cannot stop work already running.
    /// </summary>
    public Task<T> SubmitAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return SubmitCoreAsync(_ => work(), ct);
    }

    /// <summary>
    /// Submit <paramref name="work"/> for execution on a pooled worker; the work
    /// receives a linked cancellation token (caller's <paramref name="ct"/> plus
    /// the pool's shutdown token) so it can stop itself mid-flight.
    /// </summary>
    public Task<T> SubmitAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return SubmitCoreAsync(work, ct);
    }

    private async Task<T> SubmitCoreAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        await _gate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            return await work(linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reject new submissions and cancel queued waits. Running work is left to
    /// complete; the pool does not wait for it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        // The semaphore holds no unmanaged resources (its WaitHandle is created
        // lazily and never materialized here), so it is intentionally left for
        // finalization rather than disposed out from under in-flight releases.
    }
}
