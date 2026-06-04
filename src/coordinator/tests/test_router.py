import pytest
from unittest.mock import patch, AsyncMock
from fastapi.testclient import TestClient
from fastapi import FastAPI

from coordinator.config import CoordinatorConfig, WorkerNodeConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router
from coordinator.routing import RoutingDecision, WORKER_MIXED


RTX_CFG = WorkerNodeConfig(
    name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080",
    worker_type=WORKER_MIXED, slots=2, prefill_priority=1, decode_priority=2,
)
P100_CFG = WorkerNodeConfig(
    name="p100", host="192.168.122.21", rpc_port=9602, llama_url="http://192.168.122.21:8086",
    worker_type=WORKER_MIXED, slots=1, prefill_priority=2, decode_priority=1,
)


@pytest.fixture
def config():
    return CoordinatorConfig(workers=[RTX_CFG, P100_CFG])


@pytest.fixture
def config_fast():
    return CoordinatorConfig(workers=[RTX_CFG, P100_CFG], run_mode="fast")


def _make_app(config: CoordinatorConfig) -> FastAPI:
    app = FastAPI()
    table = SessionTable()
    health = HealthMonitor(config.workers)
    sm = StateManager(table, "127.0.0.1", 9500)
    router = create_router(config, table, health, sm)
    app.include_router(router)
    app.state._session_table = table
    return app


@pytest.fixture
def app(config):
    return _make_app(config)


@pytest.fixture
def app_fast(config_fast):
    return _make_app(config_fast)


@pytest.fixture
def client(app):
    return TestClient(app)


@pytest.fixture
def client_fast(app_fast):
    return TestClient(app_fast)


def make_decision(node_name="rtx", action="route", session_id="sess_test", n_past=0):
    cfg = WorkerNodeConfig(
        name=node_name, host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080",
        worker_type=WORKER_MIXED,
    )
    return RoutingDecision(
        node_name=node_name, node_config=cfg, slot_id=0,
        action=action, session_id=session_id, session_found=False, n_past=n_past,
    )


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


def test_completion_missing_messages_returns_422(client):
    resp = client.post(
        "/v1/chat/completions",
        json={"max_tokens": 512},
    )
    assert resp.status_code == 422



def test_migrate_invalid_target_node_returns_400(client):
    # Need session to exist first, then migrate to invalid target
    table = client.app.state._session_table
    table.register("sess_existing", "rtx", 0)

    resp = client.post(
        "/sessions/sess_existing/migrate",
        json={"target_node": "nonexistent_node"},
    )
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
    """Test session eviction returns 200 with evicted=True."""
    table = client.app.state._session_table
    table.register("sess_evict", "rtx", 0)

    # Mock the state_manager save_session to avoid RPC calls during test
    with patch.object(StateManager, "save_session", new_callable=AsyncMock) as mock_save:
        mock_save.return_value = {}
        resp = client.delete("/sessions/sess_evict")
        assert resp.status_code == 200
        data = resp.json()
        assert data["evicted"] is True
        assert data["session_id"] == "sess_evict"

def test_evict_session_missing_body_returns_ok(client):
    """Test session eviction works without request body (body parsed as None)."""
    table = client.app.state._session_table
    table.register("sess_evict2", "rtx", 0)

    with patch.object(StateManager, "save_session", new_callable=AsyncMock) as mock_save:
        mock_save.return_value = {}
        # Send request without JSON body - endpoint should handle None body gracefully
        resp = client.delete("/sessions/sess_evict2")
        assert resp.status_code == 200
        data = resp.json()
        assert data["evicted"] is True
        assert data["session_id"] == "sess_evict2"


def test_health_returns_200(client):
    resp = client.get("/health")
    assert resp.status_code == 200
    data = resp.json()
    assert "status" in data
    assert "nodes" in data



def test_health_shows_nodes(client):
    resp = client.get("/health")
    data = resp.json()
    assert "rtx" in data["nodes"]
    assert "p100" in data["nodes"]


def test_health_reports_store(client):
    # store_host unconfigured in this fixture → store probe is a no-op (healthy default)
    resp = client.get("/health")
    data = resp.json()
    assert "store" in data
    assert "healthy" in data["store"]


def test_health_degraded_when_store_down(config):
    # Build a router whose HealthMonitor reports the Store as down.
    table = SessionTable()
    monitor = HealthMonitor(config.workers, store_host="127.0.0.1", store_port=9500)
    monitor._store_healthy = False  # simulate failed store probe
    # Nodes report healthy so only the store drives the degraded status.
    for info in monitor._nodes.values():
        info.healthy = True
    sm = StateManager(table, "127.0.0.1", 9500)
    app = FastAPI()
    app.include_router(create_router(config, table, monitor, sm))

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


def test_completion_returns_503_when_no_healthy_nodes(client):
    with patch("coordinator.router.route_request") as mock_route:
        mock_route.side_effect = RuntimeError("No healthy workers available")
        resp = client.post(
            "/v1/chat/completions",
            json={"messages": [{"role": "user", "content": "hello"}], "stream": False},
        )
    assert resp.status_code == 503


def test_completion_non_streaming(client_fast):
    with patch("coordinator.router.proxy_completion") as mock_proxy, \
         patch("coordinator.router.route_request") as mock_route:
        mock_proxy.return_value = {
            "choices": [{"message": {"content": "hi"}}],
            "usage": {"total_tokens": 5},
        }
        mock_route.return_value = make_decision()
        resp = client_fast.post(
            "/v1/chat/completions",
            json={"messages": [{"role": "user", "content": "hello"}], "stream": False},
        )
    assert resp.status_code == 200
    assert "choices" in resp.json()


def test_delete_session_not_found(client):
    assert client.delete("/sessions/nonexistent").status_code == 404


def test_migrate_session_not_found(client):
    resp = client.post("/sessions/nonexistent/migrate", json={"target_node": "p100"})
    assert resp.status_code == 404


def test_completion_derives_session_id(client_fast):
    with patch("coordinator.router.proxy_completion") as mock_proxy, \
         patch("coordinator.router.route_request") as mock_route:
        mock_proxy.return_value = {
            "choices": [{"message": {"content": "hi"}}],
            "usage": {"total_tokens": 5},
        }
        mock_route.return_value = make_decision()
        resp = client_fast.post(
            "/v1/chat/completions",
            json={"messages": [{"role": "user", "content": "hello"}], "stream": False},
        )
    assert resp.status_code == 200


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
