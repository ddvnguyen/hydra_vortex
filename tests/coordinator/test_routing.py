import pytest
from coordinator.routing import (
    derive_session_id,
    estimate_request_tokens,
    compute_prefix_hash,
    pick_best_prefill_worker,
    pick_best_decode_worker,
    pick_best_mixed_worker,
    WORKER_PREFILL,
    WORKER_DECODE,
    WORKER_MIXED,
)
from coordinator.worker_tracker import WorkerTracker
from coordinator.config import WorkerNodeConfig


RTX = WorkerNodeConfig(
    name="rtx", host="127.0.0.1", rpc_port=9601, llama_url="http://localhost:8080",
    worker_type=WORKER_MIXED, slots=2, prefill_priority=1, decode_priority=2,
    decode_speed_tps=200, max_prefill_tokens=-1,
)
P100 = WorkerNodeConfig(
    name="p100", host="192.168.122.21", rpc_port=9602, llama_url="http://192.168.122.21:8086",
    worker_type=WORKER_MIXED, slots=1, prefill_priority=2, decode_priority=1,
    decode_speed_tps=28, max_prefill_tokens=8000,
)
WORKERS = [RTX, P100]


class MockHealth:
    def __init__(self):
        self._healthy: dict[str, bool] = {}

    def set_healthy(self, name: str, healthy: bool = True):
        self._healthy[name] = healthy

    def is_healthy(self, name: str) -> bool:
        return self._healthy.get(name, True)

    def get_node_info(self, name: str):
        return None


def make_tracker() -> WorkerTracker:
    t = WorkerTracker()
    for w in WORKERS:
        t.init_worker(w.name)
    return t


def test_derive_session_id_is_consistent():
    messages = [{"role": "user", "content": "hello"}]
    assert derive_session_id(messages) == derive_session_id(messages)


def test_derive_session_id_differs_for_diff_content():
    assert derive_session_id([{"role": "user", "content": "hello"}]) != \
           derive_session_id([{"role": "user", "content": "world"}])


def test_estimate_request_tokens():
    tokens = estimate_request_tokens([{"role": "user", "content": "hello world"}], chars_per_token=4.0)
    assert tokens == 2


def test_compute_prefix_hash_no_system():
    msgs = [{"role": "user", "content": "hello"}]
    assert compute_prefix_hash(msgs) is None


def test_compute_prefix_hash_with_system():
    msgs = [{"role": "system", "content": "you are a bot"}, {"role": "user", "content": "hi"}]
    h = compute_prefix_hash(msgs)
    assert h is not None
    assert len(h) == 16


def test_pick_best_prefill_worker_respects_priority():
    health = MockHealth()
    tracker = make_tracker()
    high_prio = WorkerNodeConfig(
        name="fast", host="10.0.0.1", rpc_port=9603, llama_url="http://10.0.0.1:8080",
        worker_type=WORKER_PREFILL, slots=1, prefill_priority=1,
    )
    low_prio = WorkerNodeConfig(
        name="slow", host="10.0.0.2", rpc_port=9604, llama_url="http://10.0.0.2:8080",
        worker_type=WORKER_MIXED, slots=2, prefill_priority=3,
    )
    tracker.init_worker("fast")
    tracker.init_worker("slow")
    result = pick_best_prefill_worker([high_prio, low_prio], tracker, health)
    assert result is not None
    assert result.name == "fast"


def test_pick_best_decode_worker_excludes_node():
    health = MockHealth()
    tracker = make_tracker()
    result = pick_best_decode_worker(WORKERS, tracker, health, exclude="rtx")
    assert result is not None
    assert result.name == "p100"


def test_pick_best_decode_worker_respects_priority():
    health = MockHealth()
    tracker = make_tracker()
    result = pick_best_decode_worker(WORKERS, tracker, health)
    assert result is not None
    assert result.name == "p100"  # decode_priority=1


def test_pick_best_prefill_worker_respects_max_tokens():
    health = MockHealth()
    tracker = make_tracker()
    # P100 has max_prefill_tokens=8000, RTX has -1 (unlimited)
    result = pick_best_prefill_worker(WORKERS, tracker, health, max_tokens=25000)
    assert result is not None
    assert result.name == "rtx"


def test_pick_best_mixed_worker_skips_busy_node():
    health = MockHealth()
    tracker = make_tracker()
    tracker.acquire("rtx", "prefill")
    result = pick_best_mixed_worker(WORKERS, tracker, health)
    assert result is not None
    assert result.name == "p100"


def test_pick_best_mixed_worker_returns_none_if_all_busy():
    health = MockHealth()
    tracker = make_tracker()
    tracker.acquire("rtx", "prefill")
    tracker.acquire("p100", "decode")
    result = pick_best_mixed_worker(WORKERS, tracker, health)
    assert result is None


def test_pick_best_prefill_worker_skips_unhealthy():
    health = MockHealth()
    health.set_healthy("rtx", False)
    tracker = make_tracker()
    result = pick_best_prefill_worker(WORKERS, tracker, health)
    assert result is not None
    assert result.name == "p100"


def test_pick_best_prefill_worker_all_unhealthy():
    health = MockHealth()
    health.set_healthy("rtx", False)
    health.set_healthy("p100", False)
    tracker = make_tracker()
    result = pick_best_prefill_worker(WORKERS, tracker, health)
    assert result is None


def test_decode_only_worker_not_selected_for_prefill():
    health = MockHealth()
    tracker = make_tracker()
    decode_only = WorkerNodeConfig(
        name="decode_gpu", host="10.0.0.3", rpc_port=9605, llama_url="http://10.0.0.3:8080",
        worker_type=WORKER_DECODE, slots=2, decode_priority=1,
    )
    tracker.init_worker("decode_gpu")
    result = pick_best_prefill_worker([decode_only, RTX], tracker, health)
    assert result is not None
    assert result.name == "rtx"
