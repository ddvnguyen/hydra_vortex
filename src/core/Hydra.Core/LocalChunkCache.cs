using Hydra.Core.Caching;

namespace Hydra.Core;

/// <summary>
/// Two-tier chunk cache facade. Preserves the legacy LocalChunkCache API
/// (used by WorkerSchedulerService and StateHandler) on top of:
///   * L1 = <see cref="LocalFsChunkCache"/>: per-session, tmpfs, byte-LRU
///     (default 20 GB), with at-write eviction and periodic sweep.
///   * L2 = <see cref="IContentChunkStore"/> (PgChunkCache): per-content-hash,
///     PG-backed, age×idle/use_count LRU (default 50 GB).
///
/// Save path: write-through to L1 and L2 simultaneously. L1 is the hot fast
/// path; L2 is the durable best-effort. L1 evictions do NOT need to demote
/// to L2 because L2 was already populated at write time.
///
/// Read path: L1 first (per session+hash); on miss, L2 by content hash.
/// The L2 read is best-effort: a PG outage or L2 throw returns null (not
/// an exception) so the caller falls through to the Store. This matches
/// the write-path best-effort contract.
/// </summary>
public sealed class LocalChunkCache : IChunkCache
{
    private readonly LocalFsChunkCache _l1;
    private readonly IContentChunkStore? _l2;
    private bool _disposed;

    /// <summary>New: L1 + optional L2.</summary>
    public LocalChunkCache(LocalFsChunkCache l1, IContentChunkStore? l2 = null)
    {
        _l1 = l1 ?? throw new ArgumentNullException(nameof(l1));
        _l2 = l2;
    }

    /// <summary>Back-compat ctor (L1 only, no L2). Used by tests and ad-hoc
    /// local runs that don't want a PG dependency.</summary>
    public LocalChunkCache(string cacheDir, int maxSessions)
        : this(new LocalFsChunkCache(cacheDir, MaxBytesFromSessions(maxSessions)), null)
    {
    }

    private static long MaxBytesFromSessions(int maxSessions)
    {
        // Back-compat: 50 sessions × 1 GB ≈ 50 GB cap. The byte-budget
        // ctor is the source of truth going forward; this is just so old
        // call sites that pass a session count keep working.
        return (long)maxSessions * 1024L * 1024L * 1024L;
    }

    public long L1UsedBytes => _l1.L1UsedBytes;

    public Task SaveHashesAsync(string sessionId, List<string> hashes, CancellationToken ct)
        => _l1.SaveHashesAsync(sessionId, hashes, ct);

    public async Task SaveChunkDataAsync(string sessionId, string hash, byte[] chunkData, CancellationToken ct)
    {
        await _l1.SaveChunkDataAsync(sessionId, hash, chunkData, ct);
        // Write-through to L2 (best-effort; L2 failures must not break the
        // foreground save path). L2 errors are counted as a metric; the
        // Store has the durable copy anyway.
        if (_l2 is not null && chunkData is not null && chunkData.Length > 0)
        {
            try
            {
                await _l2.PutAsync(hash, chunkData, ct);
            }
            catch
            {
                // Best-effort; the L1 has the chunk and the Store has the
                // durable copy. The L2 will catch up on the next read of
                // this chunk (miss → fall through to Store) or on the next
                // warm-up after a Core restart.
            }
        }
    }

    public void SaveChunkData(string sessionId, string hash, byte[] chunkData)
    {
        _l1.SaveChunkData(sessionId, hash, chunkData);
        if (_l2 is not null && chunkData is not null && chunkData.Length > 0)
        {
            _ = Task.Run(async () =>
            {
                try { await _l2.PutAsync(hash, chunkData, CancellationToken.None); }
                catch { /* best-effort */ }
            });
        }
    }

    public async Task<byte[]?> GetChunkDataAsync(string sessionId, string hash, CancellationToken ct)
    {
        var hit = await _l1.GetChunkDataAsync(sessionId, hash, ct);
        if (hit is not null)
        {
            ChunkCacheMetrics.L1Hits.Inc();
            return hit;
        }
        ChunkCacheMetrics.L1Misses.Inc();
        if (_l2 is null) return null;
        // L1 miss → L2 by content hash. The PgChunkCache.GetAsync swallows
        // its own errors and returns null, so this is the second layer of
        // defense for a dead L2. The null falls through to the Store.
        try
        {
            return await _l2.GetAsync(hash, ct);
        }
        catch
        {
            return null;
        }
    }

    public Task<List<string>> LoadHashesAsync(string sessionId, CancellationToken ct)
        => _l1.LoadHashesAsync(sessionId, ct);

    public bool HasCachedHashes(string sessionId) => _l1.HasCachedHashes(sessionId);

    public bool HasChunkData(string sessionId, string hash) => _l1.HasChunkData(sessionId, hash);

    public Task ClearAsync(string sessionId) => _l1.ClearAsync(sessionId);

    public Task<int> EvictLRUAsync() => _l1.EvictLRUAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _l1.Dispose();
        _l2?.Dispose();
    }
}
