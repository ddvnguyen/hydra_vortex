namespace Hydra.Core.Scheduling;

/// <summary>
/// Thread-safe pool of per-worker compute slots (KV-cache slots in Hydra).
///
/// <para>Each worker owns a fixed set of <see cref="SlotsPerWorker"/> slots.
/// <see cref="AcquireAsync"/> takes a free slot immediately when one exists;
/// otherwise the caller is queued as a waiter and receives the next slot that
/// <see cref="Release"/> frees. Release always hands the slot to the
/// highest-priority live waiter (lower priority value = higher priority, FIFO
/// within a priority); when no waiter is queued the slot returns to the free
/// set.</para>
///
/// <para>Cancellation is best-effort: a queued waiter that is cancelled is
/// removed from contention, but if the slot was already handed out the acquire
/// completes normally — a slot is never orphaned or double-assigned.</para>
/// </summary>
public sealed class SlotPool
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkerState> _workers = new(StringComparer.Ordinal);

    /// <summary>Creates a pool where every worker has <paramref name="slotsPerWorker"/> slots.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotsPerWorker"/>
    /// is not positive, or <paramref name="maxQueuedWaitersPerWorker"/> is not positive.</exception>
    public SlotPool(int slotsPerWorker, int maxQueuedWaitersPerWorker = 1024)
    {
        if (slotsPerWorker <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotsPerWorker), "Slots per worker must be positive.");
        if (maxQueuedWaitersPerWorker <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedWaitersPerWorker), "Max queued waiters must be positive.");
        SlotsPerWorker = slotsPerWorker;
        MaxQueuedWaitersPerWorker = maxQueuedWaitersPerWorker;
    }

    /// <summary>The number of slots every worker owns.</summary>
    public int SlotsPerWorker { get; }

    /// <summary>The maximum number of live (uncancelled) waiters a worker may queue.</summary>
    public int MaxQueuedWaitersPerWorker { get; }

    /// <summary>
    /// Acquire a free slot on <paramref name="worker"/>. Returns immediately when
    /// a slot is free; otherwise the caller waits (in priority order) until
    /// <see cref="Release"/> frees one. Slot numbers are reused lowest-first for
    /// determinism.
    /// </summary>
    /// <param name="worker">Worker name (slot namespace).</param>
    /// <param name="priority">Lower value = higher priority; default 0.</param>
    /// <param name="ct">Cancels the queue wait. If a slot has already been
    /// assigned the acquire completes normally.</param>
    /// <exception cref="ArgumentException"><paramref name="worker"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The worker's waiter queue is at
    /// <see cref="MaxQueuedWaitersPerWorker"/> live waiters.</exception>
    public ValueTask<int> AcquireAsync(string worker, int priority = 0, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(worker);

        lock (_gate)
        {
            if (ct.IsCancellationRequested)
                return new ValueTask<int>(Task.FromCanceled<int>(ct));

            var ws = GetOrCreate(worker);
            if (ws.Free.Count > 0)
            {
                var slot = ws.Free.Min!;
                ws.Free.Remove(slot);
                ws.Outstanding++;
                return new ValueTask<int>(slot);
            }

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new SlotWaiter(priority, tcs);
            if (ws.Waiters.IsFull)
                CompactLocked(ws); // drop tombstoned (cancelled) waiters to free capacity
            if (!ws.Waiters.TryEnqueue(waiter, priority))
                throw new InvalidOperationException(
                    $"Slot pool waiter queue for worker '{worker}' is full (capacity {MaxQueuedWaitersPerWorker}).");
            ws.Queued++;
            waiter.Registration = ct.Register(() => TryCancelWaiter(worker, waiter, ct));
            return new ValueTask<int>(tcs.Task);
        }
    }

    /// <summary>
    /// Release <paramref name="slot"/> back to <paramref name="worker"/>. The slot
    /// is handed to the highest-priority queued waiter, or returned to the free
    /// set when no live waiter is queued.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="worker"/> is null/empty or
    /// has no outstanding acquisition of any slot.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a
    /// valid slot number for the pool.</exception>
    /// <exception cref="InvalidOperationException">The slot is already free (double release).</exception>
    public void Release(string worker, int slot)
    {
        ArgumentException.ThrowIfNullOrEmpty(worker);
        if (slot < 0 || slot >= SlotsPerWorker)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be in [0, {SlotsPerWorker}).");

        SlotWaiter? handedTo = null;
        lock (_gate)
        {
            if (!_workers.TryGetValue(worker, out var ws))
                throw new ArgumentException(
                    $"Unknown worker '{worker}': release of slot {slot} has no matching acquire.", nameof(worker));
            if (ws.Free.Contains(slot))
                throw new InvalidOperationException($"Double release of slot {slot} on worker '{worker}'.");

            // Hand the slot to the highest-priority live waiter. Tombstoned
            // (cancelled) waiters are dropped; the slot stays in our hands.
            while (ws.Waiters.TryDequeue(out var waiter))
            {
                if (waiter.TryAssign())
                {
                    ws.Queued--;
                    handedTo = waiter;
                    break;
                }
            }

            if (handedTo is null)
            {
                // No live waiter: the slot returns to the free set.
                ws.Free.Add(slot);
                ws.Outstanding--;
                if (ws.Outstanding < 0)
                {
                    // Defensive: callers releasing more times than they acquired
                    // would break the free-set invariant — roll back and surface it.
                    ws.Free.Remove(slot);
                    ws.Outstanding++;
                    throw new InvalidOperationException(
                        $"Release of slot {slot} on worker '{worker}' exceeds outstanding acquisitions.");
                }
            }
        }

        if (handedTo is not null)
        {
            // Outside the lock: disposing a registration can block waiting for its
            // callback, and the callback itself takes the lock — doing this under
            // the lock would deadlock.
            handedTo.Registration.Dispose();
            handedTo.Completion.TrySetResult(slot);
        }
    }

    /// <summary>
    /// Number of free slots on <paramref name="worker"/> right now. A worker that
    /// has never been acquired from reports all its slots as free.
    /// </summary>
    public int AvailableSlots(string worker)
    {
        ArgumentException.ThrowIfNullOrEmpty(worker);
        lock (_gate)
        {
            return _workers.TryGetValue(worker, out var ws) ? ws.Free.Count : SlotsPerWorker;
        }
    }

    /// <summary>
    /// Number of live (uncancelled) queued waiters on <paramref name="worker"/>.
    /// </summary>
    public int QueuedWaiters(string worker)
    {
        ArgumentException.ThrowIfNullOrEmpty(worker);
        lock (_gate)
        {
            return _workers.TryGetValue(worker, out var ws) ? ws.Queued : 0;
        }
    }

    private WorkerState GetOrCreate(string worker)
    {
        if (!_workers.TryGetValue(worker, out var ws))
        {
            ws = new WorkerState(SlotsPerWorker, MaxQueuedWaitersPerWorker);
            _workers[worker] = ws;
        }

        return ws;
    }

    private void TryCancelWaiter(string worker, SlotWaiter waiter, CancellationToken ct)
    {
        if (!waiter.TryCancel())
            return; // already assigned a slot — cancellation is too late

        lock (_gate)
        {
            if (_workers.TryGetValue(worker, out var ws))
                ws.Queued--;
        }

        waiter.Registration.Dispose();
        waiter.Completion.TrySetCanceled(ct);
    }

    /// <summary>Rebuild the worker's waiter queue, dropping tombstoned waiters
    /// while preserving priority/FIFO order (used to reclaim capacity).</summary>
    private static void CompactLocked(WorkerState ws)
    {
        var live = new List<SlotWaiter>(ws.Waiters.Count);
        while (ws.Waiters.TryDequeue(out var waiter))
        {
            if (!waiter.IsCancelled)
                live.Add(waiter);
        }

        foreach (var waiter in live)
            ws.Waiters.Enqueue(waiter, waiter.Priority);
    }

    private sealed class WorkerState
    {
        public SortedSet<int> Free { get; }
        public PriorityWaiterQueue<SlotWaiter> Waiters { get; }
        public int Outstanding; // slots currently held by owners (incl. mid-handoff)
        public int Queued;      // live (uncancelled) queued waiters

        public WorkerState(int slotsPerWorker, int maxQueuedWaiters)
        {
            Free = new SortedSet<int>(Enumerable.Range(0, slotsPerWorker));
            Waiters = new PriorityWaiterQueue<SlotWaiter>(maxQueuedWaiters);
        }
    }

    /// <summary>State machine: 0 = queued, 1 = assigned a slot, 2 = cancelled.
    /// Exactly one of <see cref="TryAssign"/> / <see cref="TryCancel"/> wins, so a
    /// slot can never be handed to a cancelled waiter nor orphaned.</summary>
    private sealed class SlotWaiter
    {
        private int _state;

        public SlotWaiter(int priority, TaskCompletionSource<int> completion)
        {
            Priority = priority;
            Completion = completion;
        }

        public int Priority { get; }
        public TaskCompletionSource<int> Completion { get; }
        public CancellationTokenRegistration Registration { get; set; }
        public bool IsCancelled => Volatile.Read(ref _state) == 2;

        public bool TryAssign() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public bool TryCancel() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
    }
}
