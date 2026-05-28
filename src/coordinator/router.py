import json
import time
from typing import Optional

from fastapi import APIRouter, HTTPException
from fastapi.responses import StreamingResponse, JSONResponse
from pydantic import BaseModel

from python_shared.log_config import get_logger, new_trace_id
from coordinator.session_table import SessionTable
from coordinator.routing import route_request, estimate_request_tokens, derive_session_id
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.proxy import proxy_completion, proxy_completion_stream
from coordinator.config import CoordinatorConfig
from coordinator.metrics import metrics_endpoint, requests_total, active_sessions
from coordinator.version import VERSION, REVISION

log = get_logger()


class ChatMessage(BaseModel):
    role: str
    content: str


class ChatCompletionRequest(BaseModel):
    model: str = "darwin"
    messages: list[ChatMessage]
    max_tokens: int = 512
    temperature: float = 0.86
    top_p: float = 0.95
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
) -> APIRouter:
    router = APIRouter()
    _start_time = time.time()
    _routing_stats = {"total": 0, "affinity": 0, "store_restore": 0, "long_prompt": 0, "least_loaded": 0}

    def node_url(node_name: str) -> str:
        for n in config.nodes:
            if n.name == node_name:
                return n.llama_url
        return ""

    @router.post("/v1/chat/completions")
    async def chat_completion(req: ChatCompletionRequest):
        trace_id = new_trace_id()
        _routing_stats["total"] += 1
        request_ts = time.monotonic()

        request_dict = req.model_dump(exclude={"session_id"})
        messages_dict = [m.model_dump() for m in req.messages]

        try:
            decision = route_request(
                request_messages=messages_dict,
                session_table=session_table,
                nodes=config.nodes,
                health_info=health_monitor.get_health_summary(),
                chars_per_token=config.chars_per_token,
                long_prompt_threshold=config.long_prompt_threshold,
                session_id=req.session_id,
            )
        except RuntimeError as e:
            raise HTTPException(status_code=503, detail=str(e))

        sess_id = decision.session_id or derive_session_id(messages_dict)
        requests_total.labels(node=decision.node_name, reason=decision.action).inc()
        for n in config.nodes:
            active_sessions.labels(node=n.name).set(session_table.active_count_on_node(n.name))

        if decision.action == "store_restore":
            try:
                await state_manager.restore_session(
                    sess_id,
                    decision.node_config.host,
                    decision.node_config.rpc_port,
                )
            except Exception as e:
                log.error("restore_failed", session_id=sess_id, error=str(e))
                raise HTTPException(status_code=503, detail=f"Restore failed: {e}")
            _routing_stats["store_restore"] += 1

        if not decision.session_found:
            session_table.register(sess_id, decision.node_name, decision.slot_id or 0)
            _routing_stats["least_loaded"] += 1
        elif decision.action == "route" and decision.session_found:
            _routing_stats["affinity"] += 1

        if decision.n_past > 0:
            estimated = estimate_request_tokens(messages_dict, config.chars_per_token)
            if estimated <= decision.n_past:
                # CRITICAL: n_tokens must be > n_past or the KV cache is silently nuked.
                # Reset session so the next completion starts fresh.
                session_table.update_n_past(sess_id, 0)
                log.warning(
                    "n_past_guard_triggered",
                    session_id=sess_id,
                    n_past=decision.n_past,
                    estimated=estimated,
                    action="reset_n_past_to_0",
                )

        session_table.update_last_used(sess_id)

        node_url_base = node_url(decision.node_name)

        if req.stream:
            async def stream_with_npast():
                last_usage = None
                async for chunk in proxy_completion_stream(node_url_base, request_dict, trace_id):
                    if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                        try:
                            data = json.loads(chunk[6:])
                            if "usage" in data:
                                last_usage = data["usage"]
                        except Exception:
                            pass
                    yield chunk
                if last_usage:
                    total = last_usage.get("total_tokens", 0)
                    if total > 0:
                        entry = session_table.lookup(sess_id)
                        if entry:
                            session_table.update_n_past(sess_id, total)

            return StreamingResponse(
                stream_with_npast(),
                media_type="text/event-stream",
                headers={"X-Trace-Id": trace_id, "X-Hydra-Node": decision.node_name},
            )
        else:
            result = await proxy_completion(node_url_base, request_dict, trace_id)
            usage = result.get("usage", {})
            total = usage.get("total_tokens", 0) if isinstance(usage, dict) else 0
            if total > 0:
                entry = session_table.lookup(sess_id)
                if entry:
                    session_table.update_n_past(sess_id, total)
            return JSONResponse(
                content=result,
                headers={"X-Trace-Id": trace_id, "X-Hydra-Node": decision.node_name},
            )

    @router.get("/metrics")
    async def metrics():
        return await metrics_endpoint(None)

    @router.get("/version")
    async def version():
        return {
            "service": "hydra-coordinator",
            "version": VERSION,
            "revision": REVISION,
        }

    @router.get("/health")
    async def health():
        summary = health_monitor.get_health_summary()
        all_ok = all(v["healthy"] for v in summary.values())
        store_ok = True

        if not all_ok:
            status = "degraded"
        elif not store_ok:
            status = "degraded"
        else:
            status = "healthy"

        return {
            "status": status,
            "version": VERSION,
            "revision": REVISION,
            "nodes": summary,
            "store": {"healthy": store_ok},
        }

    @router.get("/status")
    async def status():
        uptime = time.time() - _start_time
        nodes_detail = {}
        for n in config.nodes:
            info = health_monitor.get_node_info(n.name)
            if info:
                nodes_detail[n.name] = {
                    "healthy": info.healthy,
                    "slots_total": info.slots_total,
                    "slots_idle": info.slots_idle,
                    "gpu_type": info.gpu_type,
                }
            else:
                nodes_detail[n.name] = {"healthy": False}

        return {
            "version": VERSION,
            "revision": REVISION,
            "uptime_s": uptime,
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
            "nodes": nodes_detail,
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

        # Look up NodeConfig for correct host + rpc_port (entry.node_name is just a name string).
        node_cfg = next((n for n in config.nodes if n.name == entry.node_name), None)

        if node_cfg and entry.slot_id is not None:
            try:
                await state_manager.save_session(
                    session_id,
                    node_cfg.host,
                    node_cfg.rpc_port,
                )
            except Exception as e:
                log.warning("evict_save_failed", session_id=session_id, error=str(e))

        session_table.remove(session_id)
        return {"evicted": True, "session_id": session_id}

    @router.post("/sessions/{session_id}/migrate")
    async def migrate_session(session_id: str, req: MigrateRequest):
        entry = session_table.lookup(session_id)
        if not entry:
            raise HTTPException(status_code=404, detail="Session not found")

        from_config = next((n for n in config.nodes if n.name == entry.node_name), None)
        to_config = next((n for n in config.nodes if n.name == req.target_node), None)

        if not from_config:
            raise HTTPException(status_code=400, detail=f"Source node {entry.node_name} not configured")
        if not to_config:
            raise HTTPException(status_code=400, detail=f"Target node {req.target_node} not configured")

        try:
            result = await state_manager.migrate_session(
                session_id,
                from_config.host,
                from_config.rpc_port,
                to_config.host,
                to_config.rpc_port,
                to_config.name,
            )
            return {"migrated": True, "session_id": session_id, "target": req.target_node, "result": result}
        except Exception as e:
            log.error("migrate_failed", session_id=session_id, error=str(e))
            raise HTTPException(status_code=500, detail=f"Migration failed: {e}")

    @router.post("/prefix/{checkpoint_name}/save")
    async def save_prefix(checkpoint_name: str, node_name: str = "rtx", slot_id: int = 0):
        node_cfg = next((n for n in config.nodes if n.name == node_name), None)
        if not node_cfg:
            raise HTTPException(status_code=400, detail=f"Node {node_name} not configured")

        try:
            result = await state_manager.save_prefix_checkpoint(
                checkpoint_name,
                node_cfg.host,
                node_cfg.rpc_port,
                slot_id,
            )
            return {"saved": True, "checkpoint": checkpoint_name, "node": node_name, "result": result}
        except Exception as e:
            log.error("prefix_save_failed", checkpoint=checkpoint_name, error=str(e))
            raise HTTPException(status_code=500, detail=f"Prefix save failed: {e}")

    @router.post("/prefix/{checkpoint_name}/restore")
    async def restore_prefix(checkpoint_name: str, node_name: str = "p100", slot_id: int = 0):
        node_cfg = next((n for n in config.nodes if n.name == node_name), None)
        if not node_cfg:
            raise HTTPException(status_code=400, detail=f"Node {node_name} not configured")

        try:
            result = await state_manager.restore_prefix_checkpoint(
                checkpoint_name,
                node_cfg.host,
                node_cfg.rpc_port,
                slot_id,
            )
            return {"restored": True, "checkpoint": checkpoint_name, "node": node_name, "result": result}
        except Exception as e:
            log.error("prefix_restore_failed", checkpoint=checkpoint_name, error=str(e))
            raise HTTPException(status_code=500, detail=f"Prefix restore failed: {e}")

    return router
