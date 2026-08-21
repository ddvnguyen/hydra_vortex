using Hydra.Core;
using Hydra.Core.Caching;
using Hydra.Core.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Tests.Core.Services;

// ═══════════════════════════════════════════════════════════════════════
// Issue #615: the L1 tmpfs byte-LRU eviction (EvictLRUAsync) was dead code —
// never invoked periodically — so the 30 GB /mnt/llm-ram tmpfs filled and
// every KV save failed with ENOSPC. ChunkCacheSweepService is the wired
// periodic sweep; these tests pin its behavior.
// ═══════════════════════════════════════════════════════════════════════

public sealed class ChunkCacheSweepServiceTests : IDisposable
{
    private const int Cap = 10 * 1024; // 10 KB cap → 8 KB low-water mark

    private static readonly string OldHash = new('a', 64);
    private static readonly string NewHash = new('b', 64);

    private readonly string _cacheDir;
    private readonly string _oldPath;
    private readonly string _newPath;

    public ChunkCacheSweepServiceTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"hydra-l1-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDir);
        _oldPath = Path.Combine(_cacheDir, $"sess_old.{OldHash}");
        _newPath = Path.Combine(_cacheDir, $"sess_new.{NewHash}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    // Seed raw chunk files DIRECTLY (bypassing SaveChunkDataAsync's at-write
    // eviction, which would never let the cache sit over its cap) so the L1
    // ctor's RebuildFromDisk counts them and the cache starts ABOVE the
    // low-water mark — the exact stale-over-cap state the sweep must fix.
    private void SeedOverCapCache()
    {
        File.WriteAllBytes(_oldPath, new byte[6 * 1024]);
        File.WriteAllBytes(_newPath, new byte[6 * 1024]);
        // sess_old must be the LRU-eligible candidate: older SavedAt.
        File.SetLastWriteTimeUtc(_oldPath, DateTime.UtcNow.AddMinutes(-10));
    }

    private static LocalChunkCache MakeFacade(string cacheDir) => new(new LocalFsChunkCache(cacheDir, Cap));

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    [Fact]
    public async Task SweepOnce_OverCapCache_EvictsOldestSessionAndLogs()
    {
        SeedOverCapCache();
        var l1 = new LocalFsChunkCache(_cacheDir, Cap);
        Assert.True(l1.L1UsedBytes > (long)(Cap * 0.8), "seeded cache must start over the low-water mark");

        var events = new List<LogEvent>();
        var log = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new CollectingSink(events))
            .CreateLogger();
        // Single cache instance: the service sweeps the SAME instance whose
        // byte counter the assertions read (a second instance on the same
        // dir would evict files the first instance still counts).
        var svc = new ChunkCacheSweepService(new LocalChunkCache(l1), log);

        await svc.SweepOnceAsync();

        // Evicted down to the 80% low-water mark; the oldest session's files
        // are gone, the newest session survives.
        Assert.True(l1.L1UsedBytes <= (long)(Cap * 0.8));
        Assert.False(File.Exists(_oldPath));
        Assert.True(File.Exists(_newPath));

        var sweep = events.Single(e => e.MessageTemplate.Text.Contains("chunk_cache_lru_sweep"));
        var rendered = sweep.RenderMessage();
        Assert.Contains("evicted=1", rendered);
        Assert.Contains("bytes=6144", rendered);
    }

    [Fact]
    public async Task ExecuteAsync_SweepsPeriodically_UntilCancelled()
    {
        SeedOverCapCache();
        var l1 = new LocalFsChunkCache(_cacheDir, Cap);
        var svc = new ChunkCacheSweepService(
            new LocalChunkCache(l1), Serilog.Log.Logger, interval: TimeSpan.FromMilliseconds(20));

        await svc.StartAsync(CancellationToken.None);
        // Several 20 ms ticks: the sweep must run WITHOUT any write activity
        // (this is the case the old code missed — reads-only churn).
        await Task.Delay(150);
        await svc.StopAsync(CancellationToken.None);

        Assert.True(l1.L1UsedBytes <= (long)(Cap * 0.8));
        Assert.False(File.Exists(_oldPath));
    }

    [Fact]
    public async Task SweepOnce_UnderCapCache_LogsZeroEvicted()
    {
        // Empty cache — the sweep must still run (heartbeat log) and report
        // evicted=0 rather than silently skipping.
        var events = new List<LogEvent>();
        var log = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new CollectingSink(events))
            .CreateLogger();
        var svc = new ChunkCacheSweepService(MakeFacade(_cacheDir), log);

        await svc.SweepOnceAsync();

        var sweep = events.Single(e => e.MessageTemplate.Text.Contains("chunk_cache_lru_sweep"));
        var rendered = sweep.RenderMessage();
        Assert.Contains("evicted=0", rendered);
        Assert.Contains("bytes=0", rendered);
    }
}
