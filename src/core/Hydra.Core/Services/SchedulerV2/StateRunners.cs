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

/// <summary>Runs engine prefill on the prefill worker, captures the KV blob + n_past.
/// When the engine's binary predates the PREFILL opcode (#279, NotImplemented), falls
/// back to the HTTP prefill — an n_predict=0 completion that builds the KV in the slot —
/// and the request continues via the normal path (SaveKv captures the slot KV via
/// StateGet).</summary>
public sealed class PrefillRunner : WorkerStateRunner
{
    private readonly IEngineRpcGateway _engine;
    private readonly ICompletionProxyService _proxy;
    public override WorkItemState State => WorkItemState.Prefill;

    public PrefillRunner(IEngineRpcGateway engine, ICompletionProxyService proxy)
    {
        _engine = engine;
        _proxy = proxy;
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
            var result = await _engine.PrefillAsync(req.PrefillWorker.Name, slotKey, req.Chat, ct);
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
            req.KvIdentity = new ModelIdentity
            {
                Tokenizer = result.Tokenizer,
                ModelName = result.ModelName,
                ModelQuant = result.ModelQuant,
                ModelCapabilities = result.ModelCapabilities,
            };
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
/// RestoreKv re-fetches it).</summary>
public sealed class SaveKvRunner : WorkerStateRunner
{
    private readonly IStoreGateway _store;
    private readonly ISessionLedger _ledger;
    private readonly IEngineRpcGateway _engine;
    public override WorkItemState State => WorkItemState.SaveKv;

    public SaveKvRunner(IStoreGateway store, ISessionLedger ledger, IEngineRpcGateway engine)
    {
        _store = store;
        _ledger = ledger;
        _engine = engine;
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
            saved = await _store.PutAsync(StoreKeys.KvKey(req.SessionId), kv, ct);
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
        // parity: cold_atomic_engine golden has none).
        if (req.DecodeLease is null && req.DecodeWorker.Name == req.PrefillWorker?.Name)
            return PhaseResult.Fire(SchedulerEvent.RestoreSucceeded);

        var kv = await _store.GetAsync(StoreKeys.KvKey(req.SessionId), ct);
        if (kv is null)
        {
            req.Error = new InvalidOperationException($"restore: store has no KV for session {req.SessionId}");
            return PhaseResult.Fire(SchedulerEvent.Failed);
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
