using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Tests.Core;

public sealed class ExclusivePeerReservationTests
{
    [Fact]
    public async Task Dispose_Releases_Exclusive_Reservation()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("p100", 2);
        Assert.True(tracker.TryReserveWorkerExclusive("p100"));
        Assert.True(tracker.IsExclusiveReserved("p100"));

        var res = new ExclusivePeerReservation("p100", tracker);
        Assert.Equal("p100", res.WorkerName);
        await res.DisposeAsync();

        Assert.False(tracker.IsExclusiveReserved("p100"));
        Assert.True(tracker.HasFreeSlot("p100"));
    }

    [Fact]
    public async Task Double_Dispose_Is_Safe()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("p100");
        Assert.True(tracker.TryReserveWorkerExclusive("p100"));

        var res = new ExclusivePeerReservation("p100", tracker);
        await res.DisposeAsync();
        await res.DisposeAsync(); // no-op, must not flip state back
        Assert.False(tracker.IsExclusiveReserved("p100"));
    }

    [Fact]
    public async Task Dispose_Allows_ReReservation()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("p100");
        var res1 = new ExclusivePeerReservation("p100", tracker);
        await res1.DisposeAsync();

        Assert.True(tracker.TryReserveWorkerExclusive("p100"));
        var res2 = new ExclusivePeerReservation("p100", tracker);
        await res2.DisposeAsync();
        Assert.False(tracker.IsExclusiveReserved("p100"));
    }

    [Fact]
    public async Task NoLeak_After_N_Rebinds()
    {
        // The lifecycle property: an exclusive reservation that is bound and
        // released N times must not accumulate state on the tracker.
        var tracker = new WorkerTracker();
        tracker.InitWorker("p100", 2);
        for (int i = 0; i < 25; i++)
        {
            Assert.True(tracker.TryReserveWorkerExclusive("p100"));
            Assert.True(tracker.IsExclusiveReserved("p100"));
            var res = new ExclusivePeerReservation("p100", tracker);
            await res.DisposeAsync();
            Assert.False(tracker.IsExclusiveReserved("p100"));
            Assert.Equal(2, tracker.FreeSlotCount("p100"));
        }
    }
}
