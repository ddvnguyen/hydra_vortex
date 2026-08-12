using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Tests.Core.TestHelpers;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// End-to-end v2 pipeline tests against hermetic fakes (no sockets): a cold
/// atomic request must flow Route → Prefill → SaveKv → Restore → Decode → BgSave →
/// Done and release its slot lease. These pin the v2 concurrency invariant:
/// <i>a running request owns a slot; after terminal state no slot is held</i>.
/// </summary>
public sealed class WorkerSchedulerV2Tests
{
    private readonly CoordinatorConfig _cfg = new()
    {
        RunMode = "concurrency",
        AtomicThreshold = 2048,
        LlamaRequestTimeoutS = 30, // fail fast instead of the 1800s default on a broken path
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

    private async Task Setup(FakeHealthMonitor? health = null, FakeStoreClient? storeClient = null)
    {
        _engine = new FakeEngineRpcClient();
        _tracker = new WorkerTracker();
        _ledger = new SessionLedger();
        foreach (var w in _cfg.Workers) _tracker.InitWorker(w.Name, w.Slots);
        health ??= new FakeHealthMonitor();

        storeClient ??= new FakeStoreClient();
        var store = new StoreGateway(storeClient);
        var engine = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient>
        {
            ["rtx"] = _engine,
            ["p100"] = _engine,
        });
        _proxy = new FakeCompletionProxy();
        var leases = new LeaseManager(_tracker);

        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), leases, _ledger, _cfg.Workers, _tracker, health, _cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engine, _proxy, _cfg.Workers),
            new PrefixRestoreRunner(_cfg, store, engine, _ledger),
            new SaveKvRunner(store, _ledger, engine, _cfg),
            new RestoreRunner(store, engine, _ledger, leases, _proxy, _cfg),
            new DecodeRunner(_proxy, engine, _ledger, _cfg, health),
            new BgSaveRunner(engine, store, _ledger),
        };

        _scheduler = new WorkerSchedulerV2(
            _cfg, _ledger, _tracker, health,
            new RequestClassifier(), new RoutePlanner(), new LeaseManager(_tracker),
            runners, new TimelineEmitter(), engine, store, _proxy);

        _runCts = new CancellationTokenSource();
        _ = _scheduler.RunAsync(_runCts.Token);
        await Task.Delay(50); // let the admission mailbox start
    }

    private async Task<object> Submit(bool stream = false)
    {
        var req = new Dictionary<string, object> { ["stream"] = stream, ["max_tokens"] = 30, ["model"] = "nano" };
        var msgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hello" } };
        var result = await _scheduler.SubmitAsync(req, msgs, "sess_v2", estimatedTokens: 100, maxTokens: 30, prefixHash: null, CancellationToken.None, systemPromptTokens: 0);
        return CompletionResults.Unwrap(result)!;
    }

    [Fact]
    public async Task Cold_Atomic_Completes_End_To_End()
    {
        await Setup();

        var result = await Submit();

        Assert.NotNull(result);
        Assert.Contains(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.EnginePrefill);
        Assert.Contains(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.EngineConfigure); // lazy + decode-time 0x40
        Assert.DoesNotContain(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.StatePut);  // same-node: no restore
        Assert.Equal("rtx", _scheduler.LastDispatchedNode);

        // C2: the decode slot stays WARM for the session (stashed, not released).
        Assert.Equal(1, _scheduler.WarmLeaseCount);
        Assert.True(_tracker.GetElapsedSeconds("rtx") > 0, "warm lease must hold the rtx slot");

        // Eviction releases it.
        await _scheduler.EvictWarmSessionAsync("sess_v2", "rtx", CancellationToken.None);
        Assert.Equal(0, _scheduler.WarmLeaseCount);
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx"));
        _runCts.Cancel();
    }

    [Fact]
    public async Task Engine_Fault_Fails_After_Bounded_Retries()
    {
        await Setup();
        _engine.FailPrefill = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Submit());
        Assert.Contains("simulated engine prefill fault", ex.Message);
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx")); // lease released even on failure
        _runCts.Cancel();
    }

    [Fact]
    public async Task Streaming_Suspends_Then_Completes_On_NotifyStreamComplete()
    {
        await Setup();

        var stream = await Submit(stream: true);
        Assert.NotNull(stream);
        Assert.True(stream is IAsyncEnumerable<byte[]>);
        // While streaming, the slot must stay held (decode in flight).
        Assert.True(_tracker.GetElapsedSeconds("rtx") > 0, "decode slot must be held during streaming");

        await _scheduler.NotifyStreamComplete("sess_v2");

        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx")); // released after stream teardown
        _runCts.Cancel();
    }

    [Fact]
    public async Task SubmitAsync_Returns_Typed_CompletionResult_Not_Object()
    {
        await Setup();

        var req = new Dictionary<string, object> { ["stream"] = false, ["max_tokens"] = 30, ["model"] = "nano" };
        var msgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hello" } };
        var result = await _scheduler.SubmitAsync(req, msgs, "sess_typed", estimatedTokens: 100, maxTokens: 30, prefixHash: null, CancellationToken.None, systemPromptTokens: 0);

        Assert.IsType<FinalCompletionResult>(result);
        Assert.Equal(CompletionResultKind.Final, result.Kind);
        _runCts.Cancel();
    }

    [Fact]
    public async Task Prefill_Two_Phase_Splits_Prefill_And_Decode_Across_Workers()
    {
        await Setup();

        // estimatedTokens >= AtomicThreshold → two-phase (Prefill) request.
        var req = new Dictionary<string, object> { ["stream"] = false, ["max_tokens"] = 30, ["model"] = "nano" };
        var msgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = new string('x', 5000) } };
        var result = CompletionResults.Unwrap(await _scheduler.SubmitAsync(
            req, msgs, "sess_pd", estimatedTokens: 5000, maxTokens: 30, prefixHash: null,
            CancellationToken.None, systemPromptTokens: 0));

        Assert.NotNull(result);
        Assert.Contains(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.EnginePrefill);

        // GPU-utilization rule: decode ran on the dedicated decoder (p100), NOT
        // the prefill worker (rtx) — the decode worker was picked at decode time.
        Assert.Equal("http://p100:8086", Assert.Single(_proxy.NonStreamingUrls));
        Assert.Equal("p100", _scheduler.LastDispatchedNode);

        // GPU-utilization rule: prefill slot freed at the handoff; the DECODE slot
        // stays WARM for the session (C2) until evicted.
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx"));
        Assert.Equal(1, _scheduler.WarmLeaseCount);
        Assert.True(_tracker.GetElapsedSeconds("p100") > 0, "warm decode lease must hold p100");

        await _scheduler.EvictWarmSessionAsync("sess_pd", "p100", CancellationToken.None);
        Assert.Equal(0d, _tracker.GetElapsedSeconds("p100"));
        _runCts.Cancel();
    }

    [Fact]
    public async Task Cold_Atomic_Registers_Ledger_On_The_Decode_Node()
    {
        await Setup();
        await Submit();

        var entry = _ledger.Lookup("sess_v2");
        Assert.NotNull(entry);
        Assert.Equal("rtx", entry.NodeName);        // atomic: decode on the same node
        Assert.Equal(0, entry.SlotId);
        Assert.True(entry.HasStoreState);
        Assert.Equal(15, entry.NPast);              // usage.total_tokens, NOT the prefill n_past
        _runCts.Cancel();
    }

    [Fact]
    public async Task Prefill_Two_Phase_Registers_Ledger_On_The_Decode_Node()
    {
        await Setup();

        // estimatedTokens >= AtomicThreshold → two-phase (Prefill) request.
        var req = new Dictionary<string, object> { ["stream"] = false, ["max_tokens"] = 30, ["model"] = "nano" };
        var msgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = new string('x', 5000) } };
        await _scheduler.SubmitAsync(
            req, msgs, "sess_pd", estimatedTokens: 5000, maxTokens: 30, prefixHash: null,
            CancellationToken.None, systemPromptTokens: 0);

        // C1 point 2: RestoreKv RE-REGISTERS the session on the decode node (p100) —
        // this is what the P/D goldens pin (Ledger.NodeName = decode node).
        var entry = _ledger.Lookup("sess_pd");
        Assert.NotNull(entry);
        Assert.Equal("p100", entry.NodeName);
        Assert.Equal(0, entry.SlotId);
        Assert.True(entry.HasStoreState);
        Assert.Equal(15, entry.NPast);              // usage.total_tokens
        _runCts.Cancel();
    }

    [Fact]
    public async Task Warm_Affinity_Followup_Decodes_On_The_Warm_Node()
    {
        await Setup();

        // Turn 1: two-phase P/D → session registered on p100 (the decode node).
        var req = new Dictionary<string, object> { ["stream"] = false, ["max_tokens"] = 30, ["model"] = "nano" };
        var msgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = new string('x', 5000) } };
        await _scheduler.SubmitAsync(req, msgs, "sess_warm", estimatedTokens: 5000, maxTokens: 30, prefixHash: null, CancellationToken.None, systemPromptTokens: 0);
        Assert.Equal("p100", _ledger.Lookup("sess_warm")?.NodeName);

        _proxy.NonStreamingUrls.Clear();

        // Turn 2: small follow-up on the same session → Solo (warm affinity) on p100.
        var followup = new Dictionary<string, object> { ["stream"] = false, ["max_tokens"] = 30, ["model"] = "nano" };
        var followupMsgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } };
        await _scheduler.SubmitAsync(followup, followupMsgs, "sess_warm", estimatedTokens: 100, maxTokens: 30, prefixHash: null, CancellationToken.None, systemPromptTokens: 0);

        // Warm affinity: decode on the session's node (p100), not rtx.
        Assert.Equal("http://p100:8086", Assert.Single(_proxy.NonStreamingUrls));
        Assert.Equal("p100", _scheduler.LastDispatchedNode);

        // C2: the warm slot was REUSED — still exactly one stashed lease (not two).
        Assert.Equal(1, _scheduler.WarmLeaseCount);
        _runCts.Cancel();
    }

    [Fact]
    public async Task Cold_Atomic_BgSaves_The_PostDecode_Kv()
    {
        await Setup();
        await Submit();

        // C3: after decode, the slot's FINAL KV is captured (StateGet) and persisted
        // (Put {session}.kv) — the BgSave, while the slot is still held.
        Assert.Contains(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.StateGet);
        Assert.Contains(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.EngineConfigure); // decode-time 0x40
        Assert.DoesNotContain(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.StatePut);  // same-node: no restore round-trip
        _runCts.Cancel();
    }

    [Fact]
    public async Task Store_Down_Falls_Back_To_SameNode_Decode_And_Completes()
    {
        // Golden store_exception: Store Put throws during SaveKv → fall back to
        // same-node decode (KV stays in the prefill slot); the BgSave Put failure
        // is swallowed — the request MUST complete.
        var storeClient = new FakeStoreClient();
        storeClient.SetException(Hydra.Shared.OpCode.Put, new IOException("store: tmpfs write failed"));
        await Setup(storeClient: storeClient);

        var result = await Submit();

        Assert.NotNull(result);
        Assert.Equal("rtx", _scheduler.LastDispatchedNode); // same-node decode fallback
        _runCts.Cancel();
    }
}
