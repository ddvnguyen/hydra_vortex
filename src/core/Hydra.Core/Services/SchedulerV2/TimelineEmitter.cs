using Hydra.Core.Models;
using Serilog;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Observability for the v2 scheduler: structured transition + terminal telemetry.
/// Single responsibility: turn machine transitions / terminal states into logs and
/// metrics. Wired as <c>BeforeAny</c>/<c>AfterAny</c> hooks so every transition is
/// traced without polluting the phase handlers.
/// </summary>
public interface ITimelineEmitter
{
    void OnTransitionStart(Hydra.StateMachine.Transition<WorkItemState, SchedulerEvent> transition);
    void OnTransitionEnd(Hydra.StateMachine.Transition<WorkItemState, SchedulerEvent> transition);
    void Emit(SchedulerRequest req, WorkItemState terminal);
}

public sealed class TimelineEmitter : ITimelineEmitter
{
    private readonly ILogger _log;

    public TimelineEmitter(ILogger? log = null) => _log = log ?? Serilog.Log.ForContext("component", "coordinator-v2");

    public void OnTransitionStart(Hydra.StateMachine.Transition<WorkItemState, SchedulerEvent> transition)
        => _log.Debug("v2_transition_start From={From} Event={Evt} To={To}",
            transition.Source, transition.Event, transition.Destination);

    public void OnTransitionEnd(Hydra.StateMachine.Transition<WorkItemState, SchedulerEvent> transition)
    {
        if (transition.Destination is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
            _log.Information("v2_terminal State={State} Sid={Sid}",
                transition.Destination, (transition.Payload as SchedulerRequest)?.SessionId);
    }

    public void Emit(SchedulerRequest req, WorkItemState terminal)
        => _log.Information("v2_finalize Sid={Sid} Terminal={Terminal} Phases={Phases}",
            req.SessionId, terminal, string.Join(",", req.Phases.Keys));
}
