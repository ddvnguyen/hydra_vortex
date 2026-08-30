using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Shared;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// The PLAN runner — ONE class for both plan states (epic #591): initial routing
/// (<c>RouteDecision</c>) and the decode-worker handoff (<c>PickDecode</c>) are
/// both "pick where the request runs", so they share this class. The state
/// decides which half runs:
/// <list type="bullet">
/// <item><b>RouteDecision</b> — pick the worker to START on. Prefill-type picks
/// only the prefill worker; Solo (warm) picks the decode worker (KV resident).</item>
/// <item><b>PickDecode</b> — pick the DECODE worker at decode time and swap the
/// slot lease (GPU-utilization rule: the prefill slot is released before the
/// decode slot is acquired; atomic same-slot skips the swap).</item>
/// </list>
/// </summary>
public sealed class PlanRunner : WorkerStateRunner
{
    private readonly IRoutePlanner _planner;
    private readonly ILeaseManager _leases;
    private readonly ISessionLedger _ledger;
    private readonly IReadOnlyList<WorkerConfig> _workers;
    private readonly IWorkerTracker _tracker;
    private readonly IHealthMonitorService _health;
    private readonly CoordinatorConfig _cfg;
    private readonly IWarmSlotVerifier _warmVerifier;

    public PlanRunner(
        IRoutePlanner planner,
        ILeaseManager leases,
        ISessionLedger ledger,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        CoordinatorConfig cfg,
        IWarmSlotVerifier warmVerifier)
    {
        _planner = planner;
        _leases = leases;
        _ledger = ledger;
        _workers = workers;
        _tracker = tracker;
        _health = health;
        _cfg = cfg;
        _warmVerifier = warmVerifier;
    }

    public override WorkItemState State => WorkItemState.RouteDecision;
    public override bool Handles(WorkItemState state)
        => state is WorkItemState.RouteDecision or WorkItemState.PickDecode;

    public override Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
        => ctx.Request.State == WorkItemState.RouteDecision
            ? PlanInitialAsync(ctx)
            : PlanDecodeAsync(ctx);

    private async Task<PhaseResult> PlanInitialAsync(RunnerContext ctx)
    {
        var req = ctx.Request;

        // The plan is decided ONCE before dispatch (SubmitAsync for Solo; the
        // evaluator otherwise) and must NOT be re-derived here on the FIRST entry:
        // re-planning at RouteDecision sees the just-acquired start slot as busy
        // (self-held), which would return "no capacity" for a valid Solo/warm route.
        //
        // C4 retry re-plan: a Prefill retry (Prefill --Retry--> RouteDecision)
        // re-enters with a STALE plan + the same held prefill lease — re-planning
        // against LIVE capacity lets the request escape a failing worker (the held
        // lease pins its worker as busy, so the planner picks a healthy free-slot
        // alternate). The fresh plan is adopted only when it has capacity; the
        // held lease stays valid only when the re-plan lands on the same worker.
        var plan = req.Plan;
        if (req.RetryCount > 0)
        {
            var fresh = _planner.Plan(req.Chat, req.Type, _workers, _tracker, _health, _ledger, _cfg);
            if (fresh.HasCapacity)
                plan = req.Plan = fresh;
        }

        // Warm-slot verification (review #8): when enabled, a warm (Solo) route must
        // VERIFY the warm slot holds the KV before decoding on it. On failure
        // (unreachable / stuck / KV gone), release the warm lease, mark the ledger
        // evicted, and re-route COLD — never decode over a dead slot (#469).
        if (plan.PrefillWorker is null && plan.DecodeWorker is not null && _cfg.WarmSlotVerificationEnabled)
        {
            var warmWorker = Resolve(plan.DecodeWorker);
            var entry = _ledger.Lookup(req.SessionId);
            var isWarm = warmWorker is not null && await _warmVerifier.VerifyAsync(warmWorker, entry, req.TraceId);
            if (!isWarm)
            {
                Serilog.Log.Warning("v2_warm_verify_failed Sid={Sid} Node={Node} — evicting + re-routing cold",
                    req.SessionId, plan.DecodeWorker);
                _leases.Release(req.DecodeLease);
                req.DecodeLease = null;
                _ledger.MarkEvicted(req.SessionId);

                plan = req.Plan = _planner.Plan(req.Chat, req.Type, _workers, _tracker, _health, _ledger, _cfg);
                if (!plan.HasCapacity)
                {
                    req.Error = new InvalidOperationException($"warm verify failed and no cold capacity for session {req.SessionId}");
                    return PhaseResult.Fire(SchedulerEvent.Failed);
                }
                req.PrefillWorker = plan.PrefillWorker is null ? null : Resolve(plan.PrefillWorker);
                req.DecodeWorker = plan.DecodeWorker is null ? null : Resolve(plan.DecodeWorker);
                req.PrefillLease = _leases.TryAcquire(plan.PrefillWorker, req.SessionId);
                if (req.PrefillLease is null)
                {
                    req.Error = new InvalidOperationException($"warm verify failed: no prefill slot on {plan.PrefillWorker}");
                    return PhaseResult.Fire(SchedulerEvent.Failed);
                }
                req.RecordPhase("plan_ms", 0);
                return PhaseResult.Fire(SchedulerEvent.RouteSucceeded); // cold re-route
            }
        }

        req.PrefillWorker = plan.PrefillWorker is null ? null : Resolve(plan.PrefillWorker);
        req.DecodeWorker = plan.DecodeWorker is null ? null : Resolve(plan.DecodeWorker);
        // COMBINED (epic #591): carry the plan's multi-engine selection onto the
        // request — PrefillRunner reads MultiMode/MultiEngineConfig to build the
        // hydra_config dict, and the orchestrator reserved the peer from these
        // fields in RunPipelineAsync. (Non-combined plans carry None/null and just
        // clear any stale fields from a re-plan.)
        req.MultiMode = plan.MultiMode;
        req.MultiEngineConfig = plan.MultiEngineConfig;
        req.PeerWorker = plan.PeerWorker is null ? null : Resolve(plan.PeerWorker);

        // COMBINED retry re-plan (review #2): a Prefill --Retry--> RouteDecision
        // re-plan can adopt a fresh plan whose PEER differs from the peer the
        // orchestrator reserved in RunPipelineAsync. The stale PeerLease still
        // pins the OLD peer (P1: one GPU = one task — but on the WRONG GPU) and
        // the NEW peer was never reserved. Reconcile: release the old
        // reservation and reserve the new peer; a failed reservation fails the
        // request (a combined request must not run without its peer held).
        if (req.RetryCount > 0 && req.MultiMode == MultiEngineMode.Combined)
        {
            var heldPeer = req.PeerLease?.WorkerName;
            var wantedPeer = req.PeerWorker?.Name;
            if (heldPeer != wantedPeer)
            {
                _leases.ReleasePeer(req.PeerLease);
                req.PeerLease = null;
                if (wantedPeer is not null && _leases.TryReservePeer(wantedPeer))
                {
                    req.PeerLease = new ExclusivePeerReservation(wantedPeer, _tracker);
                }
                else
                {
                    req.Error = new InvalidOperationException(
                        $"combined retry re-plan: peer {wantedPeer ?? "?"} is not exclusively reservable");
                    return PhaseResult.Fire(SchedulerEvent.Failed);
                }
            }
        }
        req.RecordPhase("plan_ms", 0);

        // Retry lease swap: release the old prefill lease ONLY when the re-plan
        // moved to a different worker (never two prefill slots held at once, and
        // never a lease on a worker we no longer run on). A failed acquisition on
        // the re-planned worker fails the request with the real reason.
        if (req.RetryCount > 0 && req.PrefillLease is not null
            && req.PrefillWorker is not null
            && req.PrefillLease.WorkerName != req.PrefillWorker.Name)
        {
            _leases.Release(req.PrefillLease);
            req.PrefillLease = null;
            req.PrefillLease = _leases.TryAcquire(req.PrefillWorker.Name, req.SessionId);
            if (req.PrefillLease is null)
            {
                req.Error = new InvalidOperationException(
                    $"retry re-route: no free slot on new prefill worker {req.PrefillWorker.Name}");
                return PhaseResult.Fire(SchedulerEvent.Failed);
            }
        }

        // C4 ReuseStoreState consume: a cold route whose session has durable store
        // KV skips the engine prefill — Route → RestoreKv restores the stored KV
        // directly (legacy migration-path semantics). Solo (warm) routes keep the
        // PrefillWorker=null shape and stay decode-only below. CROSS-NODE warm
        // fallback (epic #591): a warm Solo whose affinity node has no free slot
        // carries PrefillWorker=null + DecodeWorker=alt with ReuseStoreState=true
        // — the machine must still fire ReuseStore so RestoreRunner fetches the
        // KV from the Store (Get) and StatePuts it onto the alt worker before
        // decoding (a plain SoloRouted would decode over an empty alt slot).
        if (plan.ReuseStoreState && (plan.PrefillWorker is not null || plan.DecodeWorker is not null))
        {
            req.RestoreFromStore = true;
            return PhaseResult.Fire(SchedulerEvent.ReuseStore);
        }

        // Prefix checkpoint: a request with a prefixHash + prefill worker may restore
        // a cached prefix KV before prefill (golden prefix_hit/prefix_miss).
        if (_cfg.PrefixCheckpointEnabled && req.Chat.PrefixHash is not null && plan.PrefillWorker is not null)
            return PhaseResult.Fire(SchedulerEvent.PrefixRestoreRouted);

        return plan.PrefillWorker is null
            ? PhaseResult.Fire(SchedulerEvent.SoloRouted)  // warm/decode-only
            : PhaseResult.Fire(SchedulerEvent.RouteSucceeded);
    }

