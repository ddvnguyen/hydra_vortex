using System.Collections;

namespace Hydra.Core.Scheduling;

/// <summary>
/// A bounded, thread-safe priority queue with deterministic FIFO tie-breaking.
/// Elements are ordered first by priority (lower value = higher priority) and,
/// within the same priority, by arrival order (first enqueued is dequeued
/// first). Backed by <see cref="PriorityQueue{TElement,TPriority}"/>; every
/// operation is serialized through a single lock so all orderings are
/// deterministic regardless of caller concurrency.
///
/// <para>This is the queue primitive behind <see cref="SlotPool"/>'s waiter
/// queue and is intentionally free of cancellation/removal semantics — callers
/// that need to abandon an entry (e.g. a cancelled waiter) layer tombstoning on
/// top via <see cref="TryDequeue"/>.</para>
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class PriorityWaiterQueue<T> : IReadOnlyCollection<T>
{
    private readonly object _gate = new();
    private readonly PriorityQueue<Entry, (int Priority, long Sequence)> _queue;
    private long _nextSequence;

    /// <summary>Creates a queue with the given <paramref name="capacity"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public PriorityWaiterQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        _queue = new PriorityQueue<Entry, (int Priority, long Sequence)>(capacity);
        Capacity = capacity;
    }

    /// <summary>The maximum number of elements the queue can hold.</summary>
    public int Capacity { get; }

    /// <summary>The number of elements currently in the queue.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>True when the queue holds no elements.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>True when the queue holds <see cref="Capacity"/> elements.</summary>
    public bool IsFull => Count == Capacity;

    /// <summary>
    /// Enqueue <paramref name="item"/> with the given <paramref name="priority"/>
    /// (lower value = higher priority). Returns false without modifying the queue
    /// when it is full.
    /// </summary>
    public bool TryEnqueue(T item, int priority)
    {
        lock (_gate)
        {
            if (_queue.Count == Capacity)
                return false;
            _queue.Enqueue(new Entry(item, priority, ++_nextSequence), (priority, _nextSequence));
            return true;
        }
    }

    /// <summary>
    /// Enqueue <paramref name="item"/> with the given <paramref name="priority"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The queue is full.</exception>
    public void Enqueue(T item, int priority)
    {
        if (!TryEnqueue(item, priority))
            throw new InvalidOperationException($"PriorityWaiterQueue is full (capacity {Capacity}).");
    }

    /// <summary>
    /// Remove and return the highest-priority element (FIFO within a priority).
    /// Returns false without modifying the queue when it is empty.
    /// </summary>
    public bool TryDequeue(out T item)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                item = default!;
                return false;
            }
            item = _queue.Dequeue().Item;
            return true;
        }
    }

    /// <summary>
    /// Remove and return the highest-priority element (FIFO within a priority).
    /// </summary>
    /// <exception cref="InvalidOperationException">The queue is empty.</exception>
    public T Dequeue()
    {
        if (!TryDequeue(out var item))
            throw new InvalidOperationException("PriorityWaiterQueue is empty.");
        return item;
    }

    /// <summary>
    /// Return (without removing) the highest-priority element and its priority.
    /// Returns false when the queue is empty.
    /// </summary>
    public bool TryPeek(out T item, out int priority)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                item = default!;
                priority = 0;
                return false;
            }
            _queue.TryPeek(out var entry, out _);
            item = entry.Item;
            priority = entry.Priority;
            return true;
        }
    }

    /// <summary>Remove all elements. Capacity is retained.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _nextSequence = 0;
        }
    }

    /// <summary>Snapshot of the current elements in priority order (deterministic).</summary>
    public IEnumerator<T> GetEnumerator()
    {
        List<T> snapshot;
        lock (_gate)
        {
            snapshot = _queue.UnorderedItems
                .OrderBy(e => e.Priority.Priority)
                .ThenBy(e => e.Priority.Sequence)
                .Select(e => e.Element.Item)
                .ToList();
        }

        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private readonly record struct Entry(T Item, int Priority, long Sequence);
}
