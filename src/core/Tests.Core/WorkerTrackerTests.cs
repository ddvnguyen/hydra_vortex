using Hydra.Core.Repositories;

namespace Tests.Core;

public sealed class WorkerTrackerTests
{
    [Fact]
    public void Init_Worker_Sets_Free()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        Assert.Equal("free", t.GetStatus("rtx"));
    }

    [Fact]
    public void Init_Worker_Idempotent()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.InitWorker("rtx");
        Assert.Equal("free", t.GetStatus("rtx"));
    }

    [Fact]
    public void Acquire_Sets_Role()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        Assert.True(t.Acquire("rtx", "prefill"));
        Assert.Equal("prefill", t.GetStatus("rtx"));
    }

    [Fact]
    public void Acquire_Returns_False_When_Busy()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.Acquire("rtx", "prefill");
        Assert.False(t.Acquire("rtx", "decode"));
    }

    [Fact]
    public void Acquire_Returns_False_When_Unhealthy()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.MarkUnhealthy("rtx");
        Assert.False(t.Acquire("rtx", "prefill"));
    }

    [Fact]
    public void Acquire_Returns_False_When_Not_Init()
    {
        var t = new WorkerTracker();
        Assert.False(t.Acquire("unknown", "prefill"));
    }

    [Fact]
    public void Release_After_Acquire_Restores_Free()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.Acquire("rtx", "prefill");
        t.Release("rtx");
        Assert.Equal("free", t.GetStatus("rtx"));
        Assert.True(t.IsFree("rtx"));
    }

    [Fact]
    public void Release_Idempotent()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.Release("rtx"); // free → free
        Assert.Equal("free", t.GetStatus("rtx"));
    }

    [Fact]
    public void On_Error_Increments()
    {
        var t = new WorkerTracker(5);
        t.InitWorker("rtx");
        t.OnError("rtx");
        t.OnError("rtx");
        Assert.True(t.IsHealthy("rtx"));
    }

    [Fact]
    public void On_Error_Crosses_Threshold()
    {
        var t = new WorkerTracker(2);
        t.InitWorker("rtx");
        t.OnError("rtx");
        t.OnError("rtx");
        Assert.False(t.IsHealthy("rtx"));
    }

    [Fact]
    public void On_Success_Resets_Errors()
    {
        var t = new WorkerTracker(2);
        t.InitWorker("rtx");
        t.OnError("rtx");
        t.OnSuccess("rtx");
        t.OnError("rtx");
        Assert.True(t.IsHealthy("rtx"));
    }

    [Fact]
    public void On_Success_Does_Not_Release()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.Acquire("rtx", "prefill");
        t.OnSuccess("rtx");
        Assert.False(t.IsFree("rtx"));
    }

    [Fact]
    public void Free_Workers_Excludes_Busy()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.InitWorker("p100");
        t.Acquire("rtx", "prefill");
        var free = t.FreeWorkers();
        Assert.Single(free);
        Assert.Equal("p100", free[0]);
    }

    [Fact]
    public void Free_Workers_Excludes_Unhealthy()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.InitWorker("p100");
        t.MarkUnhealthy("rtx");
        var free = t.FreeWorkers();
        Assert.Single(free);
        Assert.Equal("p100", free[0]);
    }

    [Fact]
    public void Busy_Workers_Returns_NonFree()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.InitWorker("p100");
        t.Acquire("rtx", "decode");
        var busy = t.BusyWorkers();
        Assert.Single(busy);
        Assert.Equal("rtx", busy[0]);
    }

    [Fact]
    public void Mark_Unhealthy()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.MarkUnhealthy("rtx");
        Assert.False(t.IsHealthy("rtx"));
    }

    [Fact]
    public void Mark_Healthy_Resets_Errors()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.OnError("rtx");
        t.OnError("rtx");
        t.MarkHealthy("rtx");
        Assert.True(t.IsHealthy("rtx"));
    }

    [Fact]
    public void Elapsed_Seconds_Grows()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        Assert.True(t.GetElapsedSeconds("rtx") >= 0);
    }

    [Fact]
    public void Is_Expired_Oversized()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.Acquire("rtx", "prefill");
        Thread.Sleep(100);
        Assert.True(t.IsExpired("rtx", 0.01));
    }

    [Fact]
    public void Is_Expired_Under_Threshold()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        Assert.False(t.IsExpired("rtx", 9999));
    }

    [Fact]
    public void All_Workers_Returns_AllNames()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.InitWorker("p100");
        Assert.Equal(2, t.AllWorkers.Count);
        Assert.Contains("rtx", t.AllWorkers);
        Assert.Contains("p100", t.AllWorkers);
    }

    // ── P3.0 (#366): per-GPU exclusive reservation ──

    [Fact]
    public void TryReserveWorkerExclusive_Succeeds_When_AllFree()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.True(t.IsExclusiveReserved("p100"));
    }

    [Fact]
    public void TryReserveWorkerExclusive_Fails_For_Unknown()
    {
        var t = new WorkerTracker();
        Assert.False(t.TryReserveWorkerExclusive("unknown"));
    }

    [Fact]
    public void TryReserveWorkerExclusive_Fails_If_Any_Slot_Busy()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryAcquireSlot("p100", out _, "decode")); // 1/2 used
        Assert.False(t.TryReserveWorkerExclusive("p100"));
        Assert.False(t.IsExclusiveReserved("p100"));
    }

    [Fact]
    public void TryReserveWorkerExclusive_Fails_If_All_Slots_Busy()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryAcquireSlot("p100", out _, "decode"));
        Assert.True(t.TryAcquireSlot("p100", out _, "decode"));
        Assert.False(t.TryReserveWorkerExclusive("p100"));
    }

    [Fact]
    public void TryReserveWorkerExclusive_Fails_If_Already_Reserved()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.False(t.TryReserveWorkerExclusive("p100"));
        Assert.True(t.IsExclusiveReserved("p100"));
    }

    [Fact]
    public void TryReserveWorkerExclusive_Fails_When_Unhealthy()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        t.MarkUnhealthy("p100");
        Assert.False(t.TryReserveWorkerExclusive("p100"));
    }

    [Fact]
    public void ReleaseWorkerExclusive_Allows_ReservationAgain()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        t.ReleaseWorkerExclusive("p100");
        Assert.False(t.IsExclusiveReserved("p100"));
        Assert.True(t.TryReserveWorkerExclusive("p100"));
    }

    [Fact]
    public void ReleaseWorkerExclusive_Idempotent()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        t.ReleaseWorkerExclusive("p100"); // not reserved → no-op
        t.ReleaseWorkerExclusive("unknown"); // unknown → no-op
        Assert.False(t.IsExclusiveReserved("p100"));
    }

    [Fact]
    public void ExclusiveReserved_Blocks_TryAcquireSlot()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.False(t.TryAcquireSlot("p100", out _, "decode"));
    }

    [Fact]
    public void ExclusiveReserved_Blocks_HasFreeSlot()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.HasFreeSlot("p100"));
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.False(t.HasFreeSlot("p100"));
    }

    [Fact]
    public void ExclusiveReserved_Blocks_FreeSlotCount()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.Equal(2, t.FreeSlotCount("p100"));
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.Equal(0, t.FreeSlotCount("p100"));
    }

    [Fact]
    public void ExclusiveReserved_Blocks_IsFree()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.IsFree("p100"));
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.False(t.IsFree("p100"));
    }

    [Fact]
    public void ExclusiveReserved_Blocks_FreeWorkers()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.InitWorker("p100");
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        var free = t.FreeWorkers();
        Assert.DoesNotContain("p100", free);
        Assert.Contains("rtx", free);
    }

    [Fact]
    public void ExclusiveReserved_GetStatus_Reports_Reserved()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.Equal("free", t.GetStatus("p100"));
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.Equal("reserved", t.GetStatus("p100"));
    }

    [Fact]
    public void ExclusiveReserved_One_Slot_Only_Reserves_If_All_Free()
    {
        // The all-free check is the heart of the safety property: a peer that
        // has any active SOLO work cannot be borrowed, even partially.
        var t = new WorkerTracker();
        t.InitWorker("p100", 1);
        Assert.True(t.TryAcquireSlot("p100", out var s, "decode"));
        Assert.False(t.TryReserveWorkerExclusive("p100"));
        t.ReleaseSlot("p100", s);
        Assert.True(t.TryReserveWorkerExclusive("p100"));
    }

    [Fact]
    public void Release_After_Reservation_Restores_Slot_Acquisition()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.False(t.TryAcquireSlot("p100", out _, "decode"));
        t.ReleaseWorkerExclusive("p100");
        Assert.True(t.TryAcquireSlot("p100", out var s, "decode"));
        Assert.Equal(0, s);
    }

    // ── P3.0+ / #368: SWAPPING state (mutually exclusive with SOLO_BUSY + COMBINED_SERVING) ──

    [Fact]
    public void TryEnterSwapping_Succeeds_When_AllFree()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.True(t.IsSwapping("p100"));
    }

    [Fact]
    public void TryEnterSwapping_Fails_If_Any_Slot_Busy()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryAcquireSlot("p100", out _, "decode"));
        Assert.False(t.TryEnterSwapping("p100"));
    }

    [Fact]
    public void TryEnterSwapping_Fails_If_Exclusive_Reserved()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        Assert.True(t.TryReserveWorkerExclusive("p100"));
        Assert.False(t.TryEnterSwapping("p100"));
    }

    [Fact]
    public void TryEnterSwapping_Fails_If_Already_Swapping()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.False(t.TryEnterSwapping("p100"));
    }

    [Fact]
    public void TryEnterSwapping_Fails_If_Unhealthy()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        t.MarkUnhealthy("p100");
        Assert.False(t.TryEnterSwapping("p100"));
    }

    [Fact]
    public void TryEnterSwapping_Fails_For_Unknown()
    {
        var t = new WorkerTracker();
        Assert.False(t.TryEnterSwapping("unknown"));
    }

    [Fact]
    public void ExitSwapping_Allows_ReEntry_And_Bumps_Generation()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        Assert.Equal(0, t.GetSwapGeneration("p100"));

        Assert.True(t.TryEnterSwapping("p100"));
        t.ExitSwapping("p100");
        Assert.Equal(1, t.GetSwapGeneration("p100"));
        Assert.False(t.IsSwapping("p100"));

        Assert.True(t.TryEnterSwapping("p100"));
        t.ExitSwapping("p100");
        Assert.Equal(2, t.GetSwapGeneration("p100"));
    }

    [Fact]
    public void ExitSwapping_Idempotent()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        t.ExitSwapping("p100"); // not swapping → no-op
        t.ExitSwapping("unknown"); // unknown → no-op
        Assert.Equal(0, t.GetSwapGeneration("p100"));
    }

    [Fact]
    public void Swapping_Blocks_TryAcquireSlot()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.False(t.TryAcquireSlot("p100", out _, "decode"));
    }

    [Fact]
    public void Swapping_Blocks_HasFreeSlot_And_FreeSlotCount()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.HasFreeSlot("p100"));
        Assert.Equal(2, t.FreeSlotCount("p100"));
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.False(t.HasFreeSlot("p100"));
        Assert.Equal(0, t.FreeSlotCount("p100"));
    }

    [Fact]
    public void Swapping_Blocks_IsFree_And_FreeWorkers()
    {
        var t = new WorkerTracker();
        t.InitWorker("rtx");
        t.InitWorker("p100");
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.False(t.IsFree("p100"));
        var free = t.FreeWorkers();
        Assert.DoesNotContain("p100", free);
        Assert.Contains("rtx", free);
    }

    [Fact]
    public void Swapping_Blocks_TryReserveWorkerExclusive()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100");
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.False(t.TryReserveWorkerExclusive("p100"));
    }

    [Fact]
    public void Swapping_GetStatus_Reports_Swapping()
    {
        var t = new WorkerTracker();
        t.InitWorker("p100", 2);
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.Equal("swapping", t.GetStatus("p100"));
    }

    [Fact]
    public void Swapping_Takes_Priority_Over_ExclusiveReserved_In_GetStatus()
    {
        // (sanity) exclusive reservation must NOT be set when SWAPPING is set,
        // and GetStatus prefers the SWAPPING label.
        var t = new WorkerTracker();
        t.InitWorker("p100");
        Assert.True(t.TryEnterSwapping("p100"));
        Assert.False(t.TryReserveWorkerExclusive("p100"));
        Assert.Equal("swapping", t.GetStatus("p100"));
    }
}
