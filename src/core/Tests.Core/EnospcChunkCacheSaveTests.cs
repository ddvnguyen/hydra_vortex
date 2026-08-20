using System.Collections;
using System.Reflection;
using Hydra.Core;
using Hydra.Core.Caching;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.TestHelpers;

namespace Tests.Core;

/// <summary>
/// #470: the L1 chunk-cache save during chunked prefill is best-effort with
/// evict-on-ENOSPC recovery. A full /mnt/llm-ram tmpfs (Store chunk dir + L1
/// share the mount) surfaces as an IOException from the L1 write and must
/// NEVER abort the engine read — pre-#470 it propagated out of onChunk,
/// left the RPC socket mid-frame, and killed the turn with
/// prefill_rpc_error_exhausted (decode_node=-, tokens_out=0).
/// </summary>
public sealed class EnospcChunkCacheSaveTests : IDisposable
{
    private readonly string _cacheDir;

    public EnospcChunkCacheSaveTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"hydra-l1-enospc-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    private static WorkerSchedulerService MakeScheduler(LocalChunkCache chunkCache)
    {
        var cfg = new CoordinatorConfig
        {
            Workers = new List<WorkerConfig>
            {
                new() { Name = "rtx", Host = "localhost", RpcPort = 9601,
                    LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2,
                    PrefillPriority = 1, DecodePriority = 2 },
            },
        };
        var ledger = new SessionLedger();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var proxy = new CompletionProxyService();
        var health = new TestHealthMonitor();
        var sp = new ServiceCollection().BuildServiceProvider();
        return new WorkerSchedulerService(
            cfg, ledger, tracker, proxy, health, new FakeStoreClient(), sp,
            Serilog.Log.Logger, chunkCache);
    }

    [Fact]
    public async Task Save_WhenTmpfsFull_DoesNotThrow_AndEvictsLru()
    {
        // Cap 1 MB, low-water 800 KB. Seed 5 x 180 KB = 900 KB in an OLDER
        // session: over the low-water (so EvictLRUAsync has a victim) but
        // under the cap (so seeding itself never triggers at-write eviction).
        var cache = new LocalChunkCache(
            new LocalFsChunkCache(_cacheDir, maxBytes: 1024 * 1024), l2: null);
        var chunk = new byte[180 * 1024];
        Random.Shared.NextBytes(chunk);
        for (int i = 0; i < 5; i++)
            await cache.SaveChunkDataAsync("ses_old", $"h{i}", chunk, CancellationToken.None);
        Assert.Equal(5 * chunk.Length, cache.L1UsedBytes);

        // Inject a full filesystem: swap the cache dir for a symlink to
        // /dev/full (a char device that ENOSPCs every write). Writes under it
        // fail with an IOException (DirectoryNotFoundException) — the same
        // failure class the live ENOSPC produces — while Exists probes
        // harmlessly return false.
        Directory.Delete(_cacheDir, recursive: true);
        File.CreateSymbolicLink(_cacheDir, "/dev/full");

        var scheduler = MakeScheduler(cache);

        // Both attempts fail (eviction cannot free real space — the dir is a
        // device), but the save must complete without throwing.
        await scheduler.SaveChunkToL1BestEffortAsync(
            "ses_new", "hashnew", new byte[64], CancellationToken.None);

        // Eviction WAS attempted: the LRU session is gone from the in-memory
        // index even though the on-disk deletes could not run (dir is a file).
        var l1 = typeof(LocalChunkCache)
            .GetField("_l1", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;
        var caches = (IDictionary)typeof(LocalFsChunkCache)
            .GetField("_caches", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(l1)!;
        Assert.False(caches.Contains("ses_old"));
        Assert.True(caches.Contains("ses_new"));
    }

    [Fact]
    public async Task Save_WhenCacheHealthy_StoresChunk()
    {
        var cache = new LocalChunkCache(
            new LocalFsChunkCache(_cacheDir, maxBytes: 1024 * 1024), l2: null);
        var scheduler = MakeScheduler(cache);

        await scheduler.SaveChunkToL1BestEffortAsync(
            "ses_new", "hashA", new byte[128], CancellationToken.None);

        Assert.True(cache.HasChunkData("ses_new", "hashA"));
        Assert.Equal(128L, cache.L1UsedBytes);
    }
}
