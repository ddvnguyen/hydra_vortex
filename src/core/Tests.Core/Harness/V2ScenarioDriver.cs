using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.Integration;

namespace Tests.Core.Harness;

/// <summary>
/// v2 differential driver (epic #591 WP3): runs the SAME catalog scenarios
/// against <see cref="WorkerSchedulerV2"/> through its public surface
/// (<c>SubmitAsync</c> / <c>NotifyStreamComplete</c>), with the same
/// <see cref="ScenarioRpcClient"/> wired as both the per-worker engine channel
/// and the store — so the recorded RPC stream stays deterministically ordered
/// and the trace shape is identical to the legacy driver.
///
/// <para>The differential gate diff-able surface is the public contract only:
/// v2 has no DispatchAsync/RunItemPipeline direct-drive seams (LegacyOnly specs
/// are skipped here).</para>
/// </summary>
internal sealed class V2ScenarioDriver : IScenarioDriver
{
    public ScenarioOptions Options { get; }
    public CoordinatorConfig Cfg { get; }
    public SessionLedger Ledger { get; }
    public WorkerTracker Tracker { get; }
    public IHealthMonitorService Health { get; }
    public TestCompletionProxy Proxy { get; }
    public ScenarioRpcClient Rpc { get; }
    public WorkerSchedulerV2 Scheduler { get; }
    public string SessionId { get; }

    private readonly CancellationTokenSource _runCts = new();
    private readonly int _savedChunkSize;
    private readonly int _savedChunkConstantsSize;

    public V2ScenarioDriver(ScenarioOptions options, string sessionId = "sess_h")
    {
        Options = options;
        SessionId = sessionId;

        Health = options.HealthFactory?.Invoke() ?? new TestHealthMonitor();
        Proxy = new TestCompletionProxy(totalTokens: 150, slotId: 0);
        Ledger = new SessionLedger();
        Tracker = new WorkerTracker();
        Rpc = new ScenarioRpcClient();

        _savedChunkSize = Hydra.Core.ChunkEngine.CHUNK_SIZE;
        _savedChunkConstantsSize = Hydra.Shared.ChunkConstants.ChunkSize;

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

        // The SAME recording fake serves as both the per-worker engine channel
        // (via the adapter) and the store (StoreGateway takes an RpcClient), so
        // the ordered RPC stream is identical in shape to the legacy driver.
        var channels = new Dictionary<string, IEngineRpcClient>
        {
            ["rtx"] = new EngineRpcClientAdapter(Rpc),
            ["p100"] = new EngineRpcClientAdapter(Rpc),
        };
        var store = new StoreGateway(Rpc);
        var engine = new EngineRpcGateway(channels);

        var runners = new WorkerStateRunner[]
        {
            new PlanRunner(new RoutePlanner(), new LeaseManager(Tracker), Ledger, Cfg.Workers, Tracker, Health),
            new PrefillRunner(engine),
            new SaveKvRunner(store),
            new RestoreRunner(store, engine),
            new DecodeRunner(Proxy),
            new BgSaveRunner(),
        };

        Scheduler = new WorkerSchedulerV2(
            Cfg, Ledger, Tracker, Health,
            new RequestClassifier(), new RoutePlanner(), new LeaseManager(Tracker),
            runners, new TimelineEmitter());

        options.ConfigureRpc?.Invoke(Rpc);
        _ = Scheduler.RunAsync(_runCts.Token);
    }

    public int WarmLeaseCount => Scheduler.WarmLeaseCount;

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

        var submit = Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens, maxTokens,
            prefixHash, _runCts.Token, systemPromptTokens);

        if (stream)
        {
            var raw = CompletionResults.Unwrap(await submit.WaitAsync(TimeSpan.FromSeconds(30), ct));
            var enumerable = (IAsyncEnumerable<byte[]>)raw!;
            await foreach (var _ in enumerable.WithCancellation(ct)) { }
            await Scheduler.NotifyStreamComplete(sessionId);
            await SettleAsync();
            return raw;
        }

        var result = CompletionResults.Unwrap(await submit.WaitAsync(TimeSpan.FromSeconds(30), ct));
        await SettleAsync();
        return result;
    }

    public async Task SettleAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int lastCount = -1;
        int stableSamples = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(25);
            var count = Rpc.RpcCalls.Count;
            if (count == lastCount)
            {
                if (++stableSamples >= 4) break;
            }
            else
            {
                stableSamples = 0;
                lastCount = count;
            }
        }
        await Task.Delay(50);
    }

    public ScenarioTrace CaptureTrace(string sessionId, OutcomeClass outcome, Exception? error = null)
        => ScenarioTraceCapture.Capture(Rpc, Proxy, Ledger, Cfg.Workers, Tracker, sessionId, outcome, error);

    public async ValueTask DisposeAsync()
    {
        _runCts.Cancel();
        _runCts.Dispose();
        Hydra.Core.ChunkEngine.CHUNK_SIZE = _savedChunkSize;
        Hydra.Shared.ChunkConstants.ChunkSize = _savedChunkConstantsSize;
    }
}
