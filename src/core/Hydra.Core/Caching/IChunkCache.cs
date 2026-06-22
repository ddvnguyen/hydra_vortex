namespace Hydra.Core;

/// <summary>
/// Per-session chunk cache facade. Preserves the legacy LocalChunkCache API
/// (used by WorkerSchedulerService.cs and StateHandler.cs) on top of the new
/// two-tier L1 (tmpfs, byte-LRU) + L2 (PG, age-LRU) implementation.
///
/// All session-scoped methods key on (sessionId, hash) — the L2 is consulted
/// by content hash only on a L1 miss, so the session index is not duplicated
/// in the durable layer.
/// </summary>
public interface IChunkCache : IDisposable
{
    /// <summary>Persist the ordered chunk-hash list for a session (L1 only).</summary>
    Task SaveHashesAsync(string sessionId, List<string> hashes, CancellationToken ct);

    /// <summary>Persist a single chunk body. Writes L1; if L1 is over cap, the
    /// oldest session is evicted and its chunks are demoted to L2 by content hash.</summary>
    Task SaveChunkDataAsync(string sessionId, string hash, byte[] chunkData, CancellationToken ct);

    /// <summary>Synchronous variant — used by ChunkHashTeeStream which is not async.</summary>
    void SaveChunkData(string sessionId, string hash, byte[] chunkData);

    /// <summary>Read a single chunk body. L1 first; on miss, L2 by content hash
    /// (a hit promotes the chunk back to L1).</summary>
    Task<byte[]?> GetChunkDataAsync(string sessionId, string hash, CancellationToken ct);

    /// <summary>Read the ordered chunk-hash list for a session (L1 only).</summary>
    Task<List<string>> LoadHashesAsync(string sessionId, CancellationToken ct);

    /// <summary>True iff the session has a persisted hash list (L1 only).</summary>
    bool HasCachedHashes(string sessionId);

    /// <summary>True iff (sessionId, hash) is present in the L1 session cache.
    /// Does not probe the L2 (we don't enumerate durable content for this lookup).</summary>
    bool HasChunkData(string sessionId, string hash);

    /// <summary>Drop the L1 entry for a session. The underlying chunks are
    /// preserved in L2 if other sessions (or the same session, after a restart)
    /// need them later.</summary>
    Task ClearAsync(string sessionId);

    /// <summary>Evict oldest L1 sessions until under cap. Returns the number of
    /// sessions evicted. L2 has its own background GC.</summary>
    Task<int> EvictLRUAsync();

    /// <summary>Current L1 bytes on disk. Surfaced as a metric.</summary>
    long L1UsedBytes { get; }
}
