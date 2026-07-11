#!/usr/bin/env python3
"""app.py — FastAPI service for the hydra_vortex orchestration dashboard.

Endpoints:
  GET /                 -> static dashboard UI (auto-refreshing)
  GET /api/snapshot     -> JSON of the full collected snapshot
  GET /metrics          -> Prometheus exposition (scraped by the stack Prometheus)

Data is collected on a background timer (cheap; mirrors the heartbeat philosophy)
and cached, so HTTP requests are instant.
"""
from __future__ import annotations

import json
import threading
import time
from pathlib import Path

from fastapi import FastAPI
from fastapi.responses import FileResponse, JSONResponse, Response

from collectors import collect_all
from metrics import render, update

APP_DIR = Path(__file__).resolve().parent
STATIC_DIR = APP_DIR / "static"
REFRESH_SEC = int(__name__ and __import__("os").environ.get("REFRESH_SEC", "10"))

app = FastAPI(title="hydra_vortex orchestration dashboard")

_lock = threading.Lock()
_snapshot: dict = {"generated_at": 0, "note": "collecting..."}


def _refresh() -> None:
    global _snapshot
    snap = collect_all()
    update(snap)
    with _lock:
        _snapshot = snap


def _worker() -> None:
    while True:
        try:
            _refresh()
        except Exception:  # noqa: BLE001 - never kill the loop
            pass
        time.sleep(REFRESH_SEC)


@app.on_event("startup")
def _start() -> None:
    t = threading.Thread(target=_worker, daemon=True)
    t.start()


@app.get("/api/snapshot")
def api_snapshot() -> JSONResponse:
    with _lock:
        return JSONResponse(_snapshot)


@app.get("/api/health")
def api_health() -> JSONResponse:
    with _lock:
        return JSONResponse(
            {
                "ok": True,
                "generated_at": _snapshot.get("generated_at"),
                "daemon_up": _snapshot.get("paseo", {}).get("daemon_up"),
            }
        )


@app.get("/metrics")
def metrics() -> Response:
    return Response(render(), media_type="text/plain; version=0.0.4")


@app.get("/")
def index() -> FileResponse:
    return FileResponse(STATIC_DIR / "index.html")


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "app:app",
        host=__import__("os").environ.get("DASHBOARD_HOST", "127.0.0.1"),
        port=int(__import__("os").environ.get("DASHBOARD_PORT", "8098")),
        log_level="info",
    )
