import structlog
import uuid
import logging


def setup_logging(level: str = "INFO"):
    logging.basicConfig(
        format="%(message)s",
        level=getattr(logging, level.upper(), logging.INFO),
        force=True,
    )

    structlog.configure(
        processors=[
            structlog.stdlib.filter_by_level,
            structlog.stdlib.add_log_level,
            structlog.processors.TimeStamper(fmt="iso"),
            structlog.dev.ConsoleRenderer(),
        ],
        context_class=dict,
        logger_factory=structlog.stdlib.LoggerFactory(),
        wrapper_class=structlog.stdlib.BoundLogger,
        cache_logger_on_first_use=True,
    )


def new_trace_id() -> str:
    return uuid.uuid4().hex[:16]


def get_logger(name: str = None):
    return structlog.get_logger(name)