    private Task<PhaseResult> PlanDecodeAsync(RunnerContext ctx)
    {
        var req = ctx.Request;
        _ledger.UpdateLastUsed(req.SessionId); // keep the eviction TTL fresh at decode planning

        // Decode worker already known (atomic same-slot) → no swap, no new lease.
        if (req.DecodeWorker is not null)
            return Task.FromResult(PhaseResult.Fire(SchedulerEvent.DecodePicked));

        var decodeNode = _planner.PlanDecode(req.Chat, _ledger.Lookup(req.SessionId), _workers, _tracker, _health);
        if (decodeNode is not null)
            req.DecodeWorker = Resolve(decodeNode);

        // C4 decode-handoff fallback (legacy no_pd_worker_free mirror): no
        // decode-capable worker has a free slot at the handoff — decode ON THE
        // PREFILL NODE instead of failing the request. The KV is still resident in
        // the held prefill slot (SaveKv already persisted a durable copy), and
        // RestoreKv's same-node skip decodes in place without a store round-trip.
        if (req.DecodeWorker is null && req.PrefillWorker?.CanDecode == true)
        {
            req.DecodeWorker = req.PrefillWorker;
            Serilog.Log.Warning("v2_pick_decode_fallback_no_pd_worker Sid={Sid} Node={Node}",
                req.SessionId, req.DecodeWorker.Name);
            req.RecordPhase("pick_decode_ms", 0);
            return Task.FromResult(PhaseResult.Fire(SchedulerEvent.DecodePicked));
        }
        if (req.DecodeWorker is null)
        {
            req.Error = new InvalidOperationException(
                $"pick-decode: no decode worker available (prefill node {req.PrefillWorker?.Name ?? "?"} cannot decode)");
            return Task.FromResult(PhaseResult.Fire(SchedulerEvent.Failed));
        }

        // GPU-utilization rule: free the prefill slot, then take the decode slot —
        // never two slots held at once.
        _leases.Release(req.PrefillLease);
        req.PrefillLease = null;

        var decodeLease = _leases.TryAcquire(req.DecodeWorker.Name, req.SessionId);
        if (decodeLease is null)
        {
            // C4 fallback: the decode slot vanished between planning and acquisition
            // (a concurrent request took it). Re-keep the prefill slot — the LIFO
            // pool returns the KV's own slot — and decode in place on the prefill
            // node (RestoreKv's same-node skip applies: DecodeLease stays null).
            // The request must NOT fail: the KV is durable in the Store and still
            // resident in the re-kept prefill slot.
            if (req.PrefillWorker?.CanDecode == true)
            {
                req.DecodeWorker = req.PrefillWorker;
                req.PrefillLease = _leases.TryAcquire(req.PrefillWorker.Name, req.SessionId);
                if (req.PrefillLease is not null)
                {
                    req.RecordPhase("pick_decode_ms", 0);
                    return Task.FromResult(PhaseResult.Fire(SchedulerEvent.DecodePicked));
                }
            }
            req.Error = new InvalidOperationException(
                $"pick-decode: no decode slot free on {req.DecodeWorker.Name} and the prefill fallback is unavailable");
            return Task.FromResult(PhaseResult.Fire(SchedulerEvent.Failed));
        }
        req.DecodeLease = decodeLease;

        req.RecordPhase("pick_decode_ms", 0);
        return Task.FromResult(PhaseResult.Fire(SchedulerEvent.DecodePicked));
    }

    private WorkerConfig? Resolve(string? name)
        => name is null ? null : _workers.FirstOrDefault(w => w.Name == name);
}

/// <summary>
/// Prefix-checkpoint restore (golden prefix_hit / prefix_miss): when a request
/// carries a <c>prefixHash</c> and the coordinator has prefix checkpoints enabled,
/// restore the cached prefix KV into the prefill slot before prefill. A Store hit
/// alone is not enough — the prefix is only a "hit" after the StatePut succeeds.
/// An n_past guard skips the restore when the cached prefix already covers ≥85% of
/// the request (restoring a stale/large prefix wastes the StatePut).
/// </summary>
public sealed class PrefixRestoreRunner : WorkerStateRunner
{
    private readonly CoordinatorConfig _cfg;
    private readonly IStoreGateway _store;
    private readonly IEngineRpcGateway _engine;
    private readonly ISessionLedger _ledger;
    public override WorkItemState State => WorkItemState.PrefixRestore;

