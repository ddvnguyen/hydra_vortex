using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Hydra.Shared;
using Tests.Core.TestHelpers;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// C4 RESILIENCE (epic #591): pins the v2 resilience behaviors:
/// <list type="bullet">
/// <item><b>Decode-handoff no-capacity fallback</b> — when no decode slot can be
/// acquired at PickDecode, the request decodes ON THE PREFILL NODE instead of
/// failing (the KV stays in the prefill slot; the prefill lease is re-kept).</item>
/// <item><b>Retry re-route</b> — a Prefill retry re-entering RouteDecision re-plans
/// against live capacity and escapes a failing worker (release the stale lease,
/// acquire the alternate prefill slot).</item>
/// <item><b>Per-turn streaming keys + reaper</b> — the streaming map is keyed by
/// TraceId (two concurrent streaming turns on one session no longer overwrite each
/// other); a missed <c>NotifyStreamComplete</c> is reaped: the turn is finalized
/// (Cancelled) and its lease released.</item>
/// <item><b>ReuseStoreState consume</b> — a cold route whose session has durable
/// store KV skips the engine prefill: Route → RestoreKv restores the stored KV
/// directly (legacy migration semantics).</item>
/// </list>
/// </summary>
public sealed class C4ResilienceTests
{
    private readonly CoordinatorConfig _cfg = new()
    {
        RunMode = "concurrency",
        AtomicThreshold = 2048,
        LlamaRequestTimeoutS = 30,
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },
            new() { Name = "p100", LlamaUrl = "http://p100:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
        },
    };

    private FakeEngineRpcClient _engine = null!;
    private FakeCompletionProxy _proxy = null!;
    private WorkerTracker _tracker = null!;
    private SessionLedger _ledger = null!;
    private WorkerSchedulerV2 _scheduler = null!;
    private CancellationTokenSource _runCts = null!;

    private async Task Setup(
        FakeHealthMonitor? health = null,
        FakeStoreClient? storeClient = null,
        IReadOnlyList<WorkerConfig>? workers = null,
        IReadOnlyDictionary<string, IEngineRpcClient>? channels = null)
    {
        var workerList = workers ?? _cfg.Workers;
        var cfg = new CoordinatorConfig
        {
            RunMode = _cfg.RunMode,
            AtomicThreshold = _cfg.AtomicThreshold,
            LlamaRequestTimeoutS = _cfg.LlamaRequestTimeoutS,
            Workers = workerList.ToList(),
        };
        _engine = new FakeEngineRpcClient();
        _tracker = new WorkerTracker();
        _ledger = new SessionLedger();
        foreach (var w in workerList) _tracker.InitWorker(w.Name, w.Slots);
        health ??= new FakeHealthMonitor();

        storeClient ??= new FakeStoreClient();
        var store = new StoreGateway(storeClient);
        var engine = new EngineRpcGateway(channels ?? new Dictionary<string, IEngineRpcClient>
        {
            ["rtx"] = _engine,
            ["p100"] = _engine,
        });
        _proxy = new FakeCompletionProxy();
        var leases = new LeaseManager(_tracker);

        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, _ledger, workerList, _tracker, health, cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engine, _proxy),
            new PrefixRestoreRunner(cfg, store, engine, _ledger),
            new SaveKvRunner(store, _ledger, engine),
            new RestoreRunner(store, engine, _ledger, leases, _proxy, cfg),
            new DecodeRunner(_proxy, engine, _ledger, cfg, health),
            new BgSaveRunner(engine, store, _ledger),
        };

        _scheduler = new WorkerSchedulerV2(
            cfg, _ledger, _tracker, health,
            new RequestClassifier(), new RoutePlanner(), new LeaseManager(_tracker),
            runners, new TimelineEmitter(), engine, store, _proxy);

        _runCts = new CancellationTokenSource();
        _ = _scheduler.RunAsync(_runCts.Token);
        await Task.Delay(50); // let the admission mailbox start
    }

    private async Task<object?> Submit(
        bool stream = false,
        int estimatedTokens = 100,
        string sessionId = "sess_res",
        string? traceId = null,
        Dictionary<string, object>? extraBody = null,
        IReadOnlyList<WorkerConfig>? workerList = null)
    {
        var body = new Dictionary<string, object> { ["stream"] = stream, ["max_tokens"] = 30, ["model"] = "nano" };
        if (traceId is not null) body["trace_id"] = traceId;
        if (extraBody is not null) foreach (var (k, v) in extraBody) body[k] = v;
        var msgs = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) },
        };
        var submit = _scheduler.SubmitAsync(body, msgs, sessionId, estimatedTokens, 30,
            prefixHash: null, _runCts.Token, systemPromptTokens: 0);

        if (stream)
        {
            var raw = CompletionResults.Unwrap(await submit.WaitAsync(TimeSpan.FromSeconds(30)));
            return raw;
        }

        return CompletionResults.Unwrap(await submit.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>The suspended-stream registration lands in RunPipelineAsync's
    /// finally — a few instructions after StreamReady resolves — so polls until
    /// the map reaches the expected size (deterministic without sleeps).</summary>
    private async Task WaitForStreamingCount(int count)
    {
        for (var i = 0; i < 200 && _scheduler.StreamingRequests.Count < count; i++)
            await Task.Delay(10);
        Assert.True(_scheduler.StreamingRequests.Count >= count,
            $"expected {count} streaming request(s), got {_scheduler.StreamingRequests.Count}");
    }

    // ── 1. Decode-handoff no-capacity fallback (full pipeline) ──────────────

    [Fact]
    public async Task PickDecode_NoCapacity_FallsBack_To_Prefill_Node_And_Completes()
    {
        // rtx has exactly ONE slot so its held prefill lease exhausts it at
        // PickDecode; p100's only slot is pinned by another session's warm stash.
        var workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 1, PrefillPriority = 1, DecodePriority = 2 },
            new() { Name = "p100", LlamaUrl = "http://p100:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
        };
        await Setup(workers: workers);

        // Turn 1: P/D on sess_warm → decode on p100 → p100's slot goes WARM.
        await Submit(estimatedTokens: 5000, sessionId: "sess_warm");
        Assert.Equal("p100", _ledger.Lookup("sess_warm")?.NodeName);

        _proxy.NonStreamingUrls.Clear();

        // Turn 2: P/D on another session. At PickDecode rtx is busy (prefill lease
        // held, 1/1) and p100 is busy (warm stash) → no decode worker → FALL BACK
        // to decoding on the prefill node (rtx). The request must NOT fail.
        var result = await Submit(estimatedTokens: 5000, sessionId: "sess_fb");
        Assert.NotNull(result);
        Assert.Equal("http://localhost:8080", Assert.Single(_proxy.NonStreamingUrls)); // rtx, the prefill node
        Assert.Equal("rtx", _scheduler.LastDispatchedNode);
        // The prefill node's lease was re-kept and is stashed warm after Done.
        Assert.Equal(2, _scheduler.WarmLeaseCount); // sess_warm p100 + sess_fb rtx
        Assert.Equal(0, _scheduler.StreamingRequests.Count);
        _runCts.Cancel();
    }

    // ── 2. Decode-handoff fallback: TryAcquire returns null after the release ──

    /// <summary>PlanDecode returns a worker whose slot is NOT free — simulates the
    /// slot vanishing between planning and acquisition (the TryAcquire-null
    /// trigger the full pipeline can only hit under a race).</summary>
    private sealed class StalePlanDecodePlanner : IRoutePlanner
    {
        public RouteDecision Plan(ChatRequest chat, RequestType type, IReadOnlyList<WorkerConfig> workers,
            IWorkerTracker tracker, IHealthMonitorService health, ISessionLedger ledger)
            => new(type, PrefillWorker: "rtx", DecodeWorker: null, ReuseStoreState: false, Priority: 40);

        public string? PlanDecode(ChatRequest chat, SessionEntry? session, IReadOnlyList<WorkerConfig> workers,
            IWorkerTracker tracker, IHealthMonitorService health)
            => "p100"; // stale: p100's only slot is held by another request
    }

    [Fact]
    public async Task PickDecode_TryAcquireNull_ReKeeps_Prefill_Lease_And_Decodes_In_Place()
    {
        var workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 1, PrefillPriority = 1, DecodePriority = 2 },
            new() { Name = "p100", LlamaUrl = "http://p100:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
        };
        var tracker = new WorkerTracker();
        foreach (var w in workers) tracker.InitWorker(w.Name, w.Slots);
        var health = new FakeHealthMonitor();
        var ledger = new SessionLedger();
        var leases = new LeaseManager(tracker);

        // The request holds the rtx PREFILL lease (1/1); p100's slot is held by
        // another request (e.g. a warm stash) — so TryAcquire("p100") must fail.
        Assert.True(tracker.TryAcquireSlot("rtx", out var prefillSlot, "prefill"));
        Assert.True(tracker.TryAcquireSlot("p100", out _, "other-request"));
        var req = new SchedulerRequest(
            new ChatRequest("sess_try", "trace_try", "nano", false, 30, 5000, 30, 0, null, "",
                new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "x" } },
                new Dictionary<string, object> { ["model"] = "nano" }),
            RequestType.Prefill, 40)
        {
            State = WorkItemState.PickDecode,
            PrefillWorker = workers[0],
            PrefillLease = new SlotLease("rtx", prefillSlot, "sess_try", LeaseLifetime.Long, tracker),
        };

        var runner = new PlanRunner(new StalePlanDecodePlanner(), leases, ledger, workers, tracker, health, new CoordinatorConfig(), new FakeWarmSlotVerifier());
        var result = await runner.RunAsync(new RunnerContext(req, "rtx"), CancellationToken.None);

        // The request survives: decode on the PREFILL node with the lease re-kept.
        Assert.Equal(PhaseOutcome.Fire, result.Outcome);
        Assert.Equal(SchedulerEvent.DecodePicked, result.Event);
        Assert.Equal("rtx", req.DecodeWorker?.Name);          // fallback to the prefill node
        Assert.Null(req.DecodeLease);                          // no decode swap
        Assert.NotNull(req.PrefillLease);                      // prefill lease re-kept (not orphaned)
        Assert.Equal(0, tracker.FreeSlotCount("p100"));        // the other request's claim untouched
        Assert.True(tracker.GetElapsedSeconds("rtx") > 0, "the re-kept prefill lease must still hold the slot");
        Assert.Equal("rtx", req.PrefillLease?.WorkerName);
    }

    // ── 3. Retry re-route: prefill fails once, retry re-plans to an alternate ──

    [Fact]
    public async Task Prefill_Retry_RePlans_And_Routes_To_A_Healthy_Alternate_Prefill_Worker()
    {
        // rtx: 1 slot (its held lease exhausts it at re-plan time → the planner
        // must pick the alternate). a100: a healthy prefill+decode worker with
        // worse priority — the re-plan target. p100: the dedicated decoder.
        var workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 1, PrefillPriority = 1, DecodePriority = 2 },
            new() { Name = "a100", LlamaUrl = "http://a100:8081", WorkerType = 3, Slots = 1, PrefillPriority = 100, DecodePriority = 3 },
            new() { Name = "p100", LlamaUrl = "http://p100:8086", WorkerType = 2, Slots = 1, PrefillPriority = 1000, DecodePriority = 1 },
        };
        var rtxEngine = new FakeEngineRpcClient { FailPrefillOnce = true }; // fails the FIRST prefill only
        var altEngine = new FakeEngineRpcClient();
        await Setup(workers: workers, channels: new Dictionary<string, IEngineRpcClient>
        {
            ["rtx"] = rtxEngine,
            ["a100"] = altEngine,
            ["p100"] = altEngine,
        });

        var result = await Submit(estimatedTokens: 5000, sessionId: "sess_retry"); // P/D

        Assert.NotNull(result);
        // The failed attempt ran on rtx exactly once; the retry re-prefilled on a100.
        Assert.Equal(1, rtxEngine.Calls.Count(c => c.Op == OpCode.EnginePrefill));
        Assert.Contains(altEngine.Calls, c => c.Op == OpCode.EnginePrefill);
        // The stale rtx lease was released (no leak, no same-worker hammering).
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx"));
        // Decode still ran on the dedicated decoder.
        Assert.Equal("http://p100:8086", Assert.Single(_proxy.NonStreamingUrls));
        Assert.Equal("p100", _scheduler.LastDispatchedNode);
        Assert.Equal(1, _scheduler.WarmLeaseCount); // p100 stashed warm
        _runCts.Cancel();
    }

    // ── 4. Streaming reaper ────────────────────────────────────────────────

    [Fact]
    public async Task Streaming_Reaper_Finalizes_And_Releases_When_NotifyStreamComplete_Never_Arrives()
    {
        await Setup();

        var stream = await Submit(stream: true, sessionId: "sess_reap");
        Assert.NotNull(stream);
        await WaitForStreamingCount(1);
        Assert.Equal(1, _tracker.FreeSlotCount("rtx")); // one slot held by the suspended stream

        // Age the handoff beyond the 5-minute timeout and run one reaper pass.
        _scheduler.StreamingRequests.First().StreamStartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6);
        await _scheduler.ReapStreamedRequestsAsync(CancellationToken.None);

        Assert.Empty(_scheduler.StreamingRequests);
        Assert.Equal(2, _tracker.FreeSlotCount("rtx")); // lease released — no orphan
        Assert.True(_ledger.Lookup("sess_reap")?.SlotFreed == true, "reaped stream must mark the session evicted");
        _runCts.Cancel();
    }

    // ── 5. Per-turn streaming keys: two concurrent turns on one session ─────

    [Fact]
    public async Task Concurrent_Streaming_Turns_On_One_Session_Do_Not_Overwrite_Each_Other()
    {
        await Setup();

        // Turn 1 streams on rtx slot 1; turn 2 (same session, warm) streams on
        // rtx slot 2 — the traceId-keyed map keeps BOTH turns alive.
        await Submit(stream: true, sessionId: "sess_multi", traceId: "T1");
        await WaitForStreamingCount(1);
        await Submit(stream: true, sessionId: "sess_multi", traceId: "T2");
        await WaitForStreamingCount(2);

        Assert.Equal(2, _scheduler.StreamingRequests.Count);
        Assert.Equal(0, _tracker.FreeSlotCount("rtx")); // both rtx slots held

        // NotifyStreamComplete(session) resolves the LATEST turn (T2) — the first
        // turn (T1) must stay alive, its lease intact, until the reaper takes it.
        await _scheduler.NotifyStreamComplete("sess_multi");
        var remaining = Assert.Single(_scheduler.StreamingRequests);
        Assert.Equal("T1", remaining.TraceId);
        Assert.Equal(1, _tracker.FreeSlotCount("rtx")); // T2's slot released, T1's still held

        // The orphaned T1 is reaped: lease released, no leak.
        remaining.StreamStartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6);
        await _scheduler.ReapStreamedRequestsAsync(CancellationToken.None);
        Assert.Empty(_scheduler.StreamingRequests);
        Assert.Equal(2, _tracker.FreeSlotCount("rtx"));
        _runCts.Cancel();
    }

    // ── 6. ReuseStoreState consume: store-backed session skips prefill ──────

    [Fact]
    public async Task Store_Backed_Session_Skips_Prefill_And_Restores_Kv_From_Store()
    {
        var storeClient = new FakeStoreClient();
        // The stored KV from turn 1 (restore Get must return a real blob).
        storeClient.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[] { 1, 2, 3, 4, 5 });
        await Setup(storeClient: storeClient);

        // Turn 1: cold atomic — prefills, completes, stashes the slot warm and
        // leaves the session evicted-with-store-state (SlotFreed=true).
        await Submit(sessionId: "sess_reuse");
        var turn1 = _ledger.Lookup("sess_reuse");
        Assert.NotNull(turn1);
        Assert.True(turn1!.HasStoreState);
        Assert.True(turn1.SlotFreed);
        Assert.Equal(1, _engine.Calls.Count(c => c.Op == OpCode.EnginePrefill));

        // Turn 2: same session, cold route (SlotFreed=true) with durable store KV
        // → ReuseStoreState=true → Route → RestoreKv: NO engine prefill, the KV
        // is restored from the store and decoded in place.
        var result = await Submit(sessionId: "sess_reuse");
        Assert.NotNull(result);

        Assert.Equal(1, _engine.Calls.Count(c => c.Op == OpCode.EnginePrefill)); // still just turn 1's
        Assert.Equal(1, _engine.Calls.Count(c => c.Op == OpCode.StatePut));      // the reuse restore
        Assert.Equal(1, storeClient.CallCount(OpCode.Get));                       // the reuse store Get
        Assert.Equal(2, _proxy.NonStreamingUrls.Count);                          // turn1 + turn2 decodes
        Assert.All(_proxy.NonStreamingUrls, u => Assert.Equal("http://localhost:8080", u)); // same-node rtx
        Assert.Equal("rtx", _scheduler.LastDispatchedNode);
        Assert.Equal(1, _scheduler.WarmLeaseCount); // turn 2 re-stashed rtx (turn 1's stash reused)
        _runCts.Cancel();
    }
}
