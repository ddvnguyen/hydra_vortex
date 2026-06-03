import pytest
import coordinator.routing as _routing_module
from coordinator.routing import (
    route_request,
    derive_session_id,
    estimate_request_tokens,
    select_prefill_worker,
    select_decode_worker,
    WORKER_PREFILL,
    WORKER_DECODE,
    WORKER_MIXED,
)
from coordinator.session_table import SessionTable
from coordinator.config import WorkerNodeConfig


RTX = WorkerNodeConfig(
    name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080",
    worker_type=WORKER_MIXED, slots=2, prefill_priority=1, decode_priority=2, decode_speed_tps=200,
)
P100 = WorkerNodeConfig(
    name="p100", host="192.168.122.21", rpc_port=9602, llama_url="http://192.168.122.21:8086",
    worker_type=WORKER_MIXED, slots=1, prefill_priority=2, decode_priority=1, decode_speed_tps=28,
)
WORKERS = [RTX, P100]


def make_health(rtx_healthy=True, rtx_idle=1, p100_healthy=True, p100_idle=1):
    return {
        "rtx": {"healthy": rtx_healthy, "slots_total": 2, "slots_idle": rtx_idle},
        "p100": {"healthy": p100_healthy, "slots_total": 2, "slots_idle": p100_idle},
    }


def test_affinity_hit_returns_same_node():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0, n_past=100)
    decision = route_request(
        [{"role": "user", "content": "hello"}], table, WORKERS, make_health(), session_id="sess_abc"
    )
    assert decision.node_name == "rtx"
    assert decision.session_found is True


def test_evicted_session_with_store_restore():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0, n_past=100)
    table.mark_evicted("sess_abc")
    table.update_n_past("sess_abc", 100)
    decision = route_request(
        [{"role": "user", "content": "hello"}], table, WORKERS,
        make_health(rtx_healthy=False), session_id="sess_abc"
    )
    assert decision.action == "store_restore"
    assert decision.session_found is True


def test_long_prompt_routes_to_prefill_worker():
    # RTX has prefill_priority=1 (better than P100's 2)
    decision = route_request(
        [{"role": "user", "content": "x" * 20000}],
        SessionTable(), WORKERS, make_health(), long_prompt_threshold=4096,
    )
    assert decision.node_name == "rtx"


def test_prefill_only_worker_selected_for_long_prompt():
    prefill_only = WorkerNodeConfig(
        name="prefill_gpu", host="10.0.0.1", rpc_port=9603, llama_url="http://10.0.0.1:8080",
        worker_type=WORKER_PREFILL, slots=1, prefill_priority=1,
    )
    workers = [prefill_only, RTX, P100]
    health = {
        "prefill_gpu": {"healthy": True, "slots_total": 1, "slots_idle": 1},
        "rtx": {"healthy": True, "slots_total": 2, "slots_idle": 2},
        "p100": {"healthy": True, "slots_total": 1, "slots_idle": 1},
    }
    decision = route_request(
        [{"role": "user", "content": "x" * 20000}],
        SessionTable(), workers, health, long_prompt_threshold=4096,
    )
    assert decision.node_name == "prefill_gpu"


def test_short_prompt_rtx_busy_routes_to_p100():
    decision = route_request(
        [{"role": "user", "content": "short"}],
        SessionTable(), WORKERS, make_health(rtx_idle=0),
    )
    assert decision.node_name == "p100"


def test_both_nodes_down_raises_error():
    with pytest.raises(RuntimeError, match="No healthy workers available"):
        route_request(
            [{"role": "user", "content": "hello"}],
            SessionTable(), WORKERS, make_health(rtx_healthy=False, p100_healthy=False),
        )


def test_derive_session_id_is_consistent():
    messages = [{"role": "user", "content": "hello"}]
    assert derive_session_id(messages) == derive_session_id(messages)


def test_derive_session_id_differs_for_diff_content():
    assert derive_session_id([{"role": "user", "content": "hello"}]) != \
           derive_session_id([{"role": "user", "content": "world"}])


def test_estimate_request_tokens():
    tokens = estimate_request_tokens([{"role": "user", "content": "hello world"}], chars_per_token=4.0)
    assert tokens == 2  # 11 chars / 4 = 2.75 -> int = 2


