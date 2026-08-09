using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services.SchedulerV2;

namespace Tests.Core.SchedulerV2Tests;

public sealed class RoutePlannerTests
{
    private static readonly IReadOnlyList<WorkerConfig> Workers = new List<WorkerConfig>
    {
        new() { Name = "rtx", WorkerType = 3, Slots = 2, PrefillPriority = 1 },   // prefill + decode
        new() { Name = "p100", WorkerType = 2, Slots = 1, PrefillPriority = 100 }, // decode only
    };

    private static readonly ChatRequest Req = ChatRequest.FromSubmit(
        new Dictionary<string, object> { ["stream"] = false, ["max_tokens"] = 100 },
        new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } },
        "sess", estimatedTokens: 100, maxTokens: 100, prefixHash: null, systemPromptTokens: 0);

    [Fact]
    public void Cold_Atomic_Picks_Free_Prefill_And_Decode_Worker()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();

        var plan = new RoutePlanner().Plan(Req, RequestType.Atomic, Workers, tracker, health, ledger);

        Assert.True(plan.HasCapacity);
        Assert.Equal("rtx", plan.PrefillWorker);
        Assert.Equal("rtx", plan.DecodeWorker);
        Assert.Equal(RequestType.Atomic, plan.RequestType);
    }

    [Fact]
    public void No_Capacity_When_All_Prefill_Workers_Busy()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        Assert.True(tracker.TryAcquireSlot("rtx", out var s1));
        Assert.True(tracker.TryAcquireSlot("rtx", out var s2));
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();

        var plan = new RoutePlanner().Plan(Req, RequestType.Atomic, Workers, tracker, health, ledger);

        Assert.False(plan.HasCapacity); // p100 is decode-only and cannot satisfy a cold atomic
    }

    [Fact]
    public void Warm_Affinity_Reuses_The_Node_Holding_The_Session()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();
        ledger.Register("sess", "p100", slotId: 0, nPast: 100); // KV resident on p100

        var plan = new RoutePlanner().Plan(Req, RequestType.Solo, Workers, tracker, health, ledger);

        Assert.True(plan.HasCapacity);
        Assert.Equal("p100", plan.PrefillWorker);
        Assert.Equal(RequestType.Solo, plan.RequestType);
        Assert.True(plan.ReuseStoreState);
    }

    [Fact]
    public void Combined_Requires_A_CombinedCapable_Head()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();

        // Neither worker is CombinedCapable in this topology → no capacity.
        var plan = new RoutePlanner().Plan(Req, RequestType.Combined, Workers, tracker, health, ledger);
        Assert.False(plan.HasCapacity);

        // With a CombinedCapable head available, the combined request routes to it.
        var combinedWorkers = new List<WorkerConfig>
        {
            new() { Name = "rtx-combined", WorkerType = 3, Slots = 2, PrefillPriority = 1, CombinedCapable = true, PipelineCapable = true },
        };
        combinedWorkers.AddRange(Workers);
        tracker.InitWorker("rtx-combined", 2);
        var plan2 = new RoutePlanner().Plan(Req, RequestType.Combined, combinedWorkers, tracker, health, ledger);
        Assert.True(plan2.HasCapacity);
        Assert.Equal("rtx-combined", plan2.PrefillWorker);
        Assert.Equal(RequestType.Combined, plan2.RequestType);
    }
}
