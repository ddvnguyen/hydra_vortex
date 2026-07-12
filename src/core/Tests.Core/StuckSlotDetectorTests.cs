using Hydra.Core.Models;
using Hydra.Core.Services;
using Xunit;

namespace Tests.Core;

public sealed class StuckSlotDetectorTests
{
    private static SlotInfo Slot(int id, bool processing, int nRemain, int nPast = 0, int stuckCount = 0) =>
        new() { Id = id, IsProcessing = processing, NRemain = nRemain, NPast = nPast, StuckPollCount = stuckCount };

    [Fact]
    public void LooksStuck_OnlyWhenProcessingAndNoRemaining()
    {
        Assert.True(StuckSlotDetector.LooksStuck(Slot(0, processing: true, nRemain: 0)));
        Assert.False(StuckSlotDetector.LooksStuck(Slot(0, processing: true, nRemain: 5)));
        Assert.False(StuckSlotDetector.LooksStuck(Slot(0, processing: false, nRemain: 0)));
    }

    [Fact]
    public void Apply_IncrementsAcrossConsecutiveStuckCycles()
    {
        var threshold = 3;
        IReadOnlyList<SlotInfo>? prev = null;

        // Cycle 1
        var c1 = new List<SlotInfo> { Slot(0, true, 0) };
        Assert.Equal(0, StuckSlotDetector.Apply(prev, c1, threshold));
        Assert.Equal(1, c1[0].StuckPollCount);

        // Cycle 2
        var c2 = new List<SlotInfo> { Slot(0, true, 0) };
        Assert.Equal(0, StuckSlotDetector.Apply(c1, c2, threshold));
        Assert.Equal(2, c2[0].StuckPollCount);

        // Cycle 3 — crosses threshold
        var c3 = new List<SlotInfo> { Slot(0, true, 0) };
        Assert.Equal(1, StuckSlotDetector.Apply(c2, c3, threshold));
        Assert.Equal(3, c3[0].StuckPollCount);
    }

    [Fact]
    public void Apply_ResetsWhenSlotRecovers()
    {
        var prev = new List<SlotInfo> { Slot(0, true, 0, stuckCount: 2) };
        var current = new List<SlotInfo> { Slot(0, processing: true, nRemain: 7) }; // now generating again

        Assert.Equal(0, StuckSlotDetector.Apply(prev, current, threshold: 3));
        Assert.Equal(0, current[0].StuckPollCount);
    }

    [Fact]
    public void Apply_HealthySlotNeverCountsAsStuck()
    {
        var prev = new List<SlotInfo> { Slot(0, false, 0, stuckCount: 0) };
        var current = new List<SlotInfo> { Slot(0, processing: false, nRemain: 0) }; // idle
        Assert.Equal(0, StuckSlotDetector.Apply(prev, current, threshold: 3));
        Assert.Equal(0, current[0].StuckPollCount);
    }

    [Fact]
    public void Apply_TracksSlotsIndependentlyById()
    {
        var prev = new List<SlotInfo>
        {
            Slot(0, true, 0, stuckCount: 2),
            Slot(1, true, 0, stuckCount: 0),
        };
        var current = new List<SlotInfo>
        {
            Slot(0, true, 0), // -> 3, stuck
            Slot(1, true, 0), // -> 1, not yet
        };

        Assert.Equal(1, StuckSlotDetector.Apply(prev, current, threshold: 3));
        Assert.Equal(3, current[0].StuckPollCount);
        Assert.Equal(1, current[1].StuckPollCount);
    }

    [Fact]
    public void Apply_NoPreviousSnapshot_StartsFromZero()
    {
        // threshold 1 → a single stuck cycle already crosses for both slots.
        var current = new List<SlotInfo> { Slot(0, true, 0), Slot(1, true, 0) };
        Assert.Equal(2, StuckSlotDetector.Apply(previous: null, current, threshold: 1));
        Assert.Equal(1, current[0].StuckPollCount);
        Assert.Equal(1, current[1].StuckPollCount);
    }

    [Fact]
    public void LooksStuck_ProcessingNRemainZeroNpastStalled_CountsAsStuck()
    {
        // Slot is processing, n_remain=0, and n_past has not advanced → stuck
        var prev = new List<SlotInfo> { Slot(0, true, 0, nPast: 100, stuckCount: 2) };
        var current = new List<SlotInfo> { Slot(0, true, 0, nPast: 100) };
        Assert.Equal(1, StuckSlotDetector.Apply(prev, current, threshold: 3));
        Assert.Equal(3, current[0].StuckPollCount);
    }

    [Fact]
    public void LooksStuck_ProcessingNRemainZeroNpastAdvancing_NotStuck()
    {
        // Slot is processing and n_remain=0, but n_past advanced (100→105) → NOT stuck (#342 regression)
        var prev = new List<SlotInfo> { Slot(0, true, 0, nPast: 100, stuckCount: 2) };
        var current = new List<SlotInfo> { Slot(0, true, 0, nPast: 105) };
        Assert.Equal(0, StuckSlotDetector.Apply(prev, current, threshold: 3));
        Assert.Equal(0, current[0].StuckPollCount);
    }

    [Fact]
    public void NpastStalled_NoPrevious_ReturnsTrue()
    {
        Assert.True(StuckSlotDetector.NpastStalled(Slot(0, true, 0, nPast: 100), previous: null));
    }

    [Fact]
    public void NpastStalled_SameValue_ReturnsTrue()
    {
        var prev = Slot(0, true, 0, nPast: 100);
        var cur = Slot(0, true, 0, nPast: 100);
        Assert.True(StuckSlotDetector.NpastStalled(cur, prev));
    }

    [Fact]
    public void NpastStalled_Advanced_ReturnsFalse()
    {
        var prev = Slot(0, true, 0, nPast: 100);
        var cur = Slot(0, true, 0, nPast: 105);
        Assert.False(StuckSlotDetector.NpastStalled(cur, prev));
    }
}
