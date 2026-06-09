using System.Collections.Concurrent;
using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Tests.Store;

public sealed class SlotPoolTests
{
    [Fact]
    public void New_Pool_Has_Matching_Total()
    {
        var pool = new SlotPool(new WorkerConfig { Name = "rtx", Slots = 3 });
        Assert.Equal(3, pool.Total);
        Assert.Equal(3, pool.Free);
        Assert.True(pool.HasFree);
    }

    [Fact]
    public void Rent_Returns_Distinct_Ids()
    {
        var pool = new SlotPool(new WorkerConfig { Name = "rtx", Slots = 3 });
        Assert.True(pool.TryRent(out var s0));
        Assert.True(pool.TryRent(out var s1));
        Assert.True(pool.TryRent(out var s2));
        var ids = new[] { s0, s1, s2 };
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(0, pool.Free);
        Assert.False(pool.HasFree);
    }

    [Fact]
    public void Rent_Returns_False_When_Empty()
    {
        var pool = new SlotPool(new WorkerConfig { Name = "rtx", Slots = 1 });
        Assert.True(pool.TryRent(out _));
        Assert.False(pool.TryRent(out _));
    }

    [Fact]
    public void Return_Makes_Slot_Free_Again()
    {
        var pool = new SlotPool(new WorkerConfig { Name = "rtx", Slots = 2 });
        pool.TryRent(out var s0);
        pool.TryRent(out var s1);
        pool.Return(s0);
        Assert.Equal(1, pool.Free);
        Assert.True(pool.HasFree);
        Assert.True(pool.TryRent(out var reuse));
    }

    [Fact]
    public void Zero_Slots_Pool_Works()
    {
        var pool = new SlotPool(new WorkerConfig { Name = "mini", Slots = 0 });
        Assert.Equal(0, pool.Total);
        Assert.Equal(0, pool.Free);
        Assert.False(pool.HasFree);
        Assert.False(pool.TryRent(out _));
    }

    [Fact]
    public void Concurrent_Access_Does_Not_Corrupt()
    {
        var pool = new SlotPool(new WorkerConfig { Name = "many", Slots = 10 });
        var rented = new ConcurrentBag<int>();
        Parallel.For(0, 10, _ =>
        {
            if (pool.TryRent(out var s)) rented.Add(s);
        });
        Assert.Equal(10, rented.Count);
        Assert.Equal(0, pool.Free);
    }
}

public sealed class SlotLeaseTests
{
    [Fact]
    public async Task Dispose_Releases_Slot_Back_To_Tracker()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        Assert.True(tracker.TryAcquireSlot("rtx", out var slot, "decode"));
        Assert.Equal(1, tracker.FreeSlotCount("rtx"));

        var lease = new SlotLease("rtx", slot, "sess_1", LeaseLifetime.Short, tracker);
        await lease.DisposeAsync();
        Assert.Equal(2, tracker.FreeSlotCount("rtx"));
    }

    [Fact]
    public void Short_Lease_Has_Correct_Lifetime()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 1);
        tracker.TryAcquireSlot("rtx", out var slot);
        var lease = new SlotLease("rtx", slot, "sess_1", LeaseLifetime.Short, tracker);
        Assert.Equal(LeaseLifetime.Short, lease.Lifetime);
        Assert.Equal("rtx", lease.WorkerName);
        Assert.Equal("sess_1", lease.SessionId);
    }

    [Fact]
    public void Long_Lease_Has_Correct_Lifetime()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 1);
        tracker.TryAcquireSlot("rtx", out var slot);
        var lease = new SlotLease("rtx", slot, "sess_warm", LeaseLifetime.Long, tracker);
        Assert.Equal(LeaseLifetime.Long, lease.Lifetime);
    }

    [Fact]
    public async Task Double_Dispose_Is_Safe()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 1);
        Assert.True(tracker.TryAcquireSlot("rtx", out var slot));
        var lease = new SlotLease("rtx", slot, "sess_x", LeaseLifetime.Short, tracker);
        await lease.DisposeAsync();
        await lease.DisposeAsync(); // no-op
        Assert.Equal(1, tracker.FreeSlotCount("rtx"));
    }
}

