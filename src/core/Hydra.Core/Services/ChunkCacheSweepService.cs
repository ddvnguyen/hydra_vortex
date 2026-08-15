using Microsoft.Extensions.Hosting;
using Serilog;

namespace Hydra.Core.Services;

/// <summary>
/// Periodic L1 chunk-cache LRU sweep (#615). Calls
/// <see cref="LocalChunkCache.EvictLRUWithStatsAsync"/> every
/// <see cref="DefaultInterval"/> so the tmpfs L1 can never sit above its
/// byte cap (HYDRA_COORD_CHUNK_CACHE_L1_MAX_BYTES, 20 GB default; evicted
/// down to the 80% low-water mark). Before this service existed the L1
/// eviction was dead code invoked from nowhere — the 30 GB /mnt/llm-ram
/// tmpfs filled and every KV save failed with ENOSPC.
///
/// The L1 and the Store's chunk dir share the same tmpfs mount, so this
/// sweep also keeps headroom for the Store: it is the companion to the
/// evict-on-ENOSPC retry in WorkerSchedulerService.PushChunkBatchAsync.
/// </summary>
public sealed class ChunkCacheSweepService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(45);

    private readonly LocalChunkCache _chunkCache;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;

    public ChunkCacheSweepService(LocalChunkCache chunkCache, ILogger log, TimeSpan? interval = null)
    {
        _chunkCache = chunkCache ?? throw new ArgumentNullException(nameof(chunkCache));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _interval = interval ?? DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                await SweepOnceAsync();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "chunk_cache_lru_sweep_failed");
            }
        }
    }

    /// <summary>One sweep cycle. Internal so tests can drive it directly
    /// instead of waiting out the 45 s interval.</summary>
    internal async Task SweepOnceAsync()
    {
        var result = await _chunkCache.EvictLRUWithStatsAsync();
        // Log every tick (even evicted=0): the sweep was previously
        // invisible, which is exactly how the tmpfs filled undetected.
        _log.Information("chunk_cache_lru_sweep evicted={Evicted} bytes={BytesFreed}",
            result.Evicted, result.BytesFreed);
    }
}