    public PrefixRestoreRunner(
        CoordinatorConfig cfg,
        IStoreGateway store,
        IEngineRpcGateway engine,
        ISessionLedger ledger)
    {
        _cfg = cfg;
        _store = store;
        _engine = engine;
        _ledger = ledger;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;

        // #712: solo prefix reuse — when the session has a prior KV checkpoint
        // in the Store (HasStoreState), prefer session-KV restore over the
        // prefix-checkpoint path. The session KV is a superset of the system-
        // prompt checkpoint and enables the engine's shared-prefix detection
        // to only prefill the delta (new tokens since last turn).
        if (_cfg.SoloPrefixReuseEnabled && req.PrefillWorker is not null)
        {
            var entry = _ledger.Lookup(req.SessionId);
            if (entry is { HasStoreState: true })
            {
                var restored = await TryRestoreSessionKvAsync(req, ct);
                if (restored) return PhaseResult.Fire(SchedulerEvent.PrefixRestoreSucceeded);
            }
            // Fallback: no session KV in Store — try prefix-checkpoint path below.
        }

        if (!_cfg.PrefixCheckpointEnabled || req.Chat.PrefixHash is null || req.PrefillWorker is null)
            return PhaseResult.Fire(SchedulerEvent.PrefixRestoreSucceeded); // miss → straight to prefill

        var prefixKey = $"prefix/{req.Chat.PrefixHash}.kv";
        var blob = await _store.GetRawAsync(prefixKey, ct);
        if (blob is null)
        {
            req.PrefixCacheHit = false;
            return PhaseResult.Fire(SchedulerEvent.PrefixRestoreSucceeded); // miss → prefill
        }

        // n_past guard (legacy 0.85): skip the restore when the cached prefix
        // already covers ≥85% of the request's estimated tokens.
        var prefixNPast = await ReadPrefixNPastAsync(prefixKey, ct);
        req.PrefixNPast = prefixNPast;
        if (prefixNPast > 0 && req.Chat.EstimatedTokens > 0 && prefixNPast >= req.Chat.EstimatedTokens * 0.85)
        {
            req.PrefixCacheHit = false;
            return PhaseResult.Fire(SchedulerEvent.PrefixRestoreSucceeded); // guard → miss
        }

        var slotKey = req.PrefillLease?.SlotId.ToString() ?? "0";
        var put = await _engine.RestoreAsync(req.PrefillWorker.Name, slotKey, blob, prefixNPast, ct);
        if (put.Ok)
        {
            req.PrefixCacheHit = true; // hit only when the StatePut actually installed the KV
            if (put.NPast > 0)
                _ledger.UpdateNPast(req.SessionId, put.NPast);
        }
        else
        {
            req.PrefixCacheHit = false;
        }

        return PhaseResult.Fire(SchedulerEvent.PrefixRestoreSucceeded);
    }

    private async Task<bool> TryRestoreSessionKvAsync(SchedulerRequest req, CancellationToken ct)
    {
        if (req.PrefillWorker is null) return false;

        var entry = _ledger.Lookup(req.SessionId);
        if (entry is not { HasStoreState: true } || entry.NPast <= 0)
        {
            CoordinatorMetrics.SoloKvRestoreMisses.Inc();
            return false;
        }

        // n_tokens > n_past guard — proportional tolerance accounts for
        // generated-token growth (e.g. 64 ACK tokens/turn accumulate in NPast
        // but not in EstimatedTokens). Floor of 128 protects small sessions;
        // 5% covers 24+ turns of ACK growth.
        var soloTolerance = Math.Max(128, (int)(entry.NPast * 0.05));
        if (req.Chat.EstimatedTokens > 0
            && req.Chat.EstimatedTokens + soloTolerance < entry.NPast)
        {
            CoordinatorMetrics.SoloKvRestoreMisses.Inc();
            return false;
        }

        var storeKey = $"{req.SessionId}.kv";
        var blob = await _store.GetRawAsync(storeKey, ct);
        if (blob is null || blob.Length == 0)
        {
            CoordinatorMetrics.SoloKvRestoreMisses.Inc();
            return false;
        }

        var slotKey = req.PrefillLease?.SlotId.ToString() ?? "0";
        var put = await _engine.RestoreAsync(req.PrefillWorker.Name, slotKey, blob, entry.NPast, ct);
        if (!put.Ok)
        {
            CoordinatorMetrics.SoloKvRestoreMisses.Inc();
            return false;
        }

        req.PrefixCacheHit = true;
        if (put.NPast > 0)
        {
            _ledger.UpdateNPast(req.SessionId, put.NPast);
            req.PrefixNPast = put.NPast;
        }
        else
        {
            req.PrefixNPast = entry.NPast;
        }

        CoordinatorMetrics.SoloKvRestores.Inc();
        return true;
    }

    private async Task<int> ReadPrefixNPastAsync(string prefixKey, CancellationToken ct)
    {
        try
        {
            var manifest = await _store.GetManifestAsync(prefixKey, ct);
            if (manifest is null || manifest.Length == 0) return 0;
            using var doc = JsonDocument.Parse(manifest);
            return doc.RootElement.TryGetProperty("n_past", out var np) && np.ValueKind == JsonValueKind.Number
                ? np.GetInt32() : 0;
        }
        catch (JsonException)
        {
            return 0; // non-fatal: the guard is skipped
        }
    }
}

/// <summary>Runs engine prefill on the prefill worker, captures the KV blob + n_past.
/// When the engine's binary predates the PREFILL opcode (#279, NotImplemented), falls
/// back to the HTTP prefill — an n_predict=0 completion that builds the KV in the slot —
/// and the request continues via the normal path (SaveKv captures the slot KV via
/// StateGet).
///
/// <para><b>COMBINED (epic #591):</b> the prefill carries the hydra_config dict
/// (from <see cref="SchedulerRequest.MultiEngineConfig"/> via
/// <see cref="EngineConfig.ToHydraConfigDict"/>, rpc_servers resolved to reachable
/// endpoints) and, on success, fires <see cref="SchedulerEvent.CombinedPrefillSucceeded"/>
/// — the KV stays resident in the head slot and decode runs IN PLACE (no SaveKv,
/// no PickDecode/RestoreKv).</para>
/// </summary>
public sealed class PrefillRunner : WorkerStateRunner
{
    private readonly IEngineRpcGateway _engine;
    private readonly ICompletionProxyService _proxy;
    private readonly IReadOnlyList<WorkerConfig> _workers;
    public override WorkItemState State => WorkItemState.Prefill;

