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

    private async Task Setup(FakeHealthMonitor? health = null)
    {
        _engine = new FakeEngineRpcClient();
        _tracker = new WorkerTracker();
        _ledger = new SessionLedger();
        foreach (var w in _cfg.Workers) _tracker.InitWorker(w.Name, w.Slots);
        health ??= new FakeHealthMonitor();

        var store = new StoreGateway(new FakeStoreClient());
        var engine = new EngineRpcGateway(new Dictionary<string, IEngineRpcClient>
        {
            ["rtx"] = _engine,
            ["p100"] = _engine,
        });
        _proxy = new FakeCompletionProxy();

        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), new LeaseManager(_tracker), _ledger, _cfg.Workers, _tracker, health),
            new PrefillRunner(engine),
            new SaveKvRunner(store),
            new RestoreRunner(store, engine),
            new DecodeRunner(_proxy),
            new BgSaveRunner(),
        };

        _scheduler = new WorkerSchedulerV2(
            _cfg, _ledger, _tracker, health,
            new RequestClassifier(), new RoutePlanner(), new LeaseManager(_tracker),
            runners, new TimelineEmitter());

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
        Assert.Contains(_engine.Calls, c => c.Op == Hydra.Shared.OpCode.StatePut);
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx")); // slot released after terminal
        Assert.Equal("rtx", _scheduler.LastDispatchedNode);
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

        // No slot is held after completion: prefill slot released at the handoff,
        // decode slot released at finalize.
        Assert.Equal(0d, _tracker.GetElapsedSeconds("rtx"));
        Assert.Equal(0d, _tracker.GetElapsedSeconds("p100"));
        _runCts.Cancel();
    }
}
