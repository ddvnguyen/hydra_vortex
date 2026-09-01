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

    /// <summary>
    /// Rents exactly <paramref name="pinnedSlot"/> — used by the #718
    /// warm-slot fast path, where the session's KV is verified resident on
    /// that physical slot and the generic TryRent (top-of-stack) must NOT be
    /// allowed to hand out a different one. Returns false if the pinned slot
    /// is not currently free. Slots scanned while looking are pushed back
    /// (their relative order may change; identity is always preserved — no
    /// slot is lost or double-rented).
    /// </summary>
    public bool TryRentPinned(int pinnedSlot, out int slotId)
    {
        slotId = -1;
        var moved = new List<int>();
        while (_free.TryPop(out var s))
        {
            if (s == pinnedSlot)
            {
                slotId = s;
                foreach (var m in moved) _free.Push(m);
                return true;
            }
            moved.Add(s);
        }
        foreach (var m in moved) _free.Push(m);
        return false;
    }

    public void Return(int slotId) => _free.Push(slotId);

    public int Free => _free.Count;
    public bool HasFree => !_free.IsEmpty;
}
