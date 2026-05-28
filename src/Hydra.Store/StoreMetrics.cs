using Prometheus;

namespace Hydra.Store;

internal static class StoreMetrics
{
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
}
