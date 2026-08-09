using System.Text.Json;
using System.Text.Json.Serialization;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.Integration;

namespace Tests.Core.Harness;

// ═══════════════════════════════════════════════════════════════════════
// Normalized execution-trace model.
//
// Everything time-varying (trace ids, timestamps, warm-lease CreatedAt,
// busy-since instants) is STRIPPED. Everything else (RPC opcode/key/payload
// size, merged-decode calls, HTTP-proxy calls, final state, ledger fields,
// per-worker busy seconds) is recorded in deterministic order so the legacy
// scheduler's golden JSON can be diffed byte-for-byte against a future v2
// scheduler running the same harness.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>One normalized binary-RPC call: (opcode, key, payload byte count, response status).</summary>
internal sealed record TraceRpcCall(string Op, string Key, int Len, string Status);

/// <summary>One normalized framed merged-decode (0x43) call.</summary>
internal sealed record TraceMergedDecode(string SlotKey, string Model, bool Stream);

/// <summary>One normalized HTTP-proxy call (chat completion via llama-server).</summary>
internal sealed record TraceProxyCall(string Url, bool Stream, string? Model, int? MaxTokens, int? NPredict);

/// <summary>Ledger entry snapshot at trace-capture time (strip timestamps).</summary>
internal sealed record TraceLedger(
    string? NodeName, int? SlotId, int NPast, bool HasStoreState, bool SlotFreed);

/// <summary>The full normalized trace for one scenario.</summary>
internal sealed record ScenarioTrace(
    IReadOnlyList<TraceRpcCall> Rpc,
    IReadOnlyList<TraceMergedDecode> MergedDecode,
    IReadOnlyList<TraceProxyCall> Proxy,
    string FinalState,
    TraceLedger? Ledger,
    IReadOnlyDictionary<string, double> BusySeconds);

/// <summary>Serializable wrapper written to / compared against the golden JSON files.</summary>
internal sealed record GoldenTrace(
    string Scenario, string Description, int Version, ScenarioTrace Trace);

/// <summary>Outcome class of a driven request (final WorkItemState family).</summary>
internal enum OutcomeClass
{
    Done,
    Failed,
    Cancelled,
    /// <summary>Done, but the RPC trace shows the request was retried (BUSY re-enqueue).</summary>
    RetriedThenDone,
}

/// <summary>Result of executing one scenario against the legacy scheduler.</summary>
internal sealed record ScenarioRunResult(
    string ScenarioId,
    OutcomeClass Outcome,
    Exception? Error,
    ScenarioTrace Trace,
    int WarmLeaseCount);

/// <summary>Per-scenario options — the knobs the catalog varies to reach each route.</summary>
internal sealed class ScenarioOptions
{
    public string RunMode { get; init; } = "concurrency";
    public bool UseLlamaEngine { get; init; } = true;
    public bool PipelineEnabled { get; init; }
    public bool CombinedEnabled { get; init; }
    public string MultiEnginePolicy { get; init; } = "pipeline";
    public int RtxSlots { get; init; } = 2;
    public int P100Slots { get; init; } = 1;
    public bool PrefixCheckpointEnabled { get; init; }
    public bool WarmSlotVerificationEnabled { get; init; }
    public bool EnableChunks { get; init; }
    public int ChunkSize { get; init; } = 1024 * 1024;
    public bool NoStoreKvRestore { get; init; }
    public int AtomicThreshold { get; init; } = 2048;
    public int WarmThreshold { get; init; } = 5120;
    public bool MixPrecisionEnabled { get; init; }
    /// <summary>LlamaUrl for the rtx worker (verify-on scenarios point this at a dead port so verification deterministically fails).</summary>
    public string RtxLlamaUrl { get; init; } = "http://localhost:8080";
    /// <summary>LlamaUrl for the p100 worker (verify-on scenarios point this at a dead port).</summary>
    public string P100LlamaUrl { get; init; } = "http://192.168.122.21:8086";
    /// <summary>Health monitor: TestHealthMonitor by default; GateATestHealthMonitor for merged-decode scenarios.</summary>
    public Func<IHealthMonitorService>? HealthFactory { get; init; }
    /// <summary>Engine-mode worker topology (head/worker + peer reservation) for multi-engine scenarios.</summary>
    public bool MultiEngineTopology { get; init; }
    /// <summary>Called after the scheduler is constructed with the fake RPC — inject failures / responses.</summary>
    public Action<ScenarioRpcClient>? ConfigureRpc { get; init; }
    /// <summary>LlamaClientFactory override (Gate A scenarios inject a differing identity).</summary>
    public Func<string, LlamaClient>? LlamaClientFactory { get; init; }
    /// <summary>Extra model aliases registered for the scenario (defaults to "nano").</summary>
    public List<string> ModelAliases { get; init; } = new() { "nano" };
    /// <summary>Busy-timeout override — (stuckMs, slowMs). Long for deterministic MaxRetries exhaustion.</summary>
    public (long StuckMs, long SlowMs) BusyTimeout { get; init; } = (60_000, 60_000);
    /// <summary>Start the real evaluator loop (RunAsync). Direct-drive tests set false.</summary>
    public bool StartEvaluator { get; init; } = true;
}

