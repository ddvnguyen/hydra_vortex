namespace Tests.StateMachine;

internal enum S { Idle, Working, Done, Failed }
internal enum E { Start, Finish, Fail, Tick }

/// <summary>
/// The DSL framework is the foundation of the WorkerSchedulerService rewrite
/// (epic #591). These tests pin the framework's contract so Hydra.Core can
/// consume it confidently:
///  - guard resolution semantics (declaration order, fallback, refusal)
///  - hook ordering and cross-cutting BeforeAny/AfterAny
///  - reentrant transitions, payload flow, cancellation
///  - introspection (CanFire/PermittedTriggers/GetStates/DOT export)
/// </summary>
public sealed class StateMachineTests
{
    private static StateMachine<S, E> MakeSimple()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, S.Working);
        m.Configure(S.Working)
            .On(E.Finish, S.Done)
            .On(E.Fail, S.Failed);
        return m;
    }

    // ── Basics ──

    [Fact]
    public async Task Fire_Transitions_To_Destination()
    {
        var m = MakeSimple();
        Assert.Equal(S.Idle, m.State);
        await m.FireAsync(E.Start);
        Assert.Equal(S.Working, m.State);
        await m.FireAsync(E.Finish);
        Assert.Equal(S.Done, m.State);
    }

    [Fact]
    public async Task Fire_Unknown_Event_Throws_And_Leaves_State_Unchanged()
    {
        var m = new StateMachine<S, E>(S.Idle); // no edges configured at all
        var ex = await Assert.ThrowsAsync<StateMachineException>(() => m.FireAsync(E.Start));
        Assert.Contains("not permitted", ex.Message);
        Assert.Equal(S.Idle, m.State);
    }

    [Fact]
    public async Task Fire_Event_Not_Declared_From_Current_State_Throws()
    {
        var m = MakeSimple();
        await m.FireAsync(E.Start);
        // E.Start is only declared from Idle
        await Assert.ThrowsAsync<StateMachineException>(() => m.FireAsync(E.Start));
        Assert.Equal(S.Working, m.State);
    }

    // ── Guards ──

    [Fact]
    public async Task Guarded_Edges_Resolve_In_Declaration_Order_First_Match_Wins()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is string s && s == "fast", S.Done)
            .On(E.Start, ctx => ctx.Payload is string s && s == "slow", S.Failed);

        await m.FireAsync(E.Start, "fast");
        Assert.Equal(S.Done, m.State);
    }

    [Fact]
    public async Task Guarded_Edges_Second_Match_Fires_When_First_Guard_Fails()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is string s && s == "fast", S.Done)
            .On(E.Start, ctx => ctx.Payload is string s && s == "slow", S.Failed);

        await m.FireAsync(E.Start, "slow");
        Assert.Equal(S.Failed, m.State);
    }

    [Fact]
    public async Task Unguarded_Edge_Is_Fallback_Only_When_No_Guard_Matches()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is "fast", S.Done) // guarded declared first
            .On(E.Start, S.Failed);                              // unguarded fallback

        await m.FireAsync(E.Start, "fast");
        Assert.Equal(S.Done, m.State);

        // second machine: guard fails → unguarded fallback fires
        var m2 = new StateMachine<S, E>(S.Idle);
        m2.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is "fast", S.Done)
            .On(E.Start, S.Failed);
        await m2.FireAsync(E.Start, "nope");
        Assert.Equal(S.Failed, m2.State);
    }

    [Fact]
    public async Task No_Guard_Matches_And_No_Fallback_Throws()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is "fast", S.Done); // no fallback

        await Assert.ThrowsAsync<StateMachineException>(() => m.FireAsync(E.Start, "nope"));
        Assert.Equal(S.Idle, m.State);
    }

    [Fact]
    public async Task Entry_Guard_Denies_Transition_And_Leaves_State_Unchanged()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle).On(E.Start, S.Working);
        m.Configure(S.Working).Guard(_ => false);

        var ex = await Assert.ThrowsAsync<StateMachineException>(() => m.FireAsync(E.Start));
        Assert.Contains("Entry guard denied", ex.Message);
        Assert.Equal(S.Idle, m.State);
    }

    [Fact]
    public void CanFire_Reflects_Edge_Guards()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is "yes", S.Working)
            .On(E.Tick, S.Working);

        Assert.False(m.CanFire(E.Start)); // guard requires payload "yes"
        Assert.True(m.CanFire(E.Tick));   // unguarded edge
    }

    [Fact]
    public void CanFire_Reflects_Entry_Guard()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle).On(E.Start, S.Working);
        m.Configure(S.Working).Guard(ctx => ctx.Payload is not null);

        // CanFire probes the entry guard with a payload-free context, so an
        // entry guard that requires a payload reports false.
        Assert.False(m.CanFire(E.Start));
    }

    [Fact]
    public void PermittedTriggers_Reports_Only_Fireable_Events()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, ctx => false, S.Working)
            .On(E.Tick, S.Working);
        m.Configure(S.Working).Guard(_ => true);

        var permitted = m.PermittedTriggers;
        Assert.Contains(E.Tick, permitted);
        Assert.DoesNotContain(E.Start, permitted); // guard never passes
    }

    // ── Hooks ──

    [Fact]
    public async Task Hooks_Run_In_Documented_Order()
    {
        var order = new List<string>();
        var m = new StateMachine<S, E>(S.Idle);

        m.BeforeAny(t => { order.Add($"beforeAny:{t.Destination}"); return Task.CompletedTask; });
        m.AfterAny(t => { order.Add($"afterAny:{t.Destination}"); return Task.CompletedTask; });

        m.Configure(S.Idle)
            .OnExitAsync(_ => { order.Add("idle:onExit"); return Task.CompletedTask; })
            .On(E.Start, S.Working);

        m.Configure(S.Working)
            .Before(_ => { order.Add("working:before"); return Task.CompletedTask; })
            .OnEntryAsync(_ => { order.Add("working:onEntry"); return Task.CompletedTask; })
            .After(_ => { order.Add("working:after"); return Task.CompletedTask; });

        await m.FireAsync(E.Start);

        Assert.Equal(
            new[]
            {
                "beforeAny:Working",
                "idle:onExit",
                "working:before",
                "working:onEntry",
                "working:after",
                "afterAny:Working",
            },
            order);
    }

    [Fact]
    public async Task Reentrant_Transition_Skips_OnEntry_OnExit_But_Runs_Interceptors()
    {
        var order = new List<string>();
        var m = new StateMachine<S, E>(S.Working);

        m.BeforeAny(_ => { order.Add("beforeAny"); return Task.CompletedTask; });
        m.AfterAny(_ => { order.Add("afterAny"); return Task.CompletedTask; });

        m.Configure(S.Working)
            .OnEntryAsync(_ => { order.Add("onEntry"); return Task.CompletedTask; })
            .OnExitAsync(_ => { order.Add("onExit"); return Task.CompletedTask; })
            .Before(_ => { order.Add("before"); return Task.CompletedTask; })
            .After(_ => { order.Add("after"); return Task.CompletedTask; })
            .On(E.Tick, S.Working); // reentrant edge

        await m.FireAsync(E.Tick);

        Assert.Equal(S.Working, m.State);
        Assert.Equal(new[] { "beforeAny", "before", "after", "afterAny" }, order);
    }

    [Fact]
    public async Task Payload_Flows_To_Guards_And_Hooks()
    {
        object? seenByGuard = null;
        object? seenByEntry = null;
        object? seenByAny = null;

        var m = new StateMachine<S, E>(S.Idle);
        m.BeforeAny(t => { seenByAny = t.Payload; return Task.CompletedTask; });
        m.Configure(S.Idle)
            .On(E.Start, ctx => { seenByGuard = ctx.Payload; return true; }, S.Working);
        m.Configure(S.Working)
            .OnEntryAsync(t => { seenByEntry = t.Payload; return Task.CompletedTask; });

        var payload = new { id = 42 };
        await m.FireAsync(E.Start, payload);

        Assert.Same(payload, seenByGuard);
        Assert.Same(payload, seenByEntry);
        Assert.Same(payload, seenByAny);
    }

    [Fact]
    public async Task Cross_Cutting_Hooks_Run_On_Every_Transition()
    {
        var count = 0;
        var m = new StateMachine<S, E>(S.Idle);
        m.AfterAny(_ => { count++; return Task.CompletedTask; });
        m.Configure(S.Idle).On(E.Start, S.Working);
        m.Configure(S.Working).On(E.Finish, S.Done);

        await m.FireAsync(E.Start);
        await m.FireAsync(E.Finish);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Hook_Exception_Propagates_And_State_Is_Committed()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle).On(E.Start, S.Working);
        m.Configure(S.Working).OnEntryAsync(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.FireAsync(E.Start));
        Assert.Equal(S.Working, m.State); // documented: commit-before-hooks
    }

    // ── Cancellation ──

    [Fact]
    public async Task Cancelled_Token_Refuses_Transition_Before_Any_Mutation()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle).On(E.Start, S.Working);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.FireAsync(E.Start, null, cts.Token));
        Assert.Equal(S.Idle, m.State);
    }

    // ── Introspection / harness surface ──

    [Fact]
    public void GetStates_Reports_Configured_States_And_Edges()
    {
        var m = MakeSimple();
        var states = m.GetStates();

        var working = states.Single(s => s.State == S.Working);
        Assert.Equal(2, working.Edges.Count);
        Assert.Contains(working.Edges, e => e.Event == E.Finish && !e.HasGuard && e.Destination == S.Done);
        Assert.Contains(working.Edges, e => e.Event == E.Fail && e.Destination == S.Failed);
    }

    [Fact]
    public void ExportToDotGraph_Contains_Nodes_And_Edges()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle).On(E.Start, ctx => true, S.Working);
        m.Configure(S.Working).On(E.Finish, S.Done);

        var dot = m.ExportToDotGraph();

        Assert.Contains("digraph StateMachine", dot);
        Assert.Contains("\"Idle\" -> \"Working\"", dot);
        Assert.Contains("label=\"Start (guard)\"", dot);
        Assert.Contains("\"Working\" -> \"Done\"", dot);
        Assert.Contains("label=\"Finish\"", dot);
    }

    [Fact]
    public void ExportToDotGraph_Is_Deterministic()
    {
        var m = MakeSimple();
        Assert.Equal(m.ExportToDotGraph(), m.ExportToDotGraph());
    }

    // ── DSL spellings ──

    [Fact]
    public async Task Flat_On_Overload_And_Fluent_Chain_Are_Equivalent()
    {
        var flat = new StateMachine<S, E>(S.Idle);
        flat.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is "a", S.Done)
            .On(E.Start, S.Failed);

        var fluent = new StateMachine<S, E>(S.Idle);
        fluent.Configure(S.Idle)
            .On(E.Start).When(ctx => ctx.Payload is "a").GoTo(S.Done)
            .On(E.Start).GoTo(S.Failed);

        await flat.FireAsync(E.Start, "b");
        await fluent.FireAsync(E.Start, "b");
        Assert.Equal(flat.State, fluent.State);
        Assert.Equal(S.Failed, flat.State);
    }

    [Fact]
    public async Task Default_Edge_Acts_As_Fallback()
    {
        var m = new StateMachine<S, E>(S.Idle);
        m.Configure(S.Idle)
            .On(E.Start, ctx => ctx.Payload is "fast", S.Done)
            .Default(E.Start).GoTo(S.Failed);

        await m.FireAsync(E.Start, "slow");
        Assert.Equal(S.Failed, m.State);
    }

    [Fact]
    public void InitialState_With_No_Configuration_Is_Usable()
    {
        var m = new StateMachine<S, E>(S.Idle);
        Assert.Equal(S.Idle, m.State);
        Assert.Empty(m.PermittedTriggers);
        Assert.False(m.CanFire(E.Start));
    }
}
