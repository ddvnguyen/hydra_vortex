using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services.SchedulerV2;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// v2 is evaluated by the **hydra model + GPU worker rules** (epic #591), not by
/// byte-parity with legacy scheduler quirks. These tests pin that the classifier
/// and route planner obey the model rules:
/// <list type="bullet">
/// <item>atomic-vs-prefill split at the coordinator's AtomicThreshold,</item>
/// <item>COMBINED mode (force_mode) requires a CombinedCapable head (worker rule),</item>
/// <item>warm affinity reuses the node holding the session KV,</item>
/// <item>a cold request is never routed to a worker that cannot both prefill and decode,</item>
/// <item>no capacity → the request waits (HasCapacity=false).</item>
/// </list>
/// Worker topology mirrors production: rtx = prefill+decode head (2 slots),
/// p100 = decode-only (1 slot). Model aliases are the production hydra models.
/// </summary>
public sealed class V2HydraModelRuleTests
{
    private static readonly IReadOnlyList<WorkerConfig> Topology = new List<WorkerConfig>
    {
        new()
        {
            Name = "rtx", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2,
            CombinedCapable = true, PipelineCapable = true, ModelAlias = "moe-35b-solo",
        },
        new()
        {
            Name = "p100", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1,
            ModelAlias = "moe-35b-pd",
        },
    };

    private static readonly CoordinatorConfig Cfg = new() { AtomicThreshold = 2048, CombinedEnabled = true };

    private static ChatRequest Req(int estimatedTokens, string model = "moe-35b-solo", string? forceMode = null) => new(
        SessionId: "sess",
        TraceId: "trace",
        Model: model,
        Stream: false,
        MaxTokens: 100,
        EstimatedTokens: estimatedTokens,
        EstimatedNewTokens: 100,
        SystemPromptTokens: 0,
        PrefixHash: null,
        ForceMode: forceMode ?? "",
        Messages: new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } },
        Body: new Dictionary<string, object>());

    private static (WorkerTracker Tracker, SessionLedger Ledger, FakeHealthMonitor Health) State()
    {
        var tracker = new WorkerTracker();
        foreach (var w in Topology) tracker.InitWorker(w.Name, w.Slots);
        return (tracker, new SessionLedger(), new FakeHealthMonitor());
    }

    // ── Classifier: model rules ──

    [Fact]
    public void Small_Request_Is_Atomic_Large_Request_Is_Prefill()
    {
        var classifier = new RequestClassifier();
        Assert.Equal(RequestType.Atomic, classifier.Classify(Req(500), Cfg, hasWarmSession: false));
        Assert.Equal(RequestType.Prefill, classifier.Classify(Req(100_000), Cfg, hasWarmSession: false));
    }

    [Fact]
    public void Warm_Session_Is_Decode_Only_Followup()
    {
        var classifier = new RequestClassifier();
        Assert.Equal(RequestType.Solo, classifier.Classify(Req(500), Cfg, hasWarmSession: true));
    }

    [Fact]
    public void Forced_Combined_Is_Combined_Only_When_Enabled()
    {
        var classifier = new RequestClassifier();
        Assert.Equal(RequestType.Combined, classifier.Classify(Req(20000, "dense-27b-combined", "combined"), Cfg, hasWarmSession: false));
        Assert.Equal(RequestType.Atomic, classifier.Classify(Req(500, forceMode: "combined"), new CoordinatorConfig { AtomicThreshold = 2048, CombinedEnabled = false }, hasWarmSession: false));
    }

    // ── Planner: GPU worker rules ──

    [Fact]
    public void Cold_Request_Routes_Only_To_Prefill_And_Decode_Worker()
    {
        var (tracker, ledger, health) = State();
        var plan = new RoutePlanner().Plan(Req(500), RequestType.Atomic, Topology, tracker, health, ledger);

        Assert.True(plan.HasCapacity);
        Assert.Equal("rtx", plan.PrefillWorker); // p100 cannot prefill → excluded by the worker rule
        Assert.Equal("rtx", plan.DecodeWorker);
    }

    [Fact]
    public void Warm_Affinity_Reuses_The_Session_Node()
    {
        var (tracker, ledger, health) = State();
        ledger.Register("sess", "p100", slotId: 0, nPast: 100); // KV resident on p100

        var plan = new RoutePlanner().Plan(Req(500), RequestType.Solo, Topology, tracker, health, ledger);

        Assert.True(plan.HasCapacity);
        Assert.Null(plan.PrefillWorker); // decode-only
        Assert.Equal("p100", plan.DecodeWorker);
        Assert.True(plan.ReuseStoreState);
    }

    [Fact]
    public void Prefill_Two_Phase_Routes_Prefill_Then_Decode_Separately()
    {
        var (tracker, ledger, health) = State();

        // Two-phase: prefill worker chosen up front; decode worker deferred.
        var plan = new RoutePlanner().Plan(Req(100_000), RequestType.Prefill, Topology, tracker, health, ledger);
        Assert.Equal("rtx", plan.PrefillWorker);
        Assert.Null(plan.DecodeWorker);

        // At decode time the dedicated decoder (p100) is chosen — NOT both held at once.
        var decode = new RoutePlanner().PlanDecode(Req(100_000), ledger.Lookup("sess"), Topology, tracker, health);
        Assert.Equal("p100", decode);
    }

    [Fact]
    public void Combined_Request_Requires_A_CombinedCapable_Head()
    {
        var (tracker, ledger, health) = State();
        // rtx is CombinedCapable in the production topology.
        var plan = new RoutePlanner().Plan(Req(20000, "dense-27b-combined", "combined"), RequestType.Combined, Topology, tracker, health, ledger);
        Assert.True(plan.HasCapacity);
        Assert.Equal("rtx", plan.PrefillWorker);

        // Remove the combined-capable head → no capacity (the request waits).
        var noCombined = new List<WorkerConfig> { Topology[1] }; // p100 only
        var plan2 = new RoutePlanner().Plan(Req(20000, "dense-27b-combined", "combined"), RequestType.Combined, noCombined, tracker, health, ledger);
        Assert.False(plan2.HasCapacity);
    }

    [Fact]
    public void Busy_Workers_Are_Excluded_And_No_Capacity_When_Exhausted()
    {
        var (tracker, ledger, health) = State();
        Assert.True(tracker.TryAcquireSlot("rtx", out _));
        Assert.True(tracker.TryAcquireSlot("rtx", out _)); // rtx fully busy

        // p100 (decode-only) cannot satisfy an atomic → no capacity.
        var plan = new RoutePlanner().Plan(Req(500), RequestType.Atomic, Topology, tracker, health, ledger);
        Assert.False(plan.HasCapacity);
    }
}
