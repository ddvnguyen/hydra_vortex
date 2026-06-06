import json
import httpx

from coordinator.lib.log_config import get_logger

log = get_logger()

# Shared HTTP client for proxying requests to llama servers.
# Reuses TCP connections and SSL sessions, avoiding per-request overhead.
_http_client: httpx.AsyncClient | None = None

# Read budget for upstream llama-server requests. A large-prompt prefill can take many
# minutes; if this is shorter than the prefill, httpx aborts mid-flight, llama cancels the
# task, and the client retries → endless re-prefill loop (#134). Configured at startup via
# configure_timeout(); defaults high to match llama's --timeout.
_read_timeout_s: float = 1800.0


def configure_timeout(read_timeout_s: float) -> None:
    """Set the upstream read timeout. Call once at startup before the first request.

    Resets any existing client so the new timeout takes effect on the next request.
    """
    global _read_timeout_s, _http_client
    _read_timeout_s = float(read_timeout_s)
    _http_client = None


async def _get_client() -> httpx.AsyncClient:
    global _http_client
    if _http_client is None or _http_client.is_closed:
        # Generous read budget for long prefills; tight connect/pool so genuine
        # connectivity failures still surface quickly.
        timeout = httpx.Timeout(
            connect=10.0, read=_read_timeout_s, write=30.0, pool=10.0
        )
        _http_client = httpx.AsyncClient(timeout=timeout)
    return _http_client


async def proxy_completion(
    node_url: str,
    request_body: dict,
    trace_id: str,
) -> dict:
    client = await _get_client()
    resp = await client.post(
        f"{node_url}/v1/chat/completions",
        json=request_body,
        headers={
            "Content-Type": "application/json",
            "X-Trace-Id": trace_id,
        },
    )
    resp.raise_for_status()
    data = resp.json()

    return data


async def proxy_completion_stream(
    node_url: str,
    request_body: dict,
    trace_id: str,
):
    client = await _get_client()
    async with client.stream(
        "POST",
        f"{node_url}/v1/chat/completions",
        json=request_body,
        headers={
            "Content-Type": "application/json",
            "X-Trace-Id": trace_id,
        },
    ) as resp:
        resp.raise_for_status()
        async for line in resp.aiter_lines():
            if line:
                yield f"{line}\n\n"



async def shutdown():
    """Close the shared client when coordinator shuts down."""
    global _http_client
    if _http_client is not None and not _http_client.is_closed:
        await _http_client.aclose()
