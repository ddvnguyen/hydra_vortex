using Hydra.Core.Models;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>What a state runner wants the machine to do next.</summary>
public enum PhaseOutcome
{
    /// <summary>Fire the given event (the machine advances to the next state).</summary>
    Fire,
    /// <summary>Suspend until resumed externally (streaming → NotifyStreamComplete).</summary>
    Wait,
    /// <summary>Stop stepping (terminal state reached).</summary>
    Terminal,
}

public readonly record struct PhaseResult(PhaseOutcome Outcome, SchedulerEvent Event)
{
    public static PhaseResult Fire(SchedulerEvent evt) => new(PhaseOutcome.Fire, evt);
    public static PhaseResult Wait => new(PhaseOutcome.Wait, default);
    public static PhaseResult Terminal => new(PhaseOutcome.Terminal, default);
}

/// <summary>Per-request context handed to a state runner: the request being
/// advanced plus the worker the pipeline started on (the workers actually doing
/// the work are resolved from <see cref="SchedulerRequest.PrefillWorker"/> /
/// <see cref="SchedulerRequest.DecodeWorker"/>).</summary>
public sealed class RunnerContext
{
    public SchedulerRequest Request { get; }
    public string Worker { get; }
    public RunnerContext(SchedulerRequest request, string worker)
    {
        Request = request;
        Worker = worker;
    }
}

/// <summary>
/// Base class of every v2 state runner (epic #591): ONE class per
/// <see cref="WorkItemState"/>, all sharing this base. The base declares the
/// state a runner implements and the <see cref="RunAsync"/> contract; runners
/// receive exactly the services they need via constructor injection (DIP).
///
/// <para>A runner may implement more than one state via <see cref="Handles"/>
/// (the <c>PlanRunner</c> implements both <c>RouteDecision</c> and
/// <c>PickDecode</c> — both are "Plan" states, only the phase differs).</para>
/// </summary>
public abstract class WorkerStateRunner
{
    /// <summary>Primary state this runner implements (diagnostics/default).</summary>
    public abstract WorkItemState State { get; }

    /// <summary>True when this runner handles <paramref name="state"/>. Defaults
    /// to the primary state; multi-state runners override.</summary>
    public virtual bool Handles(WorkItemState state) => state == State;

    /// <summary>Execute this state's work and report the event to fire (or
    /// suspend / stop). Handlers must not block on capacity and must not mutate
    /// anything outside <see cref="SchedulerRequest"/>.</summary>
    public abstract Task<PhaseResult> RunAsync(RunnerContext ctx, CancellationToken ct);
}
