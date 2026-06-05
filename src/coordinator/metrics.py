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


async def metrics_endpoint(request: Request) -> Response:
    return Response(
        content=generate_latest(),
        media_type=CONTENT_TYPE_LATEST,
    )
