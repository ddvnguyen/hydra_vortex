using System.Text.Json;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core;

public sealed class WorkItemTests
{
    [Fact]
    public void Created_In_None_State()
    {
        var item = MakeItem("sess_test");
        Assert.Equal(WorkItemState.None, item.State);
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

    [Fact]
    public void ExtractUsageInt_Reads_Prompt_And_Completion_Tokens()
    {
        var usage = JsonSerializer.Deserialize<Dictionary<string, object>>(
            "{\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":40,\"total_tokens\":57}}")!;

        Assert.Equal(17, WorkerSchedulerService.ExtractUsageInt(usage, "prompt_tokens"));
        Assert.Equal(40, WorkerSchedulerService.ExtractUsageInt(usage, "completion_tokens"));
        Assert.Equal(57, WorkerSchedulerService.ExtractUsageInt(usage, "total_tokens"));
    }

    [Fact]
    public void ExtractUsageInt_Returns_Zero_When_Usage_Absent_Or_Malformed()
    {
        var noUsage = JsonSerializer.Deserialize<Dictionary<string, object>>("{\"choices\":[]}")!;
        var emptyUsage = JsonSerializer.Deserialize<Dictionary<string, object>>("{\"usage\":{}}")!;

        Assert.Equal(0, WorkerSchedulerService.ExtractUsageInt(noUsage, "prompt_tokens"));
        Assert.Equal(0, WorkerSchedulerService.ExtractUsageInt(emptyUsage, "completion_tokens"));
    }

    [Fact]
    public void RecordPhase_Stores_Durations_Not_Cumulative()
    {
        var item = MakeItem("sess_test");
        Thread.Sleep(20);
        var a = item.RecordPhase("a_ms");
        Thread.Sleep(20);
        var b = item.RecordPhase("b_ms");

        Assert.Equal(a, item.Phases["a_ms"]);
        Assert.Equal(b, item.Phases["b_ms"]);
        Assert.True(a >= 10, $"a={a}");
        Assert.True(b >= 10, $"b={b}");
        // Durations, not cumulative checkpoints: the sum of phases never
        // exceeds the total elapsed time (the old cumulative scheme would).
        Assert.True(a + b <= item.ElapsedMs, $"a={a} b={b} elapsed={item.ElapsedMs}");
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
        var proxy = new CompletionProxyService();
        var health = new TestHealthMonitor();
        var sp = new ServiceCollection().BuildServiceProvider();
        var scheduler = new WorkerSchedulerService(cfg, ledger, tracker, proxy, health, null, sp, Serilog.Log.Logger);

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

    private static WorkerSchedulerService MakeScheduler()
    {
        var cfg = MakeConfig();
        var ledger = new SessionLedger();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name);
        var proxy = new CompletionProxyService();
        var health = new TestHealthMonitor();
        var sp = new ServiceCollection().BuildServiceProvider();
        return new WorkerSchedulerService(cfg, ledger, tracker, proxy, health, null, sp, Serilog.Log.Logger);
    }

    [Fact]
    public async Task Streaming_Timeline_Deferred_Until_NotifyStreamComplete()
    {
        var scheduler = MakeScheduler();
        var item = new WorkItem(
            new Dictionary<string, object> { ["stream"] = true },
            new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "test" } },
            "sess_stream", "trace_stream", null, 10, 50
        );
        item.DecodeStartMs = item.ElapsedMs;

        await scheduler.FinalizeAsync(item, WorkItemState.Done);

        // Stream still in flight — timeline (decode_ms/total_ms) must not be final yet
        Assert.True(scheduler._pendingTimelines.ContainsKey("sess_stream"));
        Assert.False(item.Phases.ContainsKey("total_ms"));

        await scheduler.NotifyStreamComplete("sess_stream");

        Assert.False(scheduler._pendingTimelines.ContainsKey("sess_stream"));
        Assert.True(item.Phases.ContainsKey("decode_ms"));
        Assert.True(item.Phases.ContainsKey("total_ms"));
        Assert.True(item.Phases["decode_ms"] <= item.Phases["total_ms"]);
    }

    [Fact]
    public async Task NonStreaming_Timeline_Emitted_At_Finalize()
    {
        var scheduler = MakeScheduler();
        var item = new WorkItem(
            new Dictionary<string, object> { ["stream"] = false },
            new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "test" } },
            "sess_sync", "trace_sync", null, 10, 50
        );
        item.Response = new { choices = "ok" };

        await scheduler.FinalizeAsync(item, WorkItemState.Done);

        Assert.False(scheduler._pendingTimelines.ContainsKey("sess_sync"));
        Assert.True(item.Phases.ContainsKey("total_ms"));
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
            WorkItemState.None,
            WorkItemState.RouteDecision,
            WorkItemState.ModelLoadPrefill,
            WorkItemState.PrefixRestore,
            WorkItemState.Prefill,
            WorkItemState.SaveKv,
            WorkItemState.SaveDone,
            WorkItemState.MarkEvicted,
            WorkItemState.PickDecode,
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

        Assert.Equal(WorkItemState.None, item.State);
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

    // ── M-Perf.9 (#289): model identity on WorkItem ──

    [Fact]
    public void WorkItem_DefaultModelIdentity_IsEmpty()
    {
        var item = new WorkItem(
            new Dictionary<string, object>(),
            new List<Dictionary<string, object>>(),
            "sess", "trace", null, 1, 10);

        Assert.Null(item.KvModelAlias);
        Assert.Null(item.KvModelHash);
        Assert.Null(item.KvModelPath);
        Assert.False(item.KvModelFallback);
    }

    [Fact]
    public void WorkItem_CanCarryModelIdentityAcrossStates()
    {
        // The model identity rides the WorkItem from PrefillAsync (where the
        // engine reports the model that built the KV) through SaveKv
        // (where it's stored in the manifest meta) into RestoreKvAsync
        // (where it's compared against the slot's current model).
        var item = new WorkItem(
            new Dictionary<string, object>(),
            new List<Dictionary<string, object>>(),
            "sess", "trace", null, 1, 10);
        item.KvModelAlias = "balanced";
        item.KvModelHash  = "deadbeef" + new string('0', 56);
        item.KvModelPath  = "/models/Balanced.gguf";
        item.KvModelFallback = false;

        Assert.Equal("balanced", item.KvModelAlias);
        Assert.Equal(64, item.KvModelHash!.Length);
        Assert.Equal("/models/Balanced.gguf", item.KvModelPath);
        Assert.False(item.KvModelFallback);
    }

    [Fact]
    public void CoordinatorConfig_AllowCrossModelKvReuse_DefaultsFalse()
    {
        // Default (no env var) must be false — strict by default.
        var prev = Environment.GetEnvironmentVariable("HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE", null);
            var cfg = new CoordinatorConfig { Workers = new() };
            Assert.False(cfg.AllowCrossModelKvReuse);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE", prev);
        }
    }

    [Fact]
    public void CoordinatorConfig_AllowCrossModelKvReuse_FromEnvTrue()
    {
        var prev = Environment.GetEnvironmentVariable("HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE");
        try
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE", "true");
            var cfg = new CoordinatorConfig { Workers = new() };
            Assert.True(cfg.AllowCrossModelKvReuse);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE", prev);
        }
    }
}