public sealed class SlotTrackerTests
{
    [Fact]
    public void InitWorker_With_Slots_Creates_Correct_Pool()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 3);
        Assert.Equal(3, tracker.TotalSlots("rtx"));
        Assert.Equal(3, tracker.FreeSlotCount("rtx"));
        Assert.True(tracker.HasFreeSlot("rtx"));
    }

    [Fact]
    public void TryAcquireSlot_Consumes_Slot()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        Assert.True(tracker.TryAcquireSlot("rtx", out var slot, "prefill"));
        Assert.Equal(1, tracker.FreeSlotCount("rtx"));
        Assert.True(tracker.HasFreeSlot("rtx"));
        Assert.True(tracker.IsFree("rtx"));
    }

    [Fact]
    public void Acquire_All_Slots_Then_No_Free()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        Assert.True(tracker.TryAcquireSlot("rtx", out _, "prefill"));
        Assert.True(tracker.TryAcquireSlot("rtx", out _, "decode"));
        Assert.Equal(0, tracker.FreeSlotCount("rtx"));
        Assert.False(tracker.HasFreeSlot("rtx"));
        Assert.False(tracker.IsFree("rtx"));
    }

    [Fact]
    public void ReleaseSlot_Restores_Free()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.TryAcquireSlot("rtx", out var s0);
        tracker.TryAcquireSlot("rtx", out var s1);
        Assert.Equal(0, tracker.FreeSlotCount("rtx"));
        tracker.ReleaseSlot("rtx", s0);
        Assert.Equal(1, tracker.FreeSlotCount("rtx"));
        Assert.True(tracker.HasFreeSlot("rtx"));
        Assert.True(tracker.IsFree("rtx"));
    }

    [Fact]
    public void GetStatus_Reports_Correct_States()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        Assert.Equal("free", tracker.GetStatus("rtx"));

        tracker.TryAcquireSlot("rtx", out _, "prefill");
        Assert.Equal("partial", tracker.GetStatus("rtx"));

        tracker.TryAcquireSlot("rtx", out _, "decode");
        Assert.Equal("decode", tracker.GetStatus("rtx"));
    }

    [Fact]
    public void BackwardCompat_Acquire_Release_Works()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx");
        Assert.True(tracker.Acquire("rtx", "prefill"));
        Assert.False(tracker.IsFree("rtx"));
        tracker.Release("rtx");
        Assert.True(tracker.IsFree("rtx"));
    }

    [Fact]
    public void FreeWorkers_Includes_Partial()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        tracker.TryAcquireSlot("rtx", out _, "prefill"); // 1/2 used → has free
        var free = tracker.FreeWorkers();
        Assert.Contains("rtx", free);
        Assert.Contains("p100", free);
    }

    [Fact]
    public void FreeWorkers_Excludes_Full()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 1);
        tracker.InitWorker("p100", 2);
        tracker.TryAcquireSlot("rtx", out _); // 1/1 → full
        var free = tracker.FreeWorkers();
        Assert.DoesNotContain("rtx", free);
        Assert.Contains("p100", free);
    }

    [Fact]
    public void BusyWorkers_Returns_All_Full()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 1);
        tracker.InitWorker("p100", 2);
        tracker.TryAcquireSlot("rtx", out _); // full
        tracker.TryAcquireSlot("p100", out _); // partial
        var busy = tracker.BusyWorkers();
        Assert.Single(busy);
        Assert.Contains("rtx", busy); // only fully occupied
    }

    [Fact]
    public void DefaultInitWorker_One_Slot()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx");
        Assert.Equal(1, tracker.TotalSlots("rtx"));
        Assert.Equal(1, tracker.FreeSlotCount("rtx"));
        Assert.True(tracker.TryAcquireSlot("rtx", out _, "decode"));
        Assert.False(tracker.TryAcquireSlot("rtx", out _, "decode"));
    }
}
