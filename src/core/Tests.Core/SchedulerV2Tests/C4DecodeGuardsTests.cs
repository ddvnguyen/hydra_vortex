using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Hydra.Shared;
using Tests.Core.TestHelpers;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// C4 DECODE + GUARDS (epic #591): pins the v2 merged-decode (0x43) path with
/// Gate A (accept / reject / transport-fault fallback), the #279 NotImplemented →
/// HTTP-prefill fallback, and the cross-model StatePut guard that erases the slot
/// and re-prefills on the correct model. These mirror the harness goldens
/// <c>merged_decode_accept</c>, <c>merged_decode_gate_a_reject</c>,
/// <c>not_implemented_279</c> and <c>state_put_mismatch</c>.
/// </summary>
public sealed class C4DecodeGuardsTests
{
    private readonly CoordinatorConfig _cfg = new()
    {
        RunMode = "concurrency",
        UseLlamaEngine = true, // merged-decode gate requires engine mode
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
    private FakeHealthMonitor _health = null!;
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
        _health = health ?? new FakeHealthMonitor();

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
            new PlanRunner(new RoutePlanner(), leases, _ledger, _cfg.Workers, _tracker, _health, _cfg, new FakeWarmSlotVerifier()),
            new PrefillRunner(engine, _proxy),
            new PrefixRestoreRunner(_cfg, store, engine, _ledger),
            new SaveKvRunner(store, _ledger, engine),
            new RestoreRunner(store, engine, _ledger, leases, _proxy, _cfg),
            new DecodeRunner(_proxy, engine, _ledger, _cfg, _health),
            new BgSaveRunner(engine, store, _ledger),
        };

        _scheduler = new WorkerSchedulerV2(
            _cfg, _ledger, _tracker, _health,
            new RequestClassifier(), new RoutePlanner(), new LeaseManager(_tracker),
            runners, new TimelineEmitter(), engine, store, _proxy);

