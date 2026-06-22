using Hydra.Core.Models;

namespace Hydra.Core.Services;

/// <summary>
/// Pure stuck-slot detection for the health watchdog (#299/C7). A slot reporting
/// <c>is_processing &amp;&amp; n_remain==0</c> across consecutive poll cycles is "stuck"
/// (it claims to be working but has nothing left to generate). Kept side-effect-free
/// — no I/O, no clock — so it can be unit-tested without a live llama-server.
/// </summary>
public static class StuckSlotDetector
{
    /// <summary>True when a freshly-polled slot looks stuck this cycle.</summary>
    public static bool LooksStuck(SlotInfo slot) => slot.IsProcessing && slot.NRemain == 0;

    /// <summary>
    /// Carry the per-slot stuck counter forward from the previous cycle onto the
    /// current slots, and return how many current slots are at/over the threshold.
    /// Mutates <see cref="SlotInfo.StuckPollCount"/> on each entry in <paramref name="current"/>.
    /// A slot that no longer looks stuck resets to 0.
    /// </summary>
    public static int Apply(IReadOnlyList<SlotInfo>? previous, IReadOnlyList<SlotInfo> current, int threshold)
    {
        foreach (var slot in current)
        {
            var prevCount = 0;
            if (previous != null)
                foreach (var p in previous)
                    if (p.Id == slot.Id) { prevCount = p.StuckPollCount; break; }

            slot.StuckPollCount = LooksStuck(slot) ? prevCount + 1 : 0;
        }

        var stuck = 0;
        foreach (var s in current)
            if (s.StuckPollCount >= threshold) stuck++;
        return stuck;
    }
}