    public PrefillRunner(IEngineRpcGateway engine, ICompletionProxyService proxy, IReadOnlyList<WorkerConfig> workers)
    {
        _engine = engine;
        _proxy = proxy;
        _workers = workers;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.PrefillWorker is null)
        {
            req.Error = new InvalidOperationException("prefill state entered without a prefill worker");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        try
        {
            await _engine.EnsureChunkConfiguredAsync(req.PrefillWorker.Name, ct); // lazy 0x40 (wire parity)
            var slotKey = req.PrefillLease?.SlotId.ToString() ?? "0"; // engine keys prefill by slot id
            var sw = Stopwatch.StartNew();
            var result = await _engine.PrefillAsync(req.PrefillWorker.Name, slotKey, req.Chat, ct,
                hydraConfig: BuildHydraConfig(req));
            if (result.NotImplemented)
            {
                // #279: old binary without PREFILL 0x42 — fall through to the HTTP
                // prefill (n_predict=0 completion). The request must NOT fail; the
                // KV is then captured from the slot by SaveKv (KvBlob stays null).
                await HttpPrefillFallbackAsync(req, slotKey, ct);
                req.RecordPhase("prefill_ms", sw.ElapsedMilliseconds);
                return PhaseResult.Fire(SchedulerEvent.PrefillSucceeded);
            }
            req.NPastAfter = result.NPast;
            req.KvBlob = result.KVPayload;
            req.KvSlotId = req.PrefillLease?.SlotId; // the physical slot that holds this KV (review #4)
            req.KvIdentity = new ModelIdentity
            {
                Tokenizer = result.Tokenizer,
                ModelName = result.ModelName,
                ModelQuant = result.ModelQuant,
                ModelCapabilities = result.ModelCapabilities,
            };
            req.RecordPhase("prefill_ms", sw.ElapsedMilliseconds);

            // COMBINED (epic #591): the prefill delivered hydra_config and the KV
            // stays RESIDENT in the head slot — skip SaveKv entirely and decode IN
            // PLACE on the head (decode worker = prefill worker; legacy
            // WorkerSchedulerService.cs:2136-2148). The in-memory KvBlob survives
            // to BgSave, which direct-Puts it (wire parity: combined golden).
            if (req.MultiMode == MultiEngineMode.Combined)
            {
                req.HydraConfigDelivered = true;
                req.DecodeWorker = req.PrefillWorker;
                return PhaseResult.Fire(SchedulerEvent.CombinedPrefillSucceeded);
            }
            return PhaseResult.Fire(SchedulerEvent.PrefillSucceeded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transient engine fault → bounded re-route, then fail with the real error.
            req.RetryCount++;
            req.Error = ex;
            return req.RetryCount < SchedulerRequest.MaxRetries
                ? PhaseResult.Fire(SchedulerEvent.Retry)
                : PhaseResult.Fire(SchedulerEvent.Failed);
        }
    }

    /// <summary>COMBINED (epic #591): build the hydra_config dict that rides the
    /// PREFILL body — <see cref="EngineConfig.ToHydraConfigDict"/> with rpc_servers
    /// translated from logical worker names to reachable host:port endpoints
    /// (legacy <c>TranslateToWirePayloadAsync</c>, MultiEngineRouter.ResolveRpcServerEndpoints).
    /// Null for solo/atomic/P-D requests (no config injection).</summary>
    private Dictionary<string, object>? BuildHydraConfig(SchedulerRequest req)
    {
        if (req.MultiMode != MultiEngineMode.Combined || req.MultiEngineConfig is null)
            return null;
        var dict = req.MultiEngineConfig.ToHydraConfigDict();
        // Gap-1 fix: models.json rpc_servers name workers by logical name
        // ("rtx3060:9504") — translate to the reachable host:port from workers.json
        // so the fork's apply_t3_rebuild() can register the split peer device.
        if (dict.TryGetValue("rpc_servers", out var raw) && raw is string[] endpoints)
            dict["rpc_servers"] = MultiEngineRouter.ResolveRpcServerEndpoints(endpoints, _workers);
        return dict;
    }

    /// <summary>#279 HTTP prefill fallback (legacy ~2054-2131): an n_predict=0
    /// completion builds the KV in the slot without generating tokens. NPastAfter
    /// comes from the completion's total_tokens; no engine KV blob is produced
    /// (SaveKv captures the slot KV via StateGet instead).</summary>
    private async Task HttpPrefillFallbackAsync(SchedulerRequest req, string slotKey, CancellationToken ct)
    {
        var worker = req.PrefillWorker!;
        var body = new Dictionary<string, object>(req.Chat.Body)
        {
            ["stream"] = false,
            ["n_predict"] = 0,
        };
        if (!body.ContainsKey("model"))
            body["model"] = worker.ModelAlias ?? req.Chat.Model ?? "nano";
        body["id_slot"] = req.PrefillLease?.SlotId ?? 0;

        var resp = await _proxy.ProxyCompletionAsync(worker.LlamaUrl, body, req.TraceId, ct);
        req.NPastAfter = ExtractTotalTokens(resp);
        req.KvBlob = null; // no engine KV blob — SaveKv captures the slot via StateGet
        req.KvIdentity = ModelIdentity.Empty;
        Serilog.Log.Warning(
            "v2_engine_prefill_fell_back_to_http Sid={Sid} Worker={W} Slot={Slot} NPast={N}",
            req.SessionId, worker.Name, slotKey, req.NPastAfter);
    }

    private static int ExtractTotalTokens(Dictionary<string, object> response)
        => ExtractUsageInt(response, "total_tokens");

    private static int ExtractUsageInt(Dictionary<string, object> response, string field)
    {
        if (!response.TryGetValue("usage", out var usage) || usage is not JsonElement ue)
            return 0;
        if (ue.ValueKind != JsonValueKind.Object || !ue.TryGetProperty(field, out var v)
            || v.ValueKind != JsonValueKind.Number)
            return 0;
        return v.GetInt32();
    }
}

/// <summary>Persists the KV blob produced by prefill to the Store and registers the
/// session on the PREFILL node (C1 ledger timeline, point 1). When the engine produced
/// no KV blob (the #279 HTTP-prefill fallback), captures the slot's KV via StateGet
/// first so the Store still holds the prefill state. Consumes <c>req.KvBlob</c> after
/// saving (legacy wire parity: the merged-decode path re-carries the KV only when
/// RestoreKv re-fetches it).
///
/// <para><b>Chunked save (epic #591 WP3):</b> when <see cref="CoordinatorConfig.EnableChunks"/>
/// is set, the save reproduces the legacy chunked wire (WorkerSchedulerService.cs:2240)
/// instead of the plain Put: chunk the blob (ChunkEngine, SHA-256 content-addressed),
/// SYNC_MISSING the ordered hash list, PUSH_CHUNKS only the chunks the Store lacks,
/// then PUT_MANIFEST the authoritative ordered manifest (carrying the KV's model
/// identity — M-Perf.9 #289/#470). Goldens chunked_save / chunked_save_with_pushes
/// pin the exact opcodes + payload lengths (SyncMissing 269 / PushChunks 1028 /
/// PutManifest 540).</para>
/// </summary>
public sealed class SaveKvRunner : WorkerStateRunner
{
    private readonly IStoreGateway _store;
    private readonly ISessionLedger _ledger;
    private readonly IEngineRpcGateway _engine;
    private readonly CoordinatorConfig _cfg;
    public override WorkItemState State => WorkItemState.SaveKv;

