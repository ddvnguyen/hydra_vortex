import os
from pydantic import BaseModel, Field, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict, JsonConfigSettingsSource


class WorkerNodeConfig(BaseModel):
    name: str
    host: str
    rpc_port: int
    llama_url: str
    # Bitwise worker type: 1=prefill, 2=decode, 3=mixed (prefill+decode)
    worker_type: int = 3
    slots: int = 1
    # Lower priority number = preferred (1 is best, ties allowed)
    prefill_priority: int = 1
    decode_priority: int = 1
    # Estimated decode speed for smart scheduling
    decode_speed_tps: float = 30.0


class CoordinatorConfig(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="HYDRA_COORD_")

    host: str = "0.0.0.0"
    port: int = 9000
    log_level: str = "INFO"

    # Worker nodes — required. Set via HYDRA_COORD_WORKERS (JSON array) or
    # HYDRA_COORD_CONFIG_FILE (path to a JSON file containing this config).
    workers: list[WorkerNodeConfig] = Field(default_factory=list)

    store_host: str = "127.0.0.1"
    store_port: int = 9500

    health_poll_interval_s: int = 20
    health_max_failures: int = 3

    chars_per_token: float = 4.0
    long_prompt_threshold: int = 8192

    # Upstream llama-server request read budget. Must be >= the worst-case prefill time
    # for a large prompt, otherwise the coordinator kills the request mid-prefill and the
    # client retries → endless re-prefill loop (see #134). Align with llama's --timeout.
    llama_request_timeout_s: int = 1800

    session_idle_timeout_s: int = 3600

    max_tokens_default: int = 512

    prefix_checkpoint_name: str = "system_prompt"
    prefix_checkpoint_enabled: bool = True

    # "fast": one node handles both prefill and decode (minimises KV migration)
    # "concurrency": P/D disaggregation — prefill worker → store → decode worker
    run_mode: str = "concurrency"

    # If a worker has been busy for less than this many seconds, the scheduler
    # considers it "just started" and routes new requests elsewhere.
    smart_schedule_wait_threshold_s: float = 3.0

    @classmethod
    def settings_customise_sources(cls, settings_cls, **kwargs):
        sources = tuple(kwargs.values())
        config_path = os.environ.get("HYDRA_COORD_CONFIG_FILE", "")
        if config_path and os.path.exists(config_path):
            return sources + (JsonConfigSettingsSource(settings_cls, json_file=config_path),)
        return sources

    @model_validator(mode="after")
    def _validate_workers(self):
        if not self.workers:
            raise ValueError(
                "No workers configured. Set HYDRA_COORD_WORKERS (JSON array) "
                "or HYDRA_COORD_CONFIG_FILE pointing to a JSON config file."
            )
        return self
