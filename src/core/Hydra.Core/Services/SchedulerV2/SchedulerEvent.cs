namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Events that advance the v2 request state machine (a
/// <see cref="Hydra.StateMachine.StateMachine{TState,TEvent}"/> whose nodes are
/// the existing <see cref="Models.WorkItemState"/> values). Phase handlers perform
/// the work and report the event to fire; the machine owns the transition table,
/// guards and hooks. Adding a new phase = a new handler + a <c>Configure</c> edge
/// (open/closed).
/// </summary>
public enum SchedulerEvent
{
    /// <summary>Route planning succeeded; move to the prefill phase.</summary>
    RouteSucceeded,
    /// <summary>Engine prefill succeeded (KV produced); move to save-KV.</summary>
    PrefillSucceeded,
    /// <summary>KV persisted to Store; move to restore on the decode worker.</summary>
    SaveKvSucceeded,
    /// <summary>KV restored onto the decode worker; move to decode.</summary>
    RestoreSucceeded,
    /// <summary>Decode completed; move to background save (stream teardown).</summary>
    DecodeSucceeded,
    /// <summary>Background save completed; move to Done.</summary>
    BgSaveSucceeded,
    /// <summary>Transient failure; re-route (bounded by <c>WorkItem.RetryCount</c>).</summary>
    Retry,
    /// <summary>Terminal failure.</summary>
    Failed,
    /// <summary>Terminal cancellation (client disconnected).</summary>
    Cancelled,
}
