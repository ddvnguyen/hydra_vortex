using Hydra.Shared;
using Xunit;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// Direct coverage for MigrateSessionAsync (A5, #299 / review finding #318).
//
// Before the fix this method hardcoded slotId=0 and never acquired/released a
// slot on the target — a silent slot leak and a collision with whatever was
// already in slot 0. These tests exercise the standalone API directly (the
// existing StreamingSlotCleanupTests only drive RouteAsync→RestoreKvAsync and
// never call MigrateSessionAsync), pinning the post-fix contract:
//   • a REAL slot is acquired on the target and released again (no leak),
//   • the ledger is re-registered on the target with that concrete slot,
//   • n_past is carried from the StatePut meta,
//   • the guard branches throw before touching any slot.
//
// Reuses StreamingFixture: its TestRpcClient doubles as Store + llama binary
// RPC (returns Ok with n_past=2000), so nothing touches a socket.
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class MigrationApiTests
{
    [Fact]
    public async Task Migrate_AcquiresRealSlot_RegistersTarget_AndReleases()
    {
        await using var f = new StreamingFixture();
        f.Ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        f.Ledger.MarkStoreState("sess_mig"); // HasStoreState=true → migratable
        var p100FreeBefore = f.Tracker.FreeSlotCount("p100");
        Assert.True(p100FreeBefore >= 1, "fixture should leave p100 with a free slot");
        f.Rpc.ClearCalls();

        await f.Scheduler.MigrateSessionAsync("sess_mig", "p100", default);

        // Slot released after the StatePut completes → no leak (A5 core guarantee).
        Assert.Equal(p100FreeBefore, f.Tracker.FreeSlotCount("p100"));

        // Ledger now points at the target with a concrete (non-null) slot id,
        // not the old hardcoded 0-with-no-acquire.
        var entry = f.Ledger.Lookup("sess_mig")!;
        Assert.Equal("p100", entry.NodeName);
        Assert.NotNull(entry.SlotId);
        Assert.False(entry.SlotFreed);
        Assert.Equal(2000, entry.NPast); // parsed from StatePut meta

        // A Store Get (fetch KV) and a llama StatePut (restore into the slot) happened.
        Assert.True(f.Rpc.HasCall(OpCode.Get), "expected a Store Get for the KV blob");
        Assert.True(f.Rpc.HasCall(OpCode.StatePut), "expected a llama StatePut into the target slot");
    }

    [Fact]
    public async Task Migrate_NoFreeSlotOnTarget_Throws_WithoutTouchingOthersSlots()
    {
        await using var f = new StreamingFixture(p100Slots: 1);
        f.Ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        f.Ledger.MarkStoreState("sess_mig");

        // Occupy the target's only slot.
        Assert.True(f.Tracker.TryAcquireSlot("p100", out var held, "decode"));
        Assert.Equal(0, f.Tracker.FreeSlotCount("p100"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Scheduler.MigrateSessionAsync("sess_mig", "p100", default));

        // The failed migration must not have released the unrelated held slot
        // (the old Release(name) bug would pop an arbitrary one).
        Assert.Equal(0, f.Tracker.FreeSlotCount("p100"));
        f.Tracker.ReleaseSlot("p100", held);
    }

    [Fact]
    public async Task Migrate_NoStoreState_Throws_NotMigratable_WithoutAcquiringSlot()
    {
        await using var f = new StreamingFixture();
        f.Ledger.Register("sess_nostate", "rtx", slotId: 1, nPast: 100); // HasStoreState=false
        var p100FreeBefore = f.Tracker.FreeSlotCount("p100");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Scheduler.MigrateSessionAsync("sess_nostate", "p100", default));
        Assert.Contains("not migratable", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Guard fires before any slot work — target untouched.
        Assert.Equal(p100FreeBefore, f.Tracker.FreeSlotCount("p100"));
    }

    [Fact]
    public async Task Migrate_UnknownTarget_Throws_WithoutAcquiringSlot()
    {
        await using var f = new StreamingFixture();
        f.Ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        f.Ledger.MarkStoreState("sess_mig");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Scheduler.MigrateSessionAsync("sess_mig", "does_not_exist", default));
    }
}
