"""
Stress E2E tests for Coordinator HTTP API.

Tests concurrent request handling and cross-session consistency.

Requires:
  - All 6 services running (llama RTX :8080, P100 :8086, Store, Agents, Coordinator)
"""

import asyncio
import os
import time
from uuid import uuid4

import httpx
import pytest

COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")
LLAMA_RTX_URL = os.environ.get("LLAMA_RTX_URL", "http://localhost:8080")

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


async def do_completion(
    coord_url: str,
    messages: list[dict],
    session_id: str | None = None,
    stream: bool = False,
    max_tokens: int = 100,
    timeout: float = 120.0,
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


async def get_status(coord_url: str) -> dict:
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(f"{coord_url}/status")
        resp.raise_for_status()
        return resp.json()


# ── Tests ────────────────────────────────────────────────────────────────────


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_4_concurrent_completions():
    """
    Send 4 completion requests concurrently through Coordinator.
    Verifies all return valid responses and measures timing.
    """
    prompt_text = generate_text(2_000)
    messages = [{"role": "user", "content": prompt_text}]
    session_ids = [f"e2e-stress-{uuid4().hex[:12]}" for _ in range(4)]

    # ── Measure serial time (single request) ──────────────────────────
    ref_sid = f"e2e-stress-ref-{uuid4().hex[:12]}"
    t0 = time.monotonic()
    ref_resp = await do_completion(
        COORD_URL, messages, session_id=ref_sid,
    )
    serial_time = time.monotonic() - t0
    assert ref_resp.status_code == 200, f"Reference completion failed: {ref_resp.text[:200]}"
    ref_body = ref_resp.json()
    assert "choices" in ref_body
    # ── Measure concurrent time (4 requests simultaneously) ───────────
    async def send_one(sid: str) -> httpx.Response:
        return await do_completion(COORD_URL, messages, session_id=sid)

    t1 = time.monotonic()
    results = await asyncio.gather(*[send_one(sid) for sid in session_ids], return_exceptions=True)
    concurrent_time = time.monotonic() - t1

    # ── Assertions ─────────────────────────────────────────────────────
    failed = []
    for i, (sid, result) in enumerate(zip(session_ids, results)):
        if isinstance(result, Exception):
            failed.append(f"#{i} ({sid}): {result}")
            continue
        assert result.status_code == 200, f"#{i} ({sid}) status={result.status_code}: {result.text[:200]}"
        body = result.json()
        assert "choices" in body, f"#{i} ({sid}) no choices: {body}"
        assert get_output_text(body["choices"][0]["message"]), f"#{i} ({sid}) empty output"

    assert not failed, f"{len(failed)} concurrent requests failed:\n" + "\n".join(failed)

    # ── Timing assertion ──────────────────────────────────────────────
    ratio = concurrent_time / serial_time
    assert ratio < 6.0, (
        f"Concurrent time ({concurrent_time:.2f}s) exceeded "
        f"6.0x serial time ({serial_time:.2f}s) — ratio={ratio:.2f}"
    )

    # ── Verify all sessions registered ─────────────────────────────────
    status = await get_status(COORD_URL)
    registered_ids = {s["session_id"] for s in status["sessions"]["sessions"]}
    for sid in session_ids:
        assert sid in registered_ids, f"Session {sid} not found in coordinator status"

    # ── Cleanup ────────────────────────────────────────────────────────
    async with httpx.AsyncClient(timeout=30.0) as client:
        for sid in session_ids + [ref_sid]:
            try:
                await client.delete(f"{COORD_URL}/sessions/{sid}")
            except Exception:
                pass


@pytest.mark.e2e
@pytest.mark.asyncio
async def test_cross_session_consistency():
    """
    Send the same prompt directly to llama-server and through Coordinator.
    Both should return valid completions on the same topic.
    """
    prompt_text = generate_text(2_000)
    messages = [{"role": "user", "content": prompt_text}]

    # ── Direct to RTX llama-server ─────────────────────────────────────
    async with httpx.AsyncClient(timeout=120.0) as client:
        direct_resp = await client.post(
            f"{LLAMA_RTX_URL}/v1/chat/completions",
            json={"messages": messages, "max_tokens": 100, "temperature": 0},
        )
    assert direct_resp.status_code == 200, f"Direct llama completion failed: {direct_resp.text[:200]}"
    direct_body = direct_resp.json()
    assert "choices" in direct_body
    direct_msg = direct_body["choices"][0]["message"]
    assert get_output_text(direct_msg), "Direct completion returned empty output"

    # ── Through Coordinator ────────────────────────────────────────────
    session_id = f"e2e-cross-{uuid4().hex[:12]}"
    coord_resp = await do_completion(
        COORD_URL, messages, session_id=session_id,
    )
    assert coord_resp.status_code == 200, f"Coordinator completion failed: {coord_resp.text[:200]}"
    coord_body = coord_resp.json()
    assert "choices" in coord_body
    coord_msg = coord_body["choices"][0]["message"]
    assert get_output_text(coord_msg), "Coordinator completion returned empty output"

    # ── Verify hydra metadata only on coordinator response ─────────────
    assert "hydra" in coord_body, "Coordinator response missing hydra metadata"
    assert "hydra" not in direct_body, "Direct llama response should not have hydra metadata"

    # ── Verify routing stats updated ───────────────────────────────────
    status = await get_status(COORD_URL)
    assert status["routing_stats"]["total"] > 0

    # ── Cleanup ────────────────────────────────────────────────────────
    try:
        async with httpx.AsyncClient(timeout=5) as client:
            await client.delete(f"{COORD_URL}/sessions/{session_id}")
    except Exception:
        pass