def test_affinity_overrides_long_prompt():
    table = SessionTable()
    table.register("sess_abc", "p100", 0, n_past=100)
    decision = route_request(
        [{"role": "user", "content": "x" * 20000}],
        table, WORKERS, make_health(), long_prompt_threshold=4096, session_id="sess_abc",
    )
    assert decision.node_name == "p100"


def test_n_tokens_guard_metadata():
    table = SessionTable()
    table.register("sess_abc", "rtx", 0, n_past=100)
    decision = route_request(
        [{"role": "user", "content": "hi"}], table, WORKERS, make_health(), session_id="sess_abc"
    )
    assert decision.n_past == 100
    assert estimate_request_tokens([{"role": "user", "content": "hi"}], 4.0) <= 100


def test_round_robin_distributes_across_idle_nodes():
    """When both nodes are idle (load tied at 0), round-robin must give P100 a turn."""
    _routing_module._rr_counter = 0
    health = make_health(rtx_idle=2, p100_idle=2)
    nodes_seen = set()
    for _ in range(4):
        d = route_request([{"role": "user", "content": "hi"}], SessionTable(), WORKERS, health)
        nodes_seen.add(d.node_name)
    assert "rtx" in nodes_seen
    assert "p100" in nodes_seen


def test_load_fraction_prefers_less_loaded_node():
    health = {
        "rtx": {"healthy": True, "slots_total": 2, "slots_idle": 1},
        "p100": {"healthy": True, "slots_total": 1, "slots_idle": 1},
    }
    decision = route_request(
        [{"role": "user", "content": "short prompt"}], SessionTable(), WORKERS, health
    )
    assert decision.node_name == "p100"


def test_in_flight_blocks_double_booking():
    """P100 with in-flight=1 must not get another request even if health shows idle."""
    _routing_module._rr_counter = 0
    health = make_health(rtx_idle=2, p100_idle=1)
    decision = route_request(
        [{"role": "user", "content": "hi"}], SessionTable(), WORKERS, health,
        in_flight={"rtx": 0, "p100": 1},
    )
    assert decision.node_name == "rtx"


def test_in_flight_routes_to_less_loaded_with_inflight():
    """RTX in-flight=2 pushes its effective load above P100 — routing flips."""
    _routing_module._rr_counter = 0
    health = make_health(rtx_idle=2, p100_idle=1)
    decision = route_request(
        [{"role": "user", "content": "hi"}], SessionTable(), WORKERS, health,
        in_flight={"rtx": 2, "p100": 0},
    )
    assert decision.node_name == "p100"


def test_select_prefill_worker_respects_priority():
    high_prio = WorkerNodeConfig(
        name="fast", host="10.0.0.1", rpc_port=9603, llama_url="http://10.0.0.1:8080",
        worker_type=WORKER_PREFILL, slots=1, prefill_priority=1,
    )
    low_prio = WorkerNodeConfig(
        name="slow", host="10.0.0.2", rpc_port=9604, llama_url="http://10.0.0.2:8080",
        worker_type=WORKER_MIXED, slots=2, prefill_priority=3,
    )
    health = {
        "fast": {"healthy": True, "slots_total": 1, "slots_idle": 1},
        "slow": {"healthy": True, "slots_total": 2, "slots_idle": 2},
    }
    result = select_prefill_worker([high_prio, low_prio], health)
    assert result.name == "fast"


def test_select_decode_worker_excludes_node():
    health = make_health(rtx_idle=2, p100_idle=1)
    result = select_decode_worker(WORKERS, health, exclude="rtx")
    assert result is not None
    assert result.name == "p100"


def test_decode_only_worker_not_selected_for_prefill():
    decode_only = WorkerNodeConfig(
        name="decode_gpu", host="10.0.0.3", rpc_port=9605, llama_url="http://10.0.0.3:8080",
        worker_type=WORKER_DECODE, slots=2, decode_priority=1,
    )
    health = {
        "decode_gpu": {"healthy": True, "slots_total": 2, "slots_idle": 2},
        "rtx": {"healthy": True, "slots_total": 2, "slots_idle": 2},
    }
    result = select_prefill_worker([decode_only, RTX], health)
    assert result is not None
    assert result.name == "rtx"
