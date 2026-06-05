import pytest
from unittest.mock import patch, AsyncMock, MagicMock

from coordinator.proxy import proxy_completion, proxy_completion_stream


@pytest.mark.asyncio
async def test_proxy_completion():
    mock_response = {
        "choices": [{"message": {"content": "hello"}}],
        "usage": {"total_tokens": 10},
    }

    with patch("coordinator.proxy.httpx.AsyncClient") as MockClient:
        client = MagicMock()
        MockClient.return_value = client

        client.__aenter__ = AsyncMock(return_value=client)
        client.__aexit__ = AsyncMock(return_value=None)

        resp = MagicMock()
        resp.status_code = 200
        resp.json = MagicMock(return_value=mock_response)
        resp.raise_for_status = MagicMock()
        client.post = AsyncMock(return_value=resp)

        result = await proxy_completion(
            "http://localhost:8080",
            {"messages": [{"role": "user", "content": "hi"}]},
            "trace_001",
        )

    assert result["choices"][0]["message"]["content"] == "hello"


@pytest.mark.asyncio
async def test_proxy_completion_stream():
    mock_lines = [
        'data: {"choices": [{"delta": {"content": "hello"}}]}',
        "data: [DONE]",
    ]

    with patch("coordinator.proxy.httpx.AsyncClient") as MockClient:
        client = MagicMock()
        MockClient.return_value = client

        client.__aenter__ = AsyncMock(return_value=client)
        client.__aexit__ = AsyncMock(return_value=None)

        resp = MagicMock()
        resp.status_code = 200
        resp.raise_for_status = MagicMock()
        resp.aiter_lines = MagicMock(return_value=AsyncMock(
            __aiter__=lambda self: self,
            __anext__=AsyncMock(side_effect=mock_lines),
        ))

        stream_cm = MagicMock()
        stream_cm.__aenter__ = AsyncMock(return_value=resp)
        stream_cm.__aexit__ = AsyncMock(return_value=None)
        client.stream = MagicMock(return_value=stream_cm)

        lines = []
        async for chunk in proxy_completion_stream(
            "http://localhost:8080",
            {"messages": [{"role": "user", "content": "hi"}], "stream": True},
            "trace_001",
        ):
            lines.append(chunk)

    assert len(lines) > 0
    assert "hello" in lines[0]


@pytest.mark.asyncio
async def test_proxy_completion_stream_ends_at_done():
    with patch("coordinator.proxy.httpx.AsyncClient") as MockClient:
        client = MagicMock()
        MockClient.return_value = client

        client.__aenter__ = AsyncMock(return_value=client)
        client.__aexit__ = AsyncMock(return_value=None)

        resp = MagicMock()
        resp.status_code = 200
        resp.raise_for_status = MagicMock()
        resp.aiter_lines = MagicMock(return_value=AsyncMock(
            __aiter__=lambda self: self,
            __anext__=AsyncMock(
                side_effect=['data: {"choices": [{}]}', "data: [DONE]"]
            ),
        ))

        stream_cm = MagicMock()
        stream_cm.__aenter__ = AsyncMock(return_value=resp)
        stream_cm.__aexit__ = AsyncMock(return_value=None)
        client.stream = MagicMock(return_value=stream_cm)

        lines = []
        async for chunk in proxy_completion_stream(
            "http://localhost:8080",
            {},
            "trace_001",
        ):
            lines.append(chunk)

    assert len(lines) == 2
    assert "data: [DONE]" in lines[-1]
