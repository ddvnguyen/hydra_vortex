using Prometheus;

namespace Hydra.Store;

internal static class StoreMetrics
{
    // Existing metrics (keep for backward compatibility)
    public static readonly Counter OpsTotal = Metrics.CreateCounter(
        "hydra_store_ops_total", "Store operations by type", "op");

    public static readonly Counter BytesStored = Metrics.CreateCounter(
        "hydra_store_bytes_stored", "Total bytes written to store");

    public static readonly Counter BytesSent = Metrics.CreateCounter(
        "hydra_store_bytes_sent", "Total bytes read from store");

    public static readonly Gauge FileCount = Metrics.CreateGauge(
        "hydra_store_files_count", "Number of stored files");

    public static readonly Histogram OpDuration = Metrics.CreateHistogram(
        "hydra_store_op_duration_seconds", "Operation duration in seconds", "op");

    public static readonly Counter ChunksNew = Metrics.CreateCounter(
        "hydra_store_chunks_new", "New unique chunks stored");

    public static readonly Counter ChunksDeduped = Metrics.CreateCounter(
        "hydra_store_chunks_deduped", "Deduplicated (already existed) chunks");

    public static readonly Counter ChunksSent = Metrics.CreateCounter(
        "hydra_store_chunks_sent", "Chunks sent in GET_CHUNKED responses");

    public static readonly Counter ChunksRemoved = Metrics.CreateCounter(
        "hydra_store_chunks_removed", "Chunks removed by GC");

    // New observability metrics

    /// <summary>
    /// KV cache bytes saved per store operation, with node and session_type labels.
    /// Used to correlate with Agent save/restore duration for timeline views.
    /// </summary>
    public static readonly Counter KVPutBytesTotal = Metrics.CreateCounter(
        "hydra_store_kv_put_bytes_total", "KV cache bytes written during chunked saves", "node", "op");

    /// <summary>
    /// KV cache bytes transferred per get operation, with size buckets.
    /// </summary>
    public static readonly Counter KVGetBytesTotal = Metrics.CreateCounter(
        "hydra_store_kv_get_bytes_total", "KV cache bytes read during chunked restores", "node", "op");

    /// <summary>
    /// Duration of chunk-level KV operations (put_chunked, get_chunked, push_chunks).
    /// Labels: op (put_chunked/get_chunked/push_chunks/sync_missing), node.
    /// </summary>
    public static readonly Histogram ChunkOpDuration = Metrics.CreateHistogram(
        "hydra_store_chunk_op_duration_seconds", "Chunk-level KV operation duration in seconds", "op", "node");

    /// <summary>
    /// Number of chunks involved in each chunked KV operation.
    /// Labels: op, size_bucket (0-1MB, 1-5MB, 5-10MB, 10+MB).
    /// </summary>
    public static readonly Histogram ChunkCount = Metrics.CreateHistogram(
        "hydra_store_chunk_count", "Number of chunks per KV operation", "op", "size_bucket");

    /// <summary>
    /// Gauge for candidate hashes processed in sync_missing operations.
    /// Labels: state (candidate_count, missing_count).
    /// </summary>
    public static readonly Gauge SyncCandidateCount = Metrics.CreateGauge(
        "hydra_store_sync_candidate_count", "Number of candidate hashes per sync_missing operation", "node", "state");

    /// <summary>
    /// Gauge for missing hashes returned in sync_missing operations.
    /// Labels: state (candidate_count, missing_count).
    /// </summary>
    public static readonly Gauge SyncMissingCount = Metrics.CreateGauge(
        "hydra_store_sync_missing_count", "Number of missing hashes per sync_missing operation", "node", "state");
}
