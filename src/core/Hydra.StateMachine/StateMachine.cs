using System.Text;

namespace Hydra.StateMachine;

/// <summary>
/// Read-only surface of a state machine. Hydra.Core consumes this interface so
/// the concrete machine can be swapped (e.g. for a persisted implementation)
/// without touching callers.
/// </summary>
public interface IStateMachine<TState, TEvent>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    /// <summary>The current state.</summary>
    TState State { get; }

    /// <summary>True when <paramref name="evt"/> currently has a fireable transition
    /// (a matching guarded edge whose guard passes, an unguarded fallback edge,
    /// and the destination's entry guard all satisfied).</summary>
    bool CanFire(TEvent evt);

    /// <summary>All events that currently have a fireable transition, in declaration order.</summary>
    IReadOnlyCollection<TEvent> PermittedTriggers { get; }

    /// <summary>Snapshot of every configured state and its edges — the harness's
    /// structured transition table.</summary>
    IReadOnlyCollection<StateDescriptor<TState, TEvent>> GetStates();

    /// <summary>
    /// Fire <paramref name="evt"/>. Resolves the transition, commits the state,
    /// then runs hooks in order: <c>BeforeAny</c> → source <c>OnExit</c> →
    /// destination <c>Before</c> → destination <c>OnEntry</c> → destination
    /// <c>After</c> → <c>AfterAny</c>. Reentrant transitions skip <c>OnExit</c>
    /// and <c>OnEntry</c>. Throws <see cref="StateMachineException"/> when no
    /// transition is fireable; the state is unchanged in that case.
    /// </summary>
    Task FireAsync(TEvent evt, object? payload = null, CancellationToken ct = default);
}

