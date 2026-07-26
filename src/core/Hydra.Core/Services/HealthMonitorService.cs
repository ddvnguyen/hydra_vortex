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

    public bool IsStoreHealthy { get; private set; } = true;

    public HealthMonitorService(CoordinatorConfig cfg, IEnumerable<WorkerConfig> workers,
        IWorkerTracker tracker, IHttpClientFactory httpFactory, ILogger log)
    {
        _cfg = cfg; _workers = workers.ToList(); _tracker = tracker; _httpFactory = httpFactory; _log = log;
        foreach (var w in _workers) _nodes[w.Name] = new NodeInfo { NodeName = w.Name, Healthy = true };
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
    public int? GetIdleSlot(string name) { var info = GetNodeInfo(name); return info?.Slots.FirstOrDefault(s => !s.IsProcessing)?.Id; }
    public NodeInfo? GetNodeInfo(string name) { lock (_lock) return _nodes.TryGetValue(name, out var n) ? Clone(n) : null; }
    public void UpdateNodeModelIdentity(string nodeName, string tokenizer, string modelName, string modelQuant, uint modelCapabilities)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var n))
            {
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
                lock (_lock)
                {
                    _nodes[w.Name] = new NodeInfo
                    {
                        NodeName = w.Name,
                        Healthy = true,
                        SlotsTotal = 0,
                        SlotsIdle = 0,
                        ConsecutiveFailures = 0,
                    };
                }
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
                lock (_lock)
                {
                    _nodes[w.Name] = new NodeInfo
                    {
                        NodeName = w.Name,
                        Healthy = true,
                        SlotsTotal = 0,
                        SlotsIdle = 0,
                        ConsecutiveFailures = 0,
                    };
                }
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
        try
        {
            await using var rpc = new Hydra.Shared.RpcClient(w.Host, w.LlamaRpcPort > 0 ? w.LlamaRpcPort : w.RpcPort);
            var engine = new HydraEngineClient(rpc);
            var engineInfo = await engine.EngineInfoAsync($"health-{w.Name}", ct);
            if (engineInfo?.PresetAliases is { } aliases && aliases.Count > 0)
                info.PresetAliases = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
            if (engineInfo?.Capabilities is { } caps && caps.Count > 0)
                info.EngineCapabilities = new HashSet<string>(caps, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "health_poll_engine_info_failed Node={N}", w.Name);
        }

        // Stuck-slot watchdog (#299/C7): carry the per-slot stuck counter across
        // poll cycles. A slot still processing with nothing left to generate
        // (n_remain==0) for StuckSlotCycles consecutive polls is counted as stuck.
        // StuckSlots was previously declared but never populated.
        lock (_lock)
        {
            _nodes.TryGetValue(w.Name, out var prev);
            info.StuckSlots = StuckSlotDetector.Apply(prev?.Slots, info.Slots, _cfg.StuckSlotCycles);
            foreach (var slot in info.Slots)
                if (slot.StuckPollCount == _cfg.StuckSlotCycles)  // log once, on the cycle it crosses
                    _log.Warning("stuck_slot_detected Node={N} Slot={S} Cycles={C} NPast={P}",
                        w.Name, slot.Id, slot.StuckPollCount, slot.NPast);
            _nodes[w.Name] = info;
        }
        _log.Information("health_poll_ok Node={N} Slots={S} Idle={I} Stuck={K} Presets={P}",
            w.Name, slots.Count, info.SlotsIdle, info.StuckSlots,
            info.PresetAliases.Count);
    }

    private void OnFail(string name)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(name, out var info))
            {
                info.ConsecutiveFailures++;
                if (info.ConsecutiveFailures >= 3)
                    info.Healthy = false;
            }
        }
    }

    private static NodeInfo Clone(NodeInfo src) => new()
    {
        NodeName = src.NodeName,
        Healthy = src.Healthy,
        SlotsTotal = src.SlotsTotal,
        SlotsIdle = src.SlotsIdle,
        StuckSlots = src.StuckSlots,
        ConsecutiveFailures = src.ConsecutiveFailures,
        PresetAliases = new HashSet<string>(src.PresetAliases, StringComparer.OrdinalIgnoreCase),
        EngineCapabilities = new HashSet<string>(src.EngineCapabilities, StringComparer.OrdinalIgnoreCase),
        CurrentModel = src.CurrentModel,
        Slots = src.Slots.Select(s => new Models.SlotInfo
        {
            Id = s.Id, NPast = s.NPast, IsProcessing = s.IsProcessing,
            NRemain = s.NRemain, StuckPollCount = s.StuckPollCount
        }).ToList()
    };
}
