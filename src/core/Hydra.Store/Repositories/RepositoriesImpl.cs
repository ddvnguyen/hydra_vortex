using System.Collections.Concurrent;
using System.Text.Json;
using Hydra.Store.Models;

namespace Hydra.Store.Repositories;

public sealed class SessionLedger : ISessionLedger
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

    public SessionEntry? Lookup(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var e); return e;
    }

    public SessionEntry Register(string sessionId, string nodeName, int? slotId = null, int nPast = 0, string? prefixHash = null)
    {
        var now = DateTime.UtcNow;
        var entry = _sessions.GetOrAdd(sessionId, _ => new SessionEntry { SessionId = sessionId, CreatedAt = now });
        lock (entry)
        {
            entry.NodeName = nodeName; entry.SlotId = slotId; entry.NPast = nPast;
            entry.PrefixHash = prefixHash; entry.LastUsed = now; entry.SlotFreed = false;
        }
        return entry;
    }

    public void UpdateLastUsed(string sid) { if (_sessions.TryGetValue(sid, out var e)) lock (e) { e.LastUsed = DateTime.UtcNow; } }
    public void UpdateNPast(string sid, int nPast) { if (_sessions.TryGetValue(sid, out var e)) lock (e) { e.NPast = nPast; } }

    public void MarkEvicted(string sid)
    { if (_sessions.TryGetValue(sid, out var e)) lock (e) { e.SlotFreed = true; e.HasStoreState = true; } }

    public List<SessionEntry> GetSessionsOnNode(string node)
        => _sessions.Values.Where(s => { lock (s) return s.NodeName == node; }).ToList();

    public SessionEntry? GetLruSession(string node)
        => _sessions.Values
            .Where(s => { lock (s) return s.NodeName == node && s.SlotId.HasValue && !s.SlotFreed; })
            .MinBy(s => { lock (s) return s.LastUsed; });

    public int ActiveCountOnNode(string node)
        => _sessions.Values.Count(s => { lock (s) return s.NodeName == node && !s.SlotFreed; });

    public int ActiveCount => _sessions.Values.Count(s => { lock (s) return !s.SlotFreed; });

    public List<string> GetStaleSessionIds(double timeoutS)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-timeoutS);
        return _sessions.Values.Where(s => { lock (s) return s.LastUsed < cutoff && !s.SlotFreed; }).Select(s => s.SessionId).ToList();
    }

    public int EvictStale(double timeoutS) { var ids = GetStaleSessionIds(timeoutS); foreach (var id in ids) MarkEvicted(id); return ids.Count; }
    public void Remove(string sid) { _sessions.TryRemove(sid, out _); }

    public Dictionary<string, object> AllSessions()
    {
        var r = new Dictionary<string, object>();
        foreach (var (_, e) in _sessions)
            lock (e) r[e.SessionId] = new { session_id = e.SessionId, node = e.NodeName, slot_id = e.SlotId, n_past = e.NPast, has_store_state = e.HasStoreState, slot_freed = e.SlotFreed };
        return r;
    }

    public async Task SaveToFileAsync(string path, CancellationToken ct = default)
    {
        var data = _sessions.Values.Where(s => { lock (s) return s.HasStoreState; }).Select(s => new
        { s.SessionId, node_name = s.NodeName, slot_id = s.SlotId, n_past = s.NPast, prefix_hash = s.PrefixHash }).ToList();
        var json = JsonSerializer.Serialize(data); var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct); File.Move(tmp, path, overwrite: true);
    }
}

public sealed class WorkerTracker : IWorkerTracker
{
    private readonly ConcurrentDictionary<string, WorkerState> _states = new();
    private readonly int _errorThreshold;

    public WorkerTracker(int errorThreshold = 3) => _errorThreshold = errorThreshold;

    public void InitWorker(string name) => _states.GetOrAdd(name, _ => new WorkerState());

    public bool Acquire(string name, string role = "decode")
    {
        if (!_states.TryGetValue(name, out var s)) return false;
        lock (s) { if (s.Status != "free" || !s.Healthy) return false; s.Status = role; s.Role = role; s.BusySince = DateTime.UtcNow; return true; }
    }

    public void Release(string name) { if (_states.TryGetValue(name, out var s)) lock (s) { s.Status = "free"; } }
    public void OnError(string name) { if (_states.TryGetValue(name, out var s)) lock (s) { s.ErrorCount++; if (s.ErrorCount >= _errorThreshold) s.Healthy = false; } }
    public void OnSuccess(string name) { if (_states.TryGetValue(name, out var s)) lock (s) { s.ErrorCount = 0; s.Healthy = true; } }
    public void MarkUnhealthy(string name) { if (_states.TryGetValue(name, out var s)) lock (s) { s.Healthy = false; } }
    public void MarkHealthy(string name) { if (_states.TryGetValue(name, out var s)) lock (s) { s.Healthy = true; s.ErrorCount = 0; } }

    public List<string> FreeWorkers()
    {
        var r = new List<string>();
        foreach (var (n, s) in _states) lock (s) { if (s.Status == "free" && s.Healthy) r.Add(n); }
        return r;
    }

    public List<string> BusyWorkers()
    {
        var r = new List<string>();
        foreach (var (n, s) in _states) lock (s) { if (s.Status != "free") r.Add(n); }
        return r;
    }

    public bool IsFree(string n) => _states.TryGetValue(n, out var s) && lockOn(s, s => s.Status == "free" && s.Healthy);
    public string GetStatus(string n) => _states.TryGetValue(n, out var s) ? lockOn(s, s => s.Status) : "unknown";
    public bool IsHealthy(string n) => _states.TryGetValue(n, out var s) && lockOn(s, s => s.Healthy);
    public double GetElapsedSeconds(string n)
    {
        if (!_states.TryGetValue(n, out var s)) return 0;
        return lockOn(s, s => (DateTime.UtcNow - (s.BusySince ?? DateTime.UtcNow)).TotalSeconds);
    }

    public bool IsExpired(string name, double maxSeconds = 600) => GetElapsedSeconds(name) > maxSeconds;
    public List<string> AllWorkers => _states.Keys.ToList();

    private static T lockOn<T>(WorkerState s, Func<WorkerState, T> f) { lock (s) return f(s); }
    private static bool lockOn(WorkerState s, Func<WorkerState, bool> f) { lock (s) return f(s); }

    private sealed class WorkerState { public string Status = "free"; public string Role = ""; public DateTime? BusySince; public int ErrorCount; public bool Healthy = true; }
}