/// <summary>
/// A fluent-DSL, event-driven state machine. Nodes are values of
/// <typeparamref name="TState"/>; edges are keyed by <typeparamref name="TEvent"/>.
///
/// <para><b>Guard resolution (declaration order):</b> guarded edges for an event
/// are evaluated in the order they were declared; the first whose guard passes
/// wins. If none passes, the first unguarded edge for that event (in declaration
/// order) is the fallback. If neither exists, the transition is refused.</para>
///
/// <para><b>Hooks:</b> per-state <c>OnEntryAsync</c>/<c>OnExitAsync</c>, per-state
/// interceptor <c>Before</c>/<c>After</c> (wrapped around the destination's
/// entry), and cross-cutting <c>BeforeAny</c>/<c>AfterAny</c> that run for every
/// transition (e.g. lease invariants and trace recording).</para>
///
/// <para><b>Thread-safety:</b> NOT thread-safe by design. The owner is expected
/// to drive it from a single writer (e.g. a per-resource mailbox executor).</para>
///
/// <para>State is committed BEFORE hooks run. If a hook throws, the exception
/// propagates and the machine is left in the destination state (transition is
/// not rolled back). Entry guards and edge resolution, by contrast, run before
/// any mutation, so a refused transition leaves the machine unchanged.</para>
/// </summary>
public sealed class StateMachine<TState, TEvent> : IStateMachine<TState, TEvent>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    private readonly Dictionary<TState, StateData<TState, TEvent>> _states = new();
    private readonly List<Func<Transition<TState, TEvent>, Task>> _beforeAny = new();
    private readonly List<Func<Transition<TState, TEvent>, Task>> _afterAny = new();
    private TState _current;

    public StateMachine(TState initialState) => _current = initialState;

    public TState State => _current;

    // ── DSL surface ──

    /// <summary>Begin configuring <paramref name="state"/>. Re-calling for the
    /// same state mutates the same configuration (hooks/guard last-writer-wins;
    /// edges append).</summary>
    public StateConfiguration<TState, TEvent> Configure(TState state)
        => new(GetOrCreate(state));

    /// <summary>Register a global hook that runs before every transition's
    /// source-exit/destination-entry (the outermost "transition begins" step).</summary>
    public StateMachine<TState, TEvent> BeforeAny(Func<Transition<TState, TEvent>, Task> hook)
    {
        _beforeAny.Add(hook);
        return this;
    }

    /// <summary>Register a global hook that runs after every transition's
    /// destination-entry (the outermost "transition completed" step). Ideal for
    /// invariants such as lease-leak assertions.</summary>
    public StateMachine<TState, TEvent> AfterAny(Func<Transition<TState, TEvent>, Task> hook)
    {
        _afterAny.Add(hook);
        return this;
    }

    // ── Runtime ──

    public bool CanFire(TEvent evt)
    {
        var src = GetOrCreate(_current);
        var edge = ResolveEdge(src, evt, payload: null);
        if (edge is null) return false;
        var dest = GetOrCreate(edge.Destination);
        return dest.EntryGuard is null
            || dest.EntryGuard(new GuardContext<TState, TEvent>(_current, evt, null));
    }

    public IReadOnlyCollection<TEvent> PermittedTriggers
    {
        get
        {
            var src = GetOrCreate(_current);
            var seen = new HashSet<TEvent>();
            var result = new List<TEvent>();
            foreach (var edge in src.Edges)
            {
                if (seen.Add(edge.Event) && CanFire(edge.Event))
                    result.Add(edge.Event);
            }
            return result;
        }
    }

    public IReadOnlyCollection<StateDescriptor<TState, TEvent>> GetStates()
    {
        var list = new List<StateDescriptor<TState, TEvent>>();
        foreach (var data in _states.Values.OrderBy(d => d.State.ToString(), StringComparer.Ordinal))
        {
            var edges = data.Edges
                .Select(e => new EdgeDescriptor<TState, TEvent>(e.Event, e.Guard is not null, e.Destination))
                .ToList();
            list.Add(new StateDescriptor<TState, TEvent>(
                data.State,
                data.EntryGuard is not null,
                data.OnEntry is not null,
                data.OnExit is not null,
                edges));
        }
        return list;
    }

    public async Task FireAsync(TEvent evt, object? payload = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var src = GetOrCreate(_current);
        var edge = ResolveEdge(src, evt, payload)
            ?? throw new StateMachineException(
                $"Transition not permitted: no fireable transition from {_current} on {evt}.");

        var dest = GetOrCreate(edge.Destination);
        if (dest.EntryGuard is { } entryGuard
            && !entryGuard(new GuardContext<TState, TEvent>(_current, evt, payload)))
            throw new StateMachineException(
                $"Entry guard denied transition {_current} --{evt}--> {edge.Destination}.");

        var transition = new Transition<TState, TEvent>(_current, evt, edge.Destination, payload);

        // Commit BEFORE hooks (documented semantics): a hook failure leaves the
        // machine in the destination state with the exception propagated.
        _current = edge.Destination;

        foreach (var hook in _beforeAny)
            await hook(transition).ConfigureAwait(false);

        if (!transition.IsReentrant && src.OnExit is { } onExit)
            await onExit(transition).ConfigureAwait(false);

        if (dest.Before is { } before)
            await before(transition).ConfigureAwait(false);

        if (!transition.IsReentrant && dest.OnEntry is { } onEntry)
            await onEntry(transition).ConfigureAwait(false);

        if (dest.After is { } after)
            await after(transition).ConfigureAwait(false);

        foreach (var hook in _afterAny)
            await hook(transition).ConfigureAwait(false);
    }

    /// <summary>Deterministic DOT representation of the configured graph — used
    /// by the harness for transition-table snapshots (a state/edge change is a
    /// reviewed diff to the snapshot).</summary>
    public string ExportToDotGraph()
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph StateMachine {");
        foreach (var data in _states.Values.OrderBy(d => d.State.ToString(), StringComparer.Ordinal))
        {
            sb.AppendLine($"  \"{Escape(data.State.ToString())}\";");
            foreach (var edge in data.Edges)
            {
                var label = edge.Guard is not null ? $"{edge.Event} (guard)" : edge.Event.ToString();
                sb.AppendLine(
                    $"  \"{Escape(data.State.ToString())}\" -> \"{Escape(edge.Destination.ToString())}\" [label=\"{Escape(label)}\"];");
            }
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── Internals ──

    private StateData<TState, TEvent> GetOrCreate(TState state)
    {
        if (_states.TryGetValue(state, out var data)) return data;
        data = new StateData<TState, TEvent> { State = state };
        _states[state] = data;
        return data;
    }

    private TransitionEdge<TState, TEvent>? ResolveEdge(
        StateData<TState, TEvent> src, TEvent evt, object? payload)
    {
        // 1) Guarded edges in declaration order; first passing guard wins.
        foreach (var edge in src.Edges)
        {
            if (!EqualityComparer<TEvent>.Default.Equals(edge.Event, evt)) continue;
            if (edge.Guard is not null
                && edge.Guard(new GuardContext<TState, TEvent>(src.State, evt, payload)))
                return edge;
        }
        // 2) Unguarded fallback edge (declaration order).
        foreach (var edge in src.Edges)
        {
            if (!EqualityComparer<TEvent>.Default.Equals(edge.Event, evt)) continue;
            if (edge.Guard is null) return edge;
        }
        return null;
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}

/// <summary>Per-state DSL configuration. All methods mutate the underlying state
/// data and return <c>this</c> (or the next fluent step) so configurations chain.</summary>
public sealed class StateConfiguration<TState, TEvent>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    private readonly StateData<TState, TEvent> _data;

    internal StateConfiguration(StateData<TState, TEvent> data) => _data = data;

    /// <summary>Entry guard: any transition targeting this state is refused (and
    /// the machine left unchanged) while this predicate returns false.</summary>
    public StateConfiguration<TState, TEvent> Guard(Func<GuardContext<TState, TEvent>, bool> guard)
    {
        _data.EntryGuard = guard;
        return this;
    }

    /// <summary>Runs on entering this state (skipped for reentrant transitions).</summary>
    public StateConfiguration<TState, TEvent> OnEntryAsync(Func<Transition<TState, TEvent>, Task> action)
    {
        _data.OnEntry = action;
        return this;
    }

    /// <summary>Runs on exiting this state (skipped for reentrant transitions).</summary>
    public StateConfiguration<TState, TEvent> OnExitAsync(Func<Transition<TState, TEvent>, Task> action)
    {
        _data.OnExit = action;
        return this;
    }

    /// <summary>Interceptor hook that runs just before this state's <c>OnEntry</c>.</summary>
    public StateConfiguration<TState, TEvent> Before(Func<Transition<TState, TEvent>, Task> hook)
    {
        _data.Before = hook;
        return this;
    }

    /// <summary>Interceptor hook that runs just after this state's <c>OnEntry</c>.</summary>
    public StateConfiguration<TState, TEvent> After(Func<Transition<TState, TEvent>, Task> hook)
    {
        _data.After = hook;
        return this;
    }

    /// <summary>Begin declaring an edge for <paramref name="evt"/>. Chain
    /// <c>.When(guard).GoTo(dest)</c> for a guarded edge or <c>.GoTo(dest)</c>
    /// for the unguarded fallback.</summary>
    public EdgeSelector<TState, TEvent> On(TEvent evt) => new(_data, evt);

    /// <summary>Declare an unguarded (fallback) edge for <paramref name="evt"/>.</summary>
    public StateConfiguration<TState, TEvent> On(TEvent evt, TState destination)
        => On(evt).GoTo(destination);

    /// <summary>Declare a guarded edge for <paramref name="evt"/>. Multiple
    /// guarded edges for the same event are evaluated in declaration order.</summary>
    public StateConfiguration<TState, TEvent> On(
        TEvent evt, Func<GuardContext<TState, TEvent>, bool> guard, TState destination)
        => On(evt).When(guard).GoTo(destination);

    /// <summary>Declare the default (unguarded fallback) edge for
    /// <paramref name="evt"/> — used when no guarded edge matches. Semantically
    /// identical to <see cref="On(TEvent)"/> + <c>GoTo</c>; kept as a
    /// self-documenting happy-path spelling.</summary>
    public EdgeSelector<TState, TEvent> Default(TEvent evt) => new(_data, evt);
}

/// <summary>Fluent intermediate: the next step after <c>On(evt)</c>.</summary>
public sealed class EdgeSelector<TState, TEvent>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    private readonly StateData<TState, TEvent> _data;
    private readonly TEvent _event;

    internal EdgeSelector(StateData<TState, TEvent> data, TEvent evt)
    {
        _data = data;
        _event = evt;
    }

    /// <summary>Declare an unguarded (fallback) edge.</summary>
    public StateConfiguration<TState, TEvent> GoTo(TState destination)
    {
        _data.Edges.Add(new TransitionEdge<TState, TEvent> { Event = _event, Destination = destination });
        return new StateConfiguration<TState, TEvent>(_data);
    }

    /// <summary>Attach a guard to the edge being declared.</summary>
    public GuardedEdge<TState, TEvent> When(Func<GuardContext<TState, TEvent>, bool> guard)
        => new(_data, _event, guard);
}