        _runCts = new CancellationTokenSource();
        _ = _scheduler.RunAsync(_runCts.Token);
        await Task.Delay(50); // let the admission mailbox start
    }

    private async Task<object?> Submit(bool stream = false, int estimatedTokens = 100, int maxTokens = 30, string sessionId = "sess_c4")
    {
        var req = new Dictionary<string, object> { ["stream"] = stream, ["max_tokens"] = maxTokens, ["model"] = "nano" };
        var msgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) } };
        var submit = _scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens, maxTokens, prefixHash: null, _runCts.Token, systemPromptTokens: 0);

        if (stream)
        {
            var raw = CompletionResults.Unwrap(await submit.WaitAsync(TimeSpan.FromSeconds(30)));
            var enumerable = (IAsyncEnumerable<byte[]>)raw!;
            await foreach (var _ in enumerable.WithCancellation(_runCts.Token)) { }
            await _scheduler.NotifyStreamComplete(sessionId);
            return raw;
        }

        return CompletionResults.Unwrap(await submit.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private async Task TearDown()
    {
        _runCts.Cancel();
        _runCts.Dispose();
    }

    // ── 1. Merged decode (0x43) — Gate A accept ─────────────────────────────

    [Fact]
    public async Task Merged_Decode_Accept_Completes_Done_With_EngineDecode_Wire()
    {
        _health = new FakeHealthMonitor { EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Protocol.CapMergedDecode } };
        await Setup(_health);

        var result = await Submit(estimatedTokens: 500, sessionId: "sess_accept");

        Assert.NotNull(result);
        Assert.Equal(1, _engine.MergedDecodeCalls); // the framed DECODE 0x43 was issued
        Assert.Contains(_engine.Calls, c => c.Op == OpCode.EngineDecode); // wire: the 0x43 opcode
        // Gate A accepted → the HTTP proxy decode is NOT called (merged path polls /v1/decode).
        Assert.Empty(_proxy.NonStreamingUrls);
        // Merged path went through PollDecodeResultAsync (usage.total_tokens=150).
        var entry = _ledger.Lookup("sess_accept");
        Assert.NotNull(entry);
        Assert.Equal(150, entry.NPast);
        Assert.Equal("rtx", _scheduler.LastDispatchedNode);
        await TearDown();
    }

    // ── 2. Merged decode — Gate A reject (Valid=false) aborts, no HTTP fallback ──

    [Fact]
    public async Task Merged_Decode_GateA_Reject_Fails_Without_Http_Fallback()
    {
        _health = new FakeHealthMonitor { EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Protocol.CapMergedDecode } };
        await Setup(_health);
        _engine.RejectMergedDecode = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Submit(estimatedTokens: 500, sessionId: "sess_reject"));

        // #470: decoding over an empty/corrupt slot is the #469 hallucination —
        // the request aborts with a clear error and MUST NOT fall back to HTTP.
        Assert.Contains("rejected", ex.Message);
        Assert.Contains("KV not restored", ex.Message);
        Assert.Equal(1, _engine.MergedDecodeCalls);
        Assert.Empty(_proxy.NonStreamingUrls);
        // No slot held after the failure (atomic failure releases + marks evicted).
        Assert.Equal(0, _scheduler.WarmLeaseCount);
        Assert.True(_ledger.Lookup("sess_reject")?.SlotFreed == true);
        await TearDown();
    }

    // ── 3. Merged decode — transport fault falls back to the HTTP proxy ──

    [Fact]
    public async Task Merged_Decode_Transport_Fault_Falls_Back_To_Http_Proxy()
    {
        _health = new FakeHealthMonitor { EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Protocol.CapMergedDecode } };
        await Setup(_health);
        _engine.MergedDecodeThrows = true;

        var result = await Submit(estimatedTokens: 500, sessionId: "sess_fault");

        // The RPC threw (transport fault) → the request completes via the HTTP proxy decode.
        Assert.NotNull(result);
        Assert.Equal(1, _engine.MergedDecodeCalls);
        Assert.Equal("http://localhost:8080", Assert.Single(_proxy.NonStreamingUrls));
        Assert.Equal("rtx", _scheduler.LastDispatchedNode);
        await TearDown();
    }

    // ── 4. Merged decode — streaming polls the decode stream and completes ──

    [Fact]
    public async Task Merged_Decode_Streaming_Polls_And_Completes()
    {
        _health = new FakeHealthMonitor { EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Protocol.CapMergedDecode } };
        await Setup(_health);

        // Submit without consuming: the stream is handed to the caller and the slot
        // stays held until NotifyStreamComplete.
        var req = new Dictionary<string, object> { ["stream"] = true, ["max_tokens"] = 30, ["model"] = "nano" };
        var msgs = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = new string('x', 500) } };
        var submit = _scheduler.SubmitAsync(req, msgs, "sess_stream", estimatedTokens: 500, maxTokens: 30, prefixHash: null, _runCts.Token, systemPromptTokens: 0);
        var stream = CompletionResults.Unwrap(await submit.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.NotNull(stream);
        Assert.IsAssignableFrom<IAsyncEnumerable<byte[]>>(stream);
        Assert.Equal(1, _engine.MergedDecodeCalls);
        Assert.Empty(_proxy.NonStreamingUrls); // merged streaming → PollDecodeStreamAsync, not the HTTP proxy
        Assert.True(_tracker.GetElapsedSeconds("rtx") > 0, "decode slot must be held while streaming");

        await foreach (var _ in ((IAsyncEnumerable<byte[]>)stream).WithCancellation(_runCts.Token)) { }
        await _scheduler.NotifyStreamComplete("sess_stream");
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx")); // released after stream teardown
        await TearDown();
    }

    // ── 5. #279: EnginePrefill NotImplemented → HTTP prefill fallback (n_predict=0) ──

    [Fact]
    public async Task Prefill_NotImplemented_Falls_Back_To_Http_Prefill_And_Completes()
    {
        await Setup();
        _engine.MakePrefillNotImplemented = true;

        var result = await Submit(estimatedTokens: 5000, sessionId: "sess_279"); // two-phase P/D

        Assert.NotNull(result);
        // The fallback prefill is an n_predict=0 completion on the prefill worker.
        Assert.Equal(0, _proxy.NonStreamingBodies[0]["n_predict"]);
        Assert.Equal(false, _proxy.NonStreamingBodies[0]["stream"]);
        Assert.Equal("http://localhost:8080", _proxy.NonStreamingUrls[0]); // prefill worker first
        // Then the decode ran on the dedicated decoder (p100) via the HTTP proxy.
        Assert.Equal("http://p100:8086", _proxy.NonStreamingUrls[^1]);
        Assert.Equal("p100", _scheduler.LastDispatchedNode);
        // SaveKv captured the slot KV via StateGet (no engine KV blob from the #279 path).
        Assert.Contains(_engine.Calls, c => c.Op == OpCode.StateGet);
        await TearDown();
    }

    // ── 6. Cross-model guard: StatePut model_match=false → erase + re-prefill ──

    [Fact]
    public async Task StatePut_Mismatch_Erases_And_Reprefills_Then_Completes()
    {
        await Setup();
        _engine.MakeStatePutMismatch = true;

        var result = await Submit(estimatedTokens: 5000, sessionId: "sess_xmodel"); // two-phase P/D

        Assert.NotNull(result);
        // The request re-prefilled on the correct model (EnginePrefill ran twice) then completed.
        Assert.Equal(2, _engine.Calls.Count(c => c.Op == OpCode.EnginePrefill));
        // The corrupt decode slot was erased (HTTP erase on the decode worker).
        Assert.Equal(("http://p100:8086", 0), Assert.Single(_proxy.EraseCalls));
        // The session ended on the decode node (p100) with the warm lease stashed.
        Assert.Equal("p100", _ledger.Lookup("sess_xmodel")?.NodeName);
        Assert.Equal("p100", _scheduler.LastDispatchedNode);
        Assert.Equal(1, _scheduler.WarmLeaseCount);
        // v2 does NOT reproduce the documented legacy slot leak: rtx is fully released.
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx"));
        await TearDown();
    }
}
