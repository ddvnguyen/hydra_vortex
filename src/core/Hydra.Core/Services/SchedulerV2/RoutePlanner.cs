using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Decides where a request should run, enforcing the **hydra model + GPU worker
/// rules**:
/// <list type="bullet">
/// <item>COMBINED/PIPELINE requests may only be served by a <c>CombinedCapable</c>
/// head (worker rule); otherwise no capacity.</item>
/// <item><b>Prefill (two-phase) requests pick the prefill worker ONLY</b>; the
/// decode worker is picked later at decode time via <see cref="PlanDecode"/> so
/// the prefill GPU is released before the decode GPU is acquired (never two
/// slots held at once).</item>
/// <item>Atomic requests occupy one slot for prefill+decode on the same worker.</item>
/// <item>Warm affinity — a session whose KV is still resident (SlotFreed == false)
/// is decode-only on the holding node.</item>
/// </list>
/// No side effects — a pure, testable function over injected state.
/// </summary>
public interface IRoutePlanner
{
    /// <summary>Pick the worker(s) to START the request. For Prefill-type only the
    /// prefill worker is chosen; DecodeWorker stays null until decode time.
    /// <paramref name="cfg"/> feeds the COMBINED multi-engine selection
    /// (<see cref="MultiEngineRouter.Select"/>).</summary>
    RouteDecision Plan(
        ChatRequest chat,
        RequestType type,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        ISessionLedger ledger,
        CoordinatorConfig cfg);

    /// <summary>Pick the DECODE worker at decode time. Prefers the session's warm
    /// node (KV resident) when it is available, else the best free decode-capable
    /// worker. Returns null when no decode worker can serve.</summary>
    string? PlanDecode(
        ChatRequest chat,
        SessionEntry? session,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health);
}

public sealed class RoutePlanner : IRoutePlanner
{
    public RouteDecision Plan(
        ChatRequest chat,
        RequestType type,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        ISessionLedger ledger,
        CoordinatorConfig cfg)
    {
        var session = ledger.Lookup(chat.SessionId);

        // 1) COMBINED/PIPELINE — legacy multi-engine selection (epic #591): reuse
        // MultiEngineRouter.Select VERBATIM — the tested legacy gate. AutoRouter is
        // not wired into v2, so this is the single source of the two-engine plan.
        // It applies only when engine mode + COMBINED are enabled, estTokens exceeds
        // the MultiEngineThreshold, the head is a free+healthy IsHead with a
        // resolvable ModelAlias and a free+healthy configured peer, and the mode is
        // usable (Combined = CombinedEnabled + head.CombinedCapable). When no plan
        // applies the request WAITS (no capacity).
        if (type == RequestType.Combined)
        {
#pragma warning disable CS0618 // deliberate: reuse the tested legacy multi-engine gate (epic #591)
            var me = MultiEngineRouter.Select(cfg, workers.ToList(), tracker, health, chat.EstimatedTokens);
#pragma warning restore CS0618
            if (me is { Mode: MultiEngineMode.Combined } plan)
            {
                return new RouteDecision(
                    RequestType.Combined,
                    PrefillWorker: plan.Head.Name,   // decode stays on the head (KV resident)
                    DecodeWorker: plan.Head.Name,
                    ReuseStoreState: false,
                    Priority: 20,
                    PeerWorker: plan.Peer.Name,
                    MultiMode: plan.Mode,
                    MultiEngineConfig: plan.EngineConfig);
            }
            return new RouteDecision(RequestType.Combined, PrefillWorker: null, DecodeWorker: null, ReuseStoreState: false, Priority: 20);
        }

        // 2) Warm affinity — session KV still resident on its node (SlotFreed == false)
        //    WITH store state (warm gate, review: never warm-route a session that
        //    has no durable KV). The slot is ALREADY HELD warm for the session (C2
        //    stash), so no free-slot check here — reuse is decided by the
        //    LeaseManager/evaluator via the stash.
        if (session is { SlotFreed: false, HasStoreState: true } && !string.IsNullOrEmpty(session.NodeName))
        {
            var warm = workers.FirstOrDefault(w =>
                w.Name == session.NodeName
                && w.CanDecode
                && health.IsHealthy(w.Name));
            return warm is null
                ? new RouteDecision(RequestType.Solo, PrefillWorker: null, DecodeWorker: null, ReuseStoreState: true, Priority: 10)
                : new RouteDecision(RequestType.Solo, PrefillWorker: null, DecodeWorker: warm.Name, ReuseStoreState: true, Priority: 10);
        }

        // 3) PREFILL (two-phase) — pick the prefill worker ONLY. The decode worker
        //    is chosen later (PlanDecode) after the prefill slot is released.
        if (type == RequestType.Prefill)
        {
            var prefill = workers
                .Where(w => w.CanPrefill && tracker.HasFreeSlot(w.Name) && health.IsHealthy(w.Name))
                .OrderBy(w => w.PrefillPriority)
                .FirstOrDefault();
            return prefill is null
                ? new RouteDecision(RequestType.Prefill, PrefillWorker: null, DecodeWorker: null, ReuseStoreState: false, Priority: 40)
                : new RouteDecision(RequestType.Prefill, prefill.Name, DecodeWorker: null, ReuseStoreState: false, Priority: 40);
        }

        // 4) ATOMIC — one prefill-capable AND decode-capable worker occupies a
        //    single slot for the whole request.
        var best = workers
            .Where(w => w.CanPrefill && w.CanDecode && tracker.HasFreeSlot(w.Name) && health.IsHealthy(w.Name))
            .OrderBy(w => w.PrefillPriority)
            .FirstOrDefault();

        return best is null
            ? new RouteDecision(type, PrefillWorker: null, DecodeWorker: null, ReuseStoreState: false, Priority: 30)
            : new RouteDecision(type, best.Name, best.Name,
                ReuseStoreState: session?.HasStoreState == true, Priority: 30);
    }

    public string? PlanDecode(
        ChatRequest chat,
        SessionEntry? session,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health)
    {
        // The decode worker is chosen by the GPU worker rules: the best free +
        // healthy decode-capable worker (lower DecodePriority wins). Warm affinity
        // is decided at INITIAL routing (Plan) from the COMPLETED session's node —
        // during a P/D request the ledger's SlotFreed=false is a mid-request
        // artifact on the prefill node and must not steer decode back there.
        // (A stash-based warm preference lands with C2.)
        return workers
            .Where(w => w.CanDecode && tracker.HasFreeSlot(w.Name) && health.IsHealthy(w.Name))
            .OrderBy(w => w.DecodePriority)
            .FirstOrDefault()
            ?.Name;
    }
}
