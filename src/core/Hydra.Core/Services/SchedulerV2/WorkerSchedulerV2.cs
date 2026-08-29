using System.Collections.Concurrent;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Scheduling;
using Hydra.StateMachine;
using Serilog;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// v2 scheduler — the SOLID successor to <c>WorkerSchedulerService</c>. Implements
/// the same <see cref="IWorkerScheduler"/> contract (A/B swappable) with a typed
/// <see cref="ICompletionResult"/> submit, and shares ZERO implementation with the
/// legacy god class.
///
/// <para>Design (epic #591):</para>
/// <list type="bullet">
/// <item><b>State runners</b> — one class per <see cref="WorkItemState"/> deriving
/// from <see cref="WorkerStateRunner"/> (open/closed: a new state = a new runner +
/// one <c>Configure</c> edge). The <c>PlanRunner</c> implements both plan states.</item>
/// <item><b>Simple model</b> — <see cref="SchedulerRequest"/>; no legacy WorkItem.</item>
/// <item><b>DIP</b> — everything injected; no static singletons.</item>
/// <item><b>Concurrency</b> — no global semaphore: requests wait in a priority
/// queue and run only while they own a slot lease; two-phase requests hold ONE
/// slot at a time (prefill released before the decode slot is acquired).</item>
/// </list>
/// </summary>
public sealed class WorkerSchedulerV2 : IWorkerScheduler
{
    private const int AdmissionCapacity = 4096;

    /// <summary>Streaming reaper cadence: how often suspended streaming requests
    /// are scanned for a missed <c>NotifyStreamComplete</c> (C4 resilience).</summary>
    private static readonly TimeSpan StreamReaperInterval = TimeSpan.FromSeconds(10);

    /// <summary>Streaming reaper timeout: a streamed request whose stream was
    /// handed to the caller but <c>NotifyStreamComplete</c> never arrived within
    /// this window is finalized (Cancelled) and its leases released.</summary>
    private static readonly TimeSpan StreamHandoffTimeout = TimeSpan.FromMinutes(5);

    private readonly CoordinatorConfig _cfg;
    private readonly ISessionLedger _ledger;
    private readonly IWorkerTracker _tracker;
    private readonly IHealthMonitorService _health;
    private readonly IRequestClassifier _classifier;
    private readonly IRoutePlanner _planner;
    private readonly ILeaseManager _leases;
    private readonly ITimelineEmitter _timeline;
    private readonly IEngineRpcGateway _engine;
    private readonly IStoreGateway _store;
    private readonly ICompletionProxyService _proxy;
    private readonly ILogger _log;
    private readonly Dictionary<WorkItemState, WorkerStateRunner> _runners;

    private readonly PriorityWaiterQueue<SchedulerRequest> _admission = new(AdmissionCapacity);
    private readonly MailboxExecutor _admissionExecutor = new();
    // Streaming map keyed by TraceId (C4): two concurrent streaming turns on one
    // session must not overwrite each other's entry (a SessionId key would orphan
    // the first turn's lease). The session→traceId index resolves the LATEST turn
    // for the one-arg NotifyStreamComplete contract; the reaper cleans up turns
    // whose NotifyStreamComplete never arrives.
    private readonly ConcurrentDictionary<string, SchedulerRequest> _streaming = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionToTraceId = new(StringComparer.Ordinal);

    // ── IWorkerScheduler: last-dispatched telemetry (populated at decode start) ──

    public string? LastDispatchedNode { get; private set; }
    public string? LastDispatchedModel { get; private set; }
    public string? LastDispatchedTokenizer { get; private set; }
    public string? LastDispatchedModelName { get; private set; }
    public string? LastDispatchedModelQuant { get; private set; }
    public uint LastDispatchedModelCapabilities { get; private set; }

