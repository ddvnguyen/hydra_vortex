import pytest
import coordinator.routing as _routing_module
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


def test_derive_session_id_stable_across_turns():
    """Turn 2+ messages include the full history. ID must match turn 1 so affinity routing works."""
    turn1 = [{"role": "user", "content": "hello"}]
    turn2 = [
        {"role": "user", "content": "hello"},
        {"role": "assistant", "content": "hi there"},
        {"role": "user", "content": "what is 2+2?"},
    ]
    assert derive_session_id(turn1) == derive_session_id(turn2)


def test_affinity_on_second_turn():
    """Second turn of a conversation (without explicit session_id) must hit affinity routing."""
    table = SessionTable()
    health = make_health()

    # Turn 1 — new session routed somewhere.
    # router.py uses: sess_id = decision.session_id or derive_session_id(messages)
    # Replicate that logic here so the registered ID matches what derive produces.
    turn1 = [{"role": "user", "content": "hello"}]
    d1 = route_request(turn1, table, NODES, health)
    sess_id = d1.session_id or derive_session_id(turn1)
    table.register(sess_id, d1.node_name, 0, n_past=50)

    # Turn 2 — full history sent (standard OpenAI client behaviour).
    # derive_session_id must produce the same ID as turn 1 → affinity hit.
    turn2 = [
        {"role": "user", "content": "hello"},
        {"role": "assistant", "content": "hi"},
        {"role": "user", "content": "follow-up question"},
    ]
    d2 = route_request(turn2, table, NODES, health)
    assert d2.session_found is True, "turn 2 must find the existing session"
    assert d2.node_name == d1.node_name, "turn 2 must route to same node (affinity)"


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


def test_round_robin_distributes_across_idle_nodes():
    """When both nodes are idle (load tied at 0), consecutive new sessions
    must alternate between nodes — P100 must eventually get a turn."""
    table = SessionTable()
    health = make_health(rtx_idle=2, p100_idle=2)

    # Reset counter so the test is deterministic regardless of run order
    _routing_module._rr_counter = 0

    nodes_seen = set()
    for _ in range(4):
        decision = route_request(
            [{"role": "user", "content": "hi"}],
            SessionTable(),
            NODES,
            health,
        )
        nodes_seen.add(decision.node_name)

    assert "rtx" in nodes_seen, "RTX should receive at least one session"
    assert "p100" in nodes_seen, "P100 should receive at least one session (round-robin)"


def test_load_fraction_prefers_less_loaded_node():
    """When RTX has 1 busy slot and P100 is fully idle, P100 wins even though
    RTX still has a free slot — fractional load ensures fair comparison."""
    table = SessionTable()
    # RTX: 1/2 slots busy (load_fraction=0.5), P100: 0/1 slots busy (0.0)
    health = {
        "rtx": {"healthy": True, "slots_total": 2, "slots_idle": 1, "gpu_type": "rtx5060ti"},
        "p100": {"healthy": True, "slots_total": 1, "slots_idle": 1, "gpu_type": "p100"},
    }
    decision = route_request(
        [{"role": "user", "content": "short prompt"}],
        table,
        NODES,
        health,
    )
    assert decision.node_name == "p100"
