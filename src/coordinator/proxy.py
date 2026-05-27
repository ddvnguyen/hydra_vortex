import json
import httpx

from python_shared.log_config import get_logger

log = get_logger()


async def proxy_completion(
    node_url: str,
    request_body: dict,
    trace_id: str,
) -> dict:
    async with httpx.AsyncClient() as client:
        resp = await client.post(
            f"{node_url}/v1/chat/completions",
            json=request_body,
            headers={
                "Content-Type": "application/json",
                "X-Trace-Id": trace_id,
            },
            timeout=300,
        )
        resp.raise_for_status()
        data = resp.json()

    data["hydra"] = {
        "trace_id": trace_id,
        "node": node_url,
        "proxy": "hydra-coordinator",
    }
    return data


async def proxy_completion_stream(
    node_url: str,
    request_body: dict,
    trace_id: str,
):
    async with httpx.AsyncClient() as client:
        async with client.stream(
            "POST",
            f"{node_url}/v1/chat/completions",
            json=request_body,
            headers={
                "Content-Type": "application/json",
                "X-Trace-Id": trace_id,
            },
            timeout=300,
        ) as resp:
            resp.raise_for_status()
            async for line in resp.aiter_lines():
                if line:
                    yield f"{line}\n\n"

            yield f"data: {json.dumps({'hydra': {'trace_id': trace_id, 'node': node_url, 'proxy': 'hydra-coordinator'}})}\n\n"