    public SaveKvRunner(IStoreGateway store, ISessionLedger ledger, IEngineRpcGateway engine, CoordinatorConfig cfg)
    {
        _store = store;
        _ledger = ledger;
        _engine = engine;
        _cfg = cfg;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.PrefillWorker is null)
        {
            req.Error = new InvalidOperationException("save-kv entered without a prefill worker");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        byte[]? kv = req.KvBlob;
        if (kv is null)
        {
            // #279 HTTP-prefill fallback: the engine produced no KV blob — capture the
            // slot's KV (StateGet) so the Store still has the prefill state.
            var slotKey = req.PrefillLease?.SlotId.ToString() ?? "0";
            kv = await _engine.CaptureAsync(req.PrefillWorker.Name, slotKey, ct);
        }
        if (kv is null)
        {
            req.Error = new InvalidOperationException("save-kv: no KV blob and StateGet capture returned nothing");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        bool saved;
        try
        {
            if (_cfg.EnableChunks)
            {
                // Chunked save (legacy wire parity): chunk the blob at the CONFIGURED
                // chunk size (explicit — never the mutable ChunkEngine.CHUNK_SIZE
                // static, which would race under parallel test execution), sync which
                // hashes the Store lacks, push the missing bodies, then write the
                // authoritative manifest. The manifest carries req.NPastAfter (the
                // prefill n_past — golden PutManifest Len 540) + the KV's model
                // identity (#289/#470).
                var storeKey = StoreKeys.KvKey(req.SessionId);
                var chunks = ChunkEngine.ChunkAndHash(kv, _cfg.ChunkSize);
                var orderedHashes = chunks.Select(c => c.Hash).ToList();
                var missing = await _store.SyncMissingAsync(storeKey, orderedHashes, ct);
                await _store.PushChunksAsync(storeKey, missing, chunks, kv, _cfg.ChunkSize, ct);
                await _store.PutManifestAsync(storeKey, req.NPastAfter, kv.Length, chunks, ct, req.KvIdentity);
                saved = true;
            }
            else
            {
                saved = await _store.PutAsync(StoreKeys.KvKey(req.SessionId), kv, ct);
            }
        }
        catch (Exception ex)
        {
            req.Error = ex;
            saved = false;
        }

        if (!saved)
        {
            // C3 store-fallback (golden store_exception): the store write failed —
            // keep the KV in the prefill slot and decode IN PLACE on the same node
            // (skip restore). The request must NOT fail. Review #7: clear the caught
            // error — this is a NON-terminal path, and a stale req.Error would
            // surface as a false failure if later code inspects it.
            req.Error = null;
            req.DecodeWorker = req.PrefillWorker;
            return PhaseResult.Fire(SchedulerEvent.SaveKvFallbackSucceeded);
        }

        // C1: register on the prefill node with the post-prefill n_past, and stamp
        // store state (the KV blob is now durable).
        var entry = _ledger.Register(req.SessionId, req.PrefillWorker.Name, req.PrefillLease?.SlotId, req.NPastAfter, req.Chat.PrefixHash);
        lock (entry) { entry.HasStoreState = true; }

        // Wire parity: the KV blob is consumed here (legacy nulls item.KvBlob in
        // SaveKvAsync); the merged-decode (0x43) path re-carries it only when
        // RestoreKv re-fetches the blob from the Store.
        req.KvBlob = null;

        return PhaseResult.Fire(SchedulerEvent.SaveKvSucceeded);
    }
}

/// <summary>Restores the KV onto the DECODE worker before decoding, and RE-REGISTERS
/// the session on the decode node (C1 ledger timeline, point 2 — the P/D goldens
/// pin <c>Ledger.NodeName</c> = decode node).
///
/// <para><b>Cross-model guard (#470):</b> STATE_PUT returns the slot's model
/// identity; when <c>model_match=false</c> (or <see cref="CrossModelGuard"/> aborts
/// on the stored-vs-slot identity comparison), the restore is aborted, the corrupt
/// slot erased, and the request RE-PREFILLS on the correct model (route back to
/// Prefill) instead of decoding over a mismatched KV — the #469 hallucination
/// scenario. The decode slot lease is released and a fresh prefill lease acquired
/// (GPU-utilization rule: one slot at a time).</para>
///
/// <para>Wire parity: the merged-decode (0x43) path carries the KV blob forward in
/// <c>req.KvBlob</c> (legacy <c>item.KvBlob = restoreBlob</c> when merged-capable).</para>
/// </summary>
public sealed class RestoreRunner : WorkerStateRunner
{
    private readonly IStoreGateway _store;
    private readonly IEngineRpcGateway _engine;
    private readonly ISessionLedger _ledger;
    private readonly ILeaseManager _leases;
    private readonly ICompletionProxyService _proxy;
    private readonly CoordinatorConfig _cfg;
    public override WorkItemState State => WorkItemState.RestoreKv;

