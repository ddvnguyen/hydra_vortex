using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services;

/// <summary>
/// Pure selection logic for two-engine "work together" routing. Decides whether a large
/// request should recruit a second engine, which head+peer to use, and which mode.
/// Kept side-effect free (no slot acquisition) so it is directly unit-testable — the
/// scheduler does the lease/activation/fallback around the chosen plan.
/// </summary>
public static class MultiEngineRouter
{
    public readonly record struct Plan(WorkerConfig Head, WorkerConfig Peer, MultiEngineMode Mode, string OtSplit);

    /// <summary>
    /// Returns a multi-engine plan when one applies, else null. A plan applies only when:
    ///   - engine mode is on and at least one of PIPELINE/COMBINED is enabled,
    ///   - the request prompt exceeds MultiEngineThreshold,
    ///   - a head engine is free+healthy, its configured peer is free+healthy,
    ///   - the chosen mode is enabled, the head advertises capability for it, and an
    ///     --override-tensor split is configured for that mode.
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
            if (peer == null || !tracker.IsFree(peer.Name) || !health.IsHealthy(peer.Name))
                continue;

            // Resolve the mode for this head, honouring the configured preference order.
            foreach (var mode in PreferenceOrder(cfg))
            {
                if (!ModeUsable(cfg, head, mode, out var split)) continue;
                return new Plan(head, peer, mode, split);
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

    private static bool ModeUsable(CoordinatorConfig cfg, WorkerConfig head, MultiEngineMode mode, out string split)
    {
        split = "";
        if (mode == MultiEngineMode.Pipeline)
        {
            if (!cfg.PipelineEnabled || !head.PipelineCapable) return false;
            if (string.IsNullOrWhiteSpace(head.PipelineOtSplit)) return false;
            split = head.PipelineOtSplit!;
            return true;
        }
        if (mode == MultiEngineMode.Combined)
        {
            if (!cfg.CombinedEnabled || !head.CombinedCapable) return false;
            if (string.IsNullOrWhiteSpace(head.CombinedOtSplit)) return false;
            split = head.CombinedOtSplit!;
            return true;
        }
        return false;
    }
}
