using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;

namespace Tests.Core.SchedulerV2Tests;

public sealed class RoutePlannerTests
{
    private static readonly IReadOnlyList<WorkerConfig> Workers = new List<WorkerConfig>
    {
        new() { Name = "rtx", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },   // prefill + decode
        new() { Name = "p100", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 }, // decode only
    };

    /// <summary>Engine-mode cfg for COMBINED planning (MultiEngineRouter.Select gates).</summary>
    private static readonly CoordinatorConfig Cfg = new()
    {
        UseLlamaEngine = true,
        CombinedEnabled = true,
        MultiEnginePolicy = "combined",
        MultiEngineThreshold = 10,
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

        var plan = new RoutePlanner().Plan(Req, RequestType.Atomic, Workers, tracker, health, ledger, Cfg);

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

        var plan = new RoutePlanner().Plan(Req, RequestType.Atomic, Workers, tracker, health, ledger, Cfg);

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
        var warmEntry = ledger.Register("sess", "p100", slotId: 0, nPast: 100); // KV resident on p100
        warmEntry.HasStoreState = true; // warm gate: resident slot + durable store state

        // p100's slot is FREE (no warm lease renting it in this unit-level probe)
        // → the affinity probe passes → decode on p100, KV resident → NO store
        // restore (ReuseStoreState=false; the machine routes Solo → Decode).
        var plan = new RoutePlanner().Plan(Req, RequestType.Solo, Workers, tracker, health, ledger, Cfg);

        Assert.True(plan.HasCapacity);
        Assert.Null(plan.PrefillWorker); // decode-only: no prefill worker
        Assert.Equal("p100", plan.DecodeWorker);
        Assert.Equal(RequestType.Solo, plan.RequestType);
        Assert.False(plan.ReuseStoreState);
    }

    [Fact]
    public void Warm_CrossNode_Falls_Back_To_Alternate_Worker_When_Affinity_Node_Has_No_Free_Slot()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();
        var warmEntry = ledger.Register("sess", "p100", slotId: 0, nPast: 100); // KV resident on p100
        warmEntry.HasStoreState = true; // warm gate: resident slot + durable store state

        // The session's OWN warm lease rents p100's ONLY slot (C2 stash never
        // disposes it between turns) → the affinity probe (HasFreeSlot) fails →
        // the plan falls back CROSS-NODE to the best free decode worker EXCLUDING
        // p100 (rtx), restoring the KV from the Store (ReuseStoreState=true).
        Assert.True(tracker.TryAcquireSlot("p100", out _, "warm-lease"));

        var plan = new RoutePlanner().Plan(Req, RequestType.Solo, Workers, tracker, health, ledger, Cfg);

        Assert.True(plan.HasCapacity);
        Assert.Null(plan.PrefillWorker); // decode-only: no prefill worker
        Assert.Equal("rtx", plan.DecodeWorker); // the alternate (legacy PickBestDecodeWorker exclude)
        Assert.Equal(RequestType.Solo, plan.RequestType);
        Assert.True(plan.ReuseStoreState); // cross-node: KV restored from Store before decode

