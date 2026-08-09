using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Decides where a request should run. Single responsibility: turn request +
/// request type + live system state (tracker, health, ledger) into a
/// <see cref="RouteDecision"/>, enforcing the **hydra model + GPU worker rules**:
/// <list type="bullet">
/// <item>COMBINED/PIPELINE requests may only be served by a
/// <c>CombinedCapable</c> head (worker rule); otherwise no capacity.</item>
/// <item>Warm affinity — a session whose KV is still resident (SlotFreed == false)
/// is decode-only on the holding node.</item>
/// <item>Cold atomic — prefill AND decode must both be satisfiable by the chosen
/// worker (<c>CanPrefill &amp;&amp; CanDecode</c>, healthy, free slot).</item>
/// </list>
/// No side effects — a pure, testable function over injected state.
/// </summary>
public interface IRoutePlanner
{
    RouteDecision Plan(
        ChatRequest chat,
        RequestType type,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        ISessionLedger ledger);
}

public sealed class RoutePlanner : IRoutePlanner
{
    public RouteDecision Plan(
        ChatRequest chat,
        RequestType type,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        ISessionLedger ledger)
    {
        var session = ledger.Lookup(chat.SessionId);

        // 1) COMBINED/PIPELINE — worker rule: only a CombinedCapable head may serve.
        if (type == RequestType.Combined)
        {
            var head = workers.FirstOrDefault(w =>
                w.CombinedCapable
                && w.CanPrefill && w.CanDecode
                && tracker.HasFreeSlot(w.Name)
                && health.IsHealthy(w.Name));
            return head is null
                ? new RouteDecision(RequestType.Combined, PrefillWorker: "", DecodeWorker: null, ReuseStoreState: false, Priority: 20)
                : new RouteDecision(RequestType.Combined, head.Name, head.Name, ReuseStoreState: false, Priority: 20);
        }

        // 2) Warm affinity — session KV still resident on its node (SlotFreed == false).
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

        // 3) Cold — one prefill-capable AND decode-capable worker handles the whole
        //    request on a single slot. (P/D split across nodes is WP3 parity scope.)
        var best = workers
            .Where(w => w.CanPrefill && w.CanDecode && tracker.HasFreeSlot(w.Name) && health.IsHealthy(w.Name))
            .OrderBy(w => w.PrefillPriority)
            .FirstOrDefault();

        return best is null
            ? new RouteDecision(type, PrefillWorker: "", DecodeWorker: null, ReuseStoreState: false, Priority: 30)
            : new RouteDecision(type, best.Name, best.Name,
                ReuseStoreState: session?.HasStoreState == true, Priority: 30);
    }
}
