using Hydra.Store.Repositories;

namespace Tests.Store;

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
}
