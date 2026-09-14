namespace Tests.Core.Scheduling;

/// <summary>
/// Pins the contract of the per-worker slot pool used by the worker scheduler
/// rewrite (epic #591): immediate acquire when free, priority-ordered waiter
/// handoff on release, deterministic cancellation, and strict error handling for
/// double/unknown releases. All timing is driven by awaited tasks — no sleeps.
/// </summary>
public sealed class SlotPoolTests
{
    [Fact]
    public async Task Acquire_Returns_Free_Slot_Immediately()
    {
        var pool = new SlotPool(slotsPerWorker: 2);

        var slot = await pool.AcquireAsync("w");
        Assert.Equal(0, slot);
        Assert.Equal(1, pool.AvailableSlots("w"));
        Assert.Equal(0, pool.QueuedWaiters("w"));
    }

    [Fact]
    public async Task Sequential_Acquire_Returns_Increasing_Slots()
    {
        var pool = new SlotPool(slotsPerWorker: 3);

        Assert.Equal(0, await pool.AcquireAsync("w"));
        Assert.Equal(1, await pool.AcquireAsync("w"));
        Assert.Equal(2, await pool.AcquireAsync("w"));
        Assert.Equal(0, pool.AvailableSlots("w"));
    }

    [Fact]
    public async Task Acquire_When_Full_Queues_Waiter()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        var first = await pool.AcquireAsync("w");

        var second = pool.AcquireAsync("w");
        Assert.Equal(0, pool.AvailableSlots("w"));
        Assert.Equal(1, pool.QueuedWaiters("w"));
        Assert.False(second.IsCompleted); // no free slot, nothing released yet

        pool.Release("w", first);
        Assert.Equal(0, await second);
    }

    [Fact]
    public async Task Release_Hands_Slot_To_Highest_Priority_Waiter()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        var held = await pool.AcquireAsync("w");

        var lowPriority = pool.AcquireAsync("w", priority: 5); // queued first
        var highPriority = pool.AcquireAsync("w", priority: 1); // queued second, higher priority
        Assert.Equal(2, pool.QueuedWaiters("w"));

        pool.Release("w", held);

        // Higher priority (lower value) wins the slot despite being queued later.
        Assert.Equal(0, await highPriority);
        Assert.False(lowPriority.IsCompleted);
        Assert.Equal(1, pool.QueuedWaiters("w"));
        Assert.Equal(0, pool.AvailableSlots("w"));

        pool.Release("w", 0);
        Assert.Equal(0, await lowPriority);
    }

    [Fact]
    public async Task Release_With_Equal_Priority_Is_FIFO()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        var held = await pool.AcquireAsync("w");

        var first = pool.AcquireAsync("w", priority: 3);
        var second = pool.AcquireAsync("w", priority: 3);
        pool.Release("w", held);

        Assert.Equal(0, await first);   // queued first wins the tie
        Assert.False(second.IsCompleted);

        pool.Release("w", 0);
        Assert.Equal(0, await second);
    }

    [Fact]
    public async Task Acquire_Cancellation_Removes_Waiter()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        var held = await pool.AcquireAsync("w");
        using var cts = new CancellationTokenSource();

        var waiting = pool.AcquireAsync("w", ct: cts.Token).AsTask();
        Assert.Equal(1, pool.QueuedWaiters("w"));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, pool.QueuedWaiters("w")); // cancelled waiter left contention

        pool.Release("w", held);
        Assert.Equal(1, pool.AvailableSlots("w")); // slot returned to the free set
    }

    [Fact]
    public async Task Acquire_With_Already_Cancelled_Token_Fails_Immediately()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync("w", ct: cts.Token).AsTask());

        // The failed acquire must not consume anything.
        Assert.Equal(1, pool.AvailableSlots("w"));
        Assert.Equal(0, pool.QueuedWaiters("w"));
        Assert.Equal(0, await pool.AcquireAsync("w"));
    }

    [Fact]
    public async Task Acquire_After_Release_Reuses_Slot()
    {
        var pool = new SlotPool(slotsPerWorker: 2);
        var first = await pool.AcquireAsync("w");
        await pool.AcquireAsync("w");

        pool.Release("w", first);
        Assert.Equal(0, await pool.AcquireAsync("w")); // lowest free slot first
    }

    [Fact]
    public async Task Handoff_Chain_Terminates_With_Slot_Returned_To_Free_Set()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        var held = await pool.AcquireAsync("w");
        var next = pool.AcquireAsync("w");

        pool.Release("w", held); // hand to next
        Assert.Equal(0, await next);

        pool.Release("w", 0); // next releases
        Assert.Equal(1, pool.AvailableSlots("w"));
        Assert.Equal(0, pool.QueuedWaiters("w"));
    }

    [Fact]
    public async Task Double_Release_Throws()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        var slot = await pool.AcquireAsync("w");
        pool.Release("w", slot);

        var ex = Assert.Throws<InvalidOperationException>(() => pool.Release("w", slot));
        Assert.Contains("Double release", ex.Message);
    }

    [Fact]
    public void Release_Unknown_Worker_Throws()
    {
        var pool = new SlotPool(slotsPerWorker: 1);
        Assert.Throws<ArgumentException>(() => pool.Release("ghost", 0));
    }

    [Fact]
    public async Task Release_Out_Of_Range_Slot_Throws()
    {
        var pool = new SlotPool(slotsPerWorker: 2);
        await pool.AcquireAsync("w");

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Release("w", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Release("w", -1));
    }

    [Fact]
    public async Task Waiter_Queue_Overflow_Throws()
    {
        var pool = new SlotPool(slotsPerWorker: 1, maxQueuedWaitersPerWorker: 2);
        await pool.AcquireAsync("w");
        _ = pool.AcquireAsync("w");
        _ = pool.AcquireAsync("w");

        var ex = Assert.Throws<InvalidOperationException>(() => pool.AcquireAsync("w"));
        Assert.Contains("full", ex.Message);
    }

    [Fact]
    public void Constructor_Rejects_Invalid_Arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotPool(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotPool(1, 0));
        Assert.Throws<ArgumentException>(() => new SlotPool(1).AcquireAsync(""));
    }

    [Fact]
    public async Task Workers_Have_Independent_Slot_Sets()
    {
        var pool = new SlotPool(slotsPerWorker: 1);

        var slotA = await pool.AcquireAsync("a");
        var slotB = await pool.AcquireAsync("b");
        Assert.Equal(0, slotA);
        Assert.Equal(0, slotB); // each worker owns its own [0..N) slots

        pool.Release("a", slotA);
        pool.Release("b", slotB);
        Assert.Equal(1, pool.AvailableSlots("a"));
        Assert.Equal(1, pool.AvailableSlots("b"));
    }

    [Fact]
    public async Task Concurrent_Acquire_Release_Never_Exceeds_Slot_Capacity()
    {
        var pool = new SlotPool(slotsPerWorker: 2);
        var outstanding = 0;
        var maxOutstanding = 0;

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var slot = await pool.AcquireAsync("w").AsTask();
                var now = Interlocked.Increment(ref outstanding);
                TrackMaximum(ref maxOutstanding, now);
                Assert.InRange(slot, 0, 1);
                Interlocked.Decrement(ref outstanding);
                pool.Release("w", slot);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(0, outstanding);
        Assert.Equal(2, maxOutstanding); // concurrency never exceeded the pool size
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
