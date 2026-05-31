"""
System test for full KV state migration across GPU nodes.

Tests the complete path:
  llama RTX → Agent saves to Store → Agent restores to P100 → continuation

Requires running services:
  - Two patched llama-servers (RTX :8080, P100 :8086)
  - Hydra Store (:9500)
  - Two Hydra Agents (RTX :9601, P100 :9602)

Environment variables:
  RTX_LLAMA_URL       http://localhost:8080
  P100_LLAMA_URL      http://192.168.122.21:8086
  RTX_AGENT_HOST      127.0.0.1
  RTX_AGENT_PORT      9601
  P100_AGENT_HOST     127.0.0.1
  P100_AGENT_PORT     9602
  STORE_HOST          127.0.0.1
  STORE_PORT          9500
"""

import os

import httpx
import pytest

from python_shared.rpc_client import RpcClient, OpCode

RTX_LLAMA_URL = os.environ.get("RTX_LLAMA_URL", "http://localhost:8080")
P100_LLAMA_URL = os.environ.get("P100_LLAMA_URL", "http://192.168.122.21:8086")
RTX_AGENT_HOST = os.environ.get("RTX_AGENT_HOST", "127.0.0.1")
RTX_AGENT_PORT = int(os.environ.get("RTX_AGENT_PORT", "9601"))
P100_AGENT_HOST = os.environ.get("P100_AGENT_HOST", "127.0.0.1")
P100_AGENT_PORT = int(os.environ.get("P100_AGENT_PORT", "9602"))

TEST_SESSION = "system-test-session"
PROMPT = "What is the capital of France?"
NEW_QUESTION = " What is its population?"


async def send_completion(
    base_url: str,
    messages: list[dict],
    max_tokens: int = 50,
    trace_id: str = "",
) -> dict:
    headers = {"X-Hydra-Trace-Id": trace_id} if trace_id else {}
    async with httpx.AsyncClient(timeout=120.0) as client:
        response = await client.post(
            f"{base_url}/v1/chat/completions",
            json={
                "messages": messages,
                "max_tokens": max_tokens,
                "temperature": 0,
                "stream": False,
            },
            headers=headers,
        )
        response.raise_for_status()
        return response.json()


async def rpc_save_state(host: str, port: int, session_id: str, trace_id: str = "") -> dict:
    client = RpcClient(host, port)
    try:
        resp = await client.request(OpCode.SaveState, session_id, trace_id=trace_id)
        return resp.meta
    finally:
        await client.close()


async def rpc_restore_state(
    host: str, port: int, session_id: str, slot_id: int = 0, trace_id: str = ""
) -> dict:
    client = RpcClient(host, port)
    try:
        key = f"{session_id}:{slot_id}"
        resp = await client.request(OpCode.RestoreState, key, trace_id=trace_id)
        return resp.meta
    finally:
        await client.close()


@pytest.mark.system
@pytest.mark.asyncio
async def test_full_migration():
    # 1. Send prompt to RTX llama-server
    response = await send_completion(
        RTX_LLAMA_URL,
        [{"role": "user", "content": PROMPT}],
        max_tokens=50,
        trace_id="system-prompt-rtx",
    )
    assert "choices" in response, f"RTX completion response missing choices: {response}"
    assistant_reply = response["choices"][0]["message"]["content"]

    # 2. Save RTX slot state to Store via Agent
    save_meta = await rpc_save_state(
        RTX_AGENT_HOST,
        RTX_AGENT_PORT,
        TEST_SESSION,
        trace_id="system-save-rtx",
    )
    assert save_meta.get("size", 0) > 0, f"SaveState returned no size: {save_meta}"

    # 3. Restore state from Store to P100 via Agent
    restore_meta = await rpc_restore_state(
        P100_AGENT_HOST,
        P100_AGENT_PORT,
        TEST_SESSION,
        slot_id=0,
        trace_id="system-restore-p100",
    )
    assert restore_meta.get("restored"), f"RestoreState failed: {restore_meta}"

    # 4. Send continuation to P100 (n_tokens MUST be > n_past)
    continuation = await send_completion(
        P100_LLAMA_URL,
        [
            {"role": "user", "content": PROMPT},
            {"role": "assistant", "content": assistant_reply},
            {"role": "user", "content": NEW_QUESTION},
        ],
        max_tokens=50,
        trace_id="system-continuation-p100",
    )

    timings = continuation.get("timings", {})
    cache_n = timings.get("cache_n", 0)
    prompt_ms = timings.get("prompt_ms", 0)

    assert cache_n > 0, (
        f"cache_n={cache_n} — cache was not used after restore. "
        f"Continuation prompt may have fewer tokens than n_past, "
        f"or the restore failed silently."
    )
    assert prompt_ms < 5000, (
        f"prompt_ms={prompt_ms} — full re-prefill occurred instead of "
        f"using cached KV state (expected <5000ms for cached path)."
    )
