namespace Tests.Core.Scheduling;

/// <summary>
/// Pins the contract of the coarse timing wheel used by the worker scheduler
/// rewrite (epic #591): tick-granular scheduling, multi-revolution delays,
/// cancellation, and callback failure isolation. The wheel is driven manually
/// (<c>autoStart: false</c>) so every test is deterministic — no wall clock, no
/// sleeps.
/// </summary>
public sealed class TimerWheelTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);

    private static TimerWheel Make(int wheelSize = 128, Action<Exception>? onCallbackError = null) =>
        new(Tick, wheelSize, autoStart: false, onCallbackError);

    [Fact]
    public void Schedule_Zero_Delay_Fires_On_Next_Tick()
    {
        using var wheel = Make();
        var fired = 0;
        wheel.Schedule(TimeSpan.Zero, () => fired++);

        wheel.AdvanceOneTick();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Schedule_Fires_After_Exact_Delay_In_Ticks()
    {
        using var wheel = Make();
        var fired = 0;
        wheel.Schedule(TimeSpan.FromMilliseconds(30), () => fired++); // 3 ticks

        wheel.AdvanceOneTick();
        wheel.AdvanceOneTick();
        Assert.Equal(0, fired); // not yet due

        wheel.AdvanceOneTick();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Delay_Spans_Multiple_Revolutions()
    {
        using var wheel = Make(wheelSize: 4);
        var fired = 0;
        wheel.Schedule(TimeSpan.FromMilliseconds(45), () => fired++); // 5 ticks > one revolution

        for (var i = 0; i < 4; i++)
            wheel.AdvanceOneTick();
        Assert.Equal(0, fired); // a full revolution passed without firing

        wheel.AdvanceOneTick();
        Assert.Equal(1, fired); // fires on the 5th tick
    }

    [Fact]
    public void Multiple_Timers_Fire_In_Tick_Order()
    {
        using var wheel = Make();
        var order = new List<int>();
        wheel.Schedule(TimeSpan.FromMilliseconds(20), () => order.Add(2));
        wheel.Schedule(TimeSpan.FromMilliseconds(10), () => order.Add(1));
        wheel.Schedule(TimeSpan.FromMilliseconds(30), () => order.Add(3));

        for (var i = 0; i < 3; i++)
            wheel.AdvanceOneTick();

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public void Cancel_Prevents_Fire_And_Returns_True_Once()
    {
        using var wheel = Make();
        var fired = 0;
        var id = wheel.Schedule(TimeSpan.FromMilliseconds(10), () => fired++);

        Assert.True(wheel.Cancel(id));
        Assert.False(wheel.Cancel(id)); // already cancelled

        wheel.AdvanceOneTick();
        wheel.AdvanceOneTick();
        Assert.Equal(0, fired);
        Assert.Equal(0, wheel.PendingCount);
    }

    [Fact]
    public void Cancel_Unknown_Id_Returns_False()
    {
        using var wheel = Make();
        Assert.False(wheel.Cancel(12345));
        Assert.False(wheel.Cancel(0));
    }

    [Fact]
    public void Cancel_After_Fire_Returns_False()
    {
        using var wheel = Make();
        var id = wheel.Schedule(TimeSpan.FromMilliseconds(10), () => { });
        wheel.AdvanceOneTick();

        Assert.False(wheel.Cancel(id)); // already fired
    }

    [Fact]
    public void Callback_Exception_Is_Reported_And_Wheel_Continues()
    {
        Exception? captured = null;
        using var wheel = Make(onCallbackError: ex => captured = ex);
        var secondFired = 0;

        wheel.Schedule(TimeSpan.FromMilliseconds(10), () => throw new InvalidOperationException("bad callback"));
        wheel.Schedule(TimeSpan.FromMilliseconds(20), () => secondFired++);

        wheel.AdvanceOneTick(); // first timer fires and throws
        wheel.AdvanceOneTick(); // second timer still fires — the wheel survived

        Assert.Equal(1, wheel.CallbackErrorCount);
        Assert.IsType<InvalidOperationException>(captured);
        Assert.Equal(1, secondFired);
    }

    [Fact]
    public void PendingCount_Tracks_Live_Timers()
    {
        using var wheel = Make();
        var id = wheel.Schedule(TimeSpan.FromMilliseconds(10), () => { });
        wheel.Schedule(TimeSpan.FromMilliseconds(30), () => { });
        Assert.Equal(2, wheel.PendingCount);

        wheel.Cancel(id);
        Assert.Equal(1, wheel.PendingCount);

        wheel.AdvanceOneTick(); // the surviving 30 ms timer needs 3 ticks — still pending
        Assert.Equal(1, wheel.PendingCount);

        wheel.AdvanceOneTick();
        wheel.AdvanceOneTick();
        Assert.Equal(0, wheel.PendingCount); // fired and removed
    }

    [Fact]
    public void Advance_With_No_Pending_Timers_Is_Safe()
    {
        using var wheel = Make();
        for (var i = 0; i < 300; i++)
            wheel.AdvanceOneTick();

        Assert.Equal(300, wheel.CurrentTick);
        Assert.Equal(0, wheel.PendingCount);
    }

    [Fact]
    public void Callbacks_May_Schedule_And_Cancel_Inside_Other_Callbacks()
    {
        using var wheel = Make();
        var outerFired = 0;
        var innerFired = 0;
        var doomed = wheel.Schedule(TimeSpan.FromMilliseconds(20), () => outerFired++);

        wheel.Schedule(TimeSpan.FromMilliseconds(10), () =>
        {
            wheel.Schedule(TimeSpan.FromMilliseconds(10), () => innerFired++); // schedule from a callback
            wheel.Cancel(doomed);                                             // cancel from a callback
        });

        wheel.AdvanceOneTick(); // callback schedules the inner timer and cancels the doomed one
        wheel.AdvanceOneTick(); // inner timer fires; doomed timer must not

        Assert.Equal(1, innerFired);
        Assert.Equal(0, outerFired);
    }

    [Fact]
    public void Schedule_After_Dispose_Throws()
    {
        var wheel = Make();
        wheel.Dispose();

        Assert.Throws<ObjectDisposedException>(() => wheel.Schedule(TimeSpan.FromMilliseconds(10), () => { }));
    }

    [Fact]
    public void Dispose_Abandons_Pending_Timers()
    {
        var wheel = Make();
        var fired = 0;
        wheel.Schedule(TimeSpan.FromMilliseconds(10), () => fired++);

        wheel.Dispose();
        wheel.AdvanceOneTick(); // no-op after dispose

        Assert.Equal(0, fired);
        Assert.Equal(0, wheel.PendingCount);
    }

    [Fact]
    public void Constructor_Rejects_Invalid_Arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimerWheel(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimerWheel(TimeSpan.FromMilliseconds(-5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimerWheel(TimeSpan.FromMilliseconds(10), wheelSize: 0));
    }
}
