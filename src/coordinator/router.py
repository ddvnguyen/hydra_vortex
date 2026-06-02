import hashlib
import json
import time
from typing import Optional, Union
from pydantic import BaseModel, ConfigDict, field_validator

from fastapi import APIRouter, HTTPException
from fastapi.responses import StreamingResponse, JSONResponse

import httpx

from python_shared.log_config import get_logger, new_trace_id
from python_shared.rpc_client import OpCode, RpcClient
from coordinator.session_table import SessionTable
from coordinator.routing import (
    route_request, estimate_request_tokens, derive_session_id,
    select_prefill_worker, select_decode_worker,
)
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.proxy import proxy_completion, proxy_completion_stream
from coordinator.config import CoordinatorConfig
from coordinator.metrics import metrics_endpoint, requests_total, active_sessions
from coordinator.version import VERSION, REVISION

log = get_logger()


async def _resolve_slot_id(llama_url: str, expected_n_past: int, trace_id: str) -> int | None:
    """Query llama-server /slots and find the slot with matching n_past."""
    if expected_n_past <= 0:
        return None
    try:
        async with httpx.AsyncClient(timeout=5) as client:
            resp = await client.get(
                f"{llama_url.rstrip('/')}/slots",
                headers={"X-Trace-Id": trace_id},
            )
            resp.raise_for_status()
            data = resp.json()
    except Exception as exc:
        log.warning("resolve_slot_id_failed", url=llama_url, error=str(exc))
        return None

    slots = data if isinstance(data, list) else data.get("slots", [])
    for slot in slots:
        if slot.get("n_past", 0) == expected_n_past:
            return slot.get("id")
    return None


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
    _saved_prefixes: set[str] = set()
    # Per-worker in-flight counter: updated synchronously after routing (before any
    # await) so concurrent requests see accurate load without waiting for the health poll.
    _in_flight: dict[str, int] = {w.name: 0 for w in config.workers}

    def worker_url(worker_name: str) -> str:
        for w in config.workers:
            if w.name == worker_name:
                return w.llama_url
        return ""

    @router.post("/v1/chat/completions")
    async def chat_completion(req: ChatCompletionRequest):
        trace_id = new_trace_id()
        _routing_stats["total"] += 1

        request_dict = req.model_dump(exclude={"session_id"})
        messages_dict = [m.model_dump() for m in req.messages]
        health = health_monitor.get_health_summary()

        try:
            decision = route_request(
                request_messages=messages_dict,
                session_table=session_table,
                workers=config.workers,
                health_info=health,
                chars_per_token=config.chars_per_token,
                long_prompt_threshold=config.long_prompt_threshold,
                session_id=req.session_id,
                in_flight=_in_flight,
            )
        except RuntimeError as e:
            raise HTTPException(status_code=503, detail=str(e))

        # Increment immediately — before any awaits — so concurrent coroutines
        # see this worker as in-use when they call route_request().
        _in_flight[decision.node_name] = _in_flight.get(decision.node_name, 0) + 1
        _inflight_decremented = False

        def _decrement_inflight():
            nonlocal _inflight_decremented
            if not _inflight_decremented:
                _inflight_decremented = True
                _in_flight[decision.node_name] = max(
                    0, _in_flight.get(decision.node_name, 0) - 1
                )

        sess_id = decision.session_id or derive_session_id(messages_dict)
        requests_total.labels(node=decision.node_name, reason=decision.action).inc()
        for w in config.workers:
            active_sessions.labels(node=w.name).set(session_table.active_count_on_node(w.name))

        if decision.action == "store_restore":
            try:
                await state_manager.restore_session(
                    sess_id,
                    decision.node_config.host,
                    decision.node_config.rpc_port,
                )
            except Exception as e:
                log.error("restore_failed", session_id=sess_id, error=str(e))
                _decrement_inflight()
                raise HTTPException(status_code=503, detail=f"Restore failed: {e}")
            _routing_stats["store_restore"] += 1

        if not decision.session_found:
            session_table.register(sess_id, decision.node_name, decision.slot_id, n_past=0)
            _routing_stats["least_loaded"] += 1

            if config.prefix_checkpoint_enabled:
                system_msg = next((m for m in req.messages if m.role == "system"), None)
                if system_msg:
                    prompt_hash = hashlib.sha256(system_msg.content.encode()).hexdigest()[:16]
                    try:
                        meta = await state_manager.restore_prefix_checkpoint(
                            prompt_hash,
                            decision.node_config.host,
                            decision.node_config.rpc_port,
                            slot_id=decision.slot_id or 0,
                        )
                        n_past_prefix = meta.get("n_past", 0) if meta else 0
                        if n_past_prefix > 0:
                            session_table.update_n_past(sess_id, n_past_prefix)
                            log.info("prefix_restored", session_id=sess_id,
                                     checkpoint=prompt_hash, n_past=n_past_prefix)
                    except Exception:
                        pass

        elif decision.action == "route" and decision.session_found:
            _routing_stats["affinity"] += 1

        if decision.n_past > 0:
            estimated = estimate_request_tokens(messages_dict, config.chars_per_token)
            if estimated < decision.n_past * 0.85:
                session_table.update_n_past(sess_id, 0)
                log.warning(
                    "n_past_guard_triggered",
                    session_id=sess_id,
                    n_past=decision.n_past,
                    estimated=estimated,
                    action="reset_n_past_to_0",
                )
                client = RpcClient(decision.node_config.host, decision.node_config.rpc_port)
                try:
                    await client.request(OpCode.SlotErase, str(decision.slot_id or 0), trace_id=trace_id)
                except Exception as e:
                    log.warning("n_past_guard_slot_erase_failed", error=str(e))
                finally:
                    await client.close()

        session_table.update_last_used(sess_id)

        # System prompt hash for prefix checkpoint save-once logic.
        _system_msg = next((m for m in req.messages if m.role == "system"), None)
        _prompt_hash = (
            hashlib.sha256(_system_msg.content.encode()).hexdigest()[:16]
            if _system_msg and config.prefix_checkpoint_enabled else None
        )
        _prefix_key = f"{decision.node_name}:{_prompt_hash}" if _prompt_hash else None

        async def _maybe_save_prefix():
            if _prefix_key and _prefix_key not in _saved_prefixes:
                _saved_prefixes.add(_prefix_key)
                try:
                    entry = session_table.lookup(sess_id)
                    slot = entry.slot_id if entry and entry.slot_id is not None else 0
                    await state_manager.save_prefix_checkpoint(
                        _prompt_hash,  # type: ignore[arg-type]
                        decision.node_config.host,
                        decision.node_config.rpc_port,
                        slot_id=slot,
                    )
                    log.info("prefix_saved", checkpoint=_prompt_hash, node=decision.node_name)
                except Exception as exc:
                    _saved_prefixes.discard(_prefix_key)
                    log.warning("prefix_save_failed", checkpoint=_prompt_hash, error=str(exc))

        # ── fast mode: one worker handles both prefill and decode ──────────────
        if config.run_mode != "concurrency":
            node_url_base = worker_url(decision.node_name)

            if req.stream:
                _node_for_inflight = decision.node_name

                async def stream_with_npast():
                    last_usage = None
                    try:
                        async for chunk in proxy_completion_stream(node_url_base, request_dict, trace_id):
                            if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                                try:
                                    data = json.loads(chunk[6:])
                                    if "usage" in data:
                                        last_usage = data["usage"]
                                except Exception:
                                    pass
                            yield chunk
                        entry = session_table.lookup(sess_id)
                        if entry:
                            if last_usage:
                                total = last_usage.get("total_tokens", 0)
                                if total > 0:
                                    session_table.update_n_past(sess_id, total)
                            if entry.slot_id is None and last_usage:
                                total = last_usage.get("total_tokens", 0)
                                resolved = await _resolve_slot_id(node_url_base, total, trace_id)
                                if resolved is not None:
                                    entry.slot_id = resolved
                                    log.info("slot_resolved_stream",
                                             session_id=sess_id, slot_id=resolved, n_past=total)
                        await _maybe_save_prefix()
                    finally:
                        _inflight_decremented = True
                        _in_flight[_node_for_inflight] = max(
                            0, _in_flight.get(_node_for_inflight, 0) - 1
                        )

                return StreamingResponse(
                    stream_with_npast(),
                    media_type="text/event-stream",
                    headers={"X-Trace-Id": trace_id, "X-Hydra-Node": decision.node_name},
                )
            else:
                try:
                    result = await proxy_completion(node_url_base, request_dict, trace_id)
                finally:
                    _decrement_inflight()
                usage = result.get("usage", {})
                total = usage.get("total_tokens", 0) if isinstance(usage, dict) else 0
                entry = session_table.lookup(sess_id)
                if entry:
                    if total > 0:
                        session_table.update_n_past(sess_id, total)
                    if entry.slot_id is None and total > 0:
                        resolved = await _resolve_slot_id(node_url_base, total, trace_id)
                        if resolved is not None:
                            entry.slot_id = resolved
                            log.info("slot_resolved_nonstream",
                                     session_id=sess_id, slot_id=resolved, n_past=total)
                await _maybe_save_prefix()
                return JSONResponse(
                    content=result,
                    headers={"X-Trace-Id": trace_id, "X-Hydra-Node": decision.node_name},
                )

        # ── concurrency mode: P/D disaggregation ──────────────────────────────
        # Phase 1 — Prefill on the selected prefill worker (already decided above).
        # Phase 2 — Save KV cache → restore on best decode worker → stream tokens.
        prefill_node = decision.node_config
        prefill_url = worker_url(prefill_node.name)

        # Send prefill-only request (non-streaming, max_tokens=1 to just fill KV).
        prefill_dict = {**request_dict, "stream": False, "max_tokens": 1}
        try:
            prefill_result = await proxy_completion(prefill_url, prefill_dict, trace_id)
        except Exception as e:
            _decrement_inflight()
            log.error("prefill_failed", session_id=sess_id, error=str(e))
            raise HTTPException(status_code=503, detail=f"Prefill failed: {e}")

        prefill_usage = prefill_result.get("usage", {})
        n_past_after_prefill = prefill_usage.get("total_tokens", 0) if isinstance(prefill_usage, dict) else 0
        if n_past_after_prefill > 0:
            session_table.update_n_past(sess_id, n_past_after_prefill)

        log.info("prefill_complete", session_id=sess_id, node=prefill_node.name,
                 n_past=n_past_after_prefill)

        # Resolve slot used by prefill.
        prefill_slot = await _resolve_slot_id(prefill_url, n_past_after_prefill, trace_id)
        if prefill_slot is not None:
            entry = session_table.lookup(sess_id)
            if entry:
                entry.slot_id = prefill_slot

        # Save prefill KV state to Store.
        try:
            await state_manager.save_session(sess_id, prefill_node.host, prefill_node.rpc_port)
        except Exception as e:
            _decrement_inflight()
            log.error("prefill_save_failed", session_id=sess_id, error=str(e))
            raise HTTPException(status_code=503, detail=f"KV save failed: {e}")

        session_table.mark_evicted(sess_id)
        session_table.update_n_past(sess_id, n_past_after_prefill)
        _decrement_inflight()  # prefill worker is done

        # Select decode worker (prefer different worker; fall back to same if only one).
        decode_worker = select_decode_worker(
            config.workers, health_monitor.get_health_summary(), _in_flight,
            exclude=prefill_node.name,
        ) or select_decode_worker(config.workers, health_monitor.get_health_summary(), _in_flight)

        if not decode_worker:
            raise HTTPException(status_code=503, detail="No decode worker available")

        _in_flight[decode_worker.name] = _in_flight.get(decode_worker.name, 0) + 1
        decode_url = worker_url(decode_worker.name)

        # Restore KV state on decode worker.
        try:
            await state_manager.restore_session(sess_id, decode_worker.host, decode_worker.rpc_port)
        except Exception as e:
            _in_flight[decode_worker.name] = max(0, _in_flight.get(decode_worker.name, 0) - 1)
            log.error("decode_restore_failed", session_id=sess_id, error=str(e))
            raise HTTPException(status_code=503, detail=f"KV restore failed: {e}")

        session_table.register(sess_id, decode_worker.name, None, n_past=n_past_after_prefill)
        log.info("decode_started", session_id=sess_id, node=decode_worker.name,
                 n_past=n_past_after_prefill)

        _decode_node_name = decode_worker.name

        async def stream_decode():
            last_usage = None
            try:
                async for chunk in proxy_completion_stream(decode_url, request_dict, trace_id):
                    if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                        try:
                            data = json.loads(chunk[6:])
                            if "usage" in data:
                                last_usage = data["usage"]
                        except Exception:
                            pass
                    yield chunk
                entry = session_table.lookup(sess_id)
                if entry and last_usage:
                    total = last_usage.get("total_tokens", 0)
                    if total > 0:
                        session_table.update_n_past(sess_id, total)
                    if entry.slot_id is None and total > 0:
                        resolved = await _resolve_slot_id(decode_url, total, trace_id)
                        if resolved is not None:
                            entry.slot_id = resolved
                await _maybe_save_prefix()
            finally:
                _in_flight[_decode_node_name] = max(
                    0, _in_flight.get(_decode_node_name, 0) - 1
                )

        return StreamingResponse(
            stream_decode(),
            media_type="text/event-stream",
            headers={
                "X-Trace-Id": trace_id,
                "X-Hydra-Node": decode_worker.name,
                "X-Hydra-Prefill-Node": prefill_node.name,
            },
        )

    @router.get("/metrics")
    async def metrics():
        return await metrics_endpoint(None)

    @router.get("/version")
    async def version():
        return {"service": "hydra-coordinator", "version": VERSION, "revision": REVISION}

    @router.get("/health")
    async def health():
        summary = health_monitor.get_health_summary()
        all_ok = all(v["healthy"] for v in summary.values())
        status = "healthy" if all_ok else "degraded"
        return {
            "status": status,
            "version": VERSION,
            "revision": REVISION,
            "nodes": summary,
            "store": {"healthy": True},
        }

    @router.get("/status")
    async def status():
        uptime = time.time() - _start_time
        workers_detail = {}
        for w in config.workers:
            info = health_monitor.get_node_info(w.name)
            if info:
                workers_detail[w.name] = {
                    "healthy": info.healthy,
                    "slots_total": info.slots_total,
                    "slots_idle": info.slots_idle,
                    "worker_type": w.worker_type,
                    "prefill_priority": w.prefill_priority,
                    "decode_priority": w.decode_priority,
                    "in_flight": _in_flight.get(w.name, 0),
                }
            else:
                workers_detail[w.name] = {"healthy": False}

        return {
            "version": VERSION,
            "revision": REVISION,
            "uptime_s": uptime,
            "run_mode": config.run_mode,
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
