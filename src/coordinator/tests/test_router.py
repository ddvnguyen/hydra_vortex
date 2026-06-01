import pytest
from unittest.mock import patch, AsyncMock, MagicMock
from fastapi.testclient import TestClient
from fastapi import FastAPI

from coordinator.config import CoordinatorConfig, NodeConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router
from coordinator.routing import RoutingDecision


@pytest.fixture
def config():
    return CoordinatorConfig(
        nodes=[
            NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, gpu_type="rtx5060ti"),
            NodeConfig(name="p100", host="192.168.122.21", rpc_port=9602, gpu_type="p100"),
        ],
    )


@pytest.fixture
def app(config):
    app = FastAPI()
    table = SessionTable()
    health = HealthMonitor(config.nodes)
    sm = StateManager(table, "127.0.0.1", 9500)
    router = create_router(config, table, health, sm)
    app.include_router(router)
    app.state._session_table = table
    return app


@pytest.fixture
def client(app):
    return TestClient(app)


def make_decision(
    node_name="rtx",
    action="route",
    session_id="sess_test",
    n_past=0,
):
    cfg = NodeConfig(name=node_name, host="127.0.0.1", rpc_port=9601, gpu_type="rtx5060ti")
    return RoutingDecision(
        node_name=node_name,
        node_config=cfg,
        slot_id=0,
        action=action,
        session_id=session_id,
        session_found=False,
        n_past=n_past,
    )


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


def test_health_returns_200(client):
    resp = client.get("/health")
    assert resp.status_code == 200
    data = resp.json()
    assert "status" in data
    assert "nodes" in data


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
    data = resp.json()
    assert "sessions" in data
    assert len(data["sessions"]) == 0


def test_completion_returns_503_when_no_healthy_nodes(client):
    with patch("coordinator.router.route_request") as mock_route:
        mock_route.side_effect = RuntimeError("No healthy nodes available")
        resp = client.post(
            "/v1/chat/completions",
            json={
                "messages": [{"role": "user", "content": "hello"}],
                "stream": False,
            },
        )
    assert resp.status_code == 503


def test_completion_non_streaming(client):
    with patch("coordinator.router.proxy_completion") as mock_proxy:
        mock_proxy.return_value = {
            "choices": [{"message": {"content": "hi"}}],
            "usage": {"total_tokens": 5},
        }

        with patch("coordinator.router.route_request") as mock_route:
            mock_route.return_value = make_decision()

            resp = client.post(
                "/v1/chat/completions",
                json={
                    "messages": [{"role": "user", "content": "hello"}],
                    "stream": False,
                },
            )

    assert resp.status_code == 200
    data = resp.json()
    assert "choices" in data


def test_delete_session_not_found(client):
    resp = client.delete("/sessions/nonexistent")
    assert resp.status_code == 404


def test_migrate_session_not_found(client):
    resp = client.post(
        "/sessions/nonexistent/migrate",
        json={"target_node": "p100"},
    )
    assert resp.status_code == 404


def test_completion_derives_session_id(client):
    with patch("coordinator.router.proxy_completion") as mock_proxy:
        mock_proxy.return_value = {
            "choices": [{"message": {"content": "hi"}}],
            "usage": {"total_tokens": 5},
        }

        with patch("coordinator.router.route_request") as mock_route:
            mock_route.return_value = make_decision()

            resp = client.post(
                "/v1/chat/completions",
                json={
                    "messages": [{"role": "user", "content": "hello"}],
                    "stream": False,
                },
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
    resp = client.get("/metrics")
    assert resp.status_code == 200
