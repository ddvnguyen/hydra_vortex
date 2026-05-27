from prometheus_client import Counter, Histogram, Gauge, generate_latest, CONTENT_TYPE_LATEST
from starlette.requests import Request
from starlette.responses import Response

requests_total = Counter(
    "hydra_requests_total", "Total requests routed", ["node", "reason"],
)

request_latency = Histogram(
    "hydra_request_latency_seconds", "Request latency in seconds", ["node"],
    buckets=(0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0, 60.0, float("inf")),
)

cache_hits_total = Counter(
    "hydra_cache_hits_total", "Total cache hit count",
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

store_ops_total = Counter(
    "hydra_store_ops_total", "Store operations by type", ["op"],
)

store_bytes_transferred = Counter(
    "hydra_store_bytes_transferred", "Store bytes transferred", ["direction"],
)

store_chunks_total = Gauge(
    "hydra_store_chunks_total", "Total chunks in store",
)

store_dedup_ratio = Gauge(
    "hydra_store_dedup_ratio", "Deduplication ratio",
)

agent_save_duration = Histogram(
    "hydra_agent_save_duration_seconds", "Agent save duration",
    buckets=(0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0, 60.0, float("inf")),
)

agent_restore_duration = Histogram(
    "hydra_agent_restore_duration_seconds", "Agent restore duration",
    buckets=(0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0, 60.0, float("inf")),
)

agent_slot_utilization = Gauge(
    "hydra_agent_slot_utilization", "Slot utilization by node", ["node"],
)


async def metrics_endpoint(request: Request) -> Response:
    return Response(
        content=generate_latest(),
        media_type=CONTENT_TYPE_LATEST,
    )
