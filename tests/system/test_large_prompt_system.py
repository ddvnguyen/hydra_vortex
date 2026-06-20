"""
System tests for large prompt handling through Coordinator HTTP API.

Simulates coding agent behavior: large context window initial prompt,
then shorter follow-up continuation. Verifies metrics on both llama-servers.

Requires:
  - All 6 services running (llama RTX :8080, P100 :8086, Store, Agents, Coordinator)
"""

import os
from uuid import uuid4

import httpx
import pytest

COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")
LLAMA_RTX_URL = os.environ.get("LLAMA_RTX_URL", "http://localhost:8080")
LLAMA_P100_URL = os.environ.get("LLAMA_P100_URL", "http://192.168.122.21:8086")

_CHARS_PER_TOKEN = 3.0

_SEED_PARAS = [
    "Software engineering encompasses requirements gathering, system design, implementation, testing, deployment, and maintenance. Each phase requires careful planning and execution to ensure quality outcomes.",
    "Database indexing strategies significantly impact query performance. B-tree indexes excel at range queries, while hash indexes optimize point lookups. Understanding access patterns guides index selection.",
    "Container orchestration platforms automate deployment, scaling, and management of containerized applications. Kubernetes provides service discovery, load balancing, and automated rollouts.",
    "Distributed systems require careful handling of consistency, availability, and partition tolerance. The CAP theorem states that a distributed system can only guarantee two of these three properties simultaneously.",
    "API design best practices include consistent naming conventions, proper versioning strategies, comprehensive error handling, and thorough documentation. RESTful APIs should leverage HTTP methods correctly.",
    "Test-driven development writes tests before production code, ensuring requirements are clearly understood. The red-green-refactor cycle promotes incremental development and robust test coverage.",
    "Network protocols like TCP provide reliable, ordered delivery of data between applications. UDP offers lower latency but no delivery guarantees, making it suitable for real-time applications.",
    "Microservices architecture decomposes applications into independently deployable services communicating over well-defined APIs. Each service focuses on a specific business capability.",
    "Caching strategies improve application performance by storing frequently accessed data in fast storage layers. Common patterns include cache-aside, write-through, and write-behind caching.",
    "Authentication and authorization are fundamental security concerns. OAuth 2.0 provides delegated access, while JWT tokens enable stateless authentication across distributed systems.",
    "Monitoring and observability are critical for production systems. Metrics, logs, and traces provide visibility into system behavior, enabling rapid incident response and performance optimization.",
    "Machine learning pipelines transform raw data into trained models through stages of collection, preprocessing, feature engineering, training, evaluation, and deployment.",
    "Concurrency control mechanisms prevent race conditions in multi-threaded applications. Mutexes, semaphores, and atomic operations provide different levels of thread safety guarantees.",
    "Cloud infrastructure patterns include lift-and-shift migration, re-platforming with managed services, and cloud-native architecture using serverless computing and managed databases.",
    "Code review practices improve code quality through peer examination. Reviewers check for correctness, maintainability, performance, security, and adherence to team coding standards.",
    "Load balancing distributes incoming traffic across multiple servers to ensure reliability and performance. Algorithms include round-robin, least connections, and consistent hashing.",
    "Data serialization formats like Protocol Buffers and Apache Avro provide efficient binary encoding with schema evolution support, making them suitable for inter-service communication.",
    "Dead letter queues handle messages that cannot be processed successfully. They isolate problematic messages for analysis while allowing the main processing pipeline to continue uninterrupted.",
    "Circuit breaker patterns prevent cascading failures in distributed systems by detecting when a downstream service is unhealthy and failing fast instead of waiting for timeouts.",
    "Infrastructure as code manages cloud resources through declarative configuration files. Tools like Terraform and Pulumi enable version-controlled, reproducible infrastructure deployment.",
    "Reactive programming models handle asynchronous data streams and propagate changes through functional transformations. This approach excels in event-driven and real-time applications.",
    "Feature flags enable safe deployment of new functionality by toggling features on or off without code changes. They support canary releases, A/B testing, and gradual rollouts.",
    "Rate limiting protects APIs from abuse by restricting the number of requests a client can make within a time window. Common algorithms include token bucket and sliding window.",
    "Message queues decouple producers and consumers, enabling asynchronous communication and load leveling. RabbitMQ, Apache Kafka, and Amazon SQS are popular message broker implementations.",
    "Search indexing builds inverted data structures for fast full-text retrieval. Elasticsearch and Apache Solr provide distributed search capabilities with relevance scoring and faceted navigation.",
    "Pipeline automation reduces manual effort in software delivery. Continuous integration builds and tests code changes automatically, while continuous deployment pushes verified changes to production.",
    "Data partitioning strategies distribute large datasets across multiple nodes for scalability. Horizontal partitioning splits rows across shards, while vertical partitioning separates columns.",
    "Service mesh implementations like Istio and Linkerd provide observability, traffic management, and security for microservice communication without requiring application code changes.",
    "WebAssembly enables high-performance code execution in web browsers, supporting multiple languages compiled to a common binary format. It unlocks new possibilities for web applications.",
    "Chaos engineering proactively tests system resilience by introducing controlled failures. Experiments verify that systems handle unexpected conditions without degrading user experience.",
    "Event sourcing stores state changes as an append-only event log, enabling audit trails, temporal queries, and event-driven architectures. The current state is derived by replaying events.",
    "Configuration management tools like Ansible and Puppet automate server provisioning and application deployment, ensuring consistent environments across development, staging, and production.",
    "Connection pooling reuses database connections to reduce the overhead of establishing new connections. Pool size must balance resource usage against concurrent workload demands.",
    "GraphQL provides a flexible query language for APIs, allowing clients to request exactly the data they need. This reduces over-fetching and under-fetching common in REST APIs.",
    "Time-series databases optimize storage and querying for timestamped data points. They excel at handling monitoring metrics, sensor data, and financial market data at scale.",
]


