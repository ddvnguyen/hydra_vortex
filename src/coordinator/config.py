from pydantic import Field, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict
from typing import Literal


class NodeConfig(BaseSettings):
    model_config = SettingsConfigDict()

    name: str = "rtx"
    host: str = "127.0.0.1"
    rpc_port: int = 9601
    llama_url: str = "http://localhost:8080"
    gpu_type: Literal["rtx5060ti", "p100"] = "rtx5060ti"


class CoordinatorConfig(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="HYDRA_COORD_")

    host: str = "0.0.0.0"
    port: int = 9000
    log_level: str = "INFO"

    # Legacy two-node env fields (docker-compose sets HYDRA_COORD_RTX_*/P100_*).
    # Kept for deployment compatibility; when `nodes` is not supplied explicitly it
    # is derived from these by the validator below.
    rtx_host: str = "127.0.0.1"
    rtx_port: int = 9601
    rtx_llama_url: str = "http://localhost:8080"
    p100_host: str = "192.168.122.21"
    p100_port: int = 9602
    p100_llama_url: str = "http://192.168.122.21:8086"

    # Preferred node list (enables N-node setups). Empty => derived from the
    # rtx_/p100_ fields above. Construct explicitly in tests / multi-node configs.
    nodes: list[NodeConfig] = Field(default_factory=list)

    store_host: str = "127.0.0.1"
    store_port: int = 9500

    health_poll_interval_s: int = 10
    health_max_failures: int = 3

    chars_per_token: float = 4.0
    long_prompt_threshold: int = 4096

    session_idle_timeout_s: int = 3600

    max_tokens_default: int = 512

    prefix_checkpoint_name: str = "system_prompt"
    prefix_checkpoint_enabled: bool = True

    @model_validator(mode="after")
    def _derive_nodes(self):
        if not self.nodes:
            self.nodes = [
                NodeConfig(name="rtx", host=self.rtx_host, rpc_port=self.rtx_port, llama_url=self.rtx_llama_url, gpu_type="rtx5060ti"),
                NodeConfig(name="p100", host=self.p100_host, rpc_port=self.p100_port, llama_url=self.p100_llama_url, gpu_type="p100"),
            ]
        return self