    public RestoreRunner(
        IStoreGateway store,
        IEngineRpcGateway engine,
        ISessionLedger ledger,
        ILeaseManager leases,
        ICompletionProxyService proxy,
        CoordinatorConfig cfg)
    {
        _store = store;
        _engine = engine;
        _ledger = ledger;
        _leases = leases;
        _proxy = proxy;
        _cfg = cfg;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.DecodeWorker is null)
        {
            req.Error = new InvalidOperationException("restore entered without a decode worker");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        // Same-node decode (true atomic, no slot swap): the KV is already resident
        // in the held prefill slot — no store Get + StatePut round-trip (wire
        // parity: cold_atomic_engine golden has none). C4 store-reuse route
        // (RouteDecision --ReuseStore--> RestoreKv) must NOT skip: its KV lives in
        // the STORE, not in the freshly-acquired prefill slot (#469 would decode
        // over an empty slot). Review #4: the skip is keyed on the PHYSICAL slot
        // identity (KvSlotId), NOT the worker name — a decode-handoff fallback that
        // re-acquired a different slot on the same worker must restore, not skip.
        if (!req.RestoreFromStore && req.DecodeLease is null && req.PrefillLease?.SlotId == req.KvSlotId)
            return PhaseResult.Fire(SchedulerEvent.RestoreSucceeded);

        var kv = await _store.GetAsync(StoreKeys.KvKey(req.SessionId), ct);
        if (kv is null)
        {
            req.Error = new InvalidOperationException($"restore: store has no KV for session {req.SessionId}");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        // #716 Store-side diagnostic: compare GetAsync payload length against the
        // size logged at save time (req.KvBlob.Length from the prefill response or
        // manifest). A mismatch here means the Store returned a truncated blob —
        // the real root cause of the "unexpectedly reached end of buffer" at the
        // engine. The warning fires every turn until the Store-side issue is
        // fixed; on a healthy path the sizes match and no warning is emitted.
        var storeKey = StoreKeys.KvKey(req.SessionId);
        var savedSize = req.KvBlob?.Length ?? 0;
        if (savedSize > 0 && kv.Length != savedSize)
        {
            Serilog.Log.Error(
                "restore_store_size_mismatch Sid={Sid} StoreKey={Key} SavedSize={Saved} " +
                "RestoreSize={Restore} Delta={Delta} — Store returned fewer bytes than saved",
                req.SessionId, storeKey, savedSize, kv.Length, kv.Length - savedSize);
        }
        else
        {
            Serilog.Log.Debug(
                "restore_store_size_ok Sid={Sid} StoreKey={Key} Size={Size}",
                req.SessionId, storeKey, kv.Length);
        }

        var slotKey = DecodeSlotId(req)?.ToString() ?? "0";
        var put = await _engine.RestoreAsync(req.DecodeWorker.Name, slotKey, kv, req.NPastAfter, ct);
        if (!put.Ok)
        {
            req.Error = new InvalidOperationException($"StatePut failed on {req.DecodeWorker.Name} (slot {slotKey})");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        // #470 cross-model guard: the KV's model identity must match the slot's
        // resident identity, else decoding over it corrupts the output (#469).
        var slotIdentity = new ModelIdentity
        {
            Tokenizer = put.Tokenizer,
            ModelName = put.ModelName,
            ModelQuant = put.ModelQuant,
            ModelCapabilities = put.ModelCapabilities,
        };
        var abort = !put.ModelMatch;
        if (!abort)
        {
            var guard = CrossModelGuard.Decide(
                stored: req.KvIdentity,
                slot: slotIdentity,
                allowCrossModelKvReuse: _cfg.AllowCrossModelKvReuse);
            switch (guard)
            {
                case CrossModelGuard.Outcome.Proceed:
                    break;
                case CrossModelGuard.Outcome.Skip:
                    break;
                case CrossModelGuard.Outcome.WarnAndProceed:
                    Serilog.Log.Warning("v2_cross_model_kv_warned Sid={Sid} Stored={Stored} Slot={Slot}",
                        req.SessionId, req.KvIdentity.ModelName, put.ModelName);
                    break;
                case CrossModelGuard.Outcome.Abort:
                    abort = true;
                    break;
            }
        }

        if (abort)
        {
            Serilog.Log.Warning(
                "v2_state_put_model_mismatch Sid={Sid} Slot={Slot} Stored={Stored} SlotName={SlotName} — re-prefilling",
                req.SessionId, slotKey, req.KvIdentity.ModelName, put.ModelName);
            if (!await EraseSlotAndReprefillAsync(req, slotKey, ct))
                return PhaseResult.Fire(SchedulerEvent.Failed);
            return PhaseResult.Fire(SchedulerEvent.Reprefill);
        }

        // Carry the KV forward for the merged-decode (0x43) path (legacy
        // RestoreKvAsync sets item.KvBlob = restoreBlob when merged-capable).
        req.KvBlob = kv;

        // C1: re-register the session on the decode node (its KV now lives here).
        if (req.NPastAfter > 0)
            _ledger.UpdateNPast(req.SessionId, req.NPastAfter);
        _ledger.Register(
            req.SessionId, req.DecodeWorker.Name, DecodeSlotId(req),
            req.NPastAfter > 0 ? req.NPastAfter : (_ledger.Lookup(req.SessionId)?.NPast ?? 0),
            req.Chat.PrefixHash);

        return PhaseResult.Fire(SchedulerEvent.RestoreSucceeded);
    }

    /// <summary>Erase the corrupt decode slot (best-effort HTTP), release the decode
    /// lease, re-acquire the prefill slot, and reset the request for a fresh prefill.
    /// Returns false (after setting <c>req.Error</c>) when no prefill slot can be
    /// acquired — the request fails rather than prefill without a lease.</summary>
    private async Task<bool> EraseSlotAndReprefillAsync(SchedulerRequest req, string slotKey, CancellationToken ct)
    {
        var decodeWorker = req.DecodeWorker!;
        var slotId = DecodeSlotId(req) ?? 0;
        try
        {
            await _proxy.EraseSlotAsync(decodeWorker.LlamaUrl, slotId, ct);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "v2_state_put_erase_failed Sid={Sid} Slot={Slot}", req.SessionId, slotId);
        }

        // GPU-utilization rule: free the corrupt decode slot before taking the
        // prefill slot — never two slots held at once.
        _leases.Release(req.DecodeLease);
        req.DecodeLease = null;
        req.DecodeWorker = null; // re-picked at PickDecode after the re-prefill

        if (req.PrefillWorker is null)
        {
            req.Error = new InvalidOperationException("cross-model re-prefill: no prefill worker");
            return false;
        }
        req.PrefillLease = _leases.TryAcquire(req.PrefillWorker.Name, req.SessionId);
        if (req.PrefillLease is null)
        {
            req.Error = new InvalidOperationException($"cross-model re-prefill: no free slot on {req.PrefillWorker.Name}");
            return false;
        }

        req.NPastAfter = 0;
        req.KvBlob = null;
        req.KvIdentity = ModelIdentity.Empty;
        return true;
    }

    /// <summary>The slot decode runs on: the decode lease, or the prefill lease
    /// for atomic same-slot requests (decode reuses the held slot).</summary>
    internal static int? DecodeSlotId(SchedulerRequest req)
        => req.DecodeLease?.SlotId ?? req.PrefillLease?.SlotId;
}

/// <summary>
/// Decodes via the shared HTTP completion proxy — or, when the decode worker's
/// engine advertises <c>merged_decode</c> (#470), via the framed DECODE 0x43 path:
/// the engine validates the KV against the slot's resident model (Gate A) inside a
/// single RPC, then the result is polled from GET /v1/decode/{id}. A Gate-A
/// rejection aborts the request (the slot was never restored — decoding over it
/// via HTTP would be the #469 hallucination scenario); a transport fault falls
/// back to the HTTP proxy decode.
///
/// <para>Registers the session on the DECODE node when it has no ledger entry yet
/// (C1 ledger timeline, point 3) and tracks the completion's usage into the ledger
/// (NPast from total_tokens — the P/D + atomic goldens pin NPast=usage.total_tokens,
/// not the prefill n_past). Streaming hands the chunk stream to the caller and
/// suspends until <c>NotifyStreamComplete</c>; non-streaming completes inline.</para>
/// </summary>
public sealed class DecodeRunner : WorkerStateRunner
{
    private readonly ICompletionProxyService _proxy;
    private readonly IEngineRpcGateway _engine;
    private readonly ISessionLedger _ledger;
    private readonly CoordinatorConfig _cfg;
    private readonly IHealthMonitorService _health;
    public override WorkItemState State => WorkItemState.Decode;

