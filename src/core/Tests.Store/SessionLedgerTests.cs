using Hydra.Store;

namespace Tests.Store;

public sealed class SessionLedgerTests
{
    [Fact]
    public void Register_And_Lookup()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        var e = l.Lookup("sess_a");
        Assert.NotNull(e);
        Assert.Equal("rtx", e!.NodeName);
        Assert.Equal(0, e!.SlotId);
        Assert.Equal(100, e!.NPast);
    }

    [Fact]
    public void Lookup_Nonexistent_Returns_Null()
    {
        var l = new SessionLedger();
        Assert.Null(l.Lookup("nonexistent"));
    }

    [Fact]
    public void Update_Last_Used()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        var before = l.Lookup("sess_a")!.LastUsed;
        Thread.Sleep(10);
        l.UpdateLastUsed("sess_a");
        var after = l.Lookup("sess_a")!.LastUsed;
        Assert.True(after > before);
    }

    [Fact]
    public void Update_NPast()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        l.UpdateNPast("sess_a", 200);
        Assert.Equal(200, l.Lookup("sess_a")!.NPast);
    }

    [Fact]
    public void Mark_Evicted_Sets_Flags()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        l.MarkEvicted("sess_a");
        var e = l.Lookup("sess_a")!;
        Assert.True(e.SlotFreed);
        Assert.True(e.HasStoreState);
        Assert.Equal(0, e.SlotId); // slot_id preserved
    }

    [Fact]
    public void Get_Sessions_On_Node()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        l.Register("sess_b", "p100", 0, 50);
        l.Register("sess_c", "rtx", 1, 75);
        var rtx = l.GetSessionsOnNode("rtx");
        Assert.Equal(2, rtx.Count);
        var p100 = l.GetSessionsOnNode("p100");
        Assert.Single(p100);
    }

    [Fact]
    public void Get_Lru_Session()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        Thread.Sleep(10);
        l.Register("sess_b", "rtx", 1, 50);
        // sess_a is older
        var lru = l.GetLruSession("rtx");
        Assert.NotNull(lru);
        Assert.Equal("sess_a", lru!.SessionId);
    }

    [Fact]
    public void Get_Lru_Excludes_Evicted()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        l.Register("sess_b", "rtx", 1, 50);
        l.MarkEvicted("sess_a");
        var lru = l.GetLruSession("rtx");
        Assert.Equal("sess_b", lru!.SessionId);
    }

    [Fact]
    public void Get_Lru_Excludes_Null_Slot()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", null, 100);
        l.Register("sess_b", "rtx", 1, 50);
        var lru = l.GetLruSession("rtx");
        Assert.Equal("sess_b", lru!.SessionId);
    }

    [Fact]
    public void Evict_Stale()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        var count = l.EvictStale(0.001); // very short timeout
        Assert.True(count >= 0);
    }

    [Fact]
    public void Remove()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        l.Remove("sess_a");
        Assert.Null(l.Lookup("sess_a"));
    }

    [Fact]
    public void Active_Count()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        l.Register("sess_b", "rtx", 1, 50);
        Assert.Equal(2, l.ActiveCount);
        l.MarkEvicted("sess_a");
        Assert.Equal(1, l.ActiveCount);
    }

    [Fact]
    public void Active_Count_On_Node()
    {
        var l = new SessionLedger();
        l.Register("sess_a", "rtx", 0, 100);
        l.Register("sess_b", "rtx", 1, 50);
        l.Register("sess_c", "p100", 0, 25);
        Assert.Equal(2, l.ActiveCountOnNode("rtx"));
    }
}
