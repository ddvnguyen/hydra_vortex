using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.TestHelpers;

namespace Tests.Core.Services;

/// <summary>
/// Issue #581: the engine's hydra RPC protocol is strictly serial per
/// connection — a multi-hundred-MB BgSave STATE_GET stream on the shared
/// per-worker connection held <c>RpcClient._sync</c> for its whole duration,
/// stalling the next turn's STATE_META / DECODE RPCs until the coordinator
/// gave up. The fix routes large state transfers (STATE_GET / STATE_PUT)
/// over a dedicated per-worker connection so the main connection stays free
/// for DECODE / CONFIGURE / PREFILL / INFO.
/// </summary>
public sealed class StateRpcClientTests
{
    private static CoordinatorConfig MakeConfig() => new()
    {
        WarmSlotVerificationEnabled = false,
        PrefixCheckpointEnabled = false,
        EnableChunks = false,
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", Host = "localhost", RpcPort = 9601,
                LlamaRpcPort = 9602,
                LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2,
                PrefillPriority = 1, DecodePriority = 2 },
        },
    };

    private static WorkerConfig MakeWorker() => MakeConfig().Workers[0];

    private static WorkerSchedulerService MakeScheduler(Func<string, int, RpcClient> factory)
    {
        var cfg = MakeConfig();
        var ledger = new SessionLedger();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var proxy = new CompletionProxyService();
        var health = new TestHealthMonitor();
        var sp = new ServiceCollection().BuildServiceProvider();
        var scheduler = new WorkerSchedulerService(
            cfg, ledger, tracker, proxy, health, new FakeStoreClient(), sp, Serilog.Log.Logger);
        scheduler.AgentClientFactory = factory;
        return scheduler;
    }

    [Fact]
    public void StateClient_IsDistinctFromMainClient_AndCached()
    {
        var main = new FakeStoreClient();
        var state = new FakeStoreClient();
        var factoryCalls = 0;
        var scheduler = MakeScheduler((_, _) => factoryCalls++ == 0 ? main : state);

        var worker = MakeWorker();

        // First factory call wires the main client (DECODE/CONFIGURE/PREFILL/...).
        var mainClient = scheduler.GetLlamaRpcClient(worker);
        Assert.Same(main, mainClient);

        // Second factory call wires the dedicated state client (STATE_GET/PUT) —
        // a different connection so a BgSave stream never holds the main turn.
        var stateClient = scheduler.GetStateRpcClient(worker);
        Assert.Same(state, stateClient);
        Assert.NotSame(mainClient, stateClient);

        // Both are cached per worker — repeated access returns the same instances.
        Assert.Same(mainClient, scheduler.GetLlamaRpcClient(worker));
        Assert.Same(stateClient, scheduler.GetStateRpcClient(worker));
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void StateClient_WithSharedFactory_ReturnsSameInstance()
    {
        // Test fixtures route every connection through one fake — that must
        // still work (the production flow differs only in the socket).
        var fake = new FakeStoreClient();
        var scheduler = MakeScheduler((_, _) => fake);

        var worker = MakeWorker();
        Assert.Same(fake, scheduler.GetLlamaRpcClient(worker));
        Assert.Same(fake, scheduler.GetStateRpcClient(worker));
    }
}
