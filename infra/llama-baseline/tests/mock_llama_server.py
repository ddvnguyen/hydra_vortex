#!/usr/bin/env python3
"""mock_llama_server.py — minimal llama-server stand-in for hermetic harness tests.

Implements just enough of the HTTP surface the multiturn harness uses:
  GET  /health                  -> {"status":"ok"}
  GET  /props                   -> model_alias / model_ftype / build_info / is_sleeping
  POST /v1/chat/completions     -> deterministic canned reply + usage

It also records every completion request on `server.requests` and computes a
synthetic `cached_tokens` equal to the longest common message prefix with the
previous request, so a test can assert that a checkpoint sweep genuinely reuses
one prefix across calls.

Standalone use:  python3 mock_llama_server.py [port]   (0 = ephemeral)
"""

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class MockState:
    def __init__(self):
        self.requests = []
        self.last_messages = []


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):  # silence
        pass

    def _send_json(self, code, payload):
        body = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send_json(200, {"status": "ok"})
        elif self.path == "/props":
            self._send_json(200, {
                "model_alias": "mock-model",
                "model_ftype": "MOCK-1B",
                "build_info": "mock-build-0000",
                "is_sleeping": False,
                "total_slots": 1,
            })
        elif self.path == "/mock_requests":
            self._send_json(200, self.server.state.requests)
        else:
            self._send_json(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/v1/chat/completions":
            self._send_json(404, {"error": "not found"})
            return
        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length).decode())
        messages = body.get("messages", [])
        max_tokens = body.get("max_tokens", 16)
        state = self.server.state

        prompt_tokens = max(1, sum(len(str(m.get("content", ""))) for m in messages) // 4)
        cached_tokens = 0
        common = 0
        for prev, cur in zip(state.last_messages, messages):
            if prev == cur:
                common += 1
            else:
                break
        if common:
            cached_tokens = max(
                1, sum(len(str(m.get("content", ""))) for m in messages[:common]) // 4)

        record = {
            "messages": messages,
            "max_tokens": max_tokens,
            "temperature": body.get("temperature"),
            "prompt_tokens": prompt_tokens,
            "cached_tokens": cached_tokens,
        }
        state.requests.append(record)
        state.last_messages = messages

        content = f"MOCK content for {len(messages)} msgs at max_tokens={max_tokens}"
        self._send_json(200, {
            "choices": [{
                "message": {"content": content, "reasoning_content": ""},
                "finish_reason": "stop",
            }],
            "usage": {
                "prompt_tokens": prompt_tokens,
                "completion_tokens": min(max_tokens, 8),
                "prompt_tokens_details": {"cached_tokens": cached_tokens},
            },
        })


def make_server(port=0):
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.state = MockState()
    return server


def serve(port=0):
    server = make_server(port)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, thread


if __name__ == "__main__":
    requested = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    server, thread = serve(requested)
    print(f"mock llama-server on 127.0.0.1:{server.server_address[1]}", flush=True)
    try:
        thread.join()
    except KeyboardInterrupt:
        pass
