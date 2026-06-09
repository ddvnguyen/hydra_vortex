using System.Collections.Concurrent;

namespace Hydra.Core.Models;

public sealed class SlotPool
{
    private readonly ConcurrentStack<int> _free;

    public int Total { get; }
    public WorkerConfig Worker { get; }

    public SlotPool(WorkerConfig worker)
    {
        Worker = worker;
        Total = worker.Slots;
        _free = new ConcurrentStack<int>(
            Enumerable.Range(0, Total).Reverse());
    }

    public bool TryRent(out int slotId) => _free.TryPop(out slotId);

    public void Return(int slotId) => _free.Push(slotId);

    public int Free => _free.Count;
    public bool HasFree => !_free.IsEmpty;
}