def get_output_text(msg: dict) -> str:
    return msg.get("content", "") or msg.get("reasoning_content", "")


def generate_text(approx_tokens: int) -> str:
    target_chars = int(approx_tokens * _CHARS_PER_TOKEN)
    parts: list[str] = []
    length = 0
    while length < target_chars:
        for p in _SEED_PARAS:
            if length >= target_chars:
                break
            parts.append(p)
            length += len(p) + 1
    return " ".join(parts)[:target_chars]


async def scrape_llama(base_url: str) -> dict:
    async with httpx.AsyncClient(timeout=10.0) as client:
        slots_resp = await client.get(f"{base_url}/slots")
        slots_resp.raise_for_status()
        metrics_resp = await client.get(f"{base_url}/metrics")
        metrics_resp.raise_for_status()
    metrics: dict[str, float] = {}
    for line in metrics_resp.text.strip().split("\n"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if " " in line:
            name, val = line.split(" ", 1)
            try:
                metrics[name] = float(val)
            except ValueError:
                pass
    return {"slots": slots_resp.json(), "metrics": metrics}


async def do_completion(
    coord_url: str,
    messages: list[dict],
    session_id: str | None = None,
    stream: bool = False,
    max_tokens: int = 100,
    timeout: float = 300.0,
) -> httpx.Response:
    body: dict = {
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": 0,
        "stream": stream,
    }
    if session_id:
        body["session_id"] = session_id
    async with httpx.AsyncClient(timeout=timeout) as client:
        return await client.post(f"{coord_url}/v1/chat/completions", json=body)


PROMPT_SIZES = [
    (8_000, 2_000, 120),
    (8_000, 4_000, 120),
    (16_000, 2_000, 180),
    (16_000, 4_000, 180),
    (48_000, 2_000, 420),
    (48_000, 4_000, 420),
]


@pytest.mark.system
@pytest.mark.asyncio
@pytest.mark.parametrize(
    "prompt_tokens,continue_tokens,timeout_sec",
    PROMPT_SIZES,
    ids=[f"{p//1000}k_prompt_{c//1000}k_continue" for p, c, _ in PROMPT_SIZES],
)
async def test_large_prompt_with_metrics_and_continuation(
    prompt_tokens: int,
    continue_tokens: int,
    timeout_sec: int,
):
    session_id = f"system-lg-{uuid4().hex[:12]}"

    init_prompt = generate_text(prompt_tokens)
    continue_prompt = generate_text(continue_tokens)

    # ── Scrape before metrics ────────────────────────────────────────────
    rtx_before = await scrape_llama(LLAMA_RTX_URL)
    await scrape_llama(LLAMA_P100_URL)
    rtx_ptt_before = rtx_before["metrics"].get("llamacpp:prompt_tokens_total", 0)
    rtx_tpt_before = rtx_before["metrics"].get("llamacpp:tokens_predicted_total", 0)

    # ── Send initial prompt ──────────────────────────────────────────────
    init_resp = await do_completion(
        COORD_URL,
        [{"role": "user", "content": init_prompt}],
        session_id=session_id,
        max_tokens=100,
        timeout=float(timeout_sec),
    )
    assert init_resp.status_code == 200, f"Initial completion failed: {init_resp.text[:200]}"
    init_body = init_resp.json()
    assert "choices" in init_body, f"No choices in init response: {init_body}"
    assert len(init_body["choices"]) > 0
    assert get_output_text(init_body["choices"][0]["message"]), "Empty output in init response"

    # ── Scrape after metrics + verify ────────────────────────────────────
    rtx_after = await scrape_llama(LLAMA_RTX_URL)
    p100_after = await scrape_llama(LLAMA_P100_URL)
    rtx_ptt_after = rtx_after["metrics"].get("llamacpp:prompt_tokens_total", 0)
    rtx_tpt_after = rtx_after["metrics"].get("llamacpp:tokens_predicted_total", 0)
    ptt_diff = rtx_ptt_after - rtx_ptt_before
    tpt_diff = rtx_tpt_after - rtx_tpt_before

    # Verify RTX processed tokens (accounting for KV cache reuse)
    actual_prompt_tokens = init_body.get("usage", {}).get("prompt_tokens", 0)
    cached_tokens = init_body.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
    assert actual_prompt_tokens > 0, f"No prompt_tokens in response usage: {init_body}"
    # Either prompt_tokens_total increased OR tokens were served from cache
    assert ptt_diff > 0 or cached_tokens > 0, (
        f"RTX prompt_tokens_total increased by {ptt_diff:.0f} "
        f"with {cached_tokens} cached tokens — no evidence of processing"
    )
    assert tpt_diff > 0, (
        f"RTX tokens_predicted_total did not increase (diff={tpt_diff:.0f})"
    )

    # Verify no requests processing after completion
    for name, after in [("rtx", rtx_after), ("p100", p100_after)]:
        req_proc = after["metrics"].get("llamacpp:requests_processing", 0)
        assert req_proc == 0, f"{name} llama has {req_proc} requests still processing"

    # ── Send continuation with same session_id ───────────────────────────
    continue_messages = [
        {"role": "user", "content": init_prompt},
        {"role": "assistant", "content": get_output_text(init_body["choices"][0]["message"])},
        {"role": "user", "content": continue_prompt},
    ]
    cont_resp = await do_completion(
        COORD_URL,
        continue_messages,
        session_id=session_id,
        max_tokens=100,
        timeout=float(timeout_sec),
    )
    assert cont_resp.status_code == 200, f"Continuation failed: {cont_resp.text[:200]}"
    cont_body = cont_resp.json()
    assert "choices" in cont_body
    assert get_output_text(cont_body["choices"][0]["message"]), "Empty output in continuation"

    # ── Scrape metrics after continuation ────────────────────────────────
    rtx_cont = await scrape_llama(LLAMA_RTX_URL)
    rtx_tpt_cont = rtx_cont["metrics"].get("llamacpp:tokens_predicted_total", 0)
    cont_tpt_diff = rtx_tpt_cont - rtx_tpt_after
    assert cont_tpt_diff > 0, (
        f"RTX tokens_predicted_total did not increase after continuation "
        f"(diff={cont_tpt_diff:.0f})"
    )

    # Cleanup: evict session
    try:
        async with httpx.AsyncClient(timeout=5) as client:
            await client.delete(f"{COORD_URL}/sessions/{session_id}")
    except Exception:
        pass
