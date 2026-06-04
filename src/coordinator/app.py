import asyncio
from contextlib import asynccontextmanager

from fastapi import FastAPI

from python_shared.log_config import get_logger, setup_logging
from coordinator.config import CoordinatorConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router
from coordinator.proxy import shutdown as proxy_shutdown, configure_timeout
from coordinator.worker_tracker import WorkerTracker
from coordinator.scheduler import WorkerScheduler
from coordinator.version import VERSION, REVISION

log = get_logger()


def _make_lifespan(session_table: SessionTable, health_monitor: HealthMonitor,
                   scheduler: WorkerScheduler, config: CoordinatorConfig):
    @asynccontextmanager
    async def lifespan(app: FastAPI):
        setup_logging(config.log_level)
        log.info(
            "coordinator_start",
            version=VERSION,
            revision=REVISION,
            workers=[w.name for w in config.workers],
            store_host=config.store_host,
            store_port=config.store_port,
        )

        eviction_task = asyncio.create_task(
            _eviction_loop(session_table, config.session_idle_timeout_s)
        )

        await health_monitor.start()
        await scheduler.start()
        yield
        await scheduler.stop()
        eviction_task.cancel()
        await health_monitor.stop()
        await proxy_shutdown()
        log.info("coordinator_stopped")

    return lifespan


async def _eviction_loop(session_table: SessionTable, timeout_s: int, interval_s: int = 60):
    try:
        while True:
            await asyncio.sleep(interval_s)
            removed = session_table.evict_stale(timeout_s)
            if removed > 0:
                log.info("evicted_stale_sessions", count=removed, timeout_s=timeout_s)
    except asyncio.CancelledError:
        pass


def create_app(config: CoordinatorConfig | None = None) -> FastAPI:
    if config is None:
        config = CoordinatorConfig()

    configure_timeout(config.llama_request_timeout_s)

    session_table = SessionTable()
    health_monitor = HealthMonitor(
        nodes=config.workers,
        poll_interval_s=config.health_poll_interval_s,
        max_failures=config.health_max_failures,
        store_host=config.store_host,
        store_port=config.store_port,
    )
    state_manager = StateManager(
        session_table=session_table,
        store_host=config.store_host,
        store_port=config.store_port,
    )
    tracker = WorkerTracker(_error_threshold=config.worker_error_threshold)
    scheduler = WorkerScheduler(
        config=config,
        session_table=session_table,
        health_monitor=health_monitor,
        state_manager=state_manager,
        tracker=tracker,
    )

    app = FastAPI(
        title="Hydra Coordinator",
        version=VERSION,
        lifespan=_make_lifespan(session_table, health_monitor, scheduler, config),
    )
    app.state.config = config
    app.state.session_table = session_table
    app.state.health_monitor = health_monitor
    app.state.state_manager = state_manager
    app.state.scheduler = scheduler

    router = create_router(
        config=config,
        session_table=session_table,
        health_monitor=health_monitor,
        state_manager=state_manager,
        scheduler=scheduler,
    )
    app.include_router(router)

    return app
