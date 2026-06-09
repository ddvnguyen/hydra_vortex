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
    private readonly Hydra.Shared.RpcClient? _storeClient;
    private readonly ILogger _log;
    private readonly Dictionary<string, NodeInfo> _nodes = new();
    private readonly object _lock = new();

    public bool IsStoreHealthy { get; private set; } = true;

    public HealthMonitorService(CoordinatorConfig cfg, IEnumerable<WorkerConfig> workers,
        IWorkerTracker tracker, Hydra.Shared.RpcClient? storeClient, ILogger log)
    {
        _cfg = cfg; _workers = workers.ToList(); _tracker = tracker; _storeClient = storeClient; _log = log;
        foreach (var w in _workers) _nodes[w.Name] = new NodeInfo { NodeName = w.Name };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
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
            try { await PollAgentAsync(w, ct); }
            catch (Exception ex) { _log.Warning(ex, "health_poll_failed Node={N}", w.Name); OnFail(w.Name); }
        if (_storeClient != null)
            try { var r = await _storeClient.RequestAsync(Hydra.Shared.OpCode.Stat, "", ReadOnlyMemory<byte>.Empty, "health", ct); IsStoreHealthy = r.Status == 0; }
            catch { IsStoreHealthy = false; }
    }

    private async Task PollAgentAsync(WorkerConfig w, CancellationToken ct)
    {
        var client = new Hydra.Shared.RpcClient(w.Host, w.RpcPort);
        try
        {
            var resp = await client.RequestAsync(Hydra.Shared.OpCode.NodeHealth, "", ReadOnlyMemory<byte>.Empty, "health", ct);
            if (resp.Status != 0 || resp.Meta == null) { OnFail(w.Name); return; }
            var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Meta);
            if (meta is null) { OnFail(w.Name); return; }
            var healthy = meta.TryGetValue("healthy", out var h) && h.GetBoolean();
            var slots = new List<Models.SlotInfo>();
            var stuckIds = new List<int>();
            if (meta.TryGetValue("slots", out var sl) && sl.ValueKind == JsonValueKind.Array)
                foreach (var s in sl.EnumerateArray())
                {
                    var id = s.TryGetProperty("id", out var si) ? si.GetInt32() : 0;
                    var isProcessing = s.TryGetProperty("is_processing", out var sp) && sp.GetBoolean();
                    var nPast = s.TryGetProperty("n_past", out var sn) ? sn.GetInt32() : 0;
                    var nRemain = s.TryGetProperty("n_remain", out var snr) ? snr.GetInt32() : 0;
                    var slotInfo = new SlotInfo { Id = id, IsProcessing = isProcessing, NPast = nPast, LastActive = DateTime.UtcNow };
                    // Stuck: is_processing=true && n_remain==0 (finished generating but slot not freed)
                    if (isProcessing && nRemain == 0)
                    {
                        slotInfo.StuckPollCount = GetStuckPollCount(w.Name, id) + 1;
                        if (slotInfo.StuckPollCount >= _cfg.HealthMaxFailures)
                            stuckIds.Add(id);
                    }
                    slots.Add(slotInfo);
                }
            lock (_lock)
            {
                var info = _nodes[w.Name];
                info.Healthy = healthy; info.SlotsIdle = meta.TryGetValue("slots_idle", out var si2) ? si2.GetInt32() : 0;
                info.SlotsTotal = meta.TryGetValue("slots_total", out var st) ? st.GetInt32() : w.Slots;
                info.Slots = slots; info.ConsecutiveFailures = 0; info.LastCheck = DateTime.UtcNow;
                info.StuckSlots = stuckIds.Count;
            }
            _tracker.OnSuccess(w.Name);

            // Erase stuck slots — best-effort, coordinator reclaims slot for other sessions
            foreach (var sid in stuckIds)
            {
                try
                {
                    await client.RequestAsync(Hydra.Shared.OpCode.SlotErase,
                        sid.ToString(), ReadOnlyMemory<byte>.Empty,
                        $"stuck_{w.Name}_{sid}", ct);
                    _log.Warning("stuck_slot_erased Node={Node} Slot={Slot}", w.Name, sid);
                    lock (_lock)
                    {
                        var info = _nodes[w.Name];
                        var slot = info.Slots.FirstOrDefault(x => x.Id == sid);
                        if (slot != null) slot.StuckPollCount = 0;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "stuck_slot_erase_failed Node={Node} Slot={Slot}",
                        w.Name, sid);
                }
            }
        }
        catch { OnFail(w.Name); }
        finally { await client.DisposeAsync(); }
    }

    private int GetStuckPollCount(string node, int slotId)
    {
        lock (_lock)
            return _nodes.TryGetValue(node, out var info)
                ? info.Slots.FirstOrDefault(x => x.Id == slotId)?.StuckPollCount ?? 0
                : 0;
    }

    private void OnFail(string name)
    {
        lock (_lock) { var n = _nodes[name]; n.ConsecutiveFailures++; if (n.ConsecutiveFailures >= _cfg.HealthMaxFailures) n.Healthy = false; }
        _tracker.OnError(name);
    }

    private static NodeInfo Clone(NodeInfo n) => new() { NodeName = n.NodeName, Healthy = n.Healthy, SlotsTotal = n.SlotsTotal, SlotsIdle = n.SlotsIdle, StuckSlots = n.StuckSlots, Slots = n.Slots.Select(s => new SlotInfo { Id = s.Id, IsProcessing = s.IsProcessing, NPast = s.NPast, StuckPollCount = s.StuckPollCount, LastActive = s.LastActive }).ToList() };
}
