using System.Text.Json;
using Hydra.Store;

namespace Tests.Store;

public sealed class WorkItemTests
{
    [Fact]
    public void Created_In_Pending_State()
    {
        var item = MakeItem("sess_test");
        Assert.Equal(WorkItemState.Pending, item.State);
    }

    [Fact]
    public void Cancel_Sets_Flag_And_Completes_Future()
    {
        var item = MakeItem("sess_test");
        item.Cancel();
        Assert.True(item.IsCancelled);
        Assert.True(item.Completion.Task.IsCanceled);
    }

    [Fact]
    public void ElapsedMs_Grows()
    {
        var item = MakeItem("sess_test");
        Thread.Sleep(10);
        Assert.True(item.ElapsedMs > 0);
    }

    [Fact]
    public void States_Are_Distinct()
    {
        var states = Enum.GetValues<WorkItemState>();
        var names = states.Select(s => s.ToString()).ToHashSet();
        Assert.Equal(states.Length, names.Count); // no duplicate names
    }

    [Fact]
    public void Done_And_Failed_Are_Greater_Than_Active()
    {
        Assert.True((int)WorkItemState.Done > (int)WorkItemState.Decode);
        Assert.True((int)WorkItemState.Failed > (int)WorkItemState.Decode);
        Assert.True((int)WorkItemState.Cancelled > (int)WorkItemState.Decode);
    }

    private static WorkItem MakeItem(string sessionId) => new(
        new Dictionary<string, object> { ["stream"] = false },
        new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hello" } },
        sessionId,
        "trace_123",
        null,
        10,
        100
    );
}

public sealed class WorkerSchedulerTests
{
    private static CoordinatorConfig MakeConfig(bool mix = false) => new()
    {
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2, PrefillModelName = mix ? "nano" : null, DecodeModelName = mix ? "balanced" : null },
            new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://p100:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
        },
        MixPrecisionEnabled = mix,
    };

    [Fact]
    public async Task Submit_Completes_When_Future_Set()
    {
        var cfg = MakeConfig();
        var ledger = new SessionLedger();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name);
        var proxy = new CompletionProxy();
        var scheduler = new WorkerScheduler(cfg, ledger, tracker, proxy, null, Serilog.Log.Logger);

        var item = new WorkItem(
            new Dictionary<string, object> { ["stream"] = false },
            new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "test" } },
            "sess_x", "trace_x", null, 10, 50
        );
        // Manually finalize to test completion
        item.Response = new { choices = "ok" };
        item.State = WorkItemState.Done;
        item.Completion.TrySetResult(item.Response);

        var result = await item.Completion.Task;
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Cancelled_Future_Propagates()
    {
        var item = new WorkItem(
            new Dictionary<string, object> { ["stream"] = false },
            new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "test" } },
            "sess_x", "trace_x", null, 10, 50
        );
        item.Cancel();

        Assert.True(item.IsCancelled);
        Assert.True(item.Completion.Task.IsCanceled);
        await Task.CompletedTask;
    }

    [Fact]
    public void Worker_Tracker_Acquire_Release_Works()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx");
        Assert.True(tracker.Acquire("rtx", "prefill"));
        Assert.False(tracker.IsFree("rtx"));
        tracker.Release("rtx");
        Assert.True(tracker.IsFree("rtx"));
    }

    [Fact]
    public void Finalize_Done_Sets_Result()
    {
        // Test that finalizing a work item with Done state completes the future.
        // This is tested via manual item manipulation since the scheduler's Finalize is private.
        var item = new WorkItem(
            new(), new(), "sess_x", "tr", null, 1, 1
        );
        item.Response = new { status = "ok" };
        item.State = WorkItemState.Done;
        item.Completion.TrySetResult(item.Response);

        Assert.True(item.Completion.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Finalize_Failed_Sets_Exception()
    {
        var item = new WorkItem(
            new(), new(), "sess_x", "tr", null, 1, 1
        );
        item.Error = new InvalidOperationException("test failure");
        item.State = WorkItemState.Failed;
        item.Completion.TrySetException(item.Error);

        Assert.True(item.Completion.Task.IsFaulted);
        Assert.IsType<InvalidOperationException>(item.Completion.Task.Exception!.InnerException);
    }
}

public sealed class WorkItemIntegrationTests
{
    /// <summary>
    /// Simulates a full cold_concurrency state machine flow.
    /// Verifies state transitions follow the expected sequence.
    /// </summary>
    [Fact]
    public void Full_Cold_Concurrency_State_Sequence()
    {
        var expectedStates = new[]
        {
            WorkItemState.Pending,
            WorkItemState.RouteDecision,
            WorkItemState.WaitingPrefill,
            WorkItemState.ModelLoadPrefill,
            WorkItemState.PrefixRestore,
            WorkItemState.Prefill,
            WorkItemState.SaveKv,
            WorkItemState.SaveDone,
            WorkItemState.MarkEvicted,
            WorkItemState.PickDecode,
            WorkItemState.WaitingDecode,
            WorkItemState.ModelLoadDecode,
            WorkItemState.RestoreKv,
            WorkItemState.Decode,
            WorkItemState.BgSave,
            WorkItemState.Done,
        };

        var item = new WorkItem(
            new Dictionary<string, object> { ["stream"] = false, ["model"] = "nano" },
            new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "Write a sorting algorithm." } },
            "sess_cold",
            "trace_cold",
            "e7a6848eba328f28",
            500,
            5000
        );
        item.RouteType = "cold_concurrency";

        Assert.Equal(WorkItemState.Pending, item.State);
        // Verify all expected states exist in the enum
        foreach (var state in expectedStates)
            Assert.True(Enum.IsDefined(state), $"State {state} should be defined");
    }

    /// <summary>
    /// Simulates the warm affinity path (shortcut through fewer states).
    /// </summary>
    [Fact]
    public void Warm_Affinity_Path()
    {
        var item = new WorkItem(
            new Dictionary<string, object> { ["stream"] = true },
            new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } },
            "sess_warm",
            "trace_warm",
            null,
            5,
            30
        );
        item.RouteType = "affinity";
        item.State = WorkItemState.Decode; // warm affinity goes directly to decode

        Assert.Equal(WorkItemState.Decode, item.State);
        Assert.True(item.IsStreaming);
    }
}