/// <summary>Fluent intermediate: a guarded edge awaiting its destination.</summary>
public sealed class GuardedEdge<TState, TEvent>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    private readonly StateData<TState, TEvent> _data;
    private readonly TEvent _event;
    private readonly Func<GuardContext<TState, TEvent>, bool> _guard;

    internal GuardedEdge(
        StateData<TState, TEvent> data, TEvent evt, Func<GuardContext<TState, TEvent>, bool> guard)
    {
        _data = data;
        _event = evt;
        _guard = guard;
    }

    /// <summary>Commit the guarded edge to <paramref name="destination"/>.</summary>
    public StateConfiguration<TState, TEvent> GoTo(TState destination)
    {
        _data.Edges.Add(new TransitionEdge<TState, TEvent>
        {
            Event = _event,
            Guard = _guard,
            Destination = destination,
        });
        return new StateConfiguration<TState, TEvent>(_data);
    }
}

internal sealed class StateData<TState, TEvent>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    public required TState State { get; init; }
    public Func<GuardContext<TState, TEvent>, bool>? EntryGuard { get; set; }
    public Func<Transition<TState, TEvent>, Task>? OnExit { get; set; }
    public Func<Transition<TState, TEvent>, Task>? OnEntry { get; set; }
    public Func<Transition<TState, TEvent>, Task>? Before { get; set; }
    public Func<Transition<TState, TEvent>, Task>? After { get; set; }
    public List<TransitionEdge<TState, TEvent>> Edges { get; } = new();
}

internal sealed class TransitionEdge<TState, TEvent>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    public required TEvent Event { get; init; }
    public Func<GuardContext<TState, TEvent>, bool>? Guard { get; init; }
    public required TState Destination { get; init; }
}