/// <summary>
/// Per-scenario scheduler fixture (pattern: <c>TwoStageQueueDrainTests.Fixture</c> /
/// <c>EngineModeTests.EngineFixture</c>): CoordinatorConfig + SessionLedger +
/// WorkerTracker + TestHealthMonitor + ScenarioRpcClient + TestCompletionProxy +
/// TestLlamaClient + BusyTimeoutOverride, with the REAL evaluator loop running via
/// <see cref="WorkerSchedulerService.RunAsync"/>. Drives requests through
/// SubmitAsync / RunItemPipeline / DispatchAsync / FinalizeAsync /
/// NotifyStreamComplete and produces a normalized <see cref="ScenarioTrace"/>.
/// </summary>
internal sealed class SchedulerScenarioRunner : IScenarioDriver
{
    public ScenarioOptions Options { get; }
    public CoordinatorConfig Cfg { get; }
    public SessionLedger Ledger { get; }
    public WorkerTracker Tracker { get; }
    public IHealthMonitorService Health { get; }
    public TestCompletionProxy Proxy { get; }
    public ScenarioRpcClient Rpc { get; }
    public WorkerSchedulerService Scheduler { get; }
    public string SessionId { get; }

    public int WarmLeaseCount => Scheduler.WarmLeaseCount;

    private readonly CancellationTokenSource _runCts = new();
    private readonly Task _runTask;
    private readonly int _savedChunkSize;
    private readonly int _savedChunkConstantsSize;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public SchedulerScenarioRunner(ScenarioOptions options, string sessionId = "sess_h")
    {
        Options = options;
        SessionId = sessionId;

        Health = options.HealthFactory?.Invoke() ?? new TestHealthMonitor();
        Proxy = new TestCompletionProxy(totalTokens: 150, slotId: 0);
        Ledger = new SessionLedger();
        Tracker = new WorkerTracker();
        Rpc = new ScenarioRpcClient();

        // Preserve chunk static state so a chunked scenario can't bleed into
        // parallel suites (ChunkEngine.CHUNK_SIZE is mutated by the scheduler
        // ctor when EnableChunks is true).
        _savedChunkSize = ChunkEngine.CHUNK_SIZE;
        _savedChunkConstantsSize = ChunkConstants.ChunkSize;

        var multi = options.MultiEngineTopology;
        Cfg = new CoordinatorConfig
        {
            RunMode = options.RunMode,
            UseLlamaEngine = options.UseLlamaEngine,
            PipelineEnabled = options.PipelineEnabled,
            CombinedEnabled = options.CombinedEnabled,
            MultiEnginePolicy = options.MultiEnginePolicy,
            MultiEngineThreshold = 10,
            PrefixCheckpointEnabled = options.PrefixCheckpointEnabled,
            WarmSlotVerificationEnabled = options.WarmSlotVerificationEnabled,
            MixPrecisionEnabled = options.MixPrecisionEnabled,
            AtomicThreshold = options.AtomicThreshold,
            WarmThreshold = options.WarmThreshold,
            EnableChunks = options.EnableChunks,
            ChunkSize = options.ChunkSize,
            NoStoreKvRestore = options.NoStoreKvRestore,
            Workers = new List<WorkerConfig>
            {
                new()
                {
                    Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = options.RtxLlamaUrl,
                    WorkerType = 3, Slots = options.RtxSlots, PrefillPriority = 1, DecodePriority = 2,
                    Role = multi ? "head" : "standalone", PeerWorker = multi ? "p100" : null,
                    PeerHost = "192.168.122.21", PeerPort = 9700,
                    PipelineCapable = multi, CombinedCapable = multi,
                    // NB: the head's ModelAlias feeds TranslateModelAlias at
                    // decode time. "nano" has NO template in any test config
                    // (AutoRouterTests registers moe-35b-pd etc.), so the alias
                    // round-trips unchanged and the golden trace is independent
                    // of ambient ModelConfigLoader state.
                    ModelAlias = multi ? "nano" : null,
                },
                new()
                {
                    Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = options.P100LlamaUrl,
                    WorkerType = 2, Slots = options.P100Slots, PrefillPriority = 100, DecodePriority = 1,
                    Role = multi ? "worker" : "standalone",
                },
            },
        };
        foreach (var w in Cfg.Workers)
            Tracker.InitWorker(w.Name, w.Slots);

        var sp = new ServiceCollection().BuildServiceProvider();
        Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, Proxy, Health, Rpc,
            sp, Serilog.Log.Logger);
        Scheduler.AgentClientFactory = (_, _) => Rpc;
        Scheduler.LlamaClientFactory = options.LlamaClientFactory ?? (_ => new TestLlamaClient());
        Scheduler.BusyTimeoutOverride = (_, _) => (options.BusyTimeout.StuckMs, options.BusyTimeout.SlowMs);

