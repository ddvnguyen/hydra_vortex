using Prometheus;

namespace Hydra.Core;

/// <summary>
/// Prometheus metrics for the coordinator.
/// Mirrors Python's metrics.py.
/// </summary>
internal static class CoordinatorMetrics
{
    public static readonly Counter RequestsTotal = Metrics.CreateCounter(
        "hydra_requests_total", "Total requests routed", "node", "reason");

    public static readonly Counter UpstreamTimeouts = Metrics.CreateCounter(
        "hydra_upstream_timeouts_total", "Prefill/complete timeouts");

    public static readonly Counter MigrationsTotal = Metrics.CreateCounter(
        "hydra_migrations_total", "Total migrations", "from_node", "to_node");

    public static readonly Histogram MigrationLatency = Metrics.CreateHistogram(
        "hydra_migration_latency_seconds", "Migration duration", new[] { "from_node", "to_node" });

    public static readonly Gauge ActiveSessions = Metrics.CreateGauge(
        "hydra_active_sessions", "Active session count per node", "node");

    public static readonly Gauge WorkerBusySeconds = Metrics.CreateGauge(
        "hydra_worker_busy_seconds", "Worker busy duration (leak detection)", "node");

    public static readonly Counter CrossNodeAffinityTotal = Metrics.CreateCounter(
        "hydra_cross_node_affinity_total", "Cross-node affinity dispatches");

    public static readonly Gauge MixPrecisionEnabled = Metrics.CreateGauge(
        "hydra_mix_precision_enabled", "Whether mix-precision is on");

    public static readonly Histogram MixPrecisionPhaseSeconds = Metrics.CreateHistogram(
        "hydra_mix_precision_phase_seconds", "Mix-precision phase timing", "phase");

    public static readonly Histogram ModelLoadDuration = Metrics.CreateHistogram(
        "hydra_model_load_seconds", "Model load time", new[] { "model" });

    public static readonly Histogram QueueWaitDuration = Metrics.CreateHistogram(
        "hydra_queue_wait_seconds", "Time from request enqueue to first dispatch");

    public static readonly Histogram PrefillDuration = Metrics.CreateHistogram(
        "hydra_prefill_seconds", "Prefill time", new[] { "node", "session_type" });

    public static readonly Histogram DecodeDuration = Metrics.CreateHistogram(
        "hydra_decode_seconds", "Decode time", new[] { "node", "session_type" });

    public static readonly Histogram SaveKvDuration = Metrics.CreateHistogram(
        "hydra_save_kv_seconds", "KV save time", new[] { "node", "session_type" });

    public static readonly Histogram RestoreKvDuration = Metrics.CreateHistogram(
        "hydra_restore_kv_seconds", "KV restore time", new[] { "node", "session_type" });

    public static readonly Counter WarmSessionStarts = Metrics.CreateCounter(
        "hydra_warm_session_starts", "Warm slot reuse count");

    public static readonly Counter ColdSessionStarts = Metrics.CreateCounter(
        "hydra_cold_session_starts", "Cold prefill required count");

    public static readonly Counter MigrationSessionStarts = Metrics.CreateCounter(
        "hydra_migration_session_starts", "Migration path count");

    public static readonly Counter CacheHits = Metrics.CreateCounter(
        "hydra_cache_hits_total", "Cache hits (prefix or KV)");

    public static readonly Counter CacheMisses = Metrics.CreateCounter(
        "hydra_cache_misses_total", "Cache misses (prefix or KV)");

    public static readonly Counter PrefixSaves = Metrics.CreateCounter(
        "hydra_prefix_saves_total", "Prefix checkpoints saved to Store");

    public static readonly Histogram RequestLatency = Metrics.CreateHistogram(
        "hydra_request_latency_seconds", "End-to-end request latency", new[] { "node", "route_type" });

    public static readonly Gauge MainQueueDepth = Metrics.CreateGauge(
        "hydra_main_queue_depth", "Pending requests in main classifier queue");
    public static readonly Gauge PrefillQueueDepth = Metrics.CreateGauge(
        "hydra_prefill_queue_depth", "Pending requests in prefill queue");
    public static readonly Gauge DecodeQueueDepth = Metrics.CreateGauge(
        "hydra_decode_queue_depth", "Pending requests in decode queue");
}