        // No alternate (rtx busy too) → no capacity decision (waits for a release).
        Assert.True(tracker.TryAcquireSlot("rtx", out _));
        Assert.True(tracker.TryAcquireSlot("rtx", out _));
        var noCapacity = new RoutePlanner().Plan(Req, RequestType.Solo, Workers, tracker, health, ledger, Cfg);
        Assert.False(noCapacity.HasCapacity);
    }

    [Fact]
    public void Prefill_Two_Phase_Picks_Prefill_Worker_Only()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();

        // GPU-utilization rule: the decode worker is NOT reserved up front.
        var plan = new RoutePlanner().Plan(Req, RequestType.Prefill, Workers, tracker, health, ledger, Cfg);

        Assert.True(plan.HasCapacity);
        Assert.Equal("rtx", plan.PrefillWorker);
        Assert.Null(plan.DecodeWorker);
    }

    [Fact]
    public void PlanDecode_Picks_Decode_Worker_By_Worker_Rules()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();

        var planner = new RoutePlanner();

        // Best free decode-capable worker by DecodePriority (p100 = 1 < rtx = 2).
        var decode = planner.PlanDecode(Req, session: null, Workers, tracker, health);
        Assert.Equal("p100", decode);

        // Decode-time choice is NOT steered by the ledger's mid-request SlotFreed
        // (during P/D the prefill node shows SlotFreed=false; decode must go to
        // the worker rules, not back to the prefill node).
        ledger.Register("sess", "rtx", slotId: 0, nPast: 100);
        var midRequest = planner.PlanDecode(Req, ledger.Lookup("sess"), Workers, tracker, health);
        Assert.Equal("p100", midRequest);

        // Decode worker fully busy → next best.
        Assert.True(tracker.TryAcquireSlot("p100", out _));
        var busyDecode = planner.PlanDecode(Req, session: null, Workers, tracker, health);
        Assert.Equal("rtx", busyDecode);
    }

    [Fact]
    public void Combined_Requires_A_CombinedCapable_Head_With_Free_Healthy_Peer()
    {
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        tracker.InitWorker("p100", 1);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();

        // No worker is a CombinedCapable head with a configured peer → no capacity
        // (the legacy MultiEngineRouter.Select gate: IsHead + PeerWorker + alias).
        var plan = new RoutePlanner().Plan(Req, RequestType.Combined, Workers, tracker, health, ledger, Cfg);
        Assert.False(plan.HasCapacity);

        // With a CombinedCapable head (Role=head, PeerWorker=p100, resolvable
        // ModelAlias) + a healthy free peer, the combined request routes to the
        // head AND carries the peer reservation + mode + engine config.
        var combinedWorkers = new List<WorkerConfig>
        {
            new()
            {
                Name = "rtx-combined", WorkerType = 3, Slots = 2, PrefillPriority = 1,
                Role = "head", PeerWorker = "p100", CombinedCapable = true, PipelineCapable = true,
                ModelAlias = "nano",
            },
            new() { Name = "p100", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
        };
        tracker.InitWorker("rtx-combined", 2);
        ModelRegistry.RegisterForTest(new EngineConfig(
            ModelAlias: "nano", ModelPath: "/dev/null", NGpuLayers: 0, NCtx: 2048,
            ContBatching: true, Fit: false, UbatchSize: 512,
            SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));

        var plan2 = new RoutePlanner().Plan(Req, RequestType.Combined, combinedWorkers, tracker, health, ledger, Cfg);
        Assert.True(plan2.HasCapacity);
        Assert.Equal("rtx-combined", plan2.PrefillWorker);
        Assert.Equal("rtx-combined", plan2.DecodeWorker); // decode stays on the head
        Assert.Equal("p100", plan2.PeerWorker);
        Assert.Equal(MultiEngineMode.Combined, plan2.MultiMode);
        Assert.NotNull(plan2.MultiEngineConfig);
        Assert.Equal(RequestType.Combined, plan2.RequestType);

        // Peer already exclusively reserved (another COMBINED in flight) → no capacity.
        Assert.True(tracker.TryReserveWorkerExclusive("p100"));
        var plan3 = new RoutePlanner().Plan(Req, RequestType.Combined, combinedWorkers, tracker, health, ledger, Cfg);
        Assert.False(plan3.HasCapacity);

        // Head ModelAlias unresolvable via ModelRegistry → the plan is refused.
        tracker.ReleaseWorkerExclusive("p100");
        var unresolvedWorkers = new List<WorkerConfig>
        {
            new()
            {
                Name = "rtx-nomodel", WorkerType = 3, Slots = 2, PrefillPriority = 1,
                Role = "head", PeerWorker = "p100", CombinedCapable = true, PipelineCapable = true,
                ModelAlias = "alias-never-registered",
            },
            combinedWorkers[1],
        };
        tracker.InitWorker("rtx-nomodel", 2);
        var plan4 = new RoutePlanner().Plan(Req, RequestType.Combined, unresolvedWorkers, tracker, health, ledger, Cfg);
        Assert.False(plan4.HasCapacity);
    }
}
