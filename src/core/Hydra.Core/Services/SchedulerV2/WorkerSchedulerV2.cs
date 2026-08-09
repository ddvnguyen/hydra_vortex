using System.Collections.Concurrent;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Scheduling;
using Hydra.StateMachine;
using Serilog;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// v2 scheduler — the SOLID successor to <c>WorkerSchedulerService</c>. Implements
/// the same <see cref="IWorkerScheduler"/> contract (so legacy and v2 are A/B
/// swappable behind one DI toggle) but shares ZERO implementation with the legacy
/// god class.
///
/// <para>Design (epic #591):</para>
/// <list type="bullet">
/// <item><b>DIP</b> — everything is constructor-injected (classifier, planner,
/// lease manager, gateways, phase handlers, timeline). No static singletons.</item>
/// <item><b>SRP</b> — one collaborator per concern; this type is the thin
/// orchestrator (submission + queue + evaluator).</item>
/// <item><b>OCP</b> — the pipeline is a data-driven
/// <see cref="Hydra.StateMachine.StateMachine{TState,TEvent}"/>: adding a phase =
/// a new <see cref="IPhaseHandler"/> + one <c>Configure</c> edge. No switch edits.</item>
/// <item><b>Concurrency</b> — no global semaphore. Requests wait in a priority
/// queue; a request runs only while it owns a slot lease. Invariant:
/// <i>#live ≤ Σ occupied slots</i>, enforced by <see cref="ILeaseManager"/>.</item>
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
    private readonly Dictionary<WorkItemState, IPhaseHandler> _phaseHandlers;

    private readonly PriorityWaiterQueue<WorkRequest> _admission = new(AdmissionCapacity);
    private readonly MailboxExecutor _admissionExecutor = new();
    private readonly ConcurrentDictionary<string, WorkRequest> _streaming = new();

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
        IEnumerable<IPhaseHandler> phaseHandlers,
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
        _phaseHandlers = phaseHandlers.ToDictionary(h => h.State);
    }

    // ── Submission ──

    public async Task<object> SubmitAsync(
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
        var item = new WorkItem(request, messages, sessionId, chat.TraceId, prefixHash, estimatedTokens, maxTokens)
        {
            SystemPromptTokens = systemPromptTokens,
        };

        var hasWarmSession = _ledger.Lookup(sessionId) is { SlotFreed: false };
        var type = _classifier.Classify(chat, _cfg, hasWarmSession);
        var wr = new WorkRequest(chat, item, type, _classifier.ComputePriority(type));

        if (type == RequestType.Solo)
            wr.Plan = _planner.Plan(chat, type, _cfg.Workers, _tracker, _health, _ledger);

        if (!_admission.TryEnqueue(wr, wr.Priority))
        {
            item.Completion.TrySetException(new InvalidOperationException("scheduler admission queue full"));
            return await item.Completion.Task;
        }

        SignalEvaluator();

        // Streaming: return the chunk stream once decode starts (controller SSE).
        if (chat.Stream)
        {
            var stream = await item.StreamCompletion.Task.WaitAsync(TimeSpan.FromSeconds(_cfg.LlamaRequestTimeoutS), ct);
            return stream;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_cfg.LlamaRequestTimeoutS));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return (await item.Completion.Task.WaitAsync(linked.Token))!;
    }

    // ── Evaluator (serialized via the admission mailbox) ──

    private void SignalEvaluator()
        => _ = _admissionExecutor.PostAsync(() => EvaluateAsync(CancellationToken.None));

    private async Task EvaluateAsync(CancellationToken ct)
    {
        while (_admission.TryPeek(out var wr, out _))
        {
            if (ct.IsCancellationRequested) break;

            if (wr.Item.IsCancelled)
            {
                _admission.TryDequeue(out _);
                await FinalizeAsync(wr, WorkItemState.Cancelled);
                continue;
            }

            // Plan lazily (once) so warm-affinity/cold decisions reflect live capacity.
            if (!wr.Plan.HasCapacity)
            {
                wr.Plan = _planner.Plan(wr.Chat, wr.Type, _cfg.Workers, _tracker, _health, _ledger);
                if (!wr.Plan.HasCapacity)
                    break; // no viable worker — wait for a slot release / health change
            }

            if (!_leases.HasCapacity(wr.Plan))
                break;

            _admission.TryDequeue(out _);
            _ = RunPipelineAsync(wr, ct); // slot-bounded inside; signals on release
        }
    }

    private async Task RunPipelineAsync(WorkRequest wr, CancellationToken ct)
    {
        var item = wr.Item;
        var lease = _leases.TryAcquire(wr.Plan.PrefillWorker, item.SessionId);
        if (lease is null)
        {
            // Capacity lost between planning and acquisition — requeue.
            _admission.Enqueue(wr, wr.Priority);
            SignalEvaluator();
            return;
        }

        item.PrefillLease = lease;
        item.State = WorkItemState.RouteDecision; // sync item with the machine's initial state
        var suspended = false;
        try
        {
            var machine = NewMachine(WorkItemState.RouteDecision);
            suspended = await StepAsync(wr, wr.Plan.PrefillWorker, machine, ct);
        }
        finally
        {
            if (suspended)
            {
                // Streaming: the slot stays held until NotifyStreamComplete.
                _streaming[item.SessionId] = wr;
            }
            else
            {
                ReleaseLeases(item);
            }
        }
    }

    /// <summary>Stepping driver: run the current phase handler, fire its event,
    /// sync <c>WorkItem.State</c>, repeat until terminal / suspended.</summary>
    private async Task<bool> StepAsync(
        WorkRequest wr,
        string worker,
        StateMachine<WorkItemState, SchedulerEvent> machine,
        CancellationToken ct)
    {
        var item = wr.Item;
        var suspended = false;
        try
        {
            while (!item.IsCancelled && !IsTerminal(item.State))
            {
                ct.ThrowIfCancellationRequested();

                if (!_phaseHandlers.TryGetValue(item.State, out var phase))
                {
                    item.Error = new InvalidOperationException($"no phase handler for state {item.State}");
                    await machine.FireAsync(SchedulerEvent.Failed, item, CancellationToken.None);
                    item.State = machine.State;
                    break;
                }

                var result = await phase.RunAsync(new PhaseContext(wr, item, worker), ct);
                switch (result.Outcome)
                {
                    case PhaseOutcome.Fire:
                        await machine.FireAsync(result.Event, item, ct);
                        item.State = machine.State;
                        if (item.State == WorkItemState.Decode)
                            UpdateLastDispatched(item.DecodeWorker);
                        break;
                    case PhaseOutcome.Wait:
                        suspended = true;
                        return suspended;
                    default:
                        return suspended;
                }
            }
        }
        catch (OperationCanceledException) when (item.IsCancelled || ct.IsCancellationRequested)
        {
            await machine.FireAsync(SchedulerEvent.Cancelled, item, CancellationToken.None);
            item.State = machine.State;
        }
        catch (Exception ex)
        {
            item.Error = ex;
            _log.Warning(ex, "v2_pipeline_error Sid={Sid}", item.SessionId);
            await machine.FireAsync(SchedulerEvent.Failed, item, CancellationToken.None);
            item.State = machine.State;
        }
        finally
        {
            if (!suspended)
                await FinalizeAsync(wr, item.State);
        }
        return suspended;
    }

    private async Task FinalizeAsync(WorkRequest wr, WorkItemState terminal)
    {
        var item = wr.Item;
        _timeline.Emit(item, terminal);
        switch (terminal)
        {
            case WorkItemState.Done:
                item.Completion.TrySetResult(item.Response ?? new { status = "done" });
                break;
            case WorkItemState.Failed:
                item.Completion.TrySetException(item.Error ?? new InvalidOperationException($"request failed in state {terminal}"));
                break;
            case WorkItemState.Cancelled:
                item.Completion.TrySetCanceled();
                break;
            default:
                item.Completion.TrySetResult(item.Response ?? new { status = terminal.ToString() });
                break;
        }
        SignalEvaluator();
    }

    private void ReleaseLeases(WorkItem item)
    {
        _leases.Release(item.PrefillLease);
        item.PrefillLease = null;
        _leases.Release(item.DecodeLease);
        item.DecodeLease = null;
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
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.Prefill)
            .On(SchedulerEvent.PrefillSucceeded, WorkItemState.SaveKv)
            .On(SchedulerEvent.Retry, ctx => ((WorkItem)ctx.Payload!).RetryCount < WorkItem.MaxRetries, WorkItemState.RouteDecision)
            .On(SchedulerEvent.Failed, WorkItemState.Failed)
            .On(SchedulerEvent.Cancelled, WorkItemState.Cancelled);

        m.Configure(WorkItemState.SaveKv)
            .On(SchedulerEvent.SaveKvSucceeded, WorkItemState.RestoreKv)
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

    private static bool IsTerminal(WorkItemState s)
        => s is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled;

    // ── Remaining IWorkerScheduler members ──

    public int WarmLeaseCount
    {
        get
        {
            var count = 0;
            foreach (var entry in _ledger.AllSessions().Values)
                if (entry is SessionEntry { SlotFreed: false })
                    count++;
            return count;
        }
    }

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

    /// <summary>Resumes a suspended streaming request and completes it: the
    /// stream already delivered its chunks, so skip decode and run the
    /// background-save teardown to <see cref="WorkItemState.Done"/>.</summary>
    public async Task NotifyStreamComplete(string sessionId)
    {
        if (!_streaming.TryRemove(sessionId, out var wr))
            return;

        var item = wr.Item;
        try
        {
            var machine = NewMachine(WorkItemState.Decode);
            await machine.FireAsync(SchedulerEvent.DecodeSucceeded, item, CancellationToken.None);
            item.State = machine.State;

            while (!item.IsCancelled && !IsTerminal(item.State))
            {
                if (!_phaseHandlers.TryGetValue(item.State, out var phase))
                    break;
                var result = await phase.RunAsync(new PhaseContext(wr, item, wr.Plan.PrefillWorker), CancellationToken.None);
                if (result.Outcome != PhaseOutcome.Fire)
                    break;
                await machine.FireAsync(result.Event, item, CancellationToken.None);
                item.State = machine.State;
            }
        }
        catch (Exception ex)
        {
            item.Error = ex;
        }
        finally
        {
            ReleaseLeases(item);
            await FinalizeAsync(wr, item.State);
        }
    }

    /// <summary>v1: evict the session's warm entry (KV erase over RPC is WP3 scope).</summary>
    public Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct)
    {
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
