using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Serilog;

namespace Hydra.Core.Services;

/// <summary>
/// Single routing path for model selection. Replaces Router.PickBest* and MultiEngineRouter.Select.
/// Algorithm: STEP 0 warm affinity → STEP 1 candidate window → STEP 2 hardware feasibility →
/// STEP 3 swap-cost preference → STEP 4 worker plan.
/// </summary>
public static class AutoRouter
{
    private static readonly ILogger _log = Log.ForContext(typeof(AutoRouter));

    /// <summary>Result of model resolution: the chosen model alias, head worker, optional peer/decode, and merged EngineConfig.</summary>
    public readonly record struct Result(
        string ModelAlias,
        WorkerConfig Head,
        WorkerConfig? Peer,
        WorkerConfig? DecodeWorker,
        EngineConfig EngineConfig,
        string? Mode);

    /// <summary>
    /// Resolve the best model and worker plan for a request.
    /// </summary>
    /// <param name="cfg">Coordinator config with workers list.</param>
    /// <param name="loader">Model config loader (provides model templates + GPU specs).</param>
    /// <param name="tracker">Worker slot tracker.</param>
    /// <param name="health">Health monitor.</param>
    /// <param name="ledger">Session ledger for warm affinity.</param>
    /// <param name="sessionId">Request session ID.</param>
    /// <param name="promptTokens">Estimated prompt token count.</param>
    /// <param name="estTotalContext">Estimated total context (prompt + history + output).</param>
    /// <param name="requestedModel">Model from request body (alias or "hydra-auto").</param>
    /// <returns>Result with model + workers, or null if nothing serviceable.</returns>
    public static Result? Resolve(
        CoordinatorConfig cfg,
        ModelConfigLoader loader,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        ISessionLedger ledger,
        string sessionId,
        int promptTokens,
        int estTotalContext,
        string? requestedModel)
    {
        // If a specific model was requested (not auto), skip auto-routing logic
        // but still build the worker plan for that model.
        if (!string.IsNullOrEmpty(requestedModel) && requestedModel != "hydra-auto")
        {
            return ResolveExplicit(cfg, loader, tracker, health, requestedModel, promptTokens, estTotalContext);
        }

        // STEP 0: Warm session affinity (highest priority, D7)
        var warmResult = TryWarmAffinity(cfg, loader, tracker, health, ledger, sessionId);
        if (warmResult != null)
            return warmResult.Value;

        // STEP 1: Candidate models (structured window)
        var candidates = GetCandidates(cfg, loader, health, promptTokens, estTotalContext);
        if (candidates.Count == 0)
        {
            _log.Warning("auto_route_no_candidates Tokens={Tokens} Context={Context}", promptTokens, estTotalContext);
            return null;
        }

        // STEP 2: Hardware feasibility
        var feasible = GetFeasible(cfg, loader, tracker, health, candidates);
        if (feasible.Count == 0)
        {
            _log.Warning("auto_route_no_feasible Candidates={Count}", candidates.Count);
            return null;
        }

        // STEP 3: Swap-cost preference (D1 + D10)
        var chosen = ChooseBySwapCost(feasible, health, loader);

        // STEP 4: Build worker plan
        return BuildPlan(cfg, loader, tracker, health, chosen);
    }

    // ── STEP 0: Warm Affinity ──────────────────────────────────────

    private static Result? TryWarmAffinity(
        CoordinatorConfig cfg, ModelConfigLoader loader,
        IWorkerTracker tracker, IHealthMonitorService health,
        ISessionLedger ledger, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        var entry = ledger.Lookup(sessionId);
        if (entry == null || string.IsNullOrEmpty(entry.BoundModel)) return null;

        // Find the worker this session is warm on
        var worker = cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
        if (worker == null || !tracker.IsFree(worker.Name) || !health.IsHealthy(worker.Name))
            return null;

        // Verify the warm slot is still valid (sync call to async helper)
        var slotOk = Router.VerifyWarmSlotAsync(worker, entry, traceId: "")
            .GetAwaiter().GetResult();
        if (!slotOk) return null;

        _log.Information("auto_route_warm_affinity Session={Session} Model={Model} Node={Node}",
            sessionId, entry.BoundModel, worker.Name);

        var engineConfig = loader.ResolveEngineConfig(entry.BoundModel);
        return new Result(entry.BoundModel, worker, null, null, engineConfig, "warm");
    }

