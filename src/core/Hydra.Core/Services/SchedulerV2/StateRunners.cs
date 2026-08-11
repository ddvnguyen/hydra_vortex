using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

        // The plan is decided ONCE before dispatch (SubmitAsync for Solo; the
        // evaluator otherwise) and must NOT be re-derived here: re-planning at
        // RouteDecision sees the just-acquired start slot as busy (self-held),
        // which would return "no capacity" for a valid Solo/warm route.
        var plan = req.Plan;
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
        _ledger.UpdateLastUsed(req.SessionId); // keep the eviction TTL fresh at decode planning

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
        {
            req.Error = new InvalidOperationException("prefill state entered without a prefill worker");
            return PhaseResult.Fire(SchedulerEvent.Failed);
        }

        try
        {
            await _engine.EnsureChunkConfiguredAsync(req.PrefillWorker.Name, ct); // lazy 0x40 (wire parity)
            var slotKey = req.PrefillLease?.SlotId.ToString() ?? "0"; // engine keys prefill by slot id
            var sw = Stopwatch.StartNew();
            var result = await _engine.PrefillAsync(req.PrefillWorker.Name, slotKey, req.Chat, ct);
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

/// <summary>Persists the KV blob produced by prefill to the Store and registers the
/// session on the PREFILL node (C1 ledger timeline, point 1).</summary>
public sealed class SaveKvRunner : WorkerStateRunner
{
    private readonly IStoreGateway _store;
    private readonly ISessionLedger _ledger;
    public override WorkItemState State => WorkItemState.SaveKv;

    public SaveKvRunner(IStoreGateway store, ISessionLedger ledger)
    {
        _store = store;
        _ledger = ledger;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.KvBlob is null || req.PrefillWorker is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        bool saved;
        try
        {
            saved = await _store.PutAsync(StoreKeys.KvKey(req.SessionId), req.KvBlob, ct);
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
            // (skip restore). The request must NOT fail.
            req.DecodeWorker = req.PrefillWorker;
            return PhaseResult.Fire(SchedulerEvent.SaveKvFallbackSucceeded);
        }

        // C1: register on the prefill node with the post-prefill n_past, and stamp
        // store state (the KV blob is now durable).
        var entry = _ledger.Register(req.SessionId, req.PrefillWorker.Name, req.PrefillLease?.SlotId, req.NPastAfter, req.Chat.PrefixHash);
        lock (entry) { entry.HasStoreState = true; }

        return PhaseResult.Fire(SchedulerEvent.SaveKvSucceeded);
    }
}

/// <summary>Restores the KV onto the DECODE worker before decoding, and RE-REGISTERS
/// the session on the decode node (C1 ledger timeline, point 2 — the P/D goldens
/// pin <c>Ledger.NodeName</c> = decode node).</summary>
public sealed class RestoreRunner : WorkerStateRunner
{
    private readonly IStoreGateway _store;
    private readonly IEngineRpcGateway _engine;
    private readonly ISessionLedger _ledger;
    public override WorkItemState State => WorkItemState.RestoreKv;

    public RestoreRunner(IStoreGateway store, IEngineRpcGateway engine, ISessionLedger ledger)
    {
        _store = store;
        _engine = engine;
        _ledger = ledger;
    }

    public override async Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        if (req.DecodeWorker is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        // Same-node decode (true atomic, no slot swap): the KV is already resident
        // in the held prefill slot — no store Get + StatePut round-trip (wire
        // parity: cold_atomic_engine golden has none).
        if (req.DecodeLease is null && req.DecodeWorker.Name == req.PrefillWorker?.Name)
            return PhaseResult.Fire(SchedulerEvent.RestoreSucceeded);

        await _engine.EnsureChunkConfiguredAsync(req.DecodeWorker.Name, ct); // lazy 0x40 on the decode worker (P/D)
        var kv = await _store.GetAsync(StoreKeys.KvKey(req.SessionId), ct);
        if (kv is null)
            return PhaseResult.Fire(SchedulerEvent.Failed);

        if (!await _engine.RestoreAsync(req.DecodeWorker.Name, req.SessionId, kv, req.NPastAfter, ct))
            return PhaseResult.Fire(SchedulerEvent.Failed);

        // C1: re-register the session on the decode node (its KV now lives here).
        if (req.NPastAfter > 0)
            _ledger.UpdateNPast(req.SessionId, req.NPastAfter);
        _ledger.Register(
            req.SessionId, req.DecodeWorker.Name, DecodeSlotId(req),
            req.NPastAfter > 0 ? req.NPastAfter : (_ledger.Lookup(req.SessionId)?.NPast ?? 0),
            req.Chat.PrefixHash);

        return PhaseResult.Fire(SchedulerEvent.RestoreSucceeded);
    }

    /// <summary>The slot decode runs on: the decode lease, or the prefill lease
    /// for atomic same-slot requests (decode reuses the held slot).</summary>
    internal static int? DecodeSlotId(SchedulerRequest req)
        => req.DecodeLease?.SlotId ?? req.PrefillLease?.SlotId;
}

/// <summary>
/// Decodes via the shared HTTP completion proxy. Registers the session on the
/// DECODE node when it has no ledger entry yet (C1 ledger timeline, point 3) and
/// tracks the completion's usage into the ledger (NPast from total_tokens — the
/// P/D + atomic goldens pin NPast=usage.total_tokens, not the prefill n_past).
/// Streaming hands the chunk stream to the caller and suspends until
/// <c>NotifyStreamComplete</c>; non-streaming completes inline.
/// </summary>
public sealed class DecodeRunner : WorkerStateRunner
{
    private readonly ICompletionProxyService _proxy;
    private readonly IEngineRpcGateway _engine;
    private readonly ISessionLedger _ledger;
    public override WorkItemState State => WorkItemState.Decode;

    public DecodeRunner(ICompletionProxyService proxy, IEngineRpcGateway engine, ISessionLedger ledger)
    {
        _proxy = proxy;
        _engine = engine;
        _ledger = ledger;
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
        // parity: 17-byte 0x40 emitted before decode).
        var slotKey = RestoreRunner.DecodeSlotId(req)?.ToString() ?? "0";
        await _engine.ConfigureAsync(req.DecodeWorker.Name, slotKey, $"{{\"n_predict\":{req.Chat.MaxTokens}}}", ct);

        if (req.IsStreaming)
        {
            var stream = _proxy.ProxyCompletionStreamAsync(workerUrl, req.Chat.Body, req.TraceId, ct);
            req.DecodeChunks = TrackStreamUsage(req, stream);
            RegisterIfMissing(req, req.DecodeWorker.Name, RestoreRunner.DecodeSlotId(req), req.NPastAfter);
            req.StreamReady.TrySetResult(req.DecodeChunks);
            return PhaseResult.Wait; // resumed by NotifyStreamComplete
        }

        req.Response = await _proxy.ProxyCompletionAsync(workerUrl, req.Chat.Body, req.TraceId, ct);
        RegisterIfMissing(req, req.DecodeWorker.Name, RestoreRunner.DecodeSlotId(req), req.NPastAfter);
        TrackAfterCompletion(req);
        return PhaseResult.Fire(SchedulerEvent.DecodeSucceeded);
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
/// post-decode state.
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
            var slotKey = RestoreRunner.DecodeSlotId(req)?.ToString() ?? "0";
            var kv = await _engine.CaptureAsync(worker, slotKey, CancellationToken.None);
            if (kv is not null)
            {
                await _store.PutAsync(StoreKeys.KvKey(req.SessionId), kv, CancellationToken.None);
                _ledger.MarkStoreState(req.SessionId);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "v2_bg_save_failed Sid={Sid}", req.SessionId);
        }

        return PhaseResult.Fire(SchedulerEvent.BgSaveSucceeded);
    }
}