    public DecodeRunner(
        ICompletionProxyService proxy,
        IEngineRpcGateway engine,
        ISessionLedger ledger,
        CoordinatorConfig cfg,
        IHealthMonitorService health)
    {
        _proxy = proxy;
        _engine = engine;
        _ledger = ledger;
        _cfg = cfg;
        _health = health;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var workerUrl = req.DecodeWorker?.LlamaUrl;
        if (string.IsNullOrEmpty(workerUrl) || req.DecodeWorker is null)
        {
            req.Error = new InvalidOperationException($"decode entered without a decode worker (worker={req.DecodeWorker?.Name ?? "null"})");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        // Decode-time CONFIGURE with the per-request n_predict override (wire
        // parity: 17-byte 0x40 emitted before decode). The lazy state_chunk_size
        // CONFIGURE (28-byte 0x40) fires here on the DECODE worker — wire parity:
        // the P/D goldens pin it AFTER StatePut, before the decode-time 0x40
        // (the legacy emits it lazily when the decode worker's RPC client is
        // first created, i.e. at decode, never during restore).
        var slotKey = RestoreRunner.DecodeSlotId(req)?.ToString() ?? "0";
        await _engine.EnsureChunkConfiguredAsync(req.DecodeWorker.Name, ct);
        await _engine.ConfigureAsync(req.DecodeWorker.Name, slotKey, $"{{\"n_predict\":{req.Chat.MaxTokens}}}", ct);

        // #470: merged decode (0x43) — when the engine advertises merged_decode,
        // send the framed DECODE with kv_metadata + model_metadata + prompt to get
        // the decode_request_id and model identity match. On success, poll
        // GET /v1/decode/{id} (skips the HTTP proxy); a transport fault falls
        // back to the HTTP proxy decode.
        if (_cfg.UseLlamaEngine
            && _health.GetNodeInfo(req.DecodeWorker.Name)?.EngineCapabilities?.Contains(Protocol.CapMergedDecode) == true)
        {
            var merged = await TryMergedDecodeAsync(req, workerUrl, slotKey, ct);
            if (merged is not null)
                return merged.Value;
            // transport fault — fall through to the HTTP proxy decode
        }

        if (req.IsStreaming)
        {
            var stream = _proxy.ProxyCompletionStreamAsync(workerUrl, req.Chat.Body, req.TraceId, ct);
            req.DecodeChunks = TrackStreamUsage(req, stream);
            RegisterIfMissing(req, req.DecodeWorker.Name, RestoreRunner.DecodeSlotId(req), req.NPastAfter);
            req.StreamStartedAt = DateTime.UtcNow; // reaper clock: stream handed to the caller
            req.StreamReady.TrySetResult(req.DecodeChunks);
            return PhaseResult.Wait; // resumed by NotifyStreamComplete
        }

        req.Response = await _proxy.ProxyCompletionAsync(workerUrl, req.Chat.Body, req.TraceId, ct);
        RegisterIfMissing(req, req.DecodeWorker.Name, RestoreRunner.DecodeSlotId(req), req.NPastAfter);
        TrackAfterCompletion(req);
        return PhaseResult.Fire(SchedulerEvent.DecodeSucceeded);
    }

    /// <summary>
    /// Attempt the merged-decode (0x43) path. Returns the phase result on success,
    /// null on a TRANSPORT fault (exception before/while polling) — the caller
    /// falls back to the HTTP proxy. A Gate-A rejection (Valid=false) is NOT a
    /// transport fault: it returns <see cref="PhaseResult"/> with Failed after
    /// setting <c>req.Error</c> (legacy #470: abort the request, never decode over
    /// an empty/corrupt slot).
    /// </summary>
    private async Task<PhaseResult?> TryMergedDecodeAsync(SchedulerRequest req, string workerUrl, string slotKey, CancellationToken ct)
    {
        try
        {
            // kv_metadata (what built the KV) comes from the PREFILL response;
            // model_metadata (what the decode node is running) comes from the
            // node's stamped identity — a genuinely independent source (#470/A7).
            var kvIdentity = req.KvIdentity;
            var node = _health.GetNodeInfo(req.DecodeWorker!.Name);
            var modelIdentity = node is { ModelName.Length: > 0 }
                ? new ModelIdentity
                {
                    Tokenizer = node.ModelTokenizer,
                    ModelName = node.ModelName,
                    ModelQuant = node.ModelQuant,
                    ModelCapabilities = node.ModelCapabilities,
                }
                : ModelIdentity.Empty;

            // The prompt segment carries messages + n_predict (+ tools etc. when
            // present); the samplingJson channel stays empty (#576 — sampling and
            // stop travel inside the prompt object).
            var messagesJson = BuildMergedDecodePromptSegment(req);
            var nPredict = req.Chat.MaxTokens;

            var resp = await _engine.MergedDecodeAsync(
                req.DecodeWorker.Name, slotKey, req.NPastAfter,
                kvIdentity.Tokenizer, kvIdentity.ModelName, kvIdentity.ModelQuant, kvIdentity.ModelCapabilities,
                modelIdentity.Tokenizer, modelIdentity.ModelName, modelIdentity.ModelQuant, modelIdentity.ModelCapabilities,
                modelAlias: req.DecodeWorker.ModelAlias,
                messagesJson: messagesJson,
                nPredict: nPredict,
                samplingJson: null,
                stream: req.IsStreaming,
                kvBlob: req.KvBlob ?? ReadOnlyMemory<byte>.Empty,
                traceId: req.TraceId,
                ct);

            if (!resp.Valid || resp.DecodeRequestId <= 0)
            {
                // Gate A: the engine rejected the KV (identity mismatch, slot busy).
                // With merged_decode the restore skipped the blind STATE_PUT, so the
                // slot is empty — decoding via HTTP proxy here would hit an
                // empty/corrupt slot (#469). Abort the entire request.
                req.Error = new InvalidOperationException(
                    $"DECODE 0x43 rejected Sid={req.SessionId} Valid={resp.Valid} DecodeId={resp.DecodeRequestId} — KV not restored, aborting");
                return PhaseResult.Fire(SchedulerEvent.Failed);
            }

            if (req.IsStreaming)
            {
                // #470: poll GET /v1/decode/{id} for the streaming result — the
                // engine generates asynchronously; the endpoint 404s until ready.
                var mergedStream = _proxy.PollDecodeStreamAsync(workerUrl, resp.DecodeRequestId!.Value, req.TraceId, ct);
                req.DecodeChunks = TrackStreamUsage(req, mergedStream);
                RegisterIfMissing(req, req.DecodeWorker.Name, RestoreRunner.DecodeSlotId(req), req.NPastAfter);
                req.StreamStartedAt = DateTime.UtcNow; // reaper clock: stream handed to the caller
                req.StreamReady.TrySetResult(req.DecodeChunks);
                return PhaseResult.Wait; // resumed by NotifyStreamComplete
            }

            // #470: poll GET /v1/decode/{id} for the buffered result.
            req.Response = await _proxy.PollDecodeResultAsync(workerUrl, resp.DecodeRequestId!.Value, req.TraceId, ct);
            RegisterIfMissing(req, req.DecodeWorker.Name, RestoreRunner.DecodeSlotId(req), req.NPastAfter);
            TrackAfterCompletion(req);
            return PhaseResult.Fire(SchedulerEvent.DecodeSucceeded);
        }
        catch (Exception ex)
        {
            // Transport fault (or a poll failure) — NOT a Gate-A rejection (that
            // path returned already). Fall back to the HTTP proxy decode.
            Serilog.Log.Warning(ex, "v2_merged_decode_transport_fault Sid={Sid} — falling back to HTTP proxy", req.SessionId);
            return null;
        }
    }

    /// <summary>Build the merged-decode (0x43) prompt segment: messages + n_predict
    /// (+ tools/tool_choice/response_format when present). Sampling/stop would ride
    /// here too (#576); the v2 request model has no overrides channel yet.</summary>
    private static string? BuildMergedDecodePromptSegment(SchedulerRequest req)
    {
        var segment = new Dictionary<string, object?>
        {
            ["messages"] = req.Chat.Messages,
            ["n_predict"] = req.Chat.MaxTokens,
        };
        if (req.Chat.Body.TryGetValue("tools", out var tools))
            segment["tools"] = tools;
        if (req.Chat.Body.TryGetValue("tool_choice", out var toolChoice))
            segment["tool_choice"] = toolChoice;
        if (req.Chat.Body.TryGetValue("response_format", out var responseFormat))
            segment["response_format"] = responseFormat;
        return JsonSerializer.Serialize(segment);
    }

    /// <summary>Wrap the SSE stream to track the last <c>total_tokens</c> usage
    /// chunk and fold it into the ledger (TrackAfterStream equivalent — golden
    /// streaming_cold_atomic pins NPast=usage.total_tokens).</summary>
    private async IAsyncEnumerable<byte[]> TrackStreamUsage(SchedulerRequest req, IAsyncEnumerable<byte[]> source)
    {
        var lastTotal = 0;
        await foreach (var chunk in source)
        {
            lastTotal = ExtractStreamUsageTotal(chunk, lastTotal);
            yield return chunk;
        }
        if (lastTotal > 0)
            _ledger.UpdateNPast(req.SessionId, lastTotal);
    }

    private static int ExtractStreamUsageTotal(byte[] chunk, int fallback)
    {
        var text = Encoding.UTF8.GetString(chunk);
        var idx = text.IndexOf("\"total_tokens\"", StringComparison.Ordinal);
        if (idx < 0) return fallback;
        var colon = text.IndexOf(':', idx);
        if (colon < 0) return fallback;
        var end = text.IndexOfAny(new[] { ',', '}', ' ', '\n', '\r' }, colon + 1);
        var num = end < 0 ? text[(colon + 1)..] : text[(colon + 1)..end];
        return int.TryParse(num.Trim(), out var n) ? n : fallback;
    }

    /// <summary>C1 point 3: register the session on the decode node if absent.</summary>
    private void RegisterIfMissing(SchedulerRequest req, string worker, int? slotId, int nPast)
    {
        if (_ledger.Lookup(req.SessionId) == null)
            _ledger.Register(req.SessionId, worker, slotId, nPast, req.Chat.PrefixHash);
    }

    /// <summary>Ledger NPast comes from the completion's usage.total_tokens
    /// (TrackAfterCompletion in legacy).</summary>
    private void TrackAfterCompletion(SchedulerRequest req)
    {
        var total = ExtractUsageInt(req.Response, "total_tokens");
        if (total > 0)
        {
            _ledger.UpdateNPast(req.SessionId, total);
            var prompt = ExtractUsageInt(req.Response, "prompt_tokens");
            if (prompt > 0)
                _ledger.UpdateNPromptTokens(req.SessionId, prompt);
        }
    }

    private static int ExtractUsageInt(object? response, string field)
    {
        if (response is not Dictionary<string, object> dict || !dict.TryGetValue("usage", out var usage))
            return 0;
        // usage may be a Dictionary<string,object> (in-process fakes) or a
        // JsonElement (responses deserialized as Dictionary<string,object>).
        return usage switch
        {
            Dictionary<string, object> usageDict when usageDict.TryGetValue(field, out var value) => ToInt(value),
            JsonElement je when je.ValueKind is JsonValueKind.Object
                && je.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.Number => prop.GetInt32(),
            _ => 0,
        };
    }

    private static int ToInt(object value) => value switch
    {
        int i => i,
        long l => (int)l,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
        _ => 0,
    };
}

/// <summary>
/// Background save (C3 core function): capture the slot's FINAL KV (StateGet)
/// and persist it to the Store so the next turn / migration can restore the
/// post-decode state. COMBINED (epic #591) skips SaveKv, so the in-memory prefill
/// blob survives to here and is Put DIRECTLY (no StateGet — legacy KvBlob path).
///
/// <para><b>Rules (legacy #277/#286 + golden store_exception):</b> the capture
/// runs while the slot is still HELD (the streaming resume drives this state
/// before releasing the lease); the RPCs use <c>CancellationToken.None</c>; a
/// failure is logged and SWALLOWED — a bg-save failure must not fail the request.
/// The Store key is the pinned <c>{sessionId}.kv</c>.</para>
/// </summary>
public sealed class BgSaveRunner : WorkerStateRunner
{
    private readonly IEngineRpcGateway _engine;
    private readonly IStoreGateway _store;
    private readonly ISessionLedger _ledger;
    public override WorkItemState State => WorkItemState.BgSave;

