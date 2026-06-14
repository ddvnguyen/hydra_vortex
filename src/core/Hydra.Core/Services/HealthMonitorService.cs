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
        }

        IsStoreHealthy = true;
    }

    private async Task PollWorkerAsync(WorkerConfig w, CancellationToken ct)
    {
        using var http = _httpFactory.CreateClient($"health-{w.Name}");
        http.Timeout = TimeSpan.FromSeconds(5);
        var llama = new LlamaClient(http, w.LlamaUrl);
        var slots = await llama.GetSlotsAsync(ct);
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
                IsProcessing = s.IsProcessing
            }).ToList(),
            ConsecutiveFailures = 0
        };
        info.SlotsIdle = info.Slots.Count(s => !s.IsProcessing);
        info.CurrentModel = await llama.GetLoadedModelAsync(ct);

        lock (_lock) { _nodes[w.Name] = info; }
        _log.Information("health_poll_ok Node={N} Slots={S} Idle={I} Model={M}",
            w.Name, slots.Count, info.SlotsIdle, info.CurrentModel);
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
        CurrentModel = src.CurrentModel,
        Slots = src.Slots.Select(s => new Models.SlotInfo
        {
            Id = s.Id, NPast = s.NPast, IsProcessing = s.IsProcessing
        }).ToList()
    };
}
