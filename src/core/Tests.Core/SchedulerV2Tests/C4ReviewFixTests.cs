using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Tests.Core.TestHelpers;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// Regression tests for the Claude Sonnet 5 High review findings (epic #591):
/// #1 eviction save-before-erase (CRITICAL data-loss), #2 NotifyStreamComplete
/// error surfacing, #3 caller CancellationToken threading, #4 same-node skip keyed
/// on physical slot identity, #5 on-demand warm-lease eviction under pressure.
/// </summary>
public sealed class C4ReviewFixTests
{
    private sealed class ThrowingBgSaveRunner : WorkerStateRunner
    {
        public override WorkItemState State => WorkItemState.BgSave;
        public override Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("simulated bg-save failure");
    }

    private static CoordinatorConfig Config(int rtxSlots = 2, int p100Slots = 1, bool warmVerify = false) => new()
    {
        RunMode = "concurrency",
        AtomicThreshold = 2048,
        LlamaRequestTimeoutS = 15,
        WarmSlotVerificationEnabled = warmVerify,
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = rtxSlots, PrefillPriority = 1, DecodePriority = 2 },
            new() { Name = "p100", LlamaUrl = "http://p100:8086", WorkerType = 2, Slots = p100Slots, PrefillPriority = 100, DecodePriority = 1 },
        },
    };

    private static Dictionary<string, object> Req(bool stream = false) => new()
    {
        ["stream"] = stream, ["max_tokens"] = 30, ["model"] = "nano",
    };

    private static List<Dictionary<string, object>> Msgs(int tokens) => new()
    {
        new() { ["role"] = "user", ["content"] = new string('x', tokens) },
    };

    // ── Fix #1 (CRITICAL): eviction saves the KV + erases the slot before disposing ──

    [Fact]
    public async Task EvictWarmSession_Saves_Kv_And_Erases_Slot_Before_Release()
    {
        var cfg = Config();
        var engine = new FakeEngineRpcClient();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var ledger = new SessionLedger();
        var proxy = new FakeCompletionProxy();
        var storeClient = new FakeStoreClient();
        var store = new StoreGateway(storeClient);
        var health = new FakeHealthMonitor();
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine, ["p100"] = engine });
        var leases = new LeaseManager(tracker);

        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, ledger, cfg.Workers, tracker, health, cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engineGateway, proxy),
            new PrefixRestoreRunner(cfg, store, engineGateway, ledger),
            new SaveKvRunner(store, ledger, engineGateway, cfg),
            new RestoreRunner(store, engineGateway, ledger, leases, proxy, cfg),
            new DecodeRunner(proxy, engineGateway, ledger, cfg, health),
            new BgSaveRunner(engineGateway, store, ledger),
        };
        var scheduler = new WorkerSchedulerV2(cfg, ledger, tracker, health,
            new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
            engineGateway, store, proxy);
        using var runCts = new CancellationTokenSource();
        _ = scheduler.RunAsync(runCts.Token);
        await Task.Delay(50);

        await scheduler.SubmitAsync(Req(), Msgs(100), "sess_a", 100, 30, null, CancellationToken.None);
        Assert.Equal(1, scheduler.WarmLeaseCount); // slot held warm

        // CRITICAL fix: eviction must StateGet the slot KV, persist it, and EraseSlot.
        await scheduler.EvictWarmSessionAsync("sess_a", "rtx", CancellationToken.None);

        Assert.Equal(0, scheduler.WarmLeaseCount);
        Assert.Contains(engine.Calls, c => c.Op == Hydra.Shared.OpCode.StateGet); // save-capture
        Assert.Contains(proxy.EraseCalls, e => e.NodeUrl == "http://localhost:8080"); // slot erased
        Assert.Equal(0d, tracker.GetElapsedSeconds("rtx")); // lease released
        runCts.Cancel();
    }

    // ── Fix #2 (HIGH): NotifyStreamComplete surfaces a resume failure, not a fake success ──

    [Fact]
    public async Task NotifyStreamComplete_Surfaces_Resume_Failure_As_Exception()
    {
        var cfg = Config();
        var engine = new FakeEngineRpcClient();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var ledger = new SessionLedger();
        var proxy = new FakeCompletionProxy();
        var store = new StoreGateway(new FakeStoreClient());
        var health = new FakeHealthMonitor();
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine, ["p100"] = engine });
        var leases = new LeaseManager(tracker);

        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, ledger, cfg.Workers, tracker, health, cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engineGateway, proxy),
            new PrefixRestoreRunner(cfg, store, engineGateway, ledger),
            new SaveKvRunner(store, ledger, engineGateway, cfg),
            new RestoreRunner(store, engineGateway, ledger, leases, proxy, cfg),
            new DecodeRunner(proxy, engineGateway, ledger, cfg, health),
            new ThrowingBgSaveRunner(), // the resume loop throws here
        };
        var scheduler = new WorkerSchedulerV2(cfg, ledger, tracker, health,
            new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
            engineGateway, store, proxy);
        using var runCts = new CancellationTokenSource();
        _ = scheduler.RunAsync(runCts.Token);
        await Task.Delay(50);

        // Streaming: submit returns the stream; the resume (NotifyStreamComplete)
        // hits the throwing BgSave runner.
        var submit = scheduler.SubmitAsync(Req(stream: true), Msgs(100), "sess_s", 100, 30, null, CancellationToken.None);
        await submit; // stream handed over
        await scheduler.NotifyStreamComplete("sess_s");

        // Fix #2: the resume failure must surface (completion faults, not a fake
        // success) AND must not leak the slot — the streamed request's Completion
        // is now faulted with the resume error, leases released, no warm lease.
        Assert.Equal(0, scheduler.WarmLeaseCount);
        Assert.Equal(0d, tracker.GetElapsedSeconds("rtx")); // leases released on the failed resume
        runCts.Cancel();
    }

    // ── Fix #3 (HIGH): caller CancellationToken aborts the running pipeline ──

    [Fact]
    public async Task PreCancelled_Caller_Token_Aborts_And_Releases_The_Slot()
    {
        var cfg = Config();
        var engine = new FakeEngineRpcClient();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var ledger = new SessionLedger();
        var proxy = new FakeCompletionProxy();
        var store = new StoreGateway(new FakeStoreClient());
        var health = new FakeHealthMonitor();
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine, ["p100"] = engine });
        var leases = new LeaseManager(tracker);
        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, ledger, cfg.Workers, tracker, health, cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engineGateway, proxy),
            new PrefixRestoreRunner(cfg, store, engineGateway, ledger),
            new SaveKvRunner(store, ledger, engineGateway, cfg),
            new RestoreRunner(store, engineGateway, ledger, leases, proxy, cfg),
            new DecodeRunner(proxy, engineGateway, ledger, cfg, health),
            new BgSaveRunner(engineGateway, store, ledger),
        };
        var scheduler = new WorkerSchedulerV2(cfg, ledger, tracker, health,
            new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
            engineGateway, store, proxy);
        using var runCts = new CancellationTokenSource();
        _ = scheduler.RunAsync(runCts.Token);
        await Task.Delay(50);

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel(); // client disconnects before/at submit

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.SubmitAsync(Req(), Msgs(100), "sess_c", 100, 30, null, callerCts.Token));

        // The pipeline saw the caller token and released the slot (no leak).
        await Task.Delay(100);
        Assert.Equal(0d, tracker.GetElapsedSeconds("rtx"));
        runCts.Cancel();
    }

    // ── Fix #4 (HIGH): same-node decode skip is keyed on PHYSICAL slot identity ──

    [Fact]
    public async Task SameNode_Skip_Does_Not_Fire_When_Held_Slot_Is_Not_The_Kv_Slot()
    {
        var cfg = Config();
        var engine = new FakeEngineRpcClient();
        var tracker = new WorkerTracker();
        tracker.InitWorker("rtx", 2);
        var ledger = new SessionLedger();
        var storeClient = new FakeStoreClient();
        var store = new StoreGateway(storeClient);
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine });

        // Runner-level: held PrefillLease is slot 1, but the KV lives in slot 0 —
        // the same-node skip must NOT fire (restore must happen) or it would decode
        // over a slot that does not hold this request's KV (#469).
        var restore = new RestoreRunner(store, engineGateway, ledger, new LeaseManager(tracker), new FakeCompletionProxy(), cfg);
        var chat = ChatRequest.FromSubmit(Req(), Msgs(100), "sess_s", 100, 30, null, 0);
        var req = new SchedulerRequest(chat, RequestType.Atomic, 30)
        {
            PrefillWorker = cfg.Workers[0],
            DecodeWorker = cfg.Workers[0],
            PrefillLease = new SlotLease("rtx", slotId: 1, "sess_s", LeaseLifetime.Long, tracker),
            KvSlotId = 0, // KV is in slot 0, held slot is 1
        };

        var result = await restore.RunAsync(new RunnerContext(req, "rtx"), CancellationToken.None);

        Assert.Equal(PhaseOutcome.Fire, result.Outcome);
        Assert.Contains(storeClient.Calls, c => c.Op == Hydra.Shared.OpCode.Get); // restore happened (not skipped)
    }

    // ── Fix #5 (HIGH): on-demand warm-lease eviction frees a slot under pressure ──

    [Fact]
    public async Task OnDemand_Eviction_Frees_A_Slot_Under_Pressure()
    {
        var cfg = Config(rtxSlots: 1, p100Slots: 1); // single prefill+decode slot
        var engine = new FakeEngineRpcClient();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var ledger = new SessionLedger();
        var proxy = new FakeCompletionProxy();
        var store = new StoreGateway(new FakeStoreClient());
        var health = new FakeHealthMonitor();
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine, ["p100"] = engine });
        var leases = new LeaseManager(tracker);
        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, ledger, cfg.Workers, tracker, health, cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engineGateway, proxy),
            new PrefixRestoreRunner(cfg, store, engineGateway, ledger),
            new SaveKvRunner(store, ledger, engineGateway, cfg),
            new RestoreRunner(store, engineGateway, ledger, leases, proxy, cfg),
            new DecodeRunner(proxy, engineGateway, ledger, cfg, health),
            new BgSaveRunner(engineGateway, store, ledger),
        };
        var scheduler = new WorkerSchedulerV2(cfg, ledger, tracker, health,
            new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
            engineGateway, store, proxy);
        using var runCts = new CancellationTokenSource();
        _ = scheduler.RunAsync(runCts.Token);
        await Task.Delay(50);

        // Turn 1 holds the only prefill+decode slot warm.
        await scheduler.SubmitAsync(Req(), Msgs(100), "sess_a", 100, 30, null, CancellationToken.None);
        Assert.Equal(1, scheduler.WarmLeaseCount);

        // Turn 2 (different session) needs the same slot: the evaluator must evict
        // the OLDEST warm lease (save + erase) on demand and dispatch, not requeue.
        await scheduler.SubmitAsync(Req(), Msgs(100), "sess_b", 100, 30, null, CancellationToken.None);

        Assert.Contains(engine.Calls, c => c.Op == Hydra.Shared.OpCode.StateGet); // eviction save
        Assert.Equal(1, scheduler.WarmLeaseCount); // sess_b is now warm; sess_a was evicted
        runCts.Cancel();
    }

    // ── Fix #3/#8 (HIGH): a MID-FLIGHT caller cancellation aborts the running
    //    pipeline and releases the slot (not just a pre-cancelled submit) ──

    [Fact]
    public async Task MidFlight_Caller_Cancel_Aborts_Pipeline_And_Releases_Slot()
    {
        var cfg = Config();
        var engine = new FakeEngineRpcClient { BlockPrefill = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var ledger = new SessionLedger();
        var proxy = new FakeCompletionProxy();
        var store = new StoreGateway(new FakeStoreClient());
        var health = new FakeHealthMonitor();
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine, ["p100"] = engine });
        var leases = new LeaseManager(tracker);
        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, ledger, cfg.Workers, tracker, health, cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engineGateway, proxy),
            new PrefixRestoreRunner(cfg, store, engineGateway, ledger),
            new SaveKvRunner(store, ledger, engineGateway, cfg),
            new RestoreRunner(store, engineGateway, ledger, leases, proxy, cfg),
            new DecodeRunner(proxy, engineGateway, ledger, cfg, health),
            new BgSaveRunner(engineGateway, store, ledger),
        };
        var scheduler = new WorkerSchedulerV2(cfg, ledger, tracker, health,
            new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
            engineGateway, store, proxy);
        using var runCts = new CancellationTokenSource();
        _ = scheduler.RunAsync(runCts.Token);
        await Task.Delay(50);

        using var callerCts = new CancellationTokenSource();
        var submit = scheduler.SubmitAsync(Req(), Msgs(100), "sess_m", 100, 30, null, callerCts.Token);

        // Hold until the prefill is genuinely in-flight (blocked on the gate).
        await Task.Delay(300);
        Assert.Contains(engine.Calls, c => c.Op == Hydra.Shared.OpCode.EnginePrefill);

        callerCts.Cancel(); // client disconnects mid-pipeline
        engine.BlockPrefill!.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        await Task.Delay(100);
        Assert.Equal(0d, tracker.GetElapsedSeconds("rtx")); // slot released after the abort
        runCts.Cancel();
    }

    // ── Fix #6 (MEDIUM): an eviction must not orphan an in-flight warm turn's lease ──

    [Fact]
    public async Task Evict_After_Warm_Take_Does_Not_Orphan_The_InFlight_Turns_Lease()
    {
        var cfg = Config();
        var engine = new FakeEngineRpcClient();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var ledger = new SessionLedger();
        var proxy = new FakeCompletionProxy();
        var store = new StoreGateway(new FakeStoreClient());
        var health = new FakeHealthMonitor();
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine, ["p100"] = engine });
        var leases = new LeaseManager(tracker);
        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, ledger, cfg.Workers, tracker, health, cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engineGateway, proxy),
            new PrefixRestoreRunner(cfg, store, engineGateway, ledger),
            new SaveKvRunner(store, ledger, engineGateway, cfg),
            new RestoreRunner(store, engineGateway, ledger, leases, proxy, cfg),
            new DecodeRunner(proxy, engineGateway, ledger, cfg, health),
            new BgSaveRunner(engineGateway, store, ledger),
        };
        var scheduler = new WorkerSchedulerV2(cfg, ledger, tracker, health,
            new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
            engineGateway, store, proxy);
        using var runCts = new CancellationTokenSource();
        _ = scheduler.RunAsync(runCts.Token);
        await Task.Delay(50);

        // Turn 1: two-phase P/D → session registered on p100 (warm-routable,
        // SlotFreed=false) with the decode slot stashed warm on p100.
        await scheduler.SubmitAsync(Req(), Msgs(5000), "sess_a", 5000, 30, null, CancellationToken.None);
        Assert.Equal(1, scheduler.WarmLeaseCount);
        Assert.True(ledger.Lookup("sess_a") is { SlotFreed: false });

        // Simulate an in-flight warm turn taking the lease, then the eviction fires:
        // it must NOT MarkEvicted (the turn owns the slot) so the turn's later
        // re-stash stays consistent with the ledger (no orphaned warm lease).
        var inFlight = leases.TakeWarm("sess_a"); // in-flight warm turn took the lease
        Assert.NotNull(inFlight);
        await scheduler.EvictWarmSessionAsync("sess_a", "p100", CancellationToken.None);
        Assert.Equal(0, scheduler.WarmLeaseCount);
        // Re-stash by the in-flight turn keeps the warm lease; the ledger entry must
        // NOT have been marked evicted (that would orphan the re-stash).
        leases.Stash("sess_a", inFlight!);
        Assert.Equal(1, scheduler.WarmLeaseCount);
        Assert.True(ledger.Lookup("sess_a") is { SlotFreed: false }, "in-flight turn's re-stash must not be orphaned by the eviction");
        runCts.Cancel();
    }

    // ── Review #8: warm-slot verification failure re-routes COLD (golden
    //    warm_affinity_verify_on) instead of decoding over a dead slot ──

    [Fact]
    public async Task WarmVerification_Failure_ReRoutes_Cold_Instead_Of_Dead_Slot()
    {
        var cfg = Config(warmVerify: true);
        var engine = new FakeEngineRpcClient();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var ledger = new SessionLedger();
        var proxy = new FakeCompletionProxy();
        var store = new StoreGateway(new FakeStoreClient());
        var health = new FakeHealthMonitor();
        var engineGateway = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient> { ["rtx"] = engine, ["p100"] = engine });
        var leases = new LeaseManager(tracker);
        var warmVerifier = new FakeWarmSlotVerifier { Result = false }; // the warm slot is NOT verified
        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, ledger, cfg.Workers, tracker, health, cfg, warmVerifier),
            new PrefillRunner(engineGateway, proxy),
            new PrefixRestoreRunner(cfg, store, engineGateway, ledger),
            new SaveKvRunner(store, ledger, engineGateway, cfg),
            new RestoreRunner(store, engineGateway, ledger, leases, proxy, cfg),
            new DecodeRunner(proxy, engineGateway, ledger, cfg, health),
            new BgSaveRunner(engineGateway, store, ledger),
        };
        var scheduler = new WorkerSchedulerV2(cfg, ledger, tracker, health,
            new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
            engineGateway, store, proxy);
        using var runCts = new CancellationTokenSource();
        _ = scheduler.RunAsync(runCts.Token);
        await Task.Delay(50);

        // Turn 1: two-phase P/D → session registered on p100 (warm) + warm lease stashed.
        await scheduler.SubmitAsync(Req(), Msgs(5000), "sess_v", 5000, 30, null, CancellationToken.None);
        Assert.Equal("p100", scheduler.LastDispatchedNode);
        Assert.Equal(1, scheduler.WarmLeaseCount);
        proxy.NonStreamingUrls.Clear();

        // Turn 2: warm Solo — verification FAILS → evict + re-route COLD on rtx.
        await scheduler.SubmitAsync(Req(), Msgs(100), "sess_v", 100, 30, null, CancellationToken.None);

        Assert.Equal("rtx", scheduler.LastDispatchedNode); // cold re-route, not the dead warm node
        Assert.Equal("http://localhost:8080", Assert.Single(proxy.NonStreamingUrls));
        runCts.Cancel();
    }
}
