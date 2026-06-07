using Hydra.Store.Repositories;

namespace Hydra.Store.Models;

/// <summary>
/// SlotErase policy: SlotErase RPC is NEVER sent on normal decode completion.
/// Slots always stay warm unless the Coordinator explicitly decides to repurpose.
///
/// SlotErase fires ONLY in these cases:
///   1. Stuck slot detected by health monitor (is_processing && n_remain==0, 3+ cycles)
///   2. n_past guard triggered (new request context smaller than cached, cache invalid)
///   3. LRU eviction (capacity pressure, KV has been saved to Store first)
///   4. Session migration (save→erase→restore on different worker)
///   5. Worker teardown/shutdown
///   6. Client cancels request mid-processing
///   7. Cold path prefill slot after KV saved to Store
///
/// Warm/affinity slots: decode lease lives in _warmLeases across turns.
/// DisposeAsync only releases the tracker claim — never sends RPC.
/// </summary>
public enum LeaseLifetime
{
    Short,
    Long
}

public sealed class SlotLease : IAsyncDisposable
{
    private readonly IWorkerTracker _tracker;
    private bool _disposed;

    public string WorkerName { get; }
    public int SlotId { get; }
    public string SessionId { get; }
    public LeaseLifetime Lifetime { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public SlotLease(string workerName, int slotId, string sessionId,
        LeaseLifetime lifetime, IWorkerTracker tracker)
    {
        WorkerName = workerName;
        SlotId = slotId;
        SessionId = sessionId;
        Lifetime = lifetime;
        _tracker = tracker;
    }

    /// <summary>
    /// Releases the slot back to the tracker pool.
    /// Does NOT send SlotErase RPC — warm slots stay resident.
    /// Only EvictWarmSessionAsync calls SlotErase explicitly.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        var worker = WorkerName;
        var slot = SlotId;
        _tracker.ReleaseSlot(worker, slot);
        return ValueTask.CompletedTask;
    }
}
