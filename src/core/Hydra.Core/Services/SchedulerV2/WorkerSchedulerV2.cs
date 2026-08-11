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

    private readonly CoordinatorConfig _cfg;
    private readonly ISessionLedger _ledger;
    private readonly IWorkerTracker _tracker;
    private readonly IHealthMonitorService _health;
    private readonly IRequestClassifier _classifier;
    private readonly IRoutePlanner _planner;
    private readonly ILeaseManager _leases;
    private readonly ITimelineEmitter _timeline;
    private readonly ILogger _log;
    private readonly Dictionary<WorkItemState, WorkerStateRunner> _runners;

    private readonly PriorityWaiterQueue<SchedulerRequest> _admission = new(AdmissionCapacity);
    private readonly MailboxExecutor _admissionExecutor = new();
    private readonly ConcurrentDictionary<string, SchedulerRequest> _streaming = new();

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
        _log = log ?? Serilog.Log.ForContext("component", "coordinator-v2");

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
        var type = _classifier.Classify(chat, _cfg, hasWarmSession: _ledger.Lookup(sessionId) is { SlotFreed: false });
        var req = new SchedulerRequest(chat, type, _classifier.ComputePriority(type));

        if (type == RequestType.Solo)
            req.Plan = _planner.Plan(chat, type, _cfg.Workers, _tracker, _health, _ledger);

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
                req.Plan = _planner.Plan(req.Chat, req.Type, _cfg.Workers, _tracker, _health, _ledger);
                if (!req.Plan.HasCapacity)
                    break; // no viable worker — wait for a slot release / health change
            }

            if (!_leases.HasCapacity(req.Plan, req.SessionId))
                break;

            _admission.TryDequeue(out _);
            _ = RunPipelineAsync(req, ct); // slot-bounded inside; signals on release
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
        var warmLease = req.Type == RequestType.Solo ? _leases.TakeWarm(req.SessionId) : null;
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
                // (which releases it — no warm lease for streaming turns).
                _streaming[req.SessionId] = req;
            }
            else if (req.State == WorkItemState.Done && !req.IsStreaming)
            {
                StashWarm(req); // C2: hold the decode slot warm for the next turn
            }
            else
            {
                ReleaseLeases(req);
            }
        }
    }

    /// <summary>Transfer the request's held slot lease into the session's warm stash.</summary>
    private void StashWarm(SchedulerRequest req)
    {
        var warmLease = req.DecodeLease ?? req.PrefillLease;
        if (warmLease is not null)
            _leases.Stash(req.SessionId, warmLease);
        req.PrefillLease = null;
        req.DecodeLease = null;
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
            req.Error = ex;
            _log.Warning(ex, "v2_pipeline_error Sid={Sid}", req.SessionId);
            await machine.FireAsync(SchedulerEvent.Failed, req, CancellationToken.None);
            req.State = machine.State;
        }
        finally
        {
            if (!suspended)
                await FinalizeAsync(req, req.State);
        }
        return suspended;
    }

    private async Task FinalizeAsync(SchedulerRequest req, WorkItemState terminal)
    {
        _timeline.Emit(req, terminal);
        switch (terminal)
        {
            case WorkItemState.Done:
                req.Completion.TrySetResult(new FinalCompletionResult(req.Response ?? new { status = "done" }));
                break;
            case WorkItemState.Failed:
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
            .On(SchedulerEvent.SoloRouted, WorkItemState.Decode) // warm/decode-only: KV resident
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.Prefill)
            .On(SchedulerEvent.PrefillSucceeded, WorkItemState.SaveKv)
            .On(SchedulerEvent.Retry, ctx => ((SchedulerRequest)ctx.Payload!).RetryCount < SchedulerRequest.MaxRetries, WorkItemState.RouteDecision)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.SaveKv)
            .On(SchedulerEvent.SaveKvSucceeded, WorkItemState.PickDecode)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        // Two-phase handoff: pick the decode worker + swap slots here.
        m.Configure(WorkItemState.PickDecode)
            .On(SchedulerEvent.DecodePicked, WorkItemState.RestoreKv)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.RestoreKv)
            .On(SchedulerEvent.RestoreSucceeded, WorkItemState.Decode)
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
        return AwaitShutdown(ct);
    }

    private async Task AwaitShutdown(CancellationToken ct)
    {
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            _health.HealthyChanged -= SignalEvaluator;
            await _admissionExecutor.StopAsync();
        }
    }

    /// <summary>Resumes a suspended streaming request and completes it: the stream
    /// already delivered its chunks, so skip decode and run background-save to Done.</summary>
    public async Task NotifyStreamComplete(string sessionId)
    {
        if (!_streaming.TryRemove(sessionId, out var req))
            return;

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
            req.Error = ex;
        }
        finally
        {
            ReleaseLeases(req);
            await FinalizeAsync(req, req.State);
        }
    }

    /// <summary>Evict a session's warm lease (release the slot) + mark the ledger
    /// entry evicted. (Save-before-erase ordering lands with C3's StateGet.)</summary>
    public Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct)
    {
        _leases.EvictWarm(sessionId);
        _ledger.MarkEvicted(sessionId);
        return Task.CompletedTask;
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
