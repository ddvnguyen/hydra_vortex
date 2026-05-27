import pytest
from coordinator.routing import (
    route_request,
    derive_session_id,
    estimate_request_tokens,
)
from coordinator.session_table import SessionTable
from coordinator.config import NodeConfig


RTX = NodeConfig(name="rtx", host="127.0.0.1", rpc_port=9601, gpu_type="rtx5060ti")
P100 = NodeConfig(name="p100", host="192.168.122.21", rpc_port=9602, gpu_type="p100")
NODES = [RTX, P100]


def make_health(rtx_healthy=True, rtx_idle=1, p100_healthy=True, p100_idle=1):
    return {
        "rtx": {
            "healthy": rtx_healthy,
            "slots_total": 2,
            "slots_idle": rtx_idle,
            "gpu_type": "rtx5060ti",
        },
        "p100": {
            "healthy": p100_healthy,
            "slots_total": 2,
            "slots_idle": p100_idle,
            "gpu_type": "p100",
        },
    }


def test_affinity_hit_returns_same_node():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0, n_past=100)
    health = make_health()

    decision = route_request(
        [{"role": "user", "content": "hello"}],
        table,
        NODES,
        health,
        session_id="sess_abc",
    )

    assert decision.node_name == "rtx"
    assert decision.session_found is True


def test_evicted_session_with_store_restore():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0, n_past=100)
    table.mark_evicted("sess_abc")
    table.update_n_past("sess_abc", 100)
    health = make_health(rtx_healthy=False)

    decision = route_request(
        [{"role": "user", "content": "hello"}],
        table,
        NODES,
        health,
        session_id="sess_abc",
    )

    assert decision.action == "store_restore"
    assert decision.session_found is True


def test_long_prompt_routes_to_rtx():
    table = SessionTable()
    health = make_health()

    long_prompt = [{"role": "user", "content": "x" * 20000}]
    decision = route_request(
        long_prompt,
        table,
        NODES,
        health,
        long_prompt_threshold=4096,
    )

    assert decision.node_name == "rtx"


def test_short_prompt_rtx_busy_routes_to_p100():
    table = SessionTable()
    health = make_health(rtx_healthy=True, rtx_idle=0)

    decision = route_request(
        [{"role": "user", "content": "short"}],
        table,
        NODES,
        health,
    )

    assert decision.node_name == "p100"


def test_both_nodes_down_raises_error():
    table = SessionTable()
    health = make_health(rtx_healthy=False, p100_healthy=False)

    with pytest.raises(RuntimeError, match="No healthy nodes available"):
        route_request(
            [{"role": "user", "content": "hello"}],
            table,
            NODES,
            health,
        )


def test_derive_session_id_is_consistent():
    messages = [{"role": "user", "content": "hello"}]
    id1 = derive_session_id(messages)
    id2 = derive_session_id(messages)
    assert id1 == id2


def test_derive_session_id_differs_for_diff_content():
    id1 = derive_session_id([{"role": "user", "content": "hello"}])
    id2 = derive_session_id([{"role": "user", "content": "world"}])
    assert id1 != id2


def test_estimate_request_tokens():
    tokens = estimate_request_tokens(
        [{"role": "user", "content": "hello world"}], chars_per_token=4.0
    )
    assert tokens == 2  # 11 chars / 4 = 2.75 -> int = 2


def test_affinity_overrides_long_prompt():
    table = SessionTable()
    table.register("sess_abc", "p100", 0, n_past=100)
    health = make_health()

    decision = route_request(
        [{"role": "user", "content": "x" * 20000}],
        table,
        NODES,
        health,
        long_prompt_threshold=4096,
        session_id="sess_abc",
    )

    assert decision.node_name == "p100"


def test_n_tokens_guard_metadata():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0, n_past=100)
    health = make_health()

    decision = route_request(
        [{"role": "user", "content": "hi"}],
        table,
        NODES,
        health,
        session_id="sess_abc",
    )

    assert decision.n_past == 100
    assert estimate_request_tokens([{"role": "user", "content": "hi"}], 4.0) <= 100
