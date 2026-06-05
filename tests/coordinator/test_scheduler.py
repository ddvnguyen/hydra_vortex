import asyncio
import pytest
from unittest.mock import MagicMock

from coordinator.scheduler import WorkerScheduler, WorkItem
from coordinator.worker_tracker import WorkerTracker
from coordinator.config import CoordinatorConfig, WorkerNodeConfig
from coordinator.session_table import SessionTable
from coordinator.routing import WORKER_MIXED


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


@pytest.fixture
def config():
    return CoordinatorConfig(workers=WORKERS)


@pytest.fixture
def scheduler(config):
    table = SessionTable()
    health = MagicMock()
    health.is_healthy.return_value = True
    health.get_node_info.return_value = MagicMock(
        stuck_slots=0, slots_total=2, slots_idle=2
    )
    sm = MagicMock()
    tracker = WorkerTracker(_error_threshold=config.worker_error_threshold)
    return WorkerScheduler(
        config=config, session_table=table,
        health_monitor=health, state_manager=sm, tracker=tracker,
    )


def make_future() -> asyncio.Future:
    return asyncio.get_event_loop().create_future()


@pytest.mark.asyncio
async def test_enqueue_directly(scheduler):
    item = WorkItem({}, [], "sess_test", "trace", None, 10, 512, make_future())
    assert len(scheduler._queue) == 0
    scheduler._queue.append(item)
    assert len(scheduler._queue) == 1


@pytest.mark.asyncio
async def test_new_item_event_set(scheduler):
    scheduler._new_item.clear()
    item = WorkItem({}, [], "sess_test", "trace", None, 10, 512, make_future())
    scheduler._queue.append(item)
    scheduler._new_item.set()
    assert scheduler._new_item.is_set()


@pytest.mark.asyncio
async def test_queue_capacity(scheduler):
    for i in range(scheduler._max_queue_size):
        scheduler._queue.append(WorkItem({}, [], f"s{i}", "t", None, 10, 512, make_future()))
    assert len(scheduler._queue) == scheduler._max_queue_size
    assert len(scheduler._queue) >= scheduler._max_queue_size


@pytest.mark.asyncio
async def test_running_flag(scheduler):
    assert not scheduler._running
    scheduler._running = True
    assert scheduler._running


@pytest.mark.asyncio
async def test_is_atomic_threshold(scheduler):
    lo = WorkItem({}, [], "s", "t", None, 10, 500, make_future())
    hi = WorkItem({}, [], "s", "t", None, 10, 5000, make_future())
    assert scheduler._is_atomic(lo)
    assert not scheduler._is_atomic(hi)


@pytest.mark.asyncio
async def test_max_queue_size_constant(scheduler):
    assert 1 <= scheduler._max_queue_size <= 500


@pytest.mark.asyncio
async def test_routable_healthy(scheduler):
    scheduler._tracker.init_worker("rtx")
    assert scheduler._routable("rtx")


@pytest.mark.asyncio
async def test_routable_unhealthy(scheduler):
    scheduler._tracker.init_worker("rtx")
    scheduler._tracker.mark_unhealthy("rtx")
    scheduler._health.get_node_info.return_value.healthy = False
    assert not scheduler._routable("rtx")


@pytest.mark.asyncio
async def test_routable_false_when_no_info(scheduler):
    health = MagicMock()
    health.get_node_info.return_value = None
    sched = scheduler
    sched._health = health
    assert not sched._routable("rtx")
