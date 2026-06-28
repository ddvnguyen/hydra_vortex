namespace Hydra.Core.Models;

/// <summary>
/// A reservation on a peer engine held for the duration of a two-engine
/// "work together" request (PIPELINE / COMBINED). Disposed on the request
/// lifecycle: completion, head-lease expiry, peer-crash degrade to solo, and
/// the <c>ReportsSolo</c> activate-degrade path. The two implementations are
/// <see cref="SlotLease"/> (legacy, slot-owning) and
/// <see cref="ExclusivePeerReservation"/> (P3.0+ per-GPU exclusivity, no slot).
/// </summary>
public interface IPeerReservation : IAsyncDisposable
{
    string WorkerName { get; }
}

/// <summary>
/// P3.0+ per-GPU exclusive peer reservation. Holding this on a worker means
/// "no other request (SOLO or another COMBINED/PIPELINE) may dispatch work
/// onto this GPU." Backed by <see cref="Repositories.IWorkerTracker"/>'s
/// <c>ExclusiveReserved</c> flag — disposing releases that flag. The peer
/// keeps no slot in this mode; the head still owns its own head slot via a
/// normal <see cref="SlotLease"/>.
/// </summary>
public sealed class ExclusivePeerReservation : IPeerReservation
{
    private readonly Repositories.IWorkerTracker _tracker;
    private bool _disposed;

    public string WorkerName { get; }

    public ExclusivePeerReservation(string workerName, Repositories.IWorkerTracker tracker)
    {
        WorkerName = workerName;
        _tracker = tracker;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _tracker.ReleaseWorkerExclusive(WorkerName);
        return ValueTask.CompletedTask;
    }
}
