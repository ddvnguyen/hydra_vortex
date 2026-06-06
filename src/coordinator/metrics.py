from prometheus_client import Counter, Histogram, Gauge, generate_latest, CONTENT_TYPE_LATEST
from starlette.requests import Request
from starlette.responses import Response

requests_total = Counter(
    "hydra_requests_total", "Total requests routed", ["node", "reason"],
)



upstream_timeouts_total = Counter(
    "hydra_upstream_timeouts_total",
    "Upstream completion/prefill requests that hit the read timeout (#134)",
)

migrations_total = Counter(
    "hydra_migrations_total", "Total migrations", ["from_node", "to_node"],
)

migration_latency = Histogram(
    "hydra_migration_latency_seconds", "Migration duration in seconds",
    ["from_node", "to_node"],
)

active_sessions = Gauge(
    "hydra_active_sessions", "Active sessions", ["node"],
)


worker_busy_seconds = Gauge(
    "hydra_worker_busy_seconds",
    "Seconds worker has been in busy state (0 = free). Monotonically increasing = tracker leak.",
    ["node"],
)

cross_node_affinity_total = Counter(
    "hydra_cross_node_affinity_total",
    "Cross-node affinity dispatches (prefill worker never released on success)",
)


def set_worker_busy_metrics(scheduler) -> None:
    for w in scheduler._config.workers:
        worker_busy_seconds.labels(node=w.name).set(
            scheduler._tracker.elapsed_seconds(w.name)
        )

# Phase-level latency histograms for session timeline visualization
verify_warm_slot_duration = Histogram(
    "hydra_verify_warm_slot_duration_seconds",
    "Duration of warm slot verification (HTTP GET /slots)",
    ["node", "result"],  # result: success, timeout, error
    buckets=(0.01, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, float("inf")),
)

prefill_duration = Histogram(
    "hydra_prefill_duration_seconds",
    "Prefill duration by node and session type",
    ["node", "session_type"],  # session_type: warm/cold/migration
    buckets=(0.1, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 60.0, float("inf")),
)

decode_duration = Histogram(
    "hydra_decode_duration_seconds",
    "Decode (completion) duration by node and session type",
    ["node", "session_type"],  # session_type: warm/cold/migration
    buckets=(0.1, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0, 32.0, float("inf")),
)

save_kv_duration = Histogram(
    "hydra_save_kv_duration_seconds",
    "KV cache save (store push) duration by node and session type",
    ["node", "session_type"],  # session_type: warm/cold/migration
    buckets=(1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 64.0, float("inf")),
)

restore_kv_duration = Histogram(
    "hydra_restore_kv_duration_seconds",
    "KV cache restore (store pull) duration by node and session type",
    ["node", "session_type"],  # session_type: warm/cold/migration
    buckets=(1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 64.0, float("inf")),
)

n_past_guard_duration = Histogram(
    "hydra_n_past_guard_duration_seconds",
    "Duration spent in n_past guard logic (checking/restoring)",
    ["action"],  # action: check, restore, skip
)

# Counters for session warm/cold transitions
warm_session_starts = Counter(
    "hydra_warm_session_starts_total",
    "Count of session starts that reused a warm slot",
)

cold_session_starts = Counter(
    "hydra_cold_session_starts_total",
    "Count of session starts that required cold prefill",
)

migration_session_starts = Counter(
    "hydra_migration_session_starts_total",
    "Count of session starts that used cross-GPU migration (warm slot + KV restore)",
)

# Prefix cache hit/miss counters for timeline visualization
prefix_cache_hits = Counter(
    "hydra_prefix_cache_hits_total",
    "Count of prefix KV cache hits (cached prefix reused)",
)

prefix_cache_misses = Counter(
    "hydra_prefix_cache_misses_total",
    "Count of prefix KV cache misses (full prefill required)",
)

# Gauge for n_past values during warm slot verification
n_past_value = Gauge(
    "hydra_n_past_value",
    "Current n_past value on a slot (from /slots response)",
    ["node", "slot_id"],
)


async def metrics_endpoint(request: Request) -> Response:
    return Response(
        content=generate_latest(),
        media_type=CONTENT_TYPE_LATEST,
    )
