using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services;

/// <summary>
/// Pure selection logic for two-engine "work together" routing. Decides whether a large
/// request should recruit a second engine, which head+peer to use, and which mode.
/// Kept side-effect free (no slot acquisition) so it is directly unit-testable — the
/// scheduler does the lease/activation/fallback around the chosen plan.
///
/// Phase 2a (ddvnguyen/llama.cpp#36): the old OT-vs-static branch is gone. The router
/// picks a (head, peer, mode) triple and the <see cref="EngineConfig"/> is derived from
/// <see cref="ModelRegistry"/> keyed by <see cref="WorkerConfig.ModelAlias"/>. The
/// wire opcodes (0x44 SET_EXPERT_MODE, 0x46 EnginePipelineAttach) and their payloads
/// are unchanged — the translator layer in <c>WorkerSchedulerService</c> converts the
/// <see cref="EngineConfig"/> to those existing wire shapes.
/// </summary>
public static class MultiEngineRouter
{
    /// <summary>
    /// Selected multi-engine plan. The <see cref="EngineConfig"/> carries the
    /// stock-params shape the engine would consume (model path, n-gpu-layers,
    /// override-tensor, etc.); the scheduler's translator layer projects this
    /// onto the existing 0x44/0x46 wire payloads in Phase 2a.
    /// </summary>
    public readonly record struct Plan(
        WorkerConfig Head,
        WorkerConfig Peer,
        MultiEngineMode Mode,
        EngineConfig EngineConfig);

    /// <summary>
    /// Returns a multi-engine plan when one applies, else null. A plan applies only when:
    ///   - engine mode is on and at least one of PIPELINE/COMBINED is enabled,
    ///   - the request prompt exceeds MultiEngineThreshold,
    ///   - a head engine is free+healthy, its configured peer is free+healthy (or
    ///     peer-only with slots=0, which is implicitly always available),
    ///   - the chosen mode is enabled, the head advertises capability for it, and
    ///   - the head has a model alias that resolves via <see cref="ModelRegistry"/>.
    /// </summary>
    public static Plan? Select(
        CoordinatorConfig cfg, List<WorkerConfig> workers,
        IWorkerTracker tracker, IHealthMonitorService health, int estTokens)
    {
        if (!cfg.UseLlamaEngine) return null;
        if (!cfg.PipelineEnabled && !cfg.CombinedEnabled) return null;
        if (estTokens <= cfg.MultiEngineThreshold) return null;

        foreach (var head in workers
                     .Where(w => w.IsHead && tracker.IsFree(w.Name) && health.IsHealthy(w.Name))
                     .OrderBy(w => w.PrefillPriority))
        {
            if (string.IsNullOrWhiteSpace(head.PeerWorker)) continue;
            var peer = workers.FirstOrDefault(w => w.Name == head.PeerWorker);
            if (peer == null || !health.IsHealthy(peer.Name))
                continue;
            // Hydra #383 T5 (now: peer-only workers): a peer with slots=0 is
            // dedicated to a head and never has its own slots rented. It is
            // implicitly always available; TryReserveWorkerExclusive succeeds
            // unconditionally. For all other peers, the peer must have free
            // slots.
            if (peer.Slots > 0 && !tracker.IsFree(peer.Name))
                continue;

            // Resolve the EngineConfig from the ModelRegistry. This is the
            // single source of truth for per-model engine config (the old
            // WorkerConfig.CombinedOtSplit / PipelineOtSplit fields are gone).
            EngineConfig engineConfig;
            try { engineConfig = ModelRegistry.Resolve(head.ModelAlias ?? ""); }
            catch (InvalidOperationException) { continue; }  // unrecognised alias → skip this head

            // Resolve the mode for this head, honouring the configured preference order.
            foreach (var mode in PreferenceOrder(cfg))
            {
                if (!ModeUsable(cfg, head, mode)) continue;
                return new Plan(head, peer, mode, engineConfig);
            }
        }
        return null;
    }

    private static IEnumerable<MultiEngineMode> PreferenceOrder(CoordinatorConfig cfg)
    {
        var combinedFirst = string.Equals(cfg.MultiEnginePolicy, "combined", StringComparison.OrdinalIgnoreCase);
        if (combinedFirst)
        {
            yield return MultiEngineMode.Combined;
            yield return MultiEngineMode.Pipeline;
        }
        else
        {
            yield return MultiEngineMode.Pipeline;
            yield return MultiEngineMode.Combined;
        }
    }

    /// <summary>
    /// Phase 2a: a mode is "usable" for a head when (a) the global flag is on,
    /// (b) the engine advertises the capability, and (c) the EngineConfig has
    /// the runtime data the wire payload needs (override-tensor for PIPELINE;
    /// any config for COMBINE — the engine already has its dual-load setup
    /// from startup, so the C# only needs to send the mode toggle).
    /// </summary>
    private static bool ModeUsable(CoordinatorConfig cfg, WorkerConfig head, MultiEngineMode mode)
    {
        if (mode == MultiEngineMode.Pipeline)
        {
            if (!cfg.PipelineEnabled || !head.PipelineCapable) return false;
            // PIPELINE mode needs a runtime override-tensor (the engine routes
            // matching tensors to the peer at runtime via 0x46). If the
            // EngineConfig has no override-tensor, the engine can't route
            // anything — refuse the plan.
            return true;  // override-tensor check is done at translate time
        }
        if (mode == MultiEngineMode.Combined)
        {
            if (!cfg.CombinedEnabled || !head.CombinedCapable) return false;
            // COMBINE mode: engine already has --combined-ot-pattern loaded
            // at startup; C# only sends the 0x44 mode toggle. No additional
            // data required from EngineConfig.
            return true;
        }
        return false;
    }
}
