from contextlib import asynccontextmanager

from fastapi import FastAPI

from python_shared.log_config import get_logger, setup_logging
from coordinator.config import CoordinatorConfig
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.router import create_router

log = get_logger()


def _make_lifespan(health_monitor: HealthMonitor, config: CoordinatorConfig):
    """Return a lifespan context manager that starts/stops the health monitor."""
    @asynccontextmanager
    async def lifespan(app: FastAPI):
        setup_logging(config.log_level)
        log.info(
            "coordinator_start",
            nodes=[n.name for n in config.nodes],
            store_host=config.store_host,
            store_port=config.store_port,
        )
        await health_monitor.start()
        yield
        await health_monitor.stop()
        log.info("coordinator_stopped")

    return lifespan


def create_app(config: CoordinatorConfig | None = None) -> FastAPI:
    """
    Create the FastAPI application.

    All singletons (SessionTable, HealthMonitor, StateManager) are created once here
    and shared directly with both the router and the lifespan hook.  The previous
    implementation created separate instances in lifespan() vs create_router(), so the
    router's session_table was always empty regardless of registered sessions.
    """
    if config is None:
        config = CoordinatorConfig()

    # Singletons — created once, shared everywhere.
    session_table = SessionTable()
    health_monitor = HealthMonitor(
        nodes=config.nodes,
        poll_interval_s=config.health_poll_interval_s,
        max_failures=config.health_max_failures,
    )
    state_manager = StateManager(
        session_table=session_table,
        store_host=config.store_host,
        store_port=config.store_port,
    )

    app = FastAPI(
        title="Hydra Coordinator",
        version="0.2.0",
        lifespan=_make_lifespan(health_monitor, config),
    )
    app.state.config = config
    app.state.session_table = session_table
    app.state.health_monitor = health_monitor
    app.state.state_manager = state_manager

    router = create_router(
        config=config,
        session_table=session_table,
        health_monitor=health_monitor,
        state_manager=state_manager,
    )
    app.include_router(router)

    return app
