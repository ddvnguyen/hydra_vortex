import time
from typing import Optional, Union
from pydantic import BaseModel, ConfigDict, field_validator

from fastapi import APIRouter, HTTPException
from fastapi.responses import JSONResponse

from python_shared.log_config import get_logger, new_trace_id
from coordinator.session_table import SessionTable
from coordinator.routing import (
    estimate_request_tokens,
    derive_session_id,
    compute_prefix_hash,
)
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.scheduler import WorkerScheduler
from coordinator.config import CoordinatorConfig
from coordinator.metrics import (
    metrics_endpoint,
    set_worker_busy_metrics,
)
from coordinator.version import VERSION, REVISION

log = get_logger()


class ChatMessage(BaseModel):
    model_config = ConfigDict(extra="allow")

    role: str
    content: Union[str, list, None] = None

    @field_validator("content", mode="before")
    @classmethod
    def flatten_content(cls, v):
        if isinstance(v, list):
            return " ".join(
                part.get("text", "") for part in v
                if isinstance(part, dict) and part.get("type") == "text"
            )
        return v


class ChatCompletionRequest(BaseModel):
    model_config = ConfigDict(extra="allow")

    model: str = "darwin"
    messages: list[ChatMessage]
    max_tokens: int = 512
    temperature: float = 0.86
    top_k: int = 20
    stream: bool = True
    session_id: Optional[str] = None


class MigrateRequest(BaseModel):
    target_node: str


