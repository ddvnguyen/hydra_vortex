using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Hydra.Core.Services;

public sealed class HealthMonitorService : BackgroundService, IHealthMonitorService
{
    private readonly CoordinatorConfig _cfg;
    private readonly List<WorkerConfig> _workers;
    private readonly IWorkerTracker _tracker;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _log;
    private readonly Dictionary<string, NodeInfo> _nodes = new();
    private readonly object _lock = new();
    private Action? _healthyChanged;

    /// <summary>
    /// Consecutive engine INFO (0x41) RPC failures after which the node flips
    /// unhealthy even though the HTTP /slots poll still succeeds (#635).
    /// Matches the existing OnFail threshold so both health signals agree.
    /// </summary>
    private const int RpcFailureThreshold = 3;

    /// <summary>
    /// Injectable factory for the engine INFO RPC client (test seam). When
    /// null, a real <see cref="Hydra.Shared.RpcClient"/> to the worker's RPC
    /// port is created (production behavior). Mirrors the scheduler's
    /// AgentClientFactory seam.
    /// </summary>
    internal Func<string, int, Hydra.Shared.RpcClient>? EngineInfoRpcClientFactory { get; set; }

    public bool IsStoreHealthy { get; private set; } = true;

    /// <inheritdoc/>
    public event Action? HealthyChanged
    {
        add => _healthyChanged += value;
        remove => _healthyChanged -= value;
    }

    public HealthMonitorService(CoordinatorConfig cfg, IEnumerable<WorkerConfig> workers,
        IWorkerTracker tracker, IHttpClientFactory httpFactory, ILogger log)
    {
        _cfg = cfg; _workers = workers.ToList(); _tracker = tracker; _httpFactory = httpFactory; _log = log;
        foreach (var w in _workers) _nodes[w.Name] = new NodeInfo { NodeName = w.Name, Healthy = true };
    }

