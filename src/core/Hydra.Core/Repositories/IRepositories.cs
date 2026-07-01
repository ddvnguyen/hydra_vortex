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

    /// <summary>
    /// Succeeds only if the worker is healthy, has NO slots in use (all free), and
    /// is not already exclusively reserved. Marks the worker exclusive so that
    /// subsequent <see cref="TryAcquireSlot"/>, <see cref="HasFreeSlot"/>,
    /// <see cref="FreeSlotCount"/>, and <see cref="IsFree"/> all report it
    /// unavailable — no other request (SOLO or another COMBINED/PIPELINE) can
    /// dispatch compute onto this GPU while the reservation is held.
    ///
    /// This is the per-GPU admission gate for two-engine "work together" modes,
    /// enforcing principle P1 (one GPU = one task) and resolving the
    /// concurrent-load CUDA crash from ddvnguyen/llama.cpp#21 by construction:
    /// the peer GPU is exclusively held by the COMBINED head, so the peer's own
    /// SOLO decode never runs while the head is dispatching expert compute into
    /// it.
    /// </summary>
    bool TryReserveWorkerExclusive(string name);

    /// <summary>
    /// Releases an exclusive reservation made by <see cref="TryReserveWorkerExclusive"/>.
    /// Idempotent. No-op if the worker was not reserved. Called on the peer's
    /// request lifecycle: completion, head-lease expiry, peer-crash degrade to
    /// solo, and the <c>ReportsSolo</c> activate-degrade path.
    /// </summary>
    void ReleaseWorkerExclusive(string name);

    /// <summary>True if the worker is currently exclusively reserved (see <see cref="TryReserveWorkerExclusive"/>).</summary>
    bool IsExclusiveReserved(string name);
}
