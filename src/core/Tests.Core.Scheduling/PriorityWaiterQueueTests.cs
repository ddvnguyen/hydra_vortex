namespace Tests.Core.Scheduling;

/// <summary>
/// Pins the contract of the bounded, deterministic priority queue used by the
/// worker scheduler rewrite (epic #591): priority ordering (lower value = higher
/// priority), FIFO tie-breaking, capacity enforcement, and thread safety.
/// </summary>
public sealed class PriorityWaiterQueueTests
{
    [Fact]
    public void Dequeue_Returns_Lowest_Priority_Value_First()
    {
        var q = new PriorityWaiterQueue<string>(8);
        q.Enqueue("low", priority: 10);
        q.Enqueue("high", priority: 1);
        q.Enqueue("mid", priority: 5);

        Assert.Equal("high", q.Dequeue());
        Assert.Equal("mid", q.Dequeue());
        Assert.Equal("low", q.Dequeue());
        Assert.True(q.IsEmpty);
    }

    [Fact]
    public void Same_Priority_Tie_Break_Is_FIFO()
    {
        var q = new PriorityWaiterQueue<string>(8);
        q.Enqueue("first", priority: 3);
        q.Enqueue("second", priority: 3);
        q.Enqueue("third", priority: 3);

        Assert.Equal("first", q.Dequeue());
        Assert.Equal("second", q.Dequeue());
        Assert.Equal("third", q.Dequeue());
    }

    [Fact]
    public void Interleaved_Priorities_Produce_Deterministic_Order()
    {
        var q = new PriorityWaiterQueue<string>(8);
        q.Enqueue("a", priority: 1);
        q.Enqueue("b", priority: 3);
        q.Enqueue("c", priority: 1);
        q.Enqueue("d", priority: 3);

        // a before c (both priority 1, FIFO); b before d (both priority 3, FIFO).
        Assert.Equal(new[] { "a", "c", "b", "d" }, q.ToArray());
    }

    [Fact]
    public void TryEnqueue_Returns_False_When_Full_Without_Modifying()
    {
        var q = new PriorityWaiterQueue<int>(2);
        Assert.True(q.TryEnqueue(1, priority: 1));
        Assert.True(q.TryEnqueue(2, priority: 2));
        Assert.True(q.IsFull);
        Assert.False(q.TryEnqueue(3, priority: 3));

        Assert.Equal(2, q.Count);
        Assert.Equal(1, q.Dequeue());
        Assert.Equal(2, q.Dequeue());
    }

    [Fact]
    public void Enqueue_Throws_When_Full()
    {
        var q = new PriorityWaiterQueue<int>(1);
        q.Enqueue(1, priority: 1);

        var ex = Assert.Throws<InvalidOperationException>(() => q.Enqueue(2, priority: 2));
        Assert.Contains("full", ex.Message);
    }

    [Fact]
    public void TryDequeue_On_Empty_Returns_False()
    {
        var q = new PriorityWaiterQueue<int>(4);
        Assert.False(q.TryDequeue(out _));
        Assert.True(q.IsEmpty);
    }

    [Fact]
    public void Dequeue_On_Empty_Throws()
    {
        var q = new PriorityWaiterQueue<int>(4);
        var ex = Assert.Throws<InvalidOperationException>(() => q.Dequeue());
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void TryPeek_Returns_Highest_Priority_Without_Removing()
    {
        var q = new PriorityWaiterQueue<string>(8);
        q.Enqueue("mid", priority: 5);
        q.Enqueue("top", priority: 1);

        Assert.True(q.TryPeek(out var item, out var priority));
        Assert.Equal("top", item);
        Assert.Equal(1, priority);
        Assert.Equal(2, q.Count); // untouched
    }

    [Fact]
    public void TryPeek_On_Empty_Returns_False()
    {
        var q = new PriorityWaiterQueue<int>(4);
        Assert.False(q.TryPeek(out _, out _));
    }

    [Fact]
    public void Clear_Empties_But_Retains_Capacity()
    {
        var q = new PriorityWaiterQueue<int>(3);
        q.Enqueue(1, priority: 1);
        q.Enqueue(2, priority: 2);
        q.Clear();

        Assert.True(q.IsEmpty);
        Assert.Equal(3, q.Capacity);
        Assert.True(q.TryEnqueue(9, priority: 1)); // capacity was retained
    }

    [Fact]
    public void Capacity_And_Count_Are_Exposed()
    {
        var q = new PriorityWaiterQueue<int>(3);
        Assert.Equal(3, q.Capacity);
        Assert.Equal(0, q.Count);

        q.Enqueue(1, priority: 1);
        q.Enqueue(2, priority: 2);
        Assert.Equal(2, q.Count);
    }

    [Fact]
    public void Constructor_Rejects_Non_Positive_Capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriorityWaiterQueue<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriorityWaiterQueue<int>(-1));
    }

    [Fact]
    public void Enumeration_Returns_Deterministic_Priority_Order()
    {
        var q = new PriorityWaiterQueue<string>(8);
        q.Enqueue("x", priority: 2);
        q.Enqueue("y", priority: 1);
        q.Enqueue("z", priority: 2);

        Assert.Equal(new[] { "y", "x", "z" }, q.ToArray());
        Assert.Equal(3, q.Count()); // IReadOnlyCollection<T> surface
    }

    [Fact]
    public async Task Concurrent_Enqueue_Dequeue_Recovers_Every_Item_Exactly_Once()
    {
        const int items = 1000;
        var q = new PriorityWaiterQueue<int>(items);

        var producers = Enumerable.Range(0, 8).Select(worker =>
            Task.Run(() =>
            {
                for (var i = worker; i < items; i += 8)
                    q.Enqueue(i, priority: i % 5); // varied priorities, deterministic per item
            }));
        await Task.WhenAll(producers);

        Assert.Equal(items, q.Count);
        var seen = new HashSet<int>();
        var consumerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (q.TryDequeue(out var item))
            {
                lock (seen)
                {
                    seen.Add(item);
                }
            }
        }));
        await Task.WhenAll(consumerTasks);

        Assert.True(q.IsEmpty);
        // Every item was recovered exactly once — no duplicates, none lost.
        Assert.Equal(Enumerable.Range(0, items), seen.OrderBy(x => x));
    }
}
