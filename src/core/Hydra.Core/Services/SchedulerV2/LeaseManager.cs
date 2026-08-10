using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Slot/lease lifecycle for the v2 scheduler. Single responsibility: acquire and
/// release GPU-slot leases (and, later, peer reservations) so a running request
/// always owns capacity. Wraps the shared <see cref="IWorkerTracker"/> repository
/// so slot accounting stays consistent with health monitoring and session eviction.
/// </summary>
public interface ILeaseManager
{
    /// <summary>Acquire a slot lease on <paramref name="worker"/>, or null when no slot is free.</summary>
    SlotLease? TryAcquire(string worker, string sessionId);

    /// <summary>Release a lease (idempotent; null-safe).</summary>
    void Release(SlotLease? lease);

    /// <summary>True when the decision's START worker (prefill, or decode for
    /// Solo/warm) still has a free slot.</summary>
    bool HasCapacity(RouteDecision decision);
}

public sealed class LeaseManager : ILeaseManager
{
    private readonly IWorkerTracker _tracker;

    public LeaseManager(IWorkerTracker tracker) => _tracker = tracker;

    public bool HasCapacity(RouteDecision decision)
    {
        if (!decision.HasCapacity)
            return false;
        var worker = decision.PrefillWorker ?? decision.DecodeWorker;
        return !string.IsNullOrEmpty(worker) && _tracker.HasFreeSlot(worker);
    }

    public SlotLease? TryAcquire(string worker, string sessionId)
        => _tracker.TryAcquireSlot(worker, out var slot, "prefill")
            ? new SlotLease(worker, slot, sessionId, LeaseLifetime.Short, _tracker)
            : null;

    public void Release(SlotLease? lease)
    {
        if (lease is not null)
            _ = lease.DisposeAsync(); // tracker slot returns to the pool; warm stays resident
    }
}
