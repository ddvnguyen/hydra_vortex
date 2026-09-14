namespace Hydra.Core.Scheduling;

/// <summary>
/// A coarse, thread-safe timing wheel. Timers scheduled with
/// <see cref="Schedule"/> fire their callback on the tick boundary nearest after
/// their delay; scheduling is coarse (callbacks are accurate to within one tick
/// interval) which is exactly what a scheduler needs — not a precise timer.
///
/// <para>Time is measured in ticks of a monotonically advancing counter, not the
/// wall clock: in auto-start mode a background <see cref="System.Threading.Timer"/>
/// advances one tick per <see cref="TickInterval"/>; with <c>autoStart: false</c>
/// the owner (e.g. a test) drives the wheel deterministically via
/// <see cref="AdvanceOneTick"/>. Delays longer than one full revolution are
/// supported via per-entry round counting.</para>
///
/// <para>Callback exceptions are captured — reported through the optional
/// <c>onCallbackError</c> handler and counted in <see cref="CallbackErrorCount"/>
/// — and never stop the wheel. <see cref="Dispose"/> abandons pending timers
/// (their callbacks are not invoked).</para>
/// </summary>
public sealed class TimerWheel : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _tickInterval;
    private readonly Queue<WheelEntry>[] _buckets;
    private readonly Dictionary<long, WheelEntry> _entries = new();
    private readonly Action<Exception>? _onCallbackError;
    private readonly Timer? _timer; // null when autoStart is false
    private long _currentTick;
    private long _nextId;
    private int _callbackErrors;
    private int _disposed;

    /// <summary>
    /// Creates a wheel with the given <paramref name="tickInterval"/> granularity
    /// and <paramref name="wheelSize"/> buckets.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tickInterval"/>
    /// is not positive, or <paramref name="wheelSize"/> is not positive.</exception>
    public TimerWheel(TimeSpan tickInterval, int wheelSize = 128, bool autoStart = true, Action<Exception>? onCallbackError = null)
    {
        if (tickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval), "Tick interval must be positive.");
        if (wheelSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(wheelSize), "Wheel size must be positive.");

        _tickInterval = tickInterval;
        _buckets = new Queue<WheelEntry>[wheelSize];
        for (var i = 0; i < wheelSize; i++)
            _buckets[i] = new Queue<WheelEntry>();

        _onCallbackError = onCallbackError;
        if (autoStart)
        {
            _timer = new Timer(
                _ =>
                {
                    try
                    {
                        AdvanceOneTick();
                    }
                    catch (Exception ex)
                    {
                        ReportError(ex);
                    }
                },
                state: null,
                dueTime: tickInterval,
                period: tickInterval);
        }
    }

    /// <summary>The granularity of one tick.</summary>
    public TimeSpan TickInterval => _tickInterval;

    /// <summary>The number of buckets (one full revolution in ticks).</summary>
    public int WheelSize => _buckets.Length;

    /// <summary>The current tick counter (advances only via <see cref="AdvanceOneTick"/>).</summary>
    public long CurrentTick
    {
        get
        {
            lock (_gate)
            {
                return _currentTick;
            }
        }
    }

    /// <summary>Number of live (scheduled, not cancelled, not yet fired) timers.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Number of callback exceptions captured since construction.</summary>
    public int CallbackErrorCount => Volatile.Read(ref _callbackErrors);

    /// <summary>
    /// Schedule <paramref name="callback"/> to fire after approximately
    /// <paramref name="delay"/>. Returns a timer id for <see cref="Cancel"/>.
    /// A non-positive delay fires on the very next tick.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The wheel is disposed.</exception>
    public long Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var ticks = delay <= TimeSpan.Zero
            ? 1
            : (long)Math.Ceiling(delay.TotalMilliseconds / _tickInterval.TotalMilliseconds);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var id = ++_nextId;
            var dueTick = _currentTick + ticks;
            var entry = new WheelEntry(id, dueTick, (int)(ticks / _buckets.Length), callback);
            _entries[id] = entry;
            _buckets[dueTick % _buckets.Length].Enqueue(entry);
            return id;
        }
    }

    /// <summary>
    /// Cancel a pending timer. Returns true if it was pending and is now removed;
    /// false if it already fired, was already cancelled, or the id is unknown.
    /// </summary>
    public bool Cancel(long timerId)
    {
        lock (_gate)
        {
            // The entry remains in its bucket as a tombstone; AdvanceOneTick skips
            // entries that are no longer in _entries.
            return _entries.Remove(timerId);
        }
    }

    /// <summary>
    /// Advance the wheel by one tick and fire any timers that came due. In
    /// auto-start mode the background timer calls this; with <c>autoStart: false</c>
    /// the caller drives the wheel (fully deterministic, no wall clock involved).
    /// Callbacks run outside the lock so they may safely schedule or cancel.
    /// </summary>
    public void AdvanceOneTick()
    {
        List<WheelEntry> due;
        lock (_gate)
        {
            if (_disposed != 0)
                return;

            _currentTick++;
            var bucket = _buckets[_currentTick % _buckets.Length];
            var count = bucket.Count;
            due = new List<WheelEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = bucket.Dequeue();
                if (!_entries.ContainsKey(entry.Id))
                    continue; // cancelled tombstone

                if (entry.Rounds > 0)
                {
                    entry.Rounds--;
                    bucket.Enqueue(entry);
                    continue;
                }

                _entries.Remove(entry.Id);
                due.Add(entry);
            }
        }

        foreach (var entry in due)
        {
            try
            {
                entry.Callback();
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }
    }

    private void ReportError(Exception ex)
    {
        Interlocked.Increment(ref _callbackErrors);
        _onCallbackError?.Invoke(ex);
    }

    /// <summary>Stop the tick timer (if any) and abandon pending timers.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer?.Dispose();
        lock (_gate)
        {
            _entries.Clear();
            foreach (var bucket in _buckets)
                bucket.Clear();
        }
    }

    private sealed class WheelEntry
    {
        public WheelEntry(long id, long dueTick, int rounds, Action callback)
        {
            Id = id;
            DueTick = dueTick;
            Rounds = rounds;
            Callback = callback;
        }

        public long Id { get; }
        public long DueTick { get; }
        public int Rounds { get; set; } // full revolutions remaining before firing
        public Action Callback { get; }
    }
}
