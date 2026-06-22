using Hydra.Core.Models;

namespace Hydra.Core.Repositories;

public interface ISessionLedger
{
    SessionEntry? Lookup(string sessionId);
    SessionEntry Register(string sessionId, string nodeName, int? slotId = null, int nPast = 0, string? prefixHash = null);
    void UpdateLastUsed(string sessionId);
    void UpdateNPast(string sessionId, int nPast);
    void UpdateNPromptTokens(string sessionId, int nPromptTokens);
    void MarkEvicted(string sessionId);
    void MarkStoreState(string sessionId);
    List<SessionEntry> GetSessionsOnNode(string nodeName);
    SessionEntry? GetLruSession(string nodeName);
    int ActiveCountOnNode(string nodeName);
    int ActiveCount { get; }
    List<string> GetStaleSessionIds(double timeoutSeconds);
    int EvictStale(double timeoutSeconds);
    void Remove(string sessionId);
    Dictionary<string, object> AllSessions();
    Task RestoreFromStoreAsync(string storeHost, int storePort, CancellationToken ct);
    void ClearAll();
}

public interface IWorkerTracker
{
    void InitWorker(string name);
    void InitWorker(string name, int slots);

    bool Acquire(string name, string role = "decode");
    void Release(string name);
    void OnError(string name);
    void OnSuccess(string name);
    void MarkUnhealthy(string name);
    void MarkHealthy(string name);
    List<string> FreeWorkers();
    List<string> BusyWorkers();
    bool IsFree(string name);
    string GetStatus(string name);
    bool IsHealthy(string name);
    double GetElapsedSeconds(string name);
    bool IsExpired(string name, double maxSeconds = 600);
    List<string> AllWorkers { get; }

    bool TryAcquireSlot(string name, out int slotId, string role = "decode");
    void ReleaseSlot(string name, int slotId);
    int FreeSlotCount(string name);
    bool HasFreeSlot(string name);
    int TotalSlots(string name);
    void ReleaseAllSlots(string workerName);
}
