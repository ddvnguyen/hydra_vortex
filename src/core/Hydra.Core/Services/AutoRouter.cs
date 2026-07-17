using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Serilog;

namespace Hydra.Core.Services;

/// <summary>
/// Single routing path for model selection. Replaces Router.PickBest* and MultiEngineRouter.Select.
/// Algorithm: STEP 0 warm affinity → STEP 1 candidate window → STEP 2 hardware feasibility →
/// STEP 3 swap-cost preference → STEP 4 worker plan.
///
/// Two-path behavior for default_eligible:
///   FRESH session (no BoundModel): GetFeasible filters out default_eligible=false models
///   so that opt-in models like dense-27b-combined don't win by tier when a lower-tier
///   model (e.g. moe-35b-pd) is the intended auto-routing target.
///   WARM session (existing BoundModel): TryWarmAffinity handles routing; BuildPlan
///   uses default GetFeasible (requireDefaultEligible=false) so swap-cost migration
///   can still upgrade/downgrade.
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
    /// <param name="loader">Model config loader (provides model templates).</param>
    /// <param name="tracker">Worker slot tracker.</param>
    /// <param name="health">Health monitor.</param>
    /// <param name="ledger">Session ledger for warm affinity.</param>
    /// <param name="sessionId">Request session ID.</param>
    /// <param name="promptTokens">Estimated prompt token count.</param>
    /// <param name="estTotalContext">Estimated total context (prompt + history + output).</param>
    /// <param name="requestedModel">Model from request body (alias or "hydra-auto").</param>
    /// <param name="verifySlot">Optional seam for slot verification (tests inject fakes; null = use Router.VerifyWarmSlotAsync).</param>
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
        string? requestedModel,
        Func<WorkerConfig, SessionEntry, string, Task<bool>>? verifySlot = null)
    {
        // If a specific model was requested (not auto), skip auto-routing logic
        // but still build the worker plan for that model.
        if (!string.IsNullOrEmpty(requestedModel) && requestedModel != "hydra-auto")
        {
            return ResolveExplicit(cfg, loader, tracker, health, requestedModel, promptTokens, estTotalContext);
        }

        // STEP 0: Warm session affinity (highest priority, D7)
        var warmResult = TryWarmAffinity(cfg, loader, tracker, health, ledger, sessionId, verifySlot);
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
        // Fresh session (no BoundModel): filter out default_eligible=false models
        // so that models like dense-27b-combined (tier 3, opt-in only) don't win
        // by default when a lower-tier model (e.g. moe-35b-pd) is the intended
        // auto-routing target. Warm sessions (existing BoundModel) keep full
        // feasibility so swap-cost migration can still upgrade/downgrade.
        var feasible = GetFeasible(cfg, loader, tracker, health, candidates, requireDefaultEligible: true);
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
        ISessionLedger ledger, string sessionId,
        Func<WorkerConfig, SessionEntry, string, Task<bool>>? verifySlot = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        var entry = ledger.Lookup(sessionId);
        if (entry == null || string.IsNullOrEmpty(entry.BoundModel)) return null;

        // Find the worker this session is warm on
        var worker = cfg.Workers.FirstOrDefault(w => w.Name == entry.NodeName);
        if (worker == null) return null;

        // PROBE: verify the warm slot is still valid.
        // Two decisions here that must be separated:
        //   1) Which model — MUST stay pinned to BoundModel (mismatch = KV corruption)
        //   2) Which slot / warm-vs-cold — may safely fall back to cold re-prefill on same model
        var probeOk = false;
        if (tracker.IsFree(worker.Name) && health.IsHealthy(worker.Name))
        {
            var verifyFn = verifySlot ?? ((w, e, t) => Router.VerifyWarmSlotAsync(w, e, t));
            probeOk = verifyFn(worker, entry, "")
                .GetAwaiter().GetResult();
        }

        if (probeOk)
        {
            // Happy path: warm slot is live, use it directly
            _log.Information("auto_route_warm_affinity Session={Session} Model={Model} Node={Node}",
                sessionId, entry.BoundModel, worker.Name);
        }
        else
        {
            // Probe failed (slot busy, engine down, network blip, etc.)
            // but we MUST still honor BoundModel. Route a cold re-prefill
            // on the same model — never fall through to a different model
            // which would cause cross-model KV reuse → corruption.
            _log.Information("auto_route_bound_cold_fallback Session={Session} Model={Model} Node={Node}",
                sessionId, entry.BoundModel, worker.Name);
        }

        var engineConfig = loader.ResolveEngineConfig(entry.BoundModel);
        return new Result(entry.BoundModel, worker, null, null, engineConfig, probeOk ? "warm" : "cold_bound");
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
        List<KeyValuePair<string, ModelTemplate>> candidates,
        bool requireDefaultEligible = false)
    {
        var result = new List<(string Alias, ModelTemplate Template, WorkerConfig Head, WorkerConfig? Peer, WorkerConfig? DecodeWorker)>();

        foreach (var (alias, template) in candidates)
        {
            // When requireDefaultEligible is set (fresh session), skip models
            // that are opt-in only (default_eligible=false) — these should not
            // be auto-selected but can still be used by warm session migration.
            if (requireDefaultEligible && template.Routing is { DefaultEligible: false })
                continue;

            var reqs = template.Requirements;
            if (reqs == null) continue;

            // Find a head worker that meets requirements
            foreach (var head in cfg.Workers.Where(w => w.IsHead && tracker.IsFree(w.Name) && health.IsHealthy(w.Name)))
            {
                if (!MeetsRequirements(head, reqs)) continue;

                // For COMBINED mode, need a peer
                WorkerConfig? peer = null;
                if (reqs.PeerRequirements != null)
                {
                    var peerReqs = reqs.PeerRequirements;
                    peer = cfg.Workers.FirstOrDefault(w => w.Name != head.Name
                        && tracker.IsFree(w.Name) && health.IsHealthy(w.Name)
                        && MeetsRequirements(w, peerReqs));
                    if (peer == null) continue;
                }

                // For P/D mode, need a decode worker
                WorkerConfig? decodeWorker = null;
                if (reqs.DecodeRequirements != null)
                {
                    var decodeReqs = reqs.DecodeRequirements;
                    decodeWorker = cfg.Workers.FirstOrDefault(w => w.Name != head.Name
                        && tracker.IsFree(w.Name) && health.IsHealthy(w.Name)
                        && MeetsRequirements(w, decodeReqs));
                    if (decodeWorker == null) continue;
                }

                result.Add((alias, template, head, peer, decodeWorker));
            }
        }
        return result;
    }

    private static bool MeetsRequirements(WorkerConfig worker, ModelRequirements reqs)
    {
        var gpu = worker.Gpu;
        if (gpu != null)
        {
            if (gpu.VramMb < reqs.MinVramMb) return false;
            if (reqs.MinComputeTflops.HasValue && gpu.ComputeTflops < reqs.MinComputeTflops.Value) return false;
            if (reqs.MinBandwidthGbps.HasValue && gpu.BandwidthGbps < reqs.MinBandwidthGbps.Value) return false;
            if (!gpu.HasCapability(reqs.RequiredCapabilities)) return false;
            return true;
        }
        // Fallback: worker has no "gpu" block in workers.json.
        // Use WorkerConfig capability flags as proxy.
        if (reqs.RequiredCapabilities == GpuCapabilities.Combined && !worker.CombinedCapable) return false;
        if (reqs.RequiredCapabilities == GpuCapabilities.Rpc && !worker.CombinedCapable) return false;
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
