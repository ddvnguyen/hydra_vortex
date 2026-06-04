import asyncio
import json
import time
from dataclasses import dataclass
from typing import Optional

import httpx

from python_shared.log_config import get_logger, new_trace_id
from python_shared.rpc_client import RpcClient, OpCode, RpcError, StatusCode
from coordinator.config import WorkerNodeConfig

log = get_logger()


def _extract_health_data(resp) -> dict:
    if resp.meta.get("component") == "health" and resp.payload:
        return json.loads(resp.payload)
    return resp.meta


@dataclass
class NodeInfo:
    healthy: bool = False
    node_name: str = ""
    slots_total: int = 0
    slots_idle: int = 0
    gpu_type: str = ""
    llama_url: str = ""
    consecutive_failures: int = 0
    last_check: float = 0.0
    stuck_slots: int = 0


class HealthMonitor:
    def __init__(
        self,
        nodes: list[WorkerNodeConfig],
        poll_interval_s: int = 10,
        max_failures: int = 3,
        store_host: Optional[str] = None,
        store_port: Optional[int] = None,
    ):
        self._nodes = {n.name: NodeInfo() for n in nodes}
        self._node_configs = {n.name: n for n in nodes}
        self._poll_interval = poll_interval_s
        self._max_failures = max_failures
        self._task: Optional[asyncio.Task] = None
        # Store liveness — probed each poll via a cheap Stat on a sentinel key.
        # None host means "not configured" → treated as healthy (no probe).
        self._store_host = store_host
        self._store_port = store_port
        self._store_healthy = store_host is None
        self._store_failures = 0
        self._store_last_check = 0.0

    @property
    def stuck_slots(self) -> dict[str, int]:
        result = {}
        for name, info in self._nodes.items():
            if info.stuck_slots > 0:
                result[name] = info.stuck_slots
        return result

    async def _check_stuck_slots(self, node_name: str, llama_url: str):
        try:
            async with httpx.AsyncClient(timeout=5) as client:
                resp = await client.get(
                    f"{llama_url.rstrip('/')}/slots",
                    headers={"X-Trace-Id": "health-stuck-check"},
                )
                if resp.status_code == 200:
                    data = resp.json()
                    slots = data if isinstance(data, list) else data.get("slots", [])
                    stuck = sum(
                        1 for s in slots
                        if s.get("is_processing") and s.get("n_remain", 1) == 0
                    )
                    info = self._nodes.get(node_name)
                    if info:
                        info.stuck_slots = stuck
                        if stuck > 0:
                            log.warning("stuck_slots_detected", node=node_name, count=stuck)
                else:
                    info = self._nodes.get(node_name)
                    if info:
                        info.stuck_slots = 0
        except Exception:
            info = self._nodes.get(node_name)
            if info:
                info.stuck_slots = 0

    async def start(self):
        await self._poll_all()
        self._task = asyncio.create_task(self._poll_loop())

    async def stop(self):
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass

    async def _poll_loop(self):
        while True:
            try:
                await asyncio.sleep(self._poll_interval)
                await self._poll_all()
            except asyncio.CancelledError:
                break
            except Exception:
                log.exception("health_poll_error")

    async def _poll_store(self):
        """Probe Store liveness with a cheap Stat on a sentinel key. A NotFound
        response still proves the Store is up — only a connection failure (after the
        RpcClient's own retries) counts against us."""
        if self._store_host is None or self._store_port is None:
            return
        try:
            trace_id = new_trace_id()
            client = RpcClient(self._store_host, self._store_port)
            try:
                await client.request(OpCode.Stat, "__health__", trace_id=trace_id)
            except RpcError as e:
                if e.status != StatusCode.NotFound:
                    raise
            finally:
                await client.close()
            self._store_healthy = True
            self._store_failures = 0
            self._store_last_check = time.time()
        except Exception as e:
            self._store_failures += 1
            if self._store_failures >= self._max_failures:
                self._store_healthy = False
            log.warning(
                "store_health_fail",
                failures=self._store_failures,
                error=str(e) if not isinstance(e, type) else type(e).__name__,
            )

    async def _poll_all(self):
        await self._poll_store()
        for node_name, config in self._node_configs.items():
            info = self._nodes[node_name]
            try:
                trace_id = new_trace_id()
                client = RpcClient(config.host, config.rpc_port)
                try:
                    resp = await client.request(OpCode.NodeHealth, "", trace_id=trace_id)
                finally:
                    await client.close()

                info.healthy = True
                health_data = _extract_health_data(resp)
                info.node_name = health_data.get("node_name", node_name)
                info.slots_total = health_data.get("slots_total", 0)
                info.slots_idle = health_data.get("slots_idle", 0)
                info.gpu_type = health_data.get("gpu_type", "")
                info.llama_url = health_data.get("llama_url", config.llama_url)
                info.consecutive_failures = 0
                info.last_check = time.time()

                await self._check_stuck_slots(node_name, config.llama_url)

                log.info("health_ok", node=node_name)
            except Exception as e:
                info.consecutive_failures += 1
                if info.consecutive_failures >= self._max_failures:
                    info.healthy = False
                log.warning(
                    "health_fail",
                    node=node_name,
                    failures=info.consecutive_failures,
                    error=str(e) if not isinstance(e, type) else type(e).__name__,
                )

    def is_healthy(self, node_name: str) -> bool:
        info = self._nodes.get(node_name)
        if not info:
            return False
        if not info.healthy:
            return False
        if info.stuck_slots > 0:
            return False
        return True

    def is_store_healthy(self) -> bool:
        return self._store_healthy

    def store_health(self) -> dict:
        return {
            "healthy": self._store_healthy,
            "consecutive_failures": self._store_failures,
            "last_check": self._store_last_check,
        }

    def get_node_info(self, node_name: str) -> Optional[NodeInfo]:
        return self._nodes.get(node_name)

    def get_health_summary(self) -> dict:
        summary = {}
        for name, info in self._nodes.items():
            summary[name] = {
                "healthy": self.is_healthy(name),
                "slots_total": info.slots_total,
                "slots_idle": info.slots_idle,
                "gpu_type": info.gpu_type,
                "consecutive_failures": info.consecutive_failures,
                "stuck_slots": info.stuck_slots,
            }
        return summary

    @property
    def all_healthy(self) -> bool:
        return all(info.healthy for info in self._nodes.values())