    // Writes a node's health snapshot, firing HealthyChanged only when the
    // Healthy flag actually flips (capacity-significant event for the
    // scheduler's evaluator).
    private void SetNodeInfo(string name, NodeInfo info)
    {
        bool flipped;
        lock (_lock)
        {
            var prev = _nodes.TryGetValue(name, out var n) && n.Healthy;
            _nodes[name] = info;
            flipped = prev != info.Healthy;
        }
        if (flipped) _healthyChanged?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("health_monitor_start Workers={Count}", _workers.Count);
        await PollAllAsync(ct);
        _log.Information("health_monitor_first_poll_done");
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.HealthPollIntervalS), ct); }
            catch (OperationCanceledException) { break; }
            await PollAllAsync(ct);
        }
    }

    public bool IsHealthy(string name) { lock (_lock) return _nodes.TryGetValue(name, out var n) && n.Healthy; }

    /// <inheritdoc/>
    public void MarkHealthy(string name)
    {
        // #592: mutate the existing NodeInfo in place (slot/model data stays
        // fresh) and fire HealthyChanged only on a real unhealthy→healthy flip,
        // mirroring OnFail's flip-only signal semantics.
        bool flipped = false;
        lock (_lock)
        {
            if (_nodes.TryGetValue(name, out var info) && !info.Healthy)
            {
                info.Healthy = true;
                info.ConsecutiveFailures = 0;
                // #635: positive liveness evidence (prefill served / probe OK)
                // also resets the engine-INFO RPC failure counter. If the RPC
                // path is genuinely still down, the next poll re-counts it.
                info.RpcConsecutiveFailures = 0;
                info.LastCheck = DateTime.UtcNow;
                flipped = true;
            }
        }
        if (flipped) _healthyChanged?.Invoke();
    }

    public int? GetIdleSlot(string name) { var info = GetNodeInfo(name); return info?.Slots.FirstOrDefault(s => !s.IsProcessing)?.Id; }
    public NodeInfo? GetNodeInfo(string name) { lock (_lock) return _nodes.TryGetValue(name, out var n) ? Clone(n) : null; }
    public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var n))
            {
                // CurrentModel is the GGUF-file alias of the model actually
                // resident on the node (#479/S3). It feeds the request_timeline
                // prefill_model/decode_model fields and AutoRouter's residency
                // check. Only stamp when we have a real alias — don't clear a
                // known-good value with an empty string.
                if (!string.IsNullOrEmpty(modelAlias))
                    n.CurrentModel = modelAlias;
                n.ModelTokenizer = tokenizer;
                n.ModelName = modelName;
                n.ModelQuant = modelQuant;
                n.ModelCapabilities = modelCapabilities;
            }
        }
    }
    public Dictionary<string, object> GetHealthSummary()
    {
        var r = new Dictionary<string, object>();
        lock (_lock) foreach (var (name, info) in _nodes)
                r[name] = new { healthy = info.Healthy, slots_total = info.SlotsTotal, slots_idle = info.SlotsIdle, stuck_slots = info.StuckSlots };
        return r;
    }

	private async Task PollAllAsync(CancellationToken ct)
    {
        foreach (var w in _workers)
            try { await PollWorkerAsync(w, ct); }
            catch (Exception ex) { _log.Warning(ex, "health_poll_failed Node={N}", w.Name); OnFail(w.Name); }

        foreach (var w in _workers)
        {
            var count = 0;
            try { count = _tracker.TotalSlots(w.Name) - _tracker.FreeSlotCount(w.Name); }
            catch { }
            CoordinatorMetrics.ActiveSessions.WithLabels(w.Name).Set(count);

            var busy = _tracker.GetElapsedSeconds(w.Name);
            CoordinatorMetrics.WorkerBusySeconds.WithLabels(w.Name).Set(busy);

            int stuck; lock (_lock) stuck = _nodes.TryGetValue(w.Name, out var n) ? n.StuckSlots : 0;
            CoordinatorMetrics.StuckSlots.WithLabels(w.Name).Set(stuck);
        }

        IsStoreHealthy = true;
    }

    /// <summary>
    /// Test seam (mirrors the scheduler's RunItemPipeline seam): runs one
    /// full poll cycle so Tests.Core can exercise the EngineInfo-RPC health
    /// detection without waiting for the HealthPollIntervalS timer.
    /// </summary>
    internal async Task PollForTestAsync(CancellationToken ct) => await PollAllAsync(ct);

    private async Task PollWorkerAsync(WorkerConfig w, CancellationToken ct)
    {
		using var http = _httpFactory.CreateClient($"health-{w.Name}");
		http.Timeout = TimeSpan.FromSeconds(_cfg.HealthPollTimeoutS);
        var llama = new LlamaClient(http, w.LlamaUrl);

        // Hydra #383 T5: combined-static-peer workers (0 slots, peer-only engine)
        // may not serve /slots. Tolerate HTTP errors and fall back to /health.
        List<LlamaSlotInfo> slots;
        try
        {
            slots = await llama.GetSlotsAsync(ct);
        }
        catch (Exception ex) when (w.Slots == 0)
        {
            _log.Information("health_poll_slots_fallback Node={N} Slots={S} Err={Msg}", w.Name, w.Slots, ex.Message);
            var healthy = await llama.HealthAsync(ct);
            if (healthy)
            {
                SetNodeInfo(w.Name, new NodeInfo
                {
                    NodeName = w.Name,
                    Healthy = true,
                    SlotsTotal = 0,
                    SlotsIdle = 0,
                    ConsecutiveFailures = 0,
                });
                return;
            }
            _log.Warning("health_poll_fallback_fail Node={N}", w.Name);
            OnFail(w.Name);
            return;
        }

        if (slots == null || slots.Count == 0)
        {
            var healthy = await llama.HealthAsync(ct);
            if (healthy)
            {
                _log.Information("health_poll_router_ready Node={N} (no slots, server OK — router/loading)", w.Name);
                SetNodeInfo(w.Name, new NodeInfo
                {
                    NodeName = w.Name,
                    Healthy = true,
                    SlotsTotal = 0,
                    SlotsIdle = 0,
                    ConsecutiveFailures = 0,
                });
                return;
            }
            _log.Warning("health_poll_empty_slots Node={N}", w.Name); OnFail(w.Name); return;
        }

        var info = new NodeInfo
        {
            NodeName = w.Name,
            Healthy = true,
            SlotsTotal = slots.Count,
            Slots = slots.Select(s => new Models.SlotInfo
            {
                Id = s.Id,
                NPast = s.NPast,
                IsProcessing = s.IsProcessing,
                NRemain = s.NRemain
            }).ToList(),
            ConsecutiveFailures = 0
        };
        info.SlotsIdle = info.Slots.Count(s => !s.IsProcessing);

        // #479/S3: drop the legacy /v1/models poll. The resident model is now
        // learned per-request from the engine's PREFILL model_alias (stamped by
        // the worker scheduler) and the set of GGUF-file aliases this worker can
        // host comes from engine INFO preset_aliases, queried below.
        info.CurrentModel = "";

        // #479/S3: query engine INFO (0x41) for preset_aliases — the set of
        // GGUF-file aliases this node's --models-preset can host. Replaces the
        // /v1/models residency signal for AutoRouter + Router.IsModelAllowed.
        // Best-effort: a pre-#289 engine returns NotImplemented → empty set.
        var engineInfoFailed = false;
        try
        {
            var rpc = EngineInfoRpcClientFactory is null
                ? new Hydra.Shared.RpcClient(w.LlamaRpcHost, w.LlamaRpcPort > 0 ? w.LlamaRpcPort : w.RpcPort)
                : EngineInfoRpcClientFactory(w.LlamaRpcHost, w.LlamaRpcPort > 0 ? w.LlamaRpcPort : w.RpcPort);
            await using var _ = rpc;
            var engine = new HydraEngineClient(rpc);
            var engineInfo = await engine.EngineInfoAsync($"health-{w.Name}", ct);
            if (engineInfo?.PresetAliases is { } aliases && aliases.Count > 0)
                info.PresetAliases = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
            if (engineInfo?.Capabilities is { } caps && caps.Count > 0)
                info.EngineCapabilities = new HashSet<string>(caps, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            engineInfoFailed = true;
            _log.Warning(ex, "health_poll_engine_info_failed Node={N} Host={H} Port={P}",
                w.Name, w.LlamaRpcHost, w.LlamaRpcPort > 0 ? w.LlamaRpcPort : w.RpcPort);
        }

        // Stuck-slot watchdog (#299/C7): carry the per-slot stuck counter across
        // poll cycles. A slot still processing with nothing left to generate
        // (n_remain==0) for StuckSlotCycles consecutive polls is counted as stuck.
        // StuckSlots was previously declared but never populated.
        lock (_lock)
        {
            _nodes.TryGetValue(w.Name, out var prev);
            // #479/S3: the resident model is stamped per-request by the worker
            // scheduler (UpdateNodeModelIdentity). The poll doesn't know the
            // model, so carry the last-known identity forward instead of
            // replacing the node with a blank slate each cycle — otherwise the
            // request_timeline prefill_model/decode_model fields go empty
            // between the stamp and the next PREFILL.
            if (prev != null)
            {
                info.CurrentModel = prev.CurrentModel;
                info.ModelTokenizer = prev.ModelTokenizer;
                info.ModelName = prev.ModelName;
                info.ModelQuant = prev.ModelQuant;
                info.ModelCapabilities = prev.ModelCapabilities;
            }

            // #635: the EngineInfo RPC failing while /slots succeeds means the
            // RPC/prefill path is dead even though HTTP looks alive (observed:
            // ggml_abort zombie serving /slots with a dead RPC port). After
            // RpcFailureThreshold consecutive failures the node flips unhealthy
            // — the scheduler's admission gate + router then stop dispatching
            // prefill into the dying engine. One successful INFO RPC (engine
            // restarted) resets the counter and, via SetNodeInfo below, flips
            // the node back healthy (firing HealthyChanged → evaluator re-check
            // for any queued/retry-pending items, #635 fix 3).
            var rpcFails = engineInfoFailed ? (prev?.RpcConsecutiveFailures ?? 0) + 1 : 0;
            info.RpcConsecutiveFailures = rpcFails;
            if (rpcFails >= RpcFailureThreshold)
            {
                info.Healthy = false;
                _log.Warning("health_poll_engine_info_dead Node={N} RpcFails={R}/{T} — RPC/prefill path down, marking unhealthy",
                    w.Name, rpcFails, RpcFailureThreshold);
            }

            info.StuckSlots = StuckSlotDetector.Apply(prev?.Slots, info.Slots, _cfg.StuckSlotCycles);
            foreach (var slot in info.Slots)
                if (slot.StuckPollCount == _cfg.StuckSlotCycles)  // log once, on the cycle it crosses
                    _log.Warning("stuck_slot_detected Node={N} Slot={S} Cycles={C} NPast={P}",
                        w.Name, slot.Id, slot.StuckPollCount, slot.NPast);
        }
        SetNodeInfo(w.Name, info);
        _log.Information("health_poll_ok Node={N} Slots={S} Idle={I} Stuck={K} Presets={P}",
            w.Name, slots.Count, info.SlotsIdle, info.StuckSlots,
            info.PresetAliases.Count);
    }

    private void OnFail(string name)
    {
        bool flipped = false;
        lock (_lock)
        {
            if (_nodes.TryGetValue(name, out var info))
            {
                info.ConsecutiveFailures++;
                if (info.ConsecutiveFailures >= 3 && info.Healthy)
                {
                    info.Healthy = false;
                    flipped = true;
                }
            }
        }
        if (flipped) _healthyChanged?.Invoke();
    }

    private static NodeInfo Clone(NodeInfo src) => new()
    {
        NodeName = src.NodeName,
        Healthy = src.Healthy,
        SlotsTotal = src.SlotsTotal,
        SlotsIdle = src.SlotsIdle,
        StuckSlots = src.StuckSlots,
        ConsecutiveFailures = src.ConsecutiveFailures,
        RpcConsecutiveFailures = src.RpcConsecutiveFailures,
        PresetAliases = new HashSet<string>(src.PresetAliases, StringComparer.OrdinalIgnoreCase),
        EngineCapabilities = new HashSet<string>(src.EngineCapabilities, StringComparer.OrdinalIgnoreCase),
        CurrentModel = src.CurrentModel,
        ModelTokenizer = src.ModelTokenizer,
        ModelName = src.ModelName,
        ModelQuant = src.ModelQuant,
        ModelCapabilities = src.ModelCapabilities,
        Slots = src.Slots.Select(s => new Models.SlotInfo
        {
            Id = s.Id, NPast = s.NPast, IsProcessing = s.IsProcessing,
            NRemain = s.NRemain, StuckPollCount = s.StuckPollCount
        }).ToList()
    };
}