    public WorkerSchedulerV2(
        CoordinatorConfig cfg,
        ISessionLedger ledger,
        IWorkerTracker tracker,
        IHealthMonitorService health,
        IRequestClassifier classifier,
        IRoutePlanner planner,
        ILeaseManager leases,
        IEnumerable<WorkerStateRunner> runners,
        ITimelineEmitter timeline,
        IEngineRpcGateway engine,
        IStoreGateway store,
        ICompletionProxyService proxy,
        ILogger? log = null)
    {
        _cfg = cfg;
        _ledger = ledger;
        _tracker = tracker;
        _health = health;
        _classifier = classifier;
        _planner = planner;
        _leases = leases;
        _timeline = timeline;
        _engine = engine;
        _store = store;
        _proxy = proxy;
        _log = log ?? Serilog.Log.ForContext("component", "coordinator-v2");

        // NOTE: v2 does NOT sync the global ChunkEngine.CHUNK_SIZE / ChunkConstants
        // statics here (unlike the legacy ctor). The v2 chunked save passes the
        // configured chunk size EXPLICITLY (SaveKvRunner → ChunkEngine.ChunkAndHash
        // overload + StoreGateway.PushChunksAsync), so it never depends on — nor
        // mutates — those global statics (which chunked-save scenarios otherwise
        // race under parallel test execution). The legacy scheduler + DI wiring
        // (CoordinatorServiceExtensions.AddCoordinator) keep syncing them.

        // One runner per handled state (PlanRunner registers for RouteDecision + PickDecode).
        _runners = runners
            .SelectMany(r => Enum.GetValues<WorkItemState>().Where(r.Handles).Select(s => (State: s, Runner: r)))
            .ToDictionary(x => x.State, x => x.Runner);
    }

    // ── Submission (typed contract) ──

