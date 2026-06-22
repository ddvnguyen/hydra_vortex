namespace Hydra.Core;

/// <summary>
/// Content-addressed L2 chunk store. The L2 caches chunks by their SHA-256
/// hash (NOT by session), so it survives a Coordinator restart and serves
/// reads from any session whose chunks happen to share hashes (e.g. the
/// system-prompt prefix, recurrent context checkpoints).
///
/// Implemented by PgChunkCache (default) and, in a follow-up, by
/// SqliteChunkCache / S3ChunkCache / etc. The L2 has its own byte-budget
/// eviction (age×idle/use_count score) — it is NOT a session cache.
/// </summary>
public interface IContentChunkStore : IDisposable
{
    /// <summary>Insert a chunk by content hash. Idempotent: if the hash is
    /// already present, last_used is bumped but bytes and created_at are
    /// preserved (the chunk is the same content-addressed blob — first writer
    /// wins the age, which is what we want for GC scoring).</summary>
    Task PutAsync(string hash, byte[] data, CancellationToken ct);

    /// <summary>Read a chunk by content hash. Bumps last_used and use_count.
    /// Returns null on miss.</summary>
    Task<byte[]?> GetAsync(string hash, CancellationToken ct);

    /// <summary>True iff the hash is present.</summary>
    Task<bool> ExistsAsync(string hash, CancellationToken ct);

    /// <summary>Current total bytes stored (includes TOAST for PG). Used by
    /// the hard-trigger GC check on every Put.</summary>
    Task<long> GetUsedBytesAsync(CancellationToken ct);

    /// <summary>Evict the lowest-priority chunks until used bytes ≤ targetBytes.
    /// Soft-triggered every L2GcIntervalSeconds; hard-triggered by the caller
    /// when a Put pushes used bytes above the cap.</summary>
    /// <returns>Number of chunks evicted.</returns>
    Task<int> EnforceCapacityAsync(long targetBytes, int batchSize, CancellationToken ct);
}
