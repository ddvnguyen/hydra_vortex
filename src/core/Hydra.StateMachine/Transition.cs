namespace Hydra.StateMachine;

/// <summary>
/// Immutable context handed to edge guards and state entry guards. Carries the
/// firing source state, the triggering event and the optional user payload.
/// The destination is NOT known when an edge guard runs (it is deciding which
/// edge to take); it IS fixed by the time a state entry guard runs, but the
/// same context type is used for both for simplicity.
/// </summary>
public readonly record struct GuardContext<TState, TEvent>(
    TState Source,
    TEvent Event,
    object? Payload)
    where TState : struct, Enum
    where TEvent : struct, Enum;

/// <summary>
/// Immutable record of a committed (or in-flight) transition. Passed to all
/// lifecycle hooks: <c>OnExit</c> (source), <c>OnEntry</c>/<c>Before</c>/<c>After</c>
/// (destination) and the cross-cutting <c>BeforeAny</c>/<c>AfterAny</c> hooks.
/// </summary>
public sealed record Transition<TState, TEvent>(
    TState Source,
    TEvent Event,
    TState Destination,
    object? Payload)
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    /// <summary>True when the transition stays in the same state (internal transition).</summary>
    public bool IsReentrant => EqualityComparer<TState>.Default.Equals(Source, Destination);
}

/// <summary>One declared edge out of a state, used for introspection.</summary>
public sealed record EdgeDescriptor<TState, TEvent>(
    TEvent Event,
    bool HasGuard,
    TState Destination)
    where TState : struct, Enum
    where TEvent : struct, Enum;

/// <summary>Immutable snapshot of a configured state, used by the harness for
/// transition-table snapshots (the machine's <c>ExportToDotGraph</c> is the
/// human-readable form; this is the structured form).</summary>
public sealed record StateDescriptor<TState, TEvent>(
    TState State,
    bool HasEntryGuard,
    bool HasOnEntry,
    bool HasOnExit,
    IReadOnlyList<EdgeDescriptor<TState, TEvent>> Edges)
    where TState : struct, Enum
    where TEvent : struct, Enum;