    // ── STEP 1: Candidate Filtering ────────────────────────────────

    private static List<KeyValuePair<string, ModelTemplate>> GetCandidates(
        CoordinatorConfig cfg, ModelConfigLoader loader,
        IHealthMonitorService health, int promptTokens, int estTotalContext)
    {
        var result = new List<KeyValuePair<string, ModelTemplate>>();
        foreach (var (alias, template) in loader.GetAllModels())
        {
            var routing = template.Routing;
            if (routing == null || !routing.AutoEligible) continue;
            if (promptTokens < routing.MinPromptTokens || promptTokens > routing.MaxPromptTokens) continue;
            if (estTotalContext > routing.MaxContextTokens) continue;

            // Check requires_workers are healthy
            if (routing.RequiresWorkers?.Count > 0)
            {
                var allHealthy = routing.RequiresWorkers.All(w => health.IsHealthy(w));
                if (!allHealthy) continue;
            }

            result.Add(new KeyValuePair<string, ModelTemplate>(alias, template));
        }
        return result;
    }

    // ── STEP 2: Hardware Feasibility ───────────────────────────────

    private static List<(string Alias, ModelTemplate Template, WorkerConfig Head, WorkerConfig? Peer, WorkerConfig? DecodeWorker)> GetFeasible(
        CoordinatorConfig cfg, ModelConfigLoader loader,
        IWorkerTracker tracker, IHealthMonitorService health,
        List<KeyValuePair<string, ModelTemplate>> candidates)
    {
        var result = new List<(string Alias, ModelTemplate Template, WorkerConfig Head, WorkerConfig? Peer, WorkerConfig? DecodeWorker)>();

        foreach (var (alias, template) in candidates)
        {
            var reqs = template.Requirements;
            if (reqs == null) continue;

            // Find a head worker that meets requirements
            foreach (var head in cfg.Workers.Where(w => w.IsHead && tracker.IsFree(w.Name) && health.IsHealthy(w.Name)))
            {
                if (!MeetsRequirements(head, reqs, loader)) continue;

                // For COMBINED mode, need a peer
                WorkerConfig? peer = null;
                if (reqs.PeerRequirements != null)
                {
                    var peerReqs = reqs.PeerRequirements;
                    peer = cfg.Workers.FirstOrDefault(w => w.Name != head.Name
                        && tracker.IsFree(w.Name) && health.IsHealthy(w.Name)
                        && MeetsRequirements(w, peerReqs, loader));
                    if (peer == null) continue;
                }

                // For P/D mode, need a decode worker
                WorkerConfig? decodeWorker = null;
                if (reqs.DecodeRequirements != null)
                {
                    var decodeReqs = reqs.DecodeRequirements;
                    decodeWorker = cfg.Workers.FirstOrDefault(w => w.Name != head.Name
                        && tracker.IsFree(w.Name) && health.IsHealthy(w.Name)
                        && MeetsRequirements(w, decodeReqs, loader));
                    if (decodeWorker == null) continue;
                }

                result.Add((alias, template, head, peer, decodeWorker));
            }
        }
        return result;
    }

    private static bool MeetsRequirements(WorkerConfig worker, ModelRequirements reqs, ModelConfigLoader loader)
    {
        // Look up the GPU spec from the loader (worker.Gpu may not be populated at startup)
        var gpu = worker.Gpu ?? loader.GetGpuSpec(worker.GpuRef ?? worker.Name);
        if (gpu == null) return false;
        if (gpu.VramMb < reqs.MinVramMb) return false;
        if (reqs.MinComputeTflops.HasValue && gpu.ComputeTflops < reqs.MinComputeTflops.Value) return false;
        if (reqs.MinBandwidthGbps.HasValue && gpu.BandwidthGbps < reqs.MinBandwidthGbps.Value) return false;
        if (!gpu.HasCapability(reqs.RequiredCapabilities)) return false;
        return true;
    }

    // ── STEP 3: Swap-Cost Preference ───────────────────────────────

