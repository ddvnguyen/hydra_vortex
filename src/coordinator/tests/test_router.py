import pytest
from unittest.mock import patch, AsyncMock
from fastapi.testclient import TestClient
from fastapi import FastAPI

from coordinator.config import CoordinatorConfig, WorkerNodeConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router
from coordinator.routing import WORKER_MIXED
from coordinator.worker_tracker import WorkerTracker
from coordinator.scheduler import WorkerScheduler


RTX_CFG = WorkerNodeConfig(
    name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080",
    worker_type=WORKER_MIXED, slots=2, prefill_priority=1, decode_priority=2,
    max_prefill_tokens=-1,
)
P100_CFG = WorkerNodeConfig(
    name="p100", host="192.168.122.21", rpc_port=9602, llama_url="http://192.168.122.21:8086",
    worker_type=WORKER_MIXED, slots=1, prefill_priority=2, decode_priority=1,
    max_prefill_tokens=8000,
)


@pytest.fixture
def config():
    return CoordinatorConfig(workers=[RTX_CFG, P100_CFG])


def _make_app(config: CoordinatorConfig) -> FastAPI:
    app = FastAPI()
    table = SessionTable()
    health = HealthMonitor(config.workers)
    sm = StateManager(table, "127.0.0.1", 9500)
    tracker = WorkerTracker(_error_threshold=config.worker_error_threshold)
    scheduler = WorkerScheduler(
        config=config, session_table=table,
        health_monitor=health, state_manager=sm, tracker=tracker,
    )
    router = create_router(config, table, health, sm, scheduler)
    app.include_router(router)
    app.state._session_table = table
    return app


@pytest.fixture
def app(config):
    return _make_app(config)


@pytest.fixture
def client(app):
    return TestClient(app)


def test_completion_missing_messages_returns_422(client):
    resp = client.post("/v1/chat/completions", json={"max_tokens": 512})
    assert resp.status_code == 422


def test_migrate_invalid_target_node_returns_400(client):
    table = client.app.state._session_table
    table.register("sess_existing", "rtx", 0)
    resp = client.post("/sessions/sess_existing/migrate", json={"target_node": "nonexistent_node"})
    assert resp.status_code == 400
    assert "not configured" in resp.json()["detail"]


def test_prefix_save_invalid_node_returns_400(client):
    resp = client.post("/prefix/test_checkpoint/save?node_name=ghost")
    assert resp.status_code == 400
    assert "not configured" in resp.json()["detail"]


def test_prefix_restore_invalid_node_returns_400(client):
    resp = client.post("/prefix/test_checkpoint/restore?node_name=ghost")
    assert resp.status_code == 400
    assert "not configured" in resp.json()["detail"]


def test_evict_session_success(client):
    table = client.app.state._session_table
    table.register("sess_evict", "rtx", 0)
    with patch.object(StateManager, "save_session", new_callable=AsyncMock) as mock_save:
        mock_save.return_value = {}
        resp = client.delete("/sessions/sess_evict")
    assert resp.status_code == 200
    data = resp.json()
    assert data["evicted"] is True
    assert data["session_id"] == "sess_evict"


def test_evict_session_missing_body_returns_ok(client):
    table = client.app.state._session_table
    table.register("sess_evict2", "rtx", 0)
    with patch.object(StateManager, "save_session", new_callable=AsyncMock) as mock_save:
        mock_save.return_value = {}
        resp = client.delete("/sessions/sess_evict2")
    assert resp.status_code == 200
    assert resp.json()["evicted"] is True


def test_health_returns_200(client):
    resp = client.get("/health")
    assert resp.status_code == 200
    data = resp.json()
    assert "status" in data
    assert "nodes" in data
    assert "rtx" in data["nodes"]
    assert "p100" in data["nodes"]
    assert "store" in data


def test_health_degraded_when_store_down(config):
    table = SessionTable()
    monitor = HealthMonitor(config.workers, store_host="127.0.0.1", store_port=9500)
    monitor._store_healthy = False
    for info in monitor._nodes.values():
        info.healthy = True
    sm = StateManager(table, "127.0.0.1", 9500)
    tracker = WorkerTracker(_error_threshold=config.worker_error_threshold)
    scheduler = WorkerScheduler(
        config=config, session_table=table,
        health_monitor=monitor, state_manager=sm, tracker=tracker,
    )
    app = FastAPI()
    app.include_router(create_router(config, table, monitor, sm, scheduler))
    resp = TestClient(app).get("/health")
    data = resp.json()
    assert data["status"] == "degraded"
    assert data["store"]["healthy"] is False


def test_status_returns_200(client):
    resp = client.get("/status")
    assert resp.status_code == 200
    data = resp.json()
    assert "uptime_s" in data
    assert "sessions" in data
    assert "routing_stats" in data
    assert "nodes" in data


def test_list_sessions_empty(client):
    resp = client.get("/sessions")
    assert resp.status_code == 200
    assert resp.json()["sessions"] == []


def test_delete_session_not_found(client):
    assert client.delete("/sessions/nonexistent").status_code == 404


def test_migrate_session_not_found(client):
    resp = client.post("/sessions/nonexistent/migrate", json={"target_node": "p100"})
    assert resp.status_code == 404


def test_version_returns_200(client):
    resp = client.get("/version")
    assert resp.status_code == 200
    data = resp.json()
    assert data["service"] == "hydra-coordinator"
    assert "version" in data
    assert "revision" in data


def test_metrics_returns_200(client):
    assert client.get("/metrics").status_code == 200


def test_migrate_missing_target_node_returns_422(client):
    resp = client.post("/sessions/sess/migrate", json={})
    assert resp.status_code == 422
