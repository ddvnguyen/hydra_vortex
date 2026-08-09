using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Tests.Core.TestHelpers;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// Pins the v2 execution architecture (epic #591): every pipeline state has ONE
/// class deriving from the common <see cref="WorkerStateRunner"/> base, and the
/// <c>Plan</c> concern is a single class implementing BOTH plan states
/// (<c>RouteDecision</c> initial routing + <c>PickDecode</c> decode handoff).
/// </summary>
public sealed class V2ArchitectureTests
{
    private static readonly IReadOnlyList<WorkerConfig> Workers = new List<WorkerConfig>
    {
        new() { Name = "rtx", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },
        new() { Name = "p100", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
    };

    private static WorkerTracker Tracker()
    {
        var t = new WorkerTracker();
        foreach (var w in Workers) t.InitWorker(w.Name, w.Slots);
        return t;
    }

    private static IEnumerable<WorkerStateRunner> BuildRunners(IStoreGateway store, IEngineRpcGateway engine, ICompletionProxyService proxy,
        ISessionLedger ledger, IWorkerTracker tracker, IHealthMonitorService health)
    {
        yield return new PlanRunner(new RoutePlanner(), new LeaseManager(tracker), ledger, Workers, tracker, health);
        yield return new PrefillRunner(engine);
        yield return new SaveKvRunner(store);
        yield return new RestoreRunner(store, engine);
        yield return new DecodeRunner(proxy);
        yield return new BgSaveRunner();
    }

    [Fact]
    public void PlanRunner_Is_One_Class_For_Both_Plan_States()
    {
        var tracker = Tracker();
        var plan = new PlanRunner(new RoutePlanner(), new LeaseManager(tracker), new SessionLedger(), Workers, tracker, new FakeHealthMonitor());

        // The user's rule: Plan(Prefill) and PlanDecode are the SAME class — just
        // different states.
        Assert.True(plan.Handles(WorkItemState.RouteDecision));
        Assert.True(plan.Handles(WorkItemState.PickDecode));
        Assert.IsAssignableFrom<WorkerStateRunner>(plan);
    }

    [Fact]
    public void Every_Pipeline_State_Has_A_WorkerStateRunner()
    {
        var tracker = Tracker();
        var store = new StoreGateway(new FakeStoreClient());
        var engine = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = new FakeEngineRpcClient() });
        var runners = BuildRunners(store, engine, new FakeCompletionProxy(), new SessionLedger(), tracker, new FakeHealthMonitor()).ToList();

        // Same registration logic as WorkerSchedulerV2 (one runner per handled state).
        var map = runners
            .SelectMany(r => Enum.GetValues<WorkItemState>().Where(r.Handles).Select(s => (State: s, Runner: r)))
            .ToDictionary(x => x.State, x => x.Runner);

        var pipelineStates = new[]
        {
            WorkItemState.RouteDecision, WorkItemState.Prefill, WorkItemState.SaveKv,
            WorkItemState.PickDecode, WorkItemState.RestoreKv, WorkItemState.Decode, WorkItemState.BgSave,
        };
        foreach (var state in pipelineStates)
        {
            Assert.True(map.TryGetValue(state, out var runner), $"no WorkerStateRunner for {state}");
            Assert.IsAssignableFrom<WorkerStateRunner>(runner);
        }
    }
}
