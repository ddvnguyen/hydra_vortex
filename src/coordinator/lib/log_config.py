import structlog
import uuid
import logging


def _drop_internal_keys(logger, name, event_dict):
    event_dict.pop("_log_level", None)
    return event_dict


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
            _drop_internal_keys,
            structlog.processors.KeyValueRenderer(sort_keys=False, repr_native_str=False),
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
