import uvicorn
from coordinator.config import CoordinatorConfig
from coordinator.app import create_app


def run():
    config = CoordinatorConfig()
    app = create_app(config)
    uvicorn.run(
        app,
        host=config.host,
        port=config.port,
        log_level=config.log_level.lower(),
    )


if __name__ == "__main__":
    run()
