"""
Pytest fixtures + hooks for the bench suite.

Mirrors the result-saving pattern from `tests/system/conftest.py` so the
bench reports land in `tests/result/` with the same shape as system-test
results. Bench reports additionally carry a `bench` block with the
percentile metrics from `BenchmarkHarness.report()`.
"""

from __future__ import annotations

import asyncio
import json
import os
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import httpx
import pytest

# ─── URLs / defaults (match the system-test conventions) ────────────────
COORD_URL       = os.environ.get("COORD_URL",       "http://localhost:9000")
LLAMA_RTX_URL   = os.environ.get("LLAMA_RTX_URL",   "http://localhost:8080")
LLAMA_P100_URL  = os.environ.get("LLAMA_P100_URL",  "http://192.168.122.21:8086")
COORD_METRICS   = os.environ.get("COORD_METRICS_URL", "http://localhost:9501/metrics")
BENCH_DURATION_S = float(os.environ.get("BENCH_DURATION_S", "30"))
BENCH_CONCURRENCY = int(os.environ.get("BENCH_CONCURRENCY", "1"))

RESULT_DIR = Path(__file__).resolve().parent.parent / "result"
RESULT_DIR.mkdir(parents=True, exist_ok=True)


# ─── Service-snapshot helpers (reused from tests/system/conftest.py) ────

async def _scrape_json(url: str, timeout: float = 5.0) -> dict | None:
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            r = await client.get(url)
            r.raise_for_status()
            return r.json()
    except Exception:
        return None


async def _scrape_metrics(url: str, timeout: float = 5.0) -> dict[str, float] | None:
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            r = await client.get(f"{url}/metrics")
            r.raise_for_status()
    except Exception:
        return None
    out: dict[str, float] = {}
    for line in r.text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if " " not in line:
            continue
        name, _, val = line.partition(" ")
        try:
            out[name] = float(val)
        except ValueError:
            pass
    return out


# ─── Fixtures ───────────────────────────────────────────────────────────

@pytest.fixture(scope="session")
def coord_url() -> str:
    return COORD_URL


@pytest.fixture(scope="session")
def rtx_url() -> str:
    return LLAMA_RTX_URL


@pytest.fixture(scope="session")
def p100_url() -> str:
    return LLAMA_P100_URL


@pytest.fixture(scope="session")
def default_duration() -> float:
    return BENCH_DURATION_S


@pytest.fixture(scope="session")
def default_concurrency() -> int:
    return BENCH_CONCURRENCY


@pytest.fixture
async def live_stack() -> dict[str, Any]:
    """
    Verify the live stack is reachable before a bench scenario runs.
    Returns a dict of base URLs + a health-status snapshot for diagnostics
    on failure. Skips the test if any of coord / rtx / p100 is unreachable.
    """
    snap: dict[str, Any] = {
        "coord_url": COORD_URL,
        "rtx_url":   LLAMA_RTX_URL,
        "p100_url":  LLAMA_P100_URL,
    }

    coord_health = await _scrape_json(f"{COORD_URL}/health")
    snap["coord_health"] = coord_health
    if not coord_health:
        pytest.skip(f"Coordinator not reachable at {COORD_URL}/health")

    snap["rtx_metrics_keys"] = list((await _scrape_metrics(LLAMA_RTX_URL) or {}).keys())[:5]
    snap["p100_metrics_keys"] = list((await _scrape_metrics(LLAMA_P100_URL) or {}).keys())[:5]

    return snap


@pytest.fixture
def session_id_factory():
    """Returns a callable producing stable, prefix-tagged session ids."""
    from uuid import uuid4
    def _make(prefix: str = "bench") -> str:
        return f"{prefix}-{uuid4().hex[:12]}"
    return _make


# ─── Report saving (mirrors tests/system/conftest.py) ──────────────────

def _sanitize(name: str) -> str:
    for ch in ("[", "]", " ", "/", ":", "(", ")"):
        name = name.replace(ch, "_")
    return name.strip("_")


def _now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%S")


def _save_bench_result(
    nodeid: str,
    bench_payload: dict[str, Any],
    duration_s: float,
    status: str,
    error: str | None,
) -> None:
    name = _sanitize(nodeid)
    ts = _now_iso()
    out_path = RESULT_DIR / f"{name}_{ts}.json"
    payload: dict[str, Any] = {
        "test_name":  nodeid.split("::")[-1] if "::" in nodeid else nodeid,
        "nodeid":     nodeid,
        "timestamp":  ts,
        "duration_s": round(duration_s, 3),
        "status":     status,
        "bench":      bench_payload,
    }
    if error:
        payload["error"] = error
    out_path.write_text(json.dumps(payload, indent=2, default=str))
