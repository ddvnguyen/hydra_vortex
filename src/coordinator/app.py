import asyncio
from contextlib import asynccontextmanager

from fastapi import FastAPI

from coordinator.lib.log_config import get_logger, setup_logging
from coordinator.config import CoordinatorConfig, WorkerNodeConfig
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
                   scheduler: WorkerScheduler, config: CoordinatorConfig,
                   state_manager: StateManager):
    @asynccontextmanager
    async def lifespan(app: FastAPI):
        setup_logging(config.log_level)

        workers_by_name = {w.name: w for w in config.workers}
        eviction_task = asyncio.create_task(
            _eviction_loop(session_table, state_manager, workers_by_name,
                           config.session_idle_timeout_s)
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


async def _eviction_loop(
    session_table: SessionTable,
    state_manager: StateManager,
    workers_by_name: dict[str, WorkerNodeConfig],
    timeout_s: int,
    interval_s: int = 60,
):
    # Best-effort eviction: save stale sessions to Store before removing them
    # from the in-memory table. If save fails (agent down, store unreachable),
    # the KV state is lost — but the session is still removed to prevent
    # unbounded memory growth in the coordinator.
    try:
        while True:
            await asyncio.sleep(interval_s)
            stale = session_table.get_stale_session_ids(timeout_s)
            if not stale:
                continue
            for sid in stale:
                entry = session_table.lookup(sid)
                if not entry:
                    continue
                wc = workers_by_name.get(entry.node_name)
                if wc:
                    try:
                        await state_manager.save_session(sid, wc.host, wc.rpc_port)
                    except Exception as e:
                        log.warning("evict_stale_save_failed",
                                    session_id=sid, node=entry.node_name, error=str(e))
                else:
                    log.warning("evict_stale_worker_not_found",
                                session_id=sid, node=entry.node_name)
                session_table.remove(sid)
            log.info("evicted_stale_sessions",
                     count=len(stale), timeout_s=timeout_s)
    except asyncio.CancelledError:
        pass


def create_app(config: CoordinatorConfig | None = None) -> FastAPI:
    if config is None:
        config = CoordinatorConfig()

    configure_timeout(config.llama_request_timeout_s)

    session_table = SessionTable()
    store_debug_url = f"http://{config.store_host}:{config.store_port}/debug?sessions=1"

    log.info(
        "coordinator_start",
        version=VERSION,
        revision=REVISION,
        workers=[w.name for w in config.workers],
        store_host=config.store_host,
        store_port=config.store_port,
    )

    # Restore session table from Store (PG) on startup — fire and forget since
    # the Store may still be booting. The background _background_restore_sessions
    # in the lifespan retries until successful.
    def _restore_sessions():
        try:
            import httpx
            resp = httpx.get(store_debug_url, timeout=5.0)
            data = resp.json()
            for s in data.get("sessions", []):
                sid = s.get("session_id", "")
                n_past = s.get("n_past", 0)
                if sid and n_past > 0:
                    session_table.register(sid, "", None, n_past=n_past)
                    entry = session_table.lookup(sid)
                    if entry:
                        entry.has_store_state = True
            log.info("session_table_restored", count=session_table.active_count)
        except Exception as e:
            log.warning("session_table_restore_failed", error=str(e))

    _restore_sessions()
    tracker = WorkerTracker(_error_threshold=config.worker_error_threshold)
    health_monitor = HealthMonitor(
        nodes=config.workers,
        poll_interval_s=config.health_poll_interval_s,
        max_failures=config.health_max_failures,
        store_host=config.store_host,
        store_port=config.store_port,
        tracker=tracker,
    )
    state_manager = StateManager(
        session_table=session_table,
        store_host=config.store_host,
        store_port=config.store_port,
    )
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
        lifespan=_make_lifespan(session_table, health_monitor, scheduler, config, state_manager),
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