    public BgSaveRunner(IEngineRpcGateway engine, IStoreGateway store, ISessionLedger ledger)
    {
        _engine = engine;
        _store = store;
        _ledger = ledger;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var worker = req.DecodeWorker?.Name ?? req.PrefillWorker?.Name;
        if (worker is null)
            return PhaseResult.Fire(SchedulerEvent.BgSaveSucceeded); // nothing to save

        try
        {
            // COMBINED (epic #591): SaveKv was SKIPPED so the prefill KV blob is
            // still in memory — Put it DIRECTLY, no StateGet (COMBINED persists
            // the in-memory prefill blob directly (owner-approved 2026-08-26,
            // see #699/#695 drift inventory)). Gated on HydraConfigDelivered (review #5): a
            // combined request that took the #279 HTTP-prefill fallback never
            // delivered hydra_config AND nulls KvBlob, but a later cross-node
            // RestoreKv can re-set req.KvBlob to the PRE-decode store blob — the
            // direct-Put would then persist stale prefill state against the
            // post-decode ledger NPast. Requiring a successful hydra_config
            // delivery guarantees the blob is the real in-memory prefill KV.
            // Every OTHER path either nulls KvBlob in SaveKv or takes the
            // store-fallback (which keeps the legacy StateGet capture), so they
            // keep the StateGet + Put branch below.
            if (req.MultiMode == MultiEngineMode.Combined
                && req.HydraConfigDelivered
                && req.KvBlob is not null)
            {
                await _store.PutAsync(StoreKeys.KvKey(req.SessionId), req.KvBlob, CancellationToken.None);
                _ledger.MarkStoreState(req.SessionId);
                req.KvBlob = null;
            }
            else
            {
                var slotKey = RestoreRunner.DecodeSlotId(req)?.ToString() ?? "0";
                var kv = await _engine.CaptureAsync(worker, slotKey, CancellationToken.None);
                if (kv is not null)
                {
                    await _store.PutAsync(StoreKeys.KvKey(req.SessionId), kv, CancellationToken.None);
                    _ledger.MarkStoreState(req.SessionId);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "v2_bg_save_failed Sid={Sid}", req.SessionId);
        }

        return PhaseResult.Fire(SchedulerEvent.BgSaveSucceeded);
    }
}
