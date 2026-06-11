"""OpenAI-compatible client for Hydra.Core (Split-mix P/D split).

All requests go through Hydra.Core at http://localhost:9000 which routes:
  RTX (Mini model) → prefill → KV save → P100 (Balanced) → KV restore → decode
"""

import json
import time
import uuid
from typing import Any

import requests


class HydraClient:
    """Thin wrapper that sends chat completions through Hydra.Core."""

    def __init__(self, base_url: str = "http://localhost:9000", timeout: int = 1200):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def chat_completion(
        self,
        messages: list[dict[str, str]],
        model: str = "mini",
        params: dict | None = None,
    ) -> dict[str, Any]:
        """Send a non-streaming chat completion and return parsed response.

        Each request gets a unique session_id to force fresh P/D split
        routing (prevents session-affinity from keeping decode on RTX).
        """
        url = f"{self.base_url}/v1/chat/completions"
        body: dict[str, Any] = {
            "model": model,
            "messages": messages,
            "stream": False,
            "session_id": uuid.uuid4().hex[:24],  # fresh session → P/D split
        }
        if params:
            body.update(params)

        start = time.monotonic()
        resp = requests.post(url, json=body, timeout=self.timeout)
        elapsed = time.monotonic() - start

        resp.raise_for_status()
        data = resp.json()
        data["_hydra_elapsed_s"] = elapsed
        return data

    def health(self) -> dict[str, Any]:
        """Check Hydra.Core health including store and nodes."""
        url = f"{self.base_url}/health"
        resp = requests.get(url, timeout=10)
        resp.raise_for_status()
        return resp.json()


class LlamaMetrics:
    """Capture llama-server metrics from RTX and P100 for P/D split verification."""

    RTX_URL = "http://localhost:8080"
    P100_URL = "http://192.168.122.21:8086"

    def __init__(self):
        self._pre: dict[str, int] = {}

    def capture_pre(self) -> dict[str, int]:
        """Capture baseline metrics before a benchmark run."""
        self._pre = self._fetch_all()
        return self._pre

    def capture_post(self) -> dict[str, int]:
        """Capture metrics after a benchmark run and return deltas."""
        post = self._fetch_all()
        deltas: dict[str, int] = {}
        for k, v in post.items():
            pre_v = self._pre.get(k, 0)
            deltas[f"{k}_delta"] = v - pre_v
            deltas[f"{k}_pre"] = pre_v
            deltas[f"{k}_post"] = v
        self._pre = post  # advance for next run
        return deltas

    def _fetch_all(self) -> dict[str, int]:
        """Fetch key metrics from both RTX and P100."""
        result: dict[str, int] = {}
        for label, url in [("rtx", self.RTX_URL), ("p100", self.P100_URL)]:
            try:
                text = requests.get(f"{url}/metrics", timeout=10).text
                for line in text.splitlines():
                    line = line.strip()
                    if line.startswith("#") or not line:
                        continue
                    parts = line.rsplit(" ", 1)
                    if len(parts) != 2:
                        continue
                    name = parts[0]
                    try:
                        val = int(float(parts[1]))
                    except ValueError:
                        continue
                    if name in (
                        "llamacpp:prompt_tokens_total",
                        "llamacpp:tokens_predicted_total",
                    ):
                        result[f"{label}:{name}"] = val
            except Exception:
                pass
        return result

    def get_slot_n_past(self, node: str = "p100") -> int:
        """Get n_past from a node's first slot."""
        url = self.P100_URL if node == "p100" else self.RTX_URL
        try:
            slots = requests.get(f"{url}/slots", timeout=10).json()
            if slots:
                return int(slots[0].get("n_past", 0))
        except Exception:
            pass
        return 0
