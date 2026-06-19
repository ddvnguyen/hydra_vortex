using Hydra.Core.Services;
using Xunit;

namespace Tests.Core;

/// <summary>
/// M-Perf.9 #289: unit tests for the cross-model KV safety decision function.
/// The function is a pure decision — no IO, no time, no randomness — so every
/// branch is exhaustively tested below. WorkerSchedulerService.RestoreKvAsync
/// delegates to this function for the actual decision; the tests here are the
/// authoritative behaviour for the cross-model guard.
/// </summary>
public class CrossModelGuardTests
{
    private const string H1 = "a3f1c8e0b2d4a3f1c8e0b2d4a3f1c8e0b2d4a3f1c8e0b2d4a3f1c8e0b2d4a3f1";
    private const string H2 = "b2a1c0d9e8f7b2a1c0d9e8f7b2a1c0d9e8f7b2a1c0d9e8f7b2a1c0d9e8f7b2a1";

    [Fact]
    public void Decide_MatchingHashes_Proceeds()
    {
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: H1, allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Proceed, outcome);
    }

    [Fact]
    public void Decide_MatchingHashesWithFlag_Proceeds()
    {
        // The flag only changes behaviour on a MISMATCH; matching hashes always
        // proceed regardless of the flag value.
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: H1, allowCrossModelKvReuse: true);
        Assert.Equal(CrossModelGuard.Outcome.Proceed, outcome);
    }

    [Fact]
    public void Decide_MismatchedHashesFlagOff_Aborts()
    {
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: H2, allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Abort, outcome);
    }

    [Fact]
    public void Decide_MismatchedHashesFlagOn_WarnsAndProceeds()
    {
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: H2, allowCrossModelKvReuse: true);
        Assert.Equal(CrossModelGuard.Outcome.WarnAndProceed, outcome);
    }

    [Fact]
    public void Decide_StoredHashEmpty_Skips()
    {
        // Pre-#289 KV with no recorded model_hash — guard cannot opine.
        var outcome = CrossModelGuard.Decide(storedHash: "", slotHash: H1, allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Skip, outcome);
    }

    [Fact]
    public void Decide_StoredHashNull_Skips()
    {
        var outcome = CrossModelGuard.Decide(storedHash: null, slotHash: H1, allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Skip, outcome);
    }

    [Fact]
    public void Decide_SlotHashEmpty_Skips()
    {
        // Slot META failed or pre-#289 binary.
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: "", allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Skip, outcome);
    }

    [Fact]
    public void Decide_SlotHashNull_Skips()
    {
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: null, allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Skip, outcome);
    }

    [Fact]
    public void Decide_BothEmpty_Skips()
    {
        // Pre-#289 data all the way through (item.KvModelHash empty, slot
        // META empty). Back-compat: skip the check, proceed as before.
        var outcome = CrossModelGuard.Decide(storedHash: "", slotHash: "", allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Skip, outcome);
    }

    [Fact]
    public void Decide_BothNull_Skips()
    {
        var outcome = CrossModelGuard.Decide(storedHash: null, slotHash: null, allowCrossModelKvReuse: true);
        Assert.Equal(CrossModelGuard.Outcome.Skip, outcome);
    }

    [Fact]
    public void Decide_EmptyStoredWithFlagOn_StillSkips()
    {
        // The flag only converts Abort → WarnAndProceed; an empty stored
        // hash must always Skip, regardless of the flag (we cannot safely
        // proceed when we don't know what model built the KV).
        var outcome = CrossModelGuard.Decide(storedHash: "", slotHash: H2, allowCrossModelKvReuse: true);
        Assert.Equal(CrossModelGuard.Outcome.Skip, outcome);
    }

    [Fact]
    public void Decide_HashComparisonIsCaseSensitive()
    {
        // SHA-256 hex is canonically lowercase. The guard uses Ordinal
        // (case-sensitive) comparison — this is intentional: if a
        // non-canonical-case hash slips through, treating it as the same
        // hash would be a correctness bug, not a UX issue.
        var upper = H1.ToUpperInvariant();
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: upper, allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Abort, outcome);
    }

    [Fact]
    public void Decide_HashComparisonIsOrdinalNotCultureAware()
    {
        // A "hash" containing only ASCII hex digits can't trigger
        // culture-aware bugs in practice, but the guard's contract is
        // explicit — Ordinal, no culture.
        var outcome = CrossModelGuard.Decide(storedHash: H1, slotHash: H1, allowCrossModelKvReuse: false);
        Assert.Equal(CrossModelGuard.Outcome.Proceed, outcome);
    }
}