    private static (string Alias, ModelTemplate Template) ChooseBySwapCost(
        List<(string Alias, ModelTemplate Template, WorkerConfig Head, WorkerConfig? Peer, WorkerConfig? DecodeWorker)> feasible,
        IHealthMonitorService health,
        ModelConfigLoader loader)
    {
        var autoPolicy = loader.GetAutoRoutingPolicy();
        var budget = autoPolicy?.SwapCostBudgetS ?? 30;

        // Group by quality tier (highest first)
        var byTier = feasible
            .GroupBy(f => f.Template.QualityTier)
            .OrderByDescending(g => g.Key);

        foreach (var tierGroup in byTier)
        {
            // Check if any model in this tier is already resident
            var resident = tierGroup.FirstOrDefault(f => IsResident(f.Head, f.Template, health));
            if (resident.Template != null)
                return (resident.Alias, resident.Template);

            // No resident in this tier — check if swap cost is within budget
            var cheapest = tierGroup.OrderBy(f => f.Template.LoadTimeS).First();
            if (cheapest.Template.LoadTimeS <= budget)
                return (cheapest.Alias, cheapest.Template);
        }

        // Fallback: highest tier available
        var fallback = byTier.First().First();
        return (fallback.Alias, fallback.Template);
    }

    private static bool IsResident(WorkerConfig head, ModelTemplate template, IHealthMonitorService health)
    {
        var nodeInfo = health.GetNodeInfo(head.Name);
        if (nodeInfo == null || string.IsNullOrEmpty(nodeInfo.CurrentModel)) return false;
        // Check if the loaded model matches either prefill or decode file name
        return nodeInfo.CurrentModel.Contains(template.PrefillModelFileName ?? "", StringComparison.OrdinalIgnoreCase)
            || nodeInfo.CurrentModel.Contains(template.DecodeModelFileName ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ── STEP 4: Build Worker Plan ──────────────────────────────────

    private static Result? BuildPlan(
        CoordinatorConfig cfg, ModelConfigLoader loader,
        IWorkerTracker tracker, IHealthMonitorService health,
        (string Alias, ModelTemplate Template) chosen)
    {
        // Find head + peer/decode worker for this model
        var candidates = new List<KeyValuePair<string, ModelTemplate>>
        {
            new(chosen.Alias, chosen.Template)
        };
        var feasible = GetFeasible(cfg, loader, tracker, health, candidates);
        var plan = feasible.FirstOrDefault();
        if (plan.Template == null) return null;

        var mode = plan.Peer != null ? "combined" : plan.DecodeWorker != null ? "pd" : "solo";
        var engineConfig = loader.ResolveEngineConfig(chosen.Alias);

        _log.Information("auto_route_resolved Model={Model} Head={Head} Peer={Peer} Decode={Decode} Mode={Mode}",
            chosen.Alias, plan.Head.Name, plan.Peer?.Name ?? "-", plan.DecodeWorker?.Name ?? "-", mode);

        return new Result(chosen.Alias, plan.Head, plan.Peer, plan.DecodeWorker, engineConfig, mode);
    }

    // ── Explicit Model Request ─────────────────────────────────────

    private static Result? ResolveExplicit(
        CoordinatorConfig cfg, ModelConfigLoader loader,
        IWorkerTracker tracker, IHealthMonitorService health,
        string requestedModel, int promptTokens, int estTotalContext)
    {
        var template = loader.GetModelTemplate(requestedModel);
        if (template == null)
        {
            _log.Warning("auto_route_unknown_model Model={Model}", requestedModel);
            return null;
        }

        var candidates = new List<KeyValuePair<string, ModelTemplate>>
        {
            new(requestedModel, template)
        };
        var feasible = GetFeasible(cfg, loader, tracker, health, candidates);
        var plan = feasible.FirstOrDefault();
        if (plan.Template == null) return null;

        var mode = plan.Peer != null ? "combined" : plan.DecodeWorker != null ? "pd" : "solo";
        var engineConfig = loader.ResolveEngineConfig(requestedModel);

        return new Result(requestedModel, plan.Head, plan.Peer, plan.DecodeWorker, engineConfig, mode);
    }
}
