namespace Tests.Core.Scheduling;

/// <summary>
/// Pins the contract of the bounded-parallelism offload pool used by the worker
/// scheduler rewrite (epic #591): at most <see cref="OffloadPool.MaxConcurrency"/>
/// units run at once, the rest queue, cancellation reaches both the queue wait
/// and (via the token overload) the running work, and disposal rejects new
/// submissions. Gated work items make every assertion deterministic — no sleeps.
/// </summary>
public sealed class OffloadPoolTests
{
    [Fact]
    public void Constructor_Rejects_Non_Positive_Concurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OffloadPool(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OffloadPool(-1));
    }

    [Fact]
    public async Task Submit_Returns_Result()
    {
        using var pool = new OffloadPool(2);
        Assert.Equal(42, await pool.SubmitAsync(() => Task.FromResult(42)));
    }

    [Fact]
    public async Task Submit_Propagates_Work_Exception()
    {
        using var pool = new OffloadPool(2);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.SubmitAsync<int>(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Bounded_Parallelism_Queues_Excess_Work()
    {
        using var pool = new OffloadPool(2);
        var started = 0;
        var releaseA = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gates = new[] { releaseA, releaseB };

        var submissions = Enumerable.Range(0, 4).Select(i => pool.SubmitAsync(() =>
        {
            Interlocked.Increment(ref started);
            return gates[i % 2].Task;
        })).ToArray();

        // Both worker slots filled; the excess two are queued, not started.
        Assert.Equal(2, started);
        Assert.Equal(2, pool.ActiveCount);

        releaseA.TrySetResult(0);
        releaseB.TrySetResult(0);
        await Task.WhenAll(submissions);

        Assert.Equal(4, started);           // the queued work ran once workers freed
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public async Task Queue_Wait_Cancellation_Does_Not_Start_Work()
    {
        using var pool = new OffloadPool(1);
        var started = 0;
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var first = pool.SubmitAsync(() =>
        {
            Interlocked.Increment(ref started);
            return release.Task;
        });
        var queued = pool.SubmitAsync(() => Task.FromResult(7), cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, started); // the cancelled work never started

        release.TrySetResult(0);
        Assert.Equal(0, await first);
    }

    [Fact]
    public async Task Token_Overload_Passes_Linked_Cancellation_To_Work()
    {
        var pool = new OffloadPool(1);
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var work = pool.SubmitAsync(async token =>
        {
            token.Register(() => observedCancellation.TrySetResult());
            await observedCancellation.Task;
            return true;
        });

        Assert.False(work.IsCompleted);
        pool.Dispose(); // shutdown token cancels → the work observes it
        Assert.True(await work);
    }

    [Fact]
    public async Task Dispose_Rejects_New_Submissions()
    {
        var pool = new OffloadPool(1);
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.SubmitAsync(() => Task.FromResult(1)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.SubmitAsync(ct => Task.FromResult(1)));
    }

    [Fact]
    public async Task Dispose_Cancels_Queued_Waits_But_Not_Running_Work()
    {
        var pool = new OffloadPool(1);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = pool.SubmitAsync(() => release.Task);
        var queued = pool.SubmitAsync(() => Task.FromResult(1));

        pool.Dispose();

        // The queued submission is cancelled; the running work is left alone.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(running.IsCompleted);

        release.TrySetResult(0);
        Assert.Equal(0, await running); // in-flight work still completes normally
    }

    [Fact]
    public async Task Concurrent_Submissions_Respect_Capacity()
    {
        using var pool = new OffloadPool(3);
        var active = 0;
        var maxActive = 0;
        var gates = Enumerable.Range(0, 30)
            .Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        var submissions = gates.Select(gate => pool.SubmitAsync(async () =>
        {
            var now = Interlocked.Increment(ref active);
            TrackMaximum(ref maxActive, now);
            try
            {
                return await gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        })).ToArray();

        foreach (var gate in gates)
            gate.TrySetResult(0);
        await Task.WhenAll(submissions);

        Assert.Equal(0, active);
        Assert.Equal(3, maxActive); // concurrency never exceeded the pool size
    }

    private static void TrackMaximum(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