        // Upsert model registrations (RegisterForTest overwrites) so the
        // harness never WIPES another fixture's registrations mid-flight
        // (ModelRegistry is a process-wide static; ClearForTest in every
        // fixture is a pre-existing cross-collection race this runner avoids
        // by only ever adding).
        foreach (var alias in options.ModelAliases)
        {
            ModelRegistry.RegisterForTest(new EngineConfig(
                ModelAlias: alias,
                ModelPath: "/dev/null",
                NGpuLayers: 0, NCtx: 2048,
                ContBatching: true, Fit: false, UbatchSize: 512,
                SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));
        }
        // The head worker's ModelAlias must resolve for combined/pipeline plans.
        if (multi && !options.ModelAliases.Contains("moe-35b-solo"))
        {
            ModelRegistry.RegisterForTest(new EngineConfig(
                ModelAlias: "moe-35b-solo",
                ModelPath: "/dev/null",
                NGpuLayers: 99, NCpuMoe: 8, NCtx: 320000,
                OverrideTensors: new[] { "blk.*.ffn_*_exps.weight=CPU" },
                ContBatching: true, Fit: false, UbatchSize: 512,
                SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));
        }

        options.ConfigureRpc?.Invoke(Rpc);

        if (options.StartEvaluator)
            _runTask = Scheduler.RunAsync(_runCts.Token);
        else
            _runTask = Task.CompletedTask;
    }

    // ── Driving API ──

    private Dictionary<string, object> BuildRequest(int maxTokens, bool stream, string? forceMode)
    {
        var req = new Dictionary<string, object>
        {
            ["stream"] = stream,
            ["max_tokens"] = maxTokens,
            ["model"] = "nano",
        };
        if (forceMode is not null)
            req["force_mode"] = forceMode;
        return req;
    }

    /// <summary>
    /// Re-upsert the harness model registrations immediately before a submit:
    /// other fixtures' ModelRegistry.ClearForTest() can wipe them mid-flight
    /// (process-wide static), which would otherwise surface as a spurious
    /// model_not_found. Shrinks the race window to the scheduler's own read.
    /// </summary>
    private void EnsureModelRegistered()
    {
        foreach (var alias in Options.ModelAliases)
        {
            ModelRegistry.RegisterForTest(new EngineConfig(
                ModelAlias: alias,
                ModelPath: "/dev/null",
                NGpuLayers: 0, NCtx: 2048,
                ContBatching: true, Fit: false, UbatchSize: 512,
                SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));
        }
    }

    /// <summary>Submit a chat request and drive it to completion (streaming: consume + NotifyStreamComplete).</summary>
    public async Task<object?> SubmitAsync(
        string sessionId, int estimatedTokens, int maxTokens = 100,
        bool stream = false, string? prefixHash = null, string? forceMode = null,
        int systemPromptTokens = 0, CancellationToken ct = default)
    {
        var req = BuildRequest(maxTokens, stream, forceMode);
        var msgs = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) },
        };
        EnsureModelRegistered();
        var submit = Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens, maxTokens,
            prefixHash, _runCts.Token, systemPromptTokens);

        if (stream)
        {
            // SubmitAsync returns once DecodeAsync has produced the chunk enumerable.
            var chunks = await submit.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var enumerable = (IAsyncEnumerable<byte[]>)chunks!;
            await foreach (var _ in enumerable.WithCancellation(ct)) { }
            await Scheduler.NotifyStreamComplete(sessionId);
            await SettleAsync();
            return chunks;
        }

        var result = await submit.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await SettleAsync();
        return result;
    }

    /// <summary>Submit without awaiting completion (cancel-mid-flight scenarios drive it themselves).</summary>
    public Task<object> SubmitRawAsync(
        string sessionId, int estimatedTokens, int maxTokens = 100,
        bool stream = false, string? prefixHash = null, string? forceMode = null,
        CancellationToken ct = default)
    {
        var req = BuildRequest(maxTokens, stream, forceMode);
        var msgs = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) },
        };
        EnsureModelRegistered();
        return Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens, maxTokens,
            prefixHash, ct);
    }

    // ── Direct-drive seams (used by matrix + cancel-mid-flight tests) ──

    public WorkItem CreateWorkItem(string sessionId, int estimatedTokens, int maxTokens = 100,
        bool stream = false, string? prefixHash = null, string? forceMode = null)
    {
        var req = BuildRequest(maxTokens, stream, forceMode);
        var msgs = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) },
        };
        return new WorkItem(req, msgs, sessionId, $"trace_{sessionId}", prefixHash,
            estimatedTokens, maxTokens);
    }

    public Task<WorkItemState> DispatchAsync(WorkItem item, CancellationToken ct = default)
        => Scheduler.DispatchAsync(item, ct);

    public Task RunItemPipelineAsync(WorkItem item, RequestType initialType, CancellationToken ct = default)
        => Scheduler.RunItemPipeline(item, initialType, ct);

    public Task FinalizeAsync(WorkItem item, WorkItemState end)
        => Scheduler.FinalizeAsync(item, end);

    public void CancelItem(WorkItem item) => item.Cancel();

    // ── Trace capture ──

    /// <summary>Wait for fire-and-forget work (prefix save, stream save, chunk cache) to land.</summary>
    public async Task SettleAsync()
    {
        // Fire-and-forget tasks are instant in-process; 250ms plus a stability
        // check on the RPC log is far beyond what they need.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int lastCount = -1;
        int stableSamples = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(25);
            var count = Rpc.RpcCalls.Count;
            if (count == lastCount)
            {
                if (++stableSamples >= 4) break; // 100ms with no new RPC calls
            }
            else
            {
                stableSamples = 0;
                lastCount = count;
            }
        }
        await Task.Delay(50);
    }

    /// <summary>Capture the normalized trace AFTER the scenario finished and settled.</summary>
    public ScenarioTrace CaptureTrace(string sessionId, OutcomeClass outcome, Exception? error = null)
        => ScenarioTraceCapture.Capture(Rpc, Proxy, Ledger, Cfg.Workers, Tracker, sessionId, outcome, error);

    /// <summary>Serialize a golden trace to its canonical JSON (deterministic ordering).</summary>
    public static string SerializeGolden(GoldenTrace golden)
        => JsonSerializer.Serialize(golden, JsonOpts) + "\n";

    /// <summary>Run one scenario spec end-to-end against the LEGACY scheduler and capture the normalized result.</summary>
    public static async Task<ScenarioRunResult> ExecuteAsync(ScenarioSpec spec)
    {
        await using var runner = new SchedulerScenarioRunner(spec.Options, "sess_h");
        return await ExecuteOnAsync(runner, spec);
    }

    /// <summary>Run one scenario spec against an arbitrary driver (legacy or v2) and capture the normalized result.</summary>
    public static async Task<ScenarioRunResult> ExecuteOnAsync(IScenarioDriver driver, ScenarioSpec spec)
    {
        OutcomeClass outcome;
        Exception? error = null;
        try
        {
            await spec.Run(driver);
            outcome = OutcomeClass.Done;
        }
        catch (OperationCanceledException)
        {
            outcome = OutcomeClass.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = OutcomeClass.Failed;
            error = ex;
        }

        await driver.SettleAsync();
        var trace = driver.CaptureTrace(driver.SessionId, outcome, error);

        // Classify a completed-after-retries request: Done plus any BUSY
        // EnginePrefill response means the request was re-enqueued and served
        // on a later attempt (distinct from a cross-model re-prefill, which
        // returns Ok status but model_match=false meta).
        if (outcome == OutcomeClass.Done
            && trace.Rpc.Any(c => c.Op == "EnginePrefill" && c.Status == StatusCode.Busy.ToString()))
            outcome = OutcomeClass.RetriedThenDone;

        return new ScenarioRunResult(spec.Id, outcome, error, trace, driver.WarmLeaseCount);
    }

    public async ValueTask DisposeAsync()
    {
        _runCts.Cancel();
        try { await _runTask; } catch (OperationCanceledException) { }
        _runCts.Dispose();
        // Restore chunk static state (see ctor).
        ChunkEngine.CHUNK_SIZE = _savedChunkSize;
        ChunkConstants.ChunkSize = _savedChunkConstantsSize;
    }
}