    public async Task<ICompletionResult> SubmitAsync(
        Dictionary<string, object> request,
        List<Dictionary<string, object>> messages,
        string sessionId,
        int estimatedTokens,
        int maxTokens,
        string? prefixHash,
        CancellationToken ct,
        int systemPromptTokens = 0)
    {
        var chat = ChatRequest.FromSubmit(request, messages, sessionId, estimatedTokens, maxTokens, prefixHash, systemPromptTokens);
        // Warm gate (review): a session is Solo-eligible only when it holds BOTH a
        // resident slot (SlotFreed=false) AND store state — otherwise routing warm
        // could decode on a node without the resident KV (#469 class).
        var type = _classifier.Classify(chat, _cfg,
            hasWarmSession: _ledger.Lookup(sessionId) is { SlotFreed: false, HasStoreState: true });
        var req = new SchedulerRequest(chat, type, _classifier.ComputePriority(type))
        {
            CallerToken = ct, // review #3: the caller's token reaches the running pipeline
        };

        if (type == RequestType.Solo)
            AdoptPlan(req, _planner.Plan(chat, type, _cfg.Workers, _tracker, _health, _ledger, _cfg));

        if (!_admission.TryEnqueue(req, req.Priority))
        {
            req.Completion.TrySetException(new InvalidOperationException("scheduler admission queue full"));
            return await req.Completion.Task;
        }

        SignalEvaluator();

        // Streaming: return the chunk stream once decode starts (controller SSE).
        if (chat.Stream)
        {
            var chunks = await req.StreamReady.Task.WaitAsync(TimeSpan.FromSeconds(_cfg.LlamaRequestTimeoutS), ct);
            return new StreamCompletionResult(chunks);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_cfg.LlamaRequestTimeoutS));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return await req.Completion.Task.WaitAsync(linked.Token);
    }

    // ── Evaluator (serialized via the admission mailbox) ──

    private void SignalEvaluator()
        => _ = _admissionExecutor.PostAsync(() => EvaluateAsync(CancellationToken.None));

    /// <summary>Adopt a routing decision onto the request: the plan itself PLUS the
    /// COMBINED multi-engine fields (peer worker, mode, engine config) the pipeline
    /// reads later — <see cref="RunPipelineAsync"/> reserves the peer from
    /// <c>MultiMode</c>/<c>PeerWorker</c>; <see cref="PrefillRunner"/> builds the
    /// hydra_config from <c>MultiEngineConfig</c>.</summary>
    private void AdoptPlan(SchedulerRequest req, RouteDecision plan)
    {
        req.Plan = plan;
        req.MultiMode = plan.MultiMode;
        req.MultiEngineConfig = plan.MultiEngineConfig;
        req.PeerWorker = plan.PeerWorker is null ? null : _cfg.Workers.FirstOrDefault(w => w.Name == plan.PeerWorker);
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        while (_admission.TryPeek(out var req, out _))
        {
            if (ct.IsCancellationRequested) break;

            if (req.Completion.Task.IsCanceled)
            {
                _admission.TryDequeue(out _);
                await FinalizeAsync(req, WorkItemState.Cancelled);
                continue;
            }

            // Plan lazily (once) so warm-affinity/cold decisions reflect live capacity.
            if (!req.Plan.HasCapacity)
            {
                AdoptPlan(req, _planner.Plan(req.Chat, req.Type, _cfg.Workers, _tracker, _health, _ledger, _cfg));
                if (!req.Plan.HasCapacity && _leases.WarmLeaseCount > 0)
                {
                    // Review #5: on-demand warm-lease eviction — the fresh plan found
                    // no worker because warm-held slots are blocking; evict the OLDEST
                    // warm lease (save + erase) and re-plan (mirrors legacy
                    // WorkerSchedulerService.cs:986-1000, 2524-2536).
                    await EvictOldestWarmAsync();
                    AdoptPlan(req, _planner.Plan(req.Chat, req.Type, _cfg.Workers, _tracker, _health, _ledger, _cfg));
                }
                if (!req.Plan.HasCapacity)
                    break; // no viable worker — wait for a slot release / health change
            }

            if (!_leases.HasCapacity(req.Plan, req.SessionId))
            {
                if (_leases.WarmLeaseCount > 0)
                {
                    await EvictOldestWarmAsync();
                    if (_leases.HasCapacity(req.Plan, req.SessionId))
                    {
                        _admission.TryDequeue(out _);
                        _ = RunPipelineAsync(req, req.CallerToken);
                        continue;
                    }
                }
                break; // no viable worker — wait for a slot release / health change
            }

            _admission.TryDequeue(out _);
            _ = RunPipelineAsync(req, req.CallerToken); // review #3: the caller's token, not None
        }
    }

    /// <summary>Evict the oldest warm lease under slot pressure (review #5):
    /// save-before-erase, release, mark the ledger evicted.</summary>
    private async Task EvictOldestWarmAsync()
    {
        if (!_leases.TryTakeOldestWarm(out var sessionId, out var lease))
            return;
        await SaveAndEraseSlotAsync(sessionId, lease);
        _leases.Release(lease);
        _ledger.MarkEvicted(sessionId);
    }

    /// <summary>Save-before-erase for a warm slot (review #1, CRITICAL): capture the
    /// slot's KV (StateGet) + persist it, then EraseSlot the engine slot. Best-effort —
    /// failures are logged, never thrown (an eviction must not fail the caller).</summary>
    private async Task SaveAndEraseSlotAsync(string sessionId, SlotLease lease)
    {
        var workerUrl = _cfg.Workers.FirstOrDefault(w => w.Name == lease.WorkerName)?.LlamaUrl;
        try
        {
            var kv = await _engine.CaptureAsync(lease.WorkerName, lease.SlotId.ToString(), CancellationToken.None);
            if (kv is not null)
                await _store.PutAsync(StoreKeys.KvKey(sessionId), kv, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "v2_evict_save_failed Sid={Sid} Worker={W} Slot={S}", sessionId, lease.WorkerName, lease.SlotId);
        }
        if (!string.IsNullOrEmpty(workerUrl))
        {
            try
            {
                await _proxy.EraseSlotAsync(workerUrl, lease.SlotId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "v2_evict_erase_failed Sid={Sid} Worker={W} Slot={S}", sessionId, lease.WorkerName, lease.SlotId);
            }
        }
    }

    private async Task RunPipelineAsync(SchedulerRequest req, CancellationToken ct)
    {
        // GPU-utilization rule: Prefill-type acquires the PREFILL slot only; the
        // decode slot is acquired at the PlanRunner (PickDecode) handoff. Solo
        // (decode-only) acquires the decode slot directly.
        var startWorker = req.Plan.PrefillWorker ?? req.Plan.DecodeWorker;
        if (string.IsNullOrEmpty(startWorker))
        {
            _admission.Enqueue(req, req.Priority);
            SignalEvaluator();
            return;
        }

        // Warm (Solo) turn: reuse the session's held warm slot instead of
        // acquiring a new one (C2 — the slot is already warm for this session).
        // CROSS-NODE fallback (epic #591): a Solo whose plan is a cross-node
        // restore (ReuseStoreState=true — the affinity node had no free slot)
        // must acquire a FRESH slot on the planned alternate decode worker, NOT
        // reuse the session's warm stash on the affinity node (which is exactly
        // the slot that made the affinity probe fail).
        var warmLease = req.Type == RequestType.Solo && !req.Plan.ReuseStoreState
            ? _leases.TakeWarm(req.SessionId)
            : null;
        var lease = warmLease ?? _leases.TryAcquire(startWorker, req.SessionId);
        if (lease is null)
        {
            // Capacity lost between planning and acquisition — requeue.
            _admission.Enqueue(req, req.Priority);
            SignalEvaluator();
            return;
        }

        if (req.Plan.PrefillWorker is not null)
            req.PrefillLease = lease;
        else
            req.DecodeLease = lease;

        // COMBINED (epic #591): reserve the PEER GPU EXCLUSIVELY for the whole
        // request (one GPU = one task, P1) — AFTER the head slot is acquired
        // (legacy TryAcquireMultiEnginePrefill ordering). A failed peer
        // reservation DEGRADES to a solo route on the head IN PLACE — legacy
        // TryAcquireMultiEnginePrefill returns false and ColdRouteAsync falls
        // through to the normal solo cold route (WorkerSchedulerService.cs:
        // 904-913); it never hard-fails the request (review #4). The head slot
        // stays held; clearing the multi-engine fields makes the pipeline run
        // Prefill → SaveKv → decode as a normal single-GPU request (the
        // CombinedPrefillSucceeded skip no longer fires). The reservation (when
        // it succeeds) is released at finalize via ReleaseLeases → ReleasePeer.
        if (req.MultiMode == MultiEngineMode.Combined && req.PeerWorker is not null)
        {
            if (_leases.TryReservePeer(req.PeerWorker.Name))
            {
                req.PeerLease = new ExclusivePeerReservation(req.PeerWorker.Name, _tracker);
            }
            else
            {
                Serilog.Log.Warning("v2_combined_peer_reserve_failed Sid={Sid} Peer={Peer} — degrading to solo",
                    req.SessionId, req.PeerWorker.Name);
                req.MultiMode = MultiEngineMode.None;
                req.MultiEngineConfig = null;
                req.PeerWorker = null;
            }
        }

        req.State = WorkItemState.RouteDecision; // sync request with the machine's initial state
        var suspended = false;
        try
        {
            var machine = NewMachine(WorkItemState.RouteDecision);
            suspended = await StepAsync(req, startWorker, machine, ct);
        }
        finally
        {
            if (suspended)
            {
                // Streaming: the slot stays held until NotifyStreamComplete
                // (which releases it — no warm lease for streaming turns). C4:
                // keyed by TraceId so a second concurrent streaming turn on the
                // same session cannot overwrite the first; the session→traceId
                // index tracks the LATEST turn for the one-arg notify contract.
                _streaming[req.TraceId] = req;
                _sessionToTraceId[req.SessionId] = req.TraceId;
            }
            else if (req.State == WorkItemState.Done && !req.IsStreaming && req.MultiMode != MultiEngineMode.Combined)
            {
                StashWarm(req); // C2: hold the decode slot warm for the next turn
            }
            else
            {
                ReleaseLeases(req);
            }

            // Completion resolves AFTER the lease finalization (stash/release) —
            // re-review finding: a caller whose SubmitAsync returned "done" must be
            // able to rely on the warm lease being in place for an immediate
            // follow-up turn (previously the completion fired before StashWarm,
            // racing the warm-routing contract + flaking warm-lease assertions).
            if (!suspended)
                await FinalizeAsync(req, req.State);
        }
    }

    /// <summary>Transfer the request's held slot lease into the session's warm stash.
    /// Atomic (single-node) sessions are marked evicted in the ledger (SlotFreed=true)
    /// even though the slot stays warm-held — wire parity: cold_atomic_engine golden
    /// pins SlotFreed=true + rtx busy=1. P/D sessions stay warm-routable.</summary>
    private void StashWarm(SchedulerRequest req)
    {
        var warmLease = req.DecodeLease ?? req.PrefillLease;
        if (warmLease is not null)
            _leases.Stash(req.SessionId, warmLease);
        req.PrefillLease = null;
        req.DecodeLease = null;

        if (req.Type == RequestType.Atomic)
            _ledger.MarkEvicted(req.SessionId);
    }

    /// <summary>Stepping driver: run the state's runner, fire its event, sync
    /// <c>SchedulerRequest.State</c>, repeat until terminal / suspended.</summary>
    private async Task<bool> StepAsync(
        SchedulerRequest req,
        string worker,
        StateMachine<WorkItemState, SchedulerEvent> machine,
        CancellationToken ct)
    {
        var suspended = false;
        try
        {
            while (!req.IsTerminal)
            {
                ct.ThrowIfCancellationRequested();

                if (!_runners.TryGetValue(req.State, out var runner))
                {
                    req.Error = new InvalidOperationException($"no state runner for state {req.State}");
                    await machine.FireAsync(SchedulerEvent.Failed, req, CancellationToken.None);
                    req.State = machine.State;
                    break;
                }

                var result = await runner.RunAsync(new RunnerContext(req, worker), ct);
                switch (result.Outcome)
                {
                    case PhaseOutcome.Fire:
                        await machine.FireAsync(result.Event, req, ct);
                        req.State = machine.State;
                        if (req.State == WorkItemState.Decode)
                            UpdateLastDispatched(req.DecodeWorker);
                        break;
                    case PhaseOutcome.Wait:
                        suspended = true;
                        return suspended;
                    default:
                        return suspended;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || req.Completion.Task.IsCanceled)
        {
            await machine.FireAsync(SchedulerEvent.Cancelled, req, CancellationToken.None);
            req.State = machine.State;
        }
        catch (Exception ex)
        {
            // #716: increment short-write counter when the sender detected a byte-count mismatch
            if (ex is InvalidDataException ide && ide.Message.Contains("short write"))
                CoordinatorMetrics.RestoreStreamShortWrites.Inc();
            req.Error = ex;
            _log.Warning(ex, "v2_pipeline_error Sid={Sid}", req.SessionId);
            await machine.FireAsync(SchedulerEvent.Failed, req, CancellationToken.None);
            req.State = machine.State;
        }
        return suspended;
    }

    private async Task FinalizeAsync(SchedulerRequest req, WorkItemState terminal)
    {
        _timeline.Emit(req, terminal);

        // Streaming error surfacing (review): if the pipeline failed BEFORE the
        // stream started, the caller (SubmitAsync) is waiting on StreamReady —
        // surface the real error there instead of a timeout.
        if (req.IsStreaming && !req.StreamReady.Task.IsCompleted
            && terminal is WorkItemState.Failed or WorkItemState.Cancelled)
            req.StreamReady.TrySetException(req.Error ?? new InvalidOperationException($"stream failed in state {terminal}"));

        switch (terminal)
        {
            case WorkItemState.Done:
                req.Completion.TrySetResult(new FinalCompletionResult(req.Response ?? new { status = "done" }));
                break;
            case WorkItemState.Failed:
                // Atomic (single-node) requests hold no warm slot: a failure releases
                // the slot, so the ledger must reflect that the KV is store-only
                // (SlotFreed=true + HasStoreState=true — the legacy atomic wire runs
                // SaveKv → MarkEvicted before decode, so even a decode failure leaves
                // the entry evicted; golden merged_decode_gate_a_reject pins this).
                if (req.Type == RequestType.Atomic)
                    _ledger.MarkEvicted(req.SessionId);
                req.Completion.TrySetException(req.Error ?? new InvalidOperationException($"request failed in state {terminal}"));
                break;
            case WorkItemState.Cancelled:
                req.Completion.TrySetCanceled();
                break;
            default:
                req.Completion.TrySetResult(new FinalCompletionResult(req.Response ?? new { status = terminal.ToString() }));
                break;
        }
        SignalEvaluator();
    }

    private void ReleaseLeases(SchedulerRequest req)
    {
        _leases.Release(req.PrefillLease);
        req.PrefillLease = null;
        _leases.Release(req.DecodeLease);
        req.DecodeLease = null;
        // COMBINED (epic #591): release the peer's exclusive reservation so the
        // peer GPU returns to service (disposing ExclusivePeerReservation clears
        // the tracker's exclusive flag).
        _leases.ReleasePeer(req.PeerLease);
        req.PeerLease = null;
    }

    private void UpdateLastDispatched(WorkerConfig? worker)
    {
        if (worker is null) return;
        LastDispatchedNode = worker.Name;
        LastDispatchedModel = worker.ModelAlias;
        LastDispatchedTokenizer = "";
        LastDispatchedModelName = "";
        LastDispatchedModelQuant = "";
        LastDispatchedModelCapabilities = 0;
    }

    // ── DSL state machine (per-request instance; transition table shared) ──

    private StateMachine<WorkItemState, SchedulerEvent> NewMachine(WorkItemState initial)
    {
        var m = new StateMachine<WorkItemState, SchedulerEvent>(initial);
        m.BeforeAny(OnTransitionStart);
        m.AfterAny(OnTransitionEnd);

        m.Configure(WorkItemState.RouteDecision)
            .On(SchedulerEvent.RouteSucceeded, WorkItemState.Prefill)
            .On(SchedulerEvent.PrefixRestoreRouted, WorkItemState.PrefixRestore)
            .On(SchedulerEvent.SoloRouted, WorkItemState.Decode) // warm/decode-only: KV resident
            .On(SchedulerEvent.ReuseStore, WorkItemState.RestoreKv) // C4: store KV reuse skips prefill
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.PrefixRestore)
            .On(SchedulerEvent.PrefixRestoreSucceeded, WorkItemState.Prefill) // hit or miss → prefill
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.Prefill)
            .On(SchedulerEvent.PrefillSucceeded, WorkItemState.SaveKv)
            // COMBINED (epic #591): prefill delivered hydra_config + the KV is
            // resident on the head — skip SaveKv/PickDecode/RestoreKv, decode in place.
            .On(SchedulerEvent.CombinedPrefillSucceeded, WorkItemState.Decode)
            .On(SchedulerEvent.Retry, ctx => ((SchedulerRequest)ctx.Payload!).RetryCount < SchedulerRequest.MaxRetries, WorkItemState.RouteDecision)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.SaveKv)
            .On(SchedulerEvent.SaveKvSucceeded, WorkItemState.PickDecode)
            .On(SchedulerEvent.SaveKvFallbackSucceeded, WorkItemState.Decode) // store-down: decode in place
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        // Two-phase handoff: pick the decode worker + swap slots here.
        m.Configure(WorkItemState.PickDecode)
            .On(SchedulerEvent.DecodePicked, WorkItemState.RestoreKv)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.RestoreKv)
            .On(SchedulerEvent.RestoreSucceeded, WorkItemState.Decode)
            .On(SchedulerEvent.Reprefill, WorkItemState.Prefill) // #470 cross-model abort → re-prefill on the correct model
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.Decode)
            .On(SchedulerEvent.DecodeSucceeded, WorkItemState.BgSave)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.BgSave)
            .On(SchedulerEvent.BgSaveSucceeded, WorkItemState.Done)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        // Terminal absorbing states.
        m.Configure(WorkItemState.Done);
        m.Configure(WorkItemState.Failed);
        m.Configure(WorkItemState.Cancelled);
        return m;
    }

    private Task OnTransitionStart(Transition<WorkItemState, SchedulerEvent> t)
    {
        _timeline.OnTransitionStart(t);
        return Task.CompletedTask;
    }

    private Task OnTransitionEnd(Transition<WorkItemState, SchedulerEvent> t)
    {
        _timeline.OnTransitionEnd(t);
        return Task.CompletedTask;
    }

    // ── Remaining IWorkerScheduler members ──

    public int WarmLeaseCount => _leases.WarmLeaseCount;

    public Task RunAsync(CancellationToken ct)
    {
        _admissionExecutor.Start();
        _health.HealthyChanged += SignalEvaluator;
        _tracker.SlotReleased += SignalEvaluator; // wake the evaluator on ANY slot release (review)
        _ = ReapStreamsLoopAsync(ct); // C4: streaming reaper (missed NotifyStreamComplete)
        return AwaitShutdown(ct);
    }

    /// <summary>Periodic streaming reaper: finalizes + releases any streamed
    /// request whose stream was handed to the caller but <c>NotifyStreamComplete</c>
    /// never arrived within <see cref="StreamHandoffTimeout"/>. Without this a
    /// dropped SSE client would orphan the turn's slot lease forever.</summary>
    private async Task ReapStreamsLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(StreamReaperInterval, ct);
                try
                {
                    await ReapStreamedRequestsAsync(ct);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "v2_stream_reaper_cycle_error");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    /// <summary>One reaper pass over the streaming map. Internal so tests can
    /// drive it deterministically without waiting on the 10s timer.</summary>
    internal async Task ReapStreamedRequestsAsync(CancellationToken ct)
    {
        foreach (var (traceId, req) in _streaming.ToArray())
        {
            if (req.StreamStartedAt == default
                || DateTime.UtcNow - req.StreamStartedAt < StreamHandoffTimeout)
                continue;

            if (!_streaming.TryRemove(traceId, out _))
                continue; // NotifyStreamComplete won the race

            if (_sessionToTraceId.TryGetValue(req.SessionId, out var current) && current == traceId)
                _sessionToTraceId.TryRemove(req.SessionId, out _);

            _log.Warning(
                "v2_stream_reaped Sid={Sid} Trace={Trace} AgeS={AgeS:F0} — NotifyStreamComplete never arrived within {Timeout}",
                req.SessionId, req.TraceId, (DateTime.UtcNow - req.StreamStartedAt).TotalSeconds, StreamHandoffTimeout);
            req.Error = new TimeoutException(
                $"streaming request {req.TraceId} reaped: NotifyStreamComplete never arrived within {StreamHandoffTimeout}");

            var machine = NewMachine(WorkItemState.Decode);
            await machine.FireAsync(SchedulerEvent.Cancelled, req, CancellationToken.None);
            req.State = machine.State;
            ReleaseLeases(req);
            _ledger.MarkEvicted(req.SessionId); // reaped stream: no warm lease (golden SlotFreed=true)
            await FinalizeAsync(req, req.State);
        }
    }

    /// <summary>Streaming requests currently suspended awaiting NotifyStreamComplete
    /// (internal — observable for tests).</summary>
    internal IReadOnlyCollection<SchedulerRequest> StreamingRequests => _streaming.Values.ToArray();

    private async Task AwaitShutdown(CancellationToken ct)
    {
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            _health.HealthyChanged -= SignalEvaluator;
            _tracker.SlotReleased -= SignalEvaluator;
            await _admissionExecutor.StopAsync();
        }
    }

    /// <summary>Resumes a suspended streaming request and completes it: the stream
    /// already delivered its chunks, so skip decode and run background-save to Done.
    /// The streaming map is keyed by TraceId (C4), so the session's LATEST turn is
    /// resolved via the session→traceId index; callers that know the exact turn can
    /// pass its traceId via the overload.</summary>
    public Task NotifyStreamComplete(string sessionId)
    {
        _sessionToTraceId.TryGetValue(sessionId, out var traceId);
        return NotifyStreamComplete(sessionId, traceId);
    }

    /// <summary>Resume the streaming request with the given traceId directly
    /// (bypasses the session→latest-turn resolution).</summary>
    public async Task NotifyStreamComplete(string sessionId, string? traceId)
    {
        if (string.IsNullOrEmpty(traceId) || !_streaming.TryRemove(traceId, out var req))
            return;

        if (_sessionToTraceId.TryGetValue(sessionId, out var current) && current == traceId)
            _sessionToTraceId.TryRemove(sessionId, out _);

        try
        {
            var machine = NewMachine(WorkItemState.Decode);
            await machine.FireAsync(SchedulerEvent.DecodeSucceeded, req, CancellationToken.None);
            req.State = machine.State;

            while (!req.IsTerminal)
            {
                if (!_runners.TryGetValue(req.State, out var runner))
                    break;
                var result = await runner.RunAsync(
                    new RunnerContext(req, req.Plan.PrefillWorker ?? req.Plan.DecodeWorker ?? ""), CancellationToken.None);
                if (result.Outcome != PhaseOutcome.Fire)
                    break;
                await machine.FireAsync(result.Event, req, CancellationToken.None);
                req.State = machine.State;
            }
        }
        catch (Exception ex)
        {
            // Review #2 (HIGH): surface the failure — drive the machine to Failed so
            // FinalizeAsync resolves the caller's Completion with the exception, NOT
            // a silent success (req.State left at Decode would hit the default branch).
            req.Error = ex;
            _log.Warning(ex, "v2_stream_resume_failed Sid={Sid}", req.SessionId);
            try
            {
                var fail = NewMachine(req.State);
                await fail.FireAsync(SchedulerEvent.Failed, req, CancellationToken.None);
                req.State = fail.State;
            }
            catch (Exception machineEx)
            {
                _log.Warning(machineEx, "v2_stream_resume_failed_state_drive Sid={Sid}", req.SessionId);
                req.State = WorkItemState.Failed; // fallback: force the terminal state
            }
        }
        finally
        {
            ReleaseLeases(req);
            _ledger.MarkEvicted(req.SessionId); // streaming terminal: no warm lease (golden SlotFreed=true)
            await FinalizeAsync(req, req.State);
        }
    }

    /// <summary>Evict a session's warm lease with SAVE-BEFORE-ERASE (review #1,
    /// CRITICAL): capture the slot's KV (StateGet) + persist it, EraseSlot the
    /// engine slot, then release the lease + mark the ledger evicted. Matches legacy
    /// <c>SaveSlotStateBeforeEvictAsync</c> + the documented <c>SlotLease</c> contract —
    /// a TTL-driven eviction must NOT silently drop the session's KV.</summary>
    public async Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct)
    {
        var lease = _leases.TakeWarm(sessionId);
        if (lease is not null)
        {
            await SaveAndEraseSlotAsync(sessionId, lease);
            _leases.Release(lease);
            // Review #6: only mark evicted when we actually evicted the lease. If
            // TakeWarm returned null, an in-flight turn already owns the slot — a
            // MarkEvicted here would be silently UNDONE by that turn's later Stash
            // (re-adding the warm lease against an evicted ledger entry, orphaning it).
            _ledger.MarkEvicted(sessionId);
        }
    }

    /// <summary>v1: not implemented — returns a clear payload (WP3 parity scope).</summary>
    public Task<object> MigrateSessionAsync(string sessionId, string targetNodeName, CancellationToken ct)
        => Task.FromResult<object>(new { ok = false, reason = "migration not yet implemented in v2 (WP3)" });

    /// <summary>v1: not implemented — refuses (WP3 parity scope).</summary>
    public Task<bool> TrySwapQuantAsync(string workerName, string quantKey, string tensorPattern, string traceId, CancellationToken ct)
    {
        _log.Information("v2_swap_quant_refused Worker={Worker}", workerName);
        return Task.FromResult(false);
    }
}
