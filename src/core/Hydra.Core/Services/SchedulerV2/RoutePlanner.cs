using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Decides where a request should run. Single responsibility: turn request +
/// live system state (tracker, health, ledger) into a <see cref="RouteDecision"/>.
/// No side effects — a pure, testable function over injected state.
/// </summary>
public interface IRoutePlanner
{
    RouteDecision Plan(
        ChatRequest chat,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        ISessionLedger ledger);
}

public sealed class RoutePlanner : IRoutePlanner
{
    public RouteDecision Plan(
        ChatRequest chat,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        ISessionLedger ledger)
    {
        var session = ledger.Lookup(chat.SessionId);

        // 1) Warm affinity — session KV still resident on its node (SlotFreed == false).
        //    Decode-only follow-up on the same node; no store round-trip.
        if (session is { SlotFreed: false } && !string.IsNullOrEmpty(session.NodeName))
        {
            var warm = workers.FirstOrDefault(w =>
                w.Name == session.NodeName
                && w.CanDecode
                && tracker.HasFreeSlot(w.Name)
                && health.IsHealthy(w.Name));
            if (warm is not null)
                return new RouteDecision(RequestType.Solo, warm.Name, warm.Name,
                    ReuseStoreState: true, Priority: 10); // solo follow-up (classifier ladder)
        }

        // 2) Cold — one prefill-capable AND decode-capable worker handles the whole
        //    request on a single slot. (P/D split across nodes is WP3 parity scope.)
        var best = workers
            .Where(w => w.CanPrefill && w.CanDecode && tracker.HasFreeSlot(w.Name) && health.IsHealthy(w.Name))
            .OrderBy(w => w.PrefillPriority)
            .FirstOrDefault();

        return best is null
            ? new RouteDecision(RequestType.Atomic, PrefillWorker: "", DecodeWorker: null, ReuseStoreState: false, Priority: 30)
            : new RouteDecision(RequestType.Atomic, best.Name, best.Name,
                ReuseStoreState: session?.HasStoreState == true, Priority: 30);
    }
}
