using System.Collections.Concurrent;
using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Slot/lease lifecycle for the v2 scheduler. Single responsibility: acquire and
/// release GPU-slot leases, and HOLD a session's decode slot warm after a
/// completed request (C2 warm-lease stash — a CORE Hydra function, enabling
/// warm-affinity reuse and migration).
///
/// <para><b>Warm semantics</b>: a non-streaming request's decode slot is STASHED
/// keyed by session (the slot stays held), so the next turn routes straight to
/// decode on the warm node. Streaming requests release their slot at
/// <c>NotifyStreamComplete</c> (no warm lease). The stash is evictable via
/// <see cref="EvictWarm"/>. <see cref="WarmLeaseCount"/> counts the stash.</para>
///
/// <para><b>Replace ordering</b>: when a session already holds a warm lease, the
/// new lease is placed FIRST, then the stale one is disposed (legacy
/// <c>_warmLeases[sessionId] = lease</c> ordering) — this reproduces the
/// harness's coarse per-worker busy signal (warm_affinity_on p100=0).</para>
/// </summary>
public interface ILeaseManager
{
    /// <summary>Acquire a slot lease on <paramref name="worker"/>, or null when no slot is free.</summary>
    SlotLease? TryAcquire(string worker, string sessionId);

    /// <summary>Release a lease (idempotent; null-safe).</summary>
    void Release(SlotLease? lease);

    /// <summary>Reserve a worker EXCLUSIVELY (no slot) as a COMBINED peer (epic
    /// #591) — the peer GPU may not serve any other request while the reservation
    /// is held (P1: one GPU = one task). Backed by the tracker's
    /// <see cref="IWorkerTracker.TryReserveWorkerExclusive"/>. Succeeds only when
    /// the peer is healthy, has NO slots in use, and is not already reserved.</summary>
    bool TryReservePeer(string worker);

    /// <summary>Release a peer reservation (idempotent; null-safe). Disposing the
    /// <see cref="ExclusivePeerReservation"/> releases the tracker's exclusive
    /// flag, returning the peer GPU to service.</summary>
    void ReleasePeer(IPeerReservation? lease);

    /// <summary>True when the decision's START worker (prefill, or decode for
    /// Solo/warm) can serve: a warm-held slot for the session is reusable, else a
    /// free slot is required.</summary>
    bool HasCapacity(RouteDecision decision, string sessionId);

    /// <summary>Hold a slot warm for the session (C2). Replaces + disposes any
    /// prior warm lease for the session (new first, then stale).</summary>
    void Stash(string sessionId, SlotLease lease);

    /// <summary>Take the session's warm lease out of the stash (it is now in-flight
    /// for the current turn), or null when none is held.</summary>
    SlotLease? TakeWarm(string sessionId);

    /// <summary>Peek the session's warm lease without removing it (affinity checks).</summary>
    SlotLease? TryGetWarm(string sessionId);

    /// <summary>Release + remove the session's warm lease.</summary>
    void EvictWarm(string sessionId);

    /// <summary>Remove + return the OLDEST warm lease (by <see cref="SlotLease.CreatedAt"/>),
    /// or null when no warm lease is held. Used for on-demand eviction under slot
    /// pressure (review #5) — the caller saves + erases the slot before disposing.</summary>
    bool TryTakeOldestWarm(out string sessionId, out SlotLease lease);

    /// <summary>Number of currently stashed warm leases.</summary>
    int WarmLeaseCount { get; }
}

public sealed class LeaseManager : ILeaseManager
{
    private readonly IWorkerTracker _tracker;
    private readonly ConcurrentDictionary<string, SlotLease> _warm = new(StringComparer.Ordinal);

    public LeaseManager(IWorkerTracker tracker) => _tracker = tracker;

    public int WarmLeaseCount => _warm.Count;

    public bool HasCapacity(RouteDecision decision, string sessionId)
    {
        if (!decision.HasCapacity)
            return false;
        // Warm reuse: the session's slot is already HELD by the stash — no free slot needed.
        if (_warm.ContainsKey(sessionId))
            return true;
        var worker = decision.PrefillWorker ?? decision.DecodeWorker;
        return !string.IsNullOrEmpty(worker) && _tracker.HasFreeSlot(worker);
    }

    public SlotLease? TryAcquire(string worker, string sessionId)
        => _tracker.TryAcquireSlot(worker, out var slot, "prefill")
            ? new SlotLease(worker, slot, sessionId, LeaseLifetime.Long, _tracker)
            : null;

    public void Release(SlotLease? lease)
    {
        if (lease is not null)
            _ = lease.DisposeAsync(); // tracker slot returns to the pool
    }

    public bool TryReservePeer(string worker) => _tracker.TryReserveWorkerExclusive(worker);

    public void ReleasePeer(IPeerReservation? lease)
    {
        if (lease is not null)
            _ = lease.DisposeAsync(); // ExclusivePeerReservation.DisposeAsync releases the exclusive flag
    }

    public void Stash(string sessionId, SlotLease lease)
    {
        var prev = _warm.GetOrAdd(sessionId, lease);
        if (!ReferenceEquals(prev, lease))
        {
            // Replace ordering: put the NEW lease in place first, then dispose the
            // stale one (its ReleaseSlot clears the worker's coarse busy flag).
            _warm[sessionId] = lease;
            _ = prev.DisposeAsync();
        }
    }

    public SlotLease? TakeWarm(string sessionId)
        => _warm.TryRemove(sessionId, out var lease) ? lease : null;

    public SlotLease? TryGetWarm(string sessionId)
        => _warm.TryGetValue(sessionId, out var lease) ? lease : null;

    public void EvictWarm(string sessionId)
    {
        if (_warm.TryRemove(sessionId, out var lease))
            _ = lease.DisposeAsync();
    }

    public bool TryTakeOldestWarm(out string sessionId, out SlotLease lease)
    {
        SlotLease? oldest = null;
        string? oldestKey = null;
        foreach (var (key, value) in _warm)
        {
            if (oldest is null || value.CreatedAt < oldest.CreatedAt)
            {
                oldest = value;
                oldestKey = key;
            }
        }
        if (oldest is not null && oldestKey is not null && _warm.TryRemove(oldestKey, out lease))
        {
            sessionId = oldestKey;
            return true;
        }
        sessionId = "";
        lease = null!;
        return false;
    }
}