def create_router(
    config: CoordinatorConfig,
    session_table: SessionTable,
    health_monitor: HealthMonitor,
    state_manager: StateManager,
    scheduler: WorkerScheduler,
) -> APIRouter:
    router = APIRouter()
    _start_time = time.time()
    _routing_stats = {"total": 0}

    @router.post("/v1/chat/completions")
    async def chat_completion(req: ChatCompletionRequest):
        _routing_stats["total"] += 1
        request_dict = req.model_dump(exclude={"session_id"})
        messages_dict = [m.model_dump() for m in req.messages]

        sess_id = req.session_id or derive_session_id(messages_dict)
        prefix_hash = compute_prefix_hash(messages_dict)

        return await scheduler.submit(
            req=request_dict,
            messages=messages_dict,
            session_id=sess_id,
            max_tokens=req.max_tokens,
            prefix_hash=prefix_hash,
        )

    @router.get("/metrics")
    async def metrics():
        set_worker_busy_metrics(scheduler)
        return await metrics_endpoint(None)

    @router.get("/version")
    async def version():
        return {"service": "hydra-coordinator", "version": VERSION, "revision": REVISION}

    @router.get("/health")
    async def health():
        summary = health_monitor.get_health_summary()
        store = health_monitor.store_health()
        nodes_ok = all(v["healthy"] for v in summary.values())
        all_ok = nodes_ok and store["healthy"]
        status = "healthy" if all_ok else "degraded"
        return {
            "status": status,
            "version": VERSION,
            "revision": REVISION,
            "nodes": summary,
            "store": store,
        }

    @router.get("/status")
    async def status():
        uptime = time.time() - _start_time
        workers_detail = {}
        for w in config.workers:
            info = health_monitor.get_node_info(w.name)
            if info:
                tracker_status = scheduler._tracker.status(w.name) if hasattr(scheduler, '_tracker') else "unknown"
                workers_detail[w.name] = {
                    "healthy": info.healthy,
                    "slots_total": info.slots_total,
                    "slots_idle": info.slots_idle,
                    "stuck_slots": info.stuck_slots,
                    "tracker_status": tracker_status,
                    "worker_type": w.worker_type,
                    "prefill_priority": w.prefill_priority,
                    "decode_priority": w.decode_priority,
                }
            else:
                workers_detail[w.name] = {"healthy": False}

        return {
            "version": VERSION,
            "revision": REVISION,
            "uptime_s": uptime,
            "run_mode": config.run_mode,
            "queue_size": len(scheduler._queue) if hasattr(scheduler, '_queue') else 0,
            "sessions": {
                "active": session_table.active_count,
                "sessions": [
                    {
                        "session_id": s.session_id,
                        "node": s.node_name,
                        "slot_id": s.slot_id,
                        "n_past": s.n_past,
                        "has_store_state": s.has_store_state,
                        "last_used": s.last_used,
                    }
                    for s in session_table.all_sessions.values()
                ],
            },
            "routing_stats": _routing_stats,
            "nodes": workers_detail,
        }

    @router.get("/sessions")
    async def list_sessions():
        return {
            "sessions": [
                {
                    "session_id": s.session_id,
                    "node": s.node_name,
                    "slot_id": s.slot_id,
                    "n_past": s.n_past,
                    "has_store_state": s.has_store_state,
                }
                for s in session_table.all_sessions.values()
            ]
        }

    @router.delete("/sessions/{session_id}")
    async def evict_session(session_id: str):
        entry = session_table.lookup(session_id)
        if not entry:
            raise HTTPException(status_code=404, detail="Session not found")

        worker_cfg = next((w for w in config.workers if w.name == entry.node_name), None)
        if worker_cfg and entry.slot_id is not None:
            try:
                await state_manager.save_session(session_id, worker_cfg.host, worker_cfg.rpc_port)
            except Exception as e:
                log.warning("evict_save_failed", session_id=session_id, error=str(e))

        session_table.remove(session_id)
        return {"evicted": True, "session_id": session_id}

    @router.post("/sessions/{session_id}/migrate")
    async def migrate_session(session_id: str, req: MigrateRequest):
        entry = session_table.lookup(session_id)
        if not entry:
            raise HTTPException(status_code=404, detail="Session not found")

        from_cfg = next((w for w in config.workers if w.name == entry.node_name), None)
        to_cfg = next((w for w in config.workers if w.name == req.target_node), None)

        if not from_cfg:
            raise HTTPException(status_code=400, detail=f"Source worker {entry.node_name} not configured")
        if not to_cfg:
            raise HTTPException(status_code=400, detail=f"Target worker {req.target_node} not configured")

        try:
            result = await state_manager.migrate_session(
                session_id,
                from_cfg.host, from_cfg.rpc_port,
                to_cfg.host, to_cfg.rpc_port,
                to_cfg.name,
                from_node_name=entry.node_name,
            )
            return {"migrated": True, "session_id": session_id, "target": req.target_node, "result": result}
        except Exception as e:
            log.error("migrate_failed", session_id=session_id, error=str(e))
            raise HTTPException(status_code=500, detail=f"Migration failed: {e}")

    @router.post("/prefix/{checkpoint_name}/save")
    async def save_prefix(checkpoint_name: str, node_name: str = "rtx", slot_id: int = 0):
        worker_cfg = next((w for w in config.workers if w.name == node_name), None)
        if not worker_cfg:
            raise HTTPException(status_code=400, detail=f"Worker {node_name} not configured")
        try:
            result = await state_manager.save_prefix_checkpoint(
                checkpoint_name, worker_cfg.host, worker_cfg.rpc_port, slot_id)
            return {"saved": True, "checkpoint": checkpoint_name, "node": node_name, "result": result}
        except Exception as e:
            raise HTTPException(status_code=500, detail=f"Prefix save failed: {e}")

    @router.post("/prefix/{checkpoint_name}/restore")
    async def restore_prefix(checkpoint_name: str, node_name: str = "p100", slot_id: int = 0):
        worker_cfg = next((w for w in config.workers if w.name == node_name), None)
        if not worker_cfg:
            raise HTTPException(status_code=400, detail=f"Worker {node_name} not configured")
        try:
            result = await state_manager.restore_prefix_checkpoint(
                checkpoint_name, worker_cfg.host, worker_cfg.rpc_port, slot_id)
            return {"restored": True, "checkpoint": checkpoint_name, "node": node_name, "result": result}
        except Exception as e:
            raise HTTPException(status_code=500, detail=f"Prefix restore failed: {e}")

    return router
