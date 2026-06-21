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

    // Non-labelled mirror of RequestsTotal. The labelled counter's Value
    // property in prometheus-net 8.x only returns the un-labelled child
    // value, not the sum across all (node, reason) combinations. We need
    // a flat total for the /status endpoint (issue #274), so we increment
    // this alongside the labelled one at every Inc site in
    // WorkerSchedulerService.cs.
    public static readonly Counter RequestsTotalAll = Metrics.CreateCounter(
        "hydra_requests_total_all", "Total requests routed (no labels, for /status)");

    public static readonly Counter UpstreamTimeouts = Metrics.CreateCounter(
        "hydra_upstream_timeouts_total", "Prefill/complete timeouts");

    // Engine RPC prefill fell back to the HTTP path. Issues #273 (chat)
    // and #279 (prefill) — see PrefillAsync in WorkerSchedulerService.
    // Non-zero rate means the deployed llama-server binary is out of date
    // with the C# engine integration, or a regression in the engine RPC.
    public static readonly Counter EnginePrefillFallbacks = Metrics.CreateCounter(
        "hydra_engine_prefill_fallbacks_total", "Engine RPC prefill fell back to HTTP", "node", "reason");

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

    // ── Two-engine "work together" (PIPELINE / COMBINED) ──
    public static readonly Counter MultiEngineAttempts = Metrics.CreateCounter(
        "hydra_multiengine_attempts_total", "Requests that attempted a multi-engine mode", "node", "mode");
    public static readonly Counter MultiEngineActive = Metrics.CreateCounter(
        "hydra_multiengine_active_total", "Requests that activated a multi-engine mode", "node", "mode");
    public static readonly Counter MultiEngineFallback = Metrics.CreateCounter(
        "hydra_multiengine_fallback_total", "Multi-engine activations that fell back to solo", "node", "mode", "reason");
    public static readonly Gauge MultiEngineActiveSessions = Metrics.CreateGauge(
        "hydra_multiengine_active_sessions", "Currently active multi-engine sessions", "mode");
    public static readonly Gauge EnginePeerUp = Metrics.CreateGauge(
        "hydra_engine_peer_up", "Whether a head engine's peer is reachable (1/0)", "node", "peer");

    public static readonly Gauge MainQueueDepth = Metrics.CreateGauge(
        "hydra_main_queue_depth", "Pending requests in main classifier queue");
    public static readonly Gauge PrefillQueueDepth = Metrics.CreateGauge(
        "hydra_prefill_queue_depth", "Pending requests in prefill queue");
    public static readonly Gauge DecodeQueueDepth = Metrics.CreateGauge(
        "hydra_decode_queue_depth", "Pending requests in decode queue");

    // Issue #284: time from NotifyStreamComplete start to slot lease release.
    // Should be < 1s for cold sessions; up to 60-120s for warm sessions where
    // the bg-save (StateGet + Put to Store) is awaited before release.
    // A p99 > 5 min indicates the bg-save is starving the slot pool.
    public static readonly Histogram SlotReleaseLag = Metrics.CreateHistogram(
        "hydra_slot_release_lag_seconds", "Time to release slot after request end",
        new[] { "node" });

    // Issue #284: count of times NotifyStreamComplete hit a non-fatal error
    // (e.g. EmitTimeline threw). The lease-release is in a try/catch+finally
    // so the slot is still released, but the operator should know.
    public static readonly Counter SlotReleaseErrors = Metrics.CreateCounter(
        "hydra_slot_release_errors_total", "NotifyStreamComplete threw before lease release (slot was still released by finally)");

    // Issue #286: bg-save (Store Put) errors. The Put is now fire-and-forget
    // so a failure is logged + counted but does not block slot release.
    public static readonly Counter SaveKvErrors = Metrics.CreateCounter(
        "hydra_save_kv_errors_total", "Background bg-save Put failed (state lost; next resume will be cold)");

    // Issue #286: duration of the fire-and-forget bg-save Put. Useful for
    // sizing the slow-disk / write-bandwidth problem separately from the
    // slot-release-lag metric (which only captures the StateGet RPC).
    public static readonly Histogram SaveKvAsyncDuration = Metrics.CreateHistogram(
        "hydra_save_kv_async_seconds", "Background bg-save Put duration",
        new[] { "result" });

    // ── M-Perf.9 / Issue #289: cross-model KV safety ──
    public static readonly Counter CrossModelKvAborted = Metrics.CreateCounter(
        "hydra_cross_model_kv_aborted_total",
        "Restores aborted because slot model_hash differs from stored KV model_hash",
        new CounterConfiguration { LabelNames = new[] { "worker" } });

    public static readonly Counter CrossModelKvWarned = Metrics.CreateCounter(
        "hydra_cross_model_kv_warned_total",
        "Restores allowed despite model_hash mismatch (ALLOW_CROSS_MODEL_KV_REUSE=true)",
        new CounterConfiguration { LabelNames = new[] { "worker" } });

    public static readonly Counter CrossModelKvSkipped = Metrics.CreateCounter(
        "hydra_cross_model_kv_skipped_total",
        "Cross-model check skipped: at least one model_hash empty (pre-#289 data or META failure)",
        new CounterConfiguration { LabelNames = new[] { "worker" } });

    public static readonly Counter CrossModelKvProceeded = Metrics.CreateCounter(
        "hydra_cross_model_kv_proceeded_total",
        "Cross-model check passed: stored and slot model_hash match",
        new CounterConfiguration { LabelNames = new[] { "worker" } });

    public static readonly Counter ModelFallbackTotal = Metrics.CreateCounter(
        "hydra_model_fallback_total",
        "Engine PREFILL fallback: requested model unknown, used resident model",
        new CounterConfiguration { LabelNames = new[] { "worker", "requested" } });

    // ── M-Perf / Issue #306: bench-harness observability ──
    // Age (in seconds) of the oldest warm-lease currently held. A non-zero
    // value that grows over time is an early signal that the eviction
    // watchdog is not reclaiming (S10). Set in
    // WorkerSchedulerService.ReportQueueDepthAsync.
    public static readonly Gauge WarmLeaseMaxAge = Metrics.CreateGauge(
        "hydra_warm_lease_max_age_seconds",
        "Age of the oldest warm lease (seconds). 0 when no leases are held.");

    // Age (in seconds) of the item that was just dequeued from the head of
    // the main classifier queue. Captured per-dequeue in
    // ClassifyItemAsync. A persistently high value means requests are
    // queueing behind a slow worker (backpressure / starvation).
    public static readonly Gauge QueueHeadAge = Metrics.CreateGauge(
        "hydra_queue_head_age_seconds",
        "Age (s) of the most recently dequeued main-queue head. 0 when idle.");

    // Number of warm leases reclaimed by the eviction watchdog since
    // process start. Incremented in SessionEvictionService after a
    // successful EvictWarmSessionAsync. Non-zero rate in steady state is
    // expected; a non-zero absolute value with a warm-hit rate of 0 is
    // the canary for S10.
    public static readonly Counter StuckWarmLeases = Metrics.CreateCounter(
        "hydra_stuck_warm_leases_total",
        "Warm leases reclaimed by the eviction watchdog since process start.");
}
