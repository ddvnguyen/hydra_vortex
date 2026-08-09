using System.Diagnostics;
using Hydra.Core.Models;
using Hydra.Core.Repositories;

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

    public PlanRunner(
        IRoutePlanner planner,
        ILeaseManager leases,
        ISessionLedger ledger,
        IReadOnlyList<WorkerConfig> workers,
        IWorkerTracker tracker,
        IHealthMonitorService health)
    {
        _planner = planner;
        _leases = leases;
        _ledger = ledger;
        _workers = workers;
        _tracker = tracker;
        _health = health;
    }

    public override WorkItemState State => WorkItemState.RouteDecision;
    public override bool Handles(WorkItemState state)
        => state is WorkItemState.RouteDecision or WorkItemState.PickDecode;

    public override Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
        => ctx.Request.State == WorkItemState.RouteDecision
            ? PlanInitialAsync(ctx)
            : PlanDecodeAsync(ctx);

    private Task<PhaseResult> PlanInitialAsync(RunnerContext ctx)
    {
        var req = ctx.Request;
        var plan = _planner.Plan(req.Chat, req.Type, _workers, _tracker, _health, _ledger);
        req.Plan = plan;
        req.PrefillWorker = plan.PrefillWorker is null ? null : Resolve(plan.PrefillWorker);
        req.DecodeWorker = plan.DecodeWorker is null ? null : Resolve(plan.DecodeWorker);
        req.RecordPhase("plan_ms", 0);

        return plan.PrefillWorker is null
            ? Task.FromResult(PhaseResult.Fire(SchedulerEvent.SoloRouted))  // warm/decode-only
            : Task.FromResult(PhaseResult.Fire(SchedulerEvent.RouteSucceeded));
    }

    private Task<PhaseResult> PlanDecodeAsync(RunnerContext ctx)
    {
        var req = ctx.Request;

        // Decode worker already known (atomic same-slot) → no swap, no new lease.
        if (req.DecodeWorker is not null)
            return Task.FromResult(PhaseResult.Fire(SchedulerEvent.DecodePicked));

        var decodeNode = _planner.PlanDecode(req.Chat, _ledger.Lookup(req.SessionId), _workers, _tracker, _health);
        if (decodeNode is null || (req.DecodeWorker = Resolve(decodeNode)) is null)
            return Task.FromResult(PhaseResult.Fire(SchedulerEvent.Failed));

        // GPU-utilization rule: free the prefill slot, then take the decode slot —
        // never two slots held at once.
        _leases.Release(req.PrefillLease);
        req.PrefillLease = null;

        var decodeLease = _leases.TryAcquire(req.DecodeWorker.Name, req.SessionId);
        if (decodeLease is null)
            return Task.FromResult(PhaseResult.Fire(SchedulerEvent.Failed));
        req.DecodeLease = decodeLease;

        req.RecordPhase("pick_decode_ms", 0);
        return Task.FromResult(PhaseResult.Fire(SchedulerEvent.DecodePicked));
    }

    private WorkerConfig? Resolve(string? name)
        => name is null ? null : _workers.FirstOrDefault(w => w.Name == name);
}

/// <summary>Runs engine prefill on the prefill worker, captures the KV blob + n_past.</summary>
public sealed class PrefillRunner : WorkerStateRunner
{
    private readonly IEngineRpcGateway _engine;
    public override WorkItemState State => WorkItemState.Prefill;

    public PrefillRunner(IEngineRpcGateway engine) => _engine = engine;

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.PrefillWorker is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _engine.PrefillAsync(req.PrefillWorker.Name, req.Chat, ct);
            req.NPastAfter = result.NPast;
            req.KvBlob = result.KVPayload;
            req.RecordPhase("prefill_ms", sw.ElapsedMilliseconds);
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
}

/// <summary>Persists the KV blob produced by prefill to the Store.</summary>
public sealed class SaveKvRunner : WorkerStateRunner
{
    private readonly IStoreGateway _store;
    public override WorkItemState State => WorkItemState.SaveKv;

    public SaveKvRunner(IStoreGateway store) => _store = store;

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.KvBlob is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        var ok = await _store.PutAsync(req.SessionId, req.KvBlob, ct);
        if (!ok)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        return PhaseResult.Fire(SchedulerEvent.SaveKvSucceeded);
    }
}

/// <summary>Restores the KV onto the DECODE worker before decoding.</summary>
public sealed class RestoreRunner : WorkerStateRunner
{
    private readonly IStoreGateway _store;
    private readonly IEngineRpcGateway _engine;
    public override WorkItemState State => WorkItemState.RestoreKv;

    public RestoreRunner(IStoreGateway store, IEngineRpcGateway engine)
    {
        _store = store;
        _engine = engine;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.DecodeWorker is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        var kv = await _store.GetAsync(req.SessionId, ct);
        if (kv is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        if (!await _engine.RestoreAsync(req.DecodeWorker.Name, req.SessionId, kv, req.NPastAfter, ct))
            return PhaseResult.Fire(SchedulerEvent.Failed);

        return PhaseResult.Fire(SchedulerEvent.RestoreSucceeded);
    }
}

/// <summary>
/// Decodes via the shared HTTP completion proxy. Streaming hands the chunk
/// stream to the caller and suspends until <c>NotifyStreamComplete</c>;
/// non-streaming completes inline.
/// </summary>
public sealed class DecodeRunner : WorkerStateRunner
{
    private readonly ICompletionProxyService _proxy;
    public override WorkItemState State => WorkItemState.Decode;

    public DecodeRunner(ICompletionProxyService proxy) => _proxy = proxy;

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var workerUrl = req.DecodeWorker?.LlamaUrl;
        if (string.IsNullOrEmpty(workerUrl))
            return PhaseResult.Fire(SchedulerEvent.Failed);

        if (req.IsStreaming)
        {
            var stream = _proxy.ProxyCompletionStreamAsync(workerUrl, req.Chat.Body, req.TraceId, ct);
            req.DecodeChunks = stream;
            req.StreamReady.TrySetResult(stream);
            return PhaseResult.Wait; // resumed by NotifyStreamComplete
        }

        req.Response = await _proxy.ProxyCompletionAsync(workerUrl, req.Chat.Body, req.TraceId, ct);
        return PhaseResult.Fire(SchedulerEvent.DecodeSucceeded);
    }
}

/// <summary>Background save after a stream ends. No-op for the current tranche
/// (single-blob save already happened); write-behind is a later tranche.</summary>
public sealed class BgSaveRunner : WorkerStateRunner
{
    public override WorkItemState State => WorkItemState.BgSave;

    public override Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
        => Task.FromResult(PhaseResult.Fire(SchedulerEvent.BgSaveSucceeded));
}
