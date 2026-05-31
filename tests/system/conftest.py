"""
pytest conftest for system tests -- saves a result JSON file after every test.

Result files go to tests/result/{test_name}_{timestamp}.json
and include test metadata plus service snapshots (coordinator status,
llama-server metrics) for post-run review.
"""

import asyncio
import json
import os
from datetime import datetime, timezone
from pathlib import Path

import httpx
import pytest

RESULT_DIR = Path(__file__).resolve().parent.parent / "result"
COORD_URL = os.environ.get("COORD_URL", "http://localhost:9000")
LLAMA_RTX_URL = os.environ.get("LLAMA_RTX_URL", "http://localhost:8080")
LLAMA_P100_URL = os.environ.get("LLAMA_P100_URL", "http://192.168.122.21:8086")


def _sanitize(name: str) -> str:
    for ch in ("[", "]", " ", "/", ":", "(", ")"):
        name = name.replace(ch, "_")
    return name.strip("_")


def _now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%S")


async def _scrape_json(url: str, timeout: float = 5.0) -> dict | None:
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            resp = await client.get(url)
            resp.raise_for_status()
            return resp.json()
    except Exception:
        return None


async def _scrape_metrics(url: str, timeout: float = 5.0) -> dict[str, float] | None:
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            resp = await client.get(f"{url}/metrics")
            resp.raise_for_status()
    except Exception:
        return None
    metrics: dict[str, float] = {}
    for line in resp.text.strip().split("\n"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if " " in line:
            name, val = line.split(" ", 1)
            try:
                metrics[name] = float(val)
            except ValueError:
                pass
    return metrics


async def _scrape_slots(url: str, timeout: float = 5.0) -> list | None:
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            resp = await client.get(f"{url}/slots")
            resp.raise_for_status()
            return resp.json()
    except Exception:
        return None


async def _enrich_with_service_snapshots(result: dict) -> dict:
    coord = await _scrape_json(f"{COORD_URL}/status")
    if coord:
        sessions = coord.get("sessions", {})
        result["coordinator"] = {
            "uptime_s": coord.get("uptime_s"),
            "session_count": (
                len(sessions.get("sessions", []))
                if isinstance(sessions, dict)
                else 0
            ),
            "routing_stats": coord.get("routing_stats"),
        }

    health = await _scrape_json(f"{COORD_URL}/health")
    if health:
        if "coordinator" not in result:
            result["coordinator"] = {}
        result["coordinator"]["health"] = health

    for label, base_url in [("rtx", LLAMA_RTX_URL), ("p100", LLAMA_P100_URL)]:
        metrics = await _scrape_metrics(base_url)
        slots = await _scrape_slots(base_url)
        llama: dict = {}
        if metrics:
            llama["prompt_tokens_total"] = metrics.get("llamacpp:prompt_tokens_total")
            llama["tokens_predicted_total"] = metrics.get(
                "llamacpp:tokens_predicted_total"
            )
            llama["requests_processing"] = metrics.get(
                "llamacpp:requests_processing"
            )
        if slots:
            llama["slots"] = [
                {
                    "id": s["id"],
                    "is_processing": s["is_processing"],
                    "n_past": s.get("n_past", 0),
                }
                for s in slots
            ]
        if llama:
            result[f"llama_{label}"] = llama

    return result


def _save_result(report: pytest.TestReport) -> None:
    """Build and write result file synchronously."""
    test_name = _sanitize(report.nodeid)
    timestamp = _now_iso()
    raw_name = report.nodeid.split("::")[-1] if "::" in report.nodeid else report.nodeid
    filename = f"{test_name}_{timestamp}.json"

    result = {
        "test_name": raw_name,
        "nodeid": report.nodeid,
        "timestamp": timestamp,
        "duration_s": round(getattr(report, "duration", 0.0), 3),
        "status": report.outcome,
    }
    if report.longrepr:
        result["error"] = str(report.longrepr)

    try:
        result = asyncio.run(_enrich_with_service_snapshots(result))
    except Exception:
        pass

    RESULT_DIR.mkdir(parents=True, exist_ok=True)
    filepath = RESULT_DIR / filename
    try:
        filepath.write_text(json.dumps(result, indent=2, default=str))
    except Exception:
        pass


@pytest.hookimpl(trylast=True)
def pytest_runtest_logreport(report: pytest.TestReport) -> None:
    """Called after each test phase report is logged."""
    if report.when != "call":
        return
    _save_result(report)
