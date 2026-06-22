using Prometheus;

namespace Hydra.Core.Caching;

/// <summary>
/// Prometheus metrics for the two-tier chunk cache. All gauges / counters
/// here are surfaced in the Hydra dashboard; alerts in
/// infra/prometheus/alerts.yml watch the l2_bytes gauge and the
/// l2_evicted_bytes_total counter.
/// </summary>
public static class ChunkCacheMetrics
{
    // ── L1 (LocalFsChunkCache, tmpfs) ───────────────────────────────────
    public static readonly Gauge L1Bytes =
        Prometheus.Metrics.CreateGauge(
            "hydra_chunk_cache_l1_bytes",
            "L1 chunk cache bytes on disk (tmpfs).");
    public static readonly Gauge L1Chunks =
        Prometheus.Metrics.CreateGauge(
            "hydra_chunk_cache_l1_chunks",
            "L1 chunk cache row count (one file per chunk).");
    public static readonly Counter L1Hits =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l1_hits_total",
            "L1 chunk cache hits (returned from L1 tmpfs).");
    public static readonly Counter L1Misses =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l1_misses_total",
            "L1 chunk cache misses (fell through to L2 or Store).");
    public static readonly Counter L1EvictedSessions =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l1_evicted_sessions_total",
            "Number of L1 sessions evicted by byte-budget LRU (sessions, not chunks).");
    public static readonly Counter L1EvictedBytes =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l1_evicted_bytes_total",
            "L1 bytes freed by byte-budget LRU.");

    // ── L2 (PgChunkCache, PG chunk_data_l2) ─────────────────────────────
    public static readonly Gauge L2Bytes =
        Prometheus.Metrics.CreateGauge(
            "hydra_chunk_cache_l2_bytes",
            "L2 chunk cache bytes (PG pg_total_relation_size, includes TOAST).");
    public static readonly Gauge L2Chunks =
        Prometheus.Metrics.CreateGauge(
            "hydra_chunk_cache_l2_chunks",
            "L2 chunk cache row count.");
    public static readonly Counter L2Hits =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l2_hits_total",
            "L2 chunk cache hits (returned from PG after L1 miss).");
    public static readonly Counter L2Misses =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l2_misses_total",
            "L2 chunk cache misses (fell through to Store).");
    public static readonly Counter L2Puts =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l2_puts_total",
            "L2 chunk cache successful Puts (write-through from the facade).");
    public static readonly Counter L2EvictedChunks =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l2_evicted_chunks_total",
            "L2 chunks evicted by age×idle/use_count GC.");
    public static readonly Counter L2EvictedBytes =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l2_evicted_bytes_total",
            "L2 bytes freed by GC.");
    public static readonly Counter L2GcRuns =
        Prometheus.Metrics.CreateCounter(
            "hydra_chunk_cache_l2_gc_runs_total",
            "Number of L2 GC cycles (soft + hard triggers).");
    public static readonly Histogram L2GcDuration =
        Prometheus.Metrics.CreateHistogram(
            "hydra_chunk_cache_l2_gc_duration_seconds",
            "Wall-clock per L2 GC cycle.",
            new HistogramConfiguration
            {
                Buckets = new[] { 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60 }
            });
    public static readonly Gauge L2OldestAge =
        Prometheus.Metrics.CreateGauge(
            "hydra_chunk_cache_l2_oldest_chunk_age_seconds",
            "Age of the oldest L2 chunk (proxy for cache churn).");
    public static readonly Gauge L2SizeCap =
        Prometheus.Metrics.CreateGauge(
            "hydra_chunk_cache_l2_size_cap_bytes",
            "Configured L2 cap (50 GB default).");
    public static readonly Gauge L2SizeHighWater =
        Prometheus.Metrics.CreateGauge(
            "hydra_chunk_cache_l2_size_high_water_bytes",
            "Configured L2 high-water mark (cap × low_water_ratio).");
}
