import asyncio
import hashlib
import json
import time
from dataclasses import dataclass, field
from typing import Optional, Union

from fastapi.responses import StreamingResponse, JSONResponse
from fastapi import HTTPException
import httpx

from coordinator.lib.log_config import get_logger, new_trace_id
from coordinator.lib.rpc_client import RpcClient, OpCode
from coordinator.config import CoordinatorConfig, WorkerNodeConfig
from coordinator.worker_tracker import WorkerTracker
from coordinator.routing import (
    estimate_request_tokens,
    derive_session_id,
    compute_prefix_hash,
    _pick_idle_slot,
    pick_best_prefill_worker,
    pick_best_decode_worker,
    pick_best_mixed_worker,
    WORKER_PREFILL,
    WORKER_DECODE,
    WORKER_MIXED,
)
from coordinator.session_table import SessionTable
from coordinator.health import HealthMonitor
from coordinator.state_manager import StateManager
from coordinator.proxy import proxy_completion, proxy_completion_stream
from coordinator.metrics import (
    requests_total,
    active_sessions,
    upstream_timeouts_total,
    cross_node_affinity_total,
)

log = get_logger()


@dataclass
class WorkItem:
    request: dict
    messages: list[dict]
    session_id: str
    trace_id: str
    prefix_hash: Optional[str]
    estimated_tokens: int
    estimated_new_tokens: int
    future: asyncio.Future
    retry_count: int = 0
    _start_time: float = field(default_factory=time.time)


class WorkerScheduler:
    def __init__(
        self,
        config: CoordinatorConfig,
        session_table: SessionTable,
        health_monitor: HealthMonitor,
        state_manager: StateManager,
        tracker: WorkerTracker,
    ):
        self._config = config
        self._session_table = session_table
        self._health = health_monitor
        self._state = state_manager
        self._tracker = tracker

        self._queue: list[WorkItem] = []
        self._max_queue_size = len(config.workers) * 10
        self._new_item = asyncio.Event()
        self._worker_freed = asyncio.Event()
        self._running = False
        self._prefix_set: set[str] = set()

    @staticmethod
    def _elapsed_ms(item: WorkItem) -> int:
        return int((time.time() - item._start_time) * 1000)

    async def start(self):
        self._running = True
        self._task = asyncio.create_task(self._run())

    async def stop(self):
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass

    async def submit(
        self,
        req: dict,
        messages: list[dict],
        session_id: str,
        max_tokens: int,
        prefix_hash: Optional[str] = None,
    ) -> Union[StreamingResponse, JSONResponse]:
        if not self._running:
            raise HTTPException(status_code=503, detail="Server shutting down")
        if len(self._queue) >= self._max_queue_size:
            raise HTTPException(status_code=503, detail="Server busy")

        trace_id = new_trace_id()
        estimated = estimate_request_tokens(messages, self._config.chars_per_token)
        loop = asyncio.get_running_loop()
        future = loop.create_future()
        item = WorkItem(
            request=req,
            messages=messages,
            session_id=session_id,
            trace_id=trace_id,
            prefix_hash=prefix_hash,
            estimated_tokens=estimated,
            estimated_new_tokens=max_tokens,
            future=future,
        )
        self._queue.append(item)
        self._new_item.set()

        log.info("request_received",
                 trace_id=trace_id,
                 session_id=session_id,
                 estimated_tokens=estimated,
                 estimated_new_tokens=max_tokens,
                 stream=req.get("stream", False),
                 prefix_hash=prefix_hash,
                 queue_size=len(self._queue),
        )

        try:
            return await asyncio.wait_for(future, timeout=1800.0)
        except asyncio.TimeoutError:
            if item in self._queue:
                self._queue.remove(item)
            if not future.done():
                future.set_exception(HTTPException(status_code=504, detail="Request timed out"))
            raise HTTPException(status_code=504, detail="Request timed out")

    # ── helpers ──────────────────────────────────────────────────────────

    def _worker_by_name(self, name: str) -> Optional[WorkerNodeConfig]:
        for w in self._config.workers:
            if w.name == name:
                return w
        return None

    def _is_atomic(self, item: WorkItem) -> bool:
        if self._config.run_mode == "fast":
            return True
        return item.estimated_new_tokens <= self._config.atomic_token_threshold

    def _routable(self, wname: str) -> bool:
        info = self._health.get_node_info(wname)
        if not info or not info.healthy:
            return False
        return True

    def _can_handle(self, item: WorkItem) -> Optional[WorkerNodeConfig]:
        for wname in self._tracker.free_workers():
            w = self._worker_by_name(wname)
            if not w:
                continue
            if not (w.worker_type & WORKER_PREFILL):
                continue
            if not self._routable(wname):
                continue
            if w.max_prefill_tokens != -1 and item.estimated_new_tokens > w.max_prefill_tokens:
                continue
            return w
        return None

    # ── main loop ────────────────────────────────────────────────────────

    async def _run(self):
        while self._running:
            try:
                if not self._queue:
                    await self._wait_for_wakeup()
                    continue

                front = self._queue[0]
                worker = self._can_handle(front)
                if worker and self._tracker.acquire(worker.name, "prefill"):
                    self._queue.pop(0)
                    asyncio.create_task(self._process(front, worker))
                    continue

                handled = False
                for i in range(1, len(self._queue)):
                    candidate = self._queue[i]
                    w = self._can_handle(candidate)
                    if w and self._tracker.acquire(w.name, "prefill"):
                        self._queue.pop(i)
                        asyncio.create_task(self._process(candidate, w))
                        handled = True
                        break

                if not handled:
                    await self._wait_for_wakeup()
            except Exception as exc:
                log.exception("scheduler_loop_error")

    async def _wait_for_wakeup(self):
        self._new_item.clear()
        self._worker_freed.clear()
        tasks = [
            asyncio.create_task(self._worker_freed.wait()),
            asyncio.create_task(self._new_item.wait()),
        ]
        try:
            done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED, timeout=30.0)
            for t in pending:
                t.cancel()
            if pending:
                await asyncio.gather(*pending, return_exceptions=True)
        except Exception:
            pass

    # ── process ──────────────────────────────────────────────────────────

    async def _process(self, item: WorkItem, worker: WorkerNodeConfig):
        affinity_dispatched = False
        try:
            entry = self._session_table.lookup(item.session_id)

            if entry and entry.slot_id is not None:
                if entry.node_name == worker.name:
                    affinity_dispatched = True
                    await self._execute_affinity(item, worker, entry)
                    return
                else:
                    target = self._worker_by_name(entry.node_name)
                    if target and self._tracker.is_free(target.name) and self._routable(target.name):
                        if self._tracker.acquire(target.name, "decode"):
                            cross_node_affinity_total.inc()
                            await self._execute_affinity(item, target, entry)
                            return

                entry = self._session_table.lookup(item.session_id)

            if entry and entry.has_store_state:
                self._tracker.release(worker.name)
                self._worker_freed.set()
                await self._execute_store_restore(item)
                return

            requests_total.labels(node=worker.name, reason="cold").inc()
            for w in self._config.workers:
                active_sessions.labels(node=w.name).set(
                    self._session_table.active_count_on_node(w.name)
                )

            if self._is_atomic(item):
                await self._execute_atomic(item, worker)
            else:
                await self._execute_concurrency(item, worker)
        except Exception as e:
            status = e.status_code if isinstance(e, HTTPException) else 503
            log_fn = log.warning if isinstance(e, HTTPException) else log.error
            log_fn("scheduler_process_failed",
                   trace_id=item.trace_id, session_id=item.session_id, node=worker.name,
                   elapsed_ms=self._elapsed_ms(item), error=str(e),
                   status_code=status,
                   estimated_tokens=item.estimated_tokens,
                   estimated_new_tokens=item.estimated_new_tokens,
                   stream=item.request.get("stream", False))
            if not affinity_dispatched:
                self._tracker.release(worker.name)
            self._worker_freed.set()
            if not item.future.done():
                item.future.set_exception(HTTPException(status_code=status, detail=str(e)))

    # ── 1. Affinity path ─────────────────────────────────────────────────

    async def _execute_affinity(self, item: WorkItem, worker: WorkerNodeConfig, entry):
        sess_id = item.session_id
        log.info("affinity_route",
                 trace_id=item.trace_id, session_id=sess_id, node=worker.name, slot=entry.slot_id,
                 estimated_tokens=item.estimated_tokens,
                 estimated_new_tokens=item.estimated_new_tokens,
                 n_past=entry.n_past,
                 stream=item.request.get("stream", False),
                 elapsed_ms=self._elapsed_ms(item))

        self._session_table.update_last_used(sess_id)

        if entry.n_past > 0:
            estimated = estimate_request_tokens(item.messages, self._config.chars_per_token)
            if estimated < entry.n_past * 0.85:
                self._session_table.update_n_past(sess_id, 0)
                log.warning("n_past_guard_triggered",
                             trace_id=item.trace_id, session_id=sess_id, node=worker.name,
                             n_past=entry.n_past, estimated=estimated)
                client = RpcClient(worker.host, worker.rpc_port)
                try:
                    await client.request(OpCode.SlotErase, str(entry.slot_id or 0), trace_id=item.trace_id)
                except Exception as e:
                    log.warning("n_past_guard_slot_erase_failed",
                                 trace_id=item.trace_id, session_id=sess_id, node=worker.name, error=str(e))
                finally:
                    await client.close()
                entry.slot_id = None

        node_url = worker.llama_url
        if item.request.get("stream", False):
            async def stream_affinity():
                last_usage = None
                try:
                    async for chunk in proxy_completion_stream(node_url, item.request, item.trace_id):
                        if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                            try:
                                data = json.loads(chunk[6:])
                                if "usage" in data:
                                    last_usage = data["usage"]
                            except Exception:
                                pass
                        yield chunk
                    await self._track_after_stream(sess_id, node_url, last_usage, item)
                except Exception:
                    self._tracker.on_error(worker.name)
                    raise
                else:
                    self._tracker.on_success(worker.name)
                finally:
                    self._tracker.release(worker.name)
                    self._worker_freed.set()

            item.future.set_result(StreamingResponse(
                stream_affinity(),
                media_type="text/event-stream",
                headers={"X-Trace-Id": item.trace_id, "X-Hydra-Node": worker.name},
            ))
        else:
            self._tracker.release(worker.name)
            try:
                result = await proxy_completion(node_url, item.request, item.trace_id)
            except httpx.TimeoutException as e:
                upstream_timeouts_total.inc()
                self._tracker.on_error(worker.name)
                log.warning("affinity_timeout",
                             trace_id=item.trace_id, session_id=sess_id, node=worker.name,
                             timeout_s=self._config.llama_request_timeout_s,
                             elapsed_ms=self._elapsed_ms(item), error=str(e))
                raise HTTPException(status_code=504, detail=f"Completion exceeded {self._config.llama_request_timeout_s}s")
            except Exception:
                self._tracker.on_error(worker.name)
                raise
            finally:
                self._worker_freed.set()
            self._tracker.on_success(worker.name)
            await self._track_after_completion(sess_id, node_url, result, item)
            item.future.set_result(JSONResponse(
                content=result,
                headers={"X-Trace-Id": item.trace_id, "X-Hydra-Node": worker.name},
            ))

    # ── 2. Store-Restore path ────────────────────────────────────────────

    async def _execute_store_restore(self, item: WorkItem):
        sess_id = item.session_id
        log.info("store_restore_route",
                 trace_id=item.trace_id, session_id=sess_id,
                 estimated_tokens=item.estimated_tokens,
                 estimated_new_tokens=item.estimated_new_tokens,
                 stream=item.request.get("stream", False),
                 elapsed_ms=self._elapsed_ms(item))

        decode_worker = pick_best_decode_worker(
            self._config.workers, self._tracker, self._health,
        )
        if not decode_worker or not self._tracker.acquire(decode_worker.name, "decode"):
            raise HTTPException(status_code=503, detail="No decode worker available")

        entry = self._session_table.lookup(sess_id)
        n_past_entry = entry.n_past if entry else 0

        try:
            await self._state.restore_session(sess_id, decode_worker.host, decode_worker.rpc_port, slot_id=0)
        except Exception as e:
            self._tracker.release(decode_worker.name)
            self._worker_freed.set()
            self._tracker.on_error(decode_worker.name)
            raise HTTPException(status_code=503, detail=f"Restore failed: {e}")

        self._session_table.register(sess_id, decode_worker.name, None, n_past=n_past_entry, prefix_hash=item.prefix_hash)

        requests_total.labels(node=decode_worker.name, reason="store_restore").inc()
        for w in self._config.workers:
            active_sessions.labels(node=w.name).set(
                self._session_table.active_count_on_node(w.name)
            )

        if n_past_entry > 0:
            estimated = estimate_request_tokens(item.messages, self._config.chars_per_token)
            if estimated < n_past_entry * 0.85:
                self._session_table.update_n_past(sess_id, 0)
                log.warning("restore_n_past_guard",
                             trace_id=item.trace_id, session_id=sess_id,
                             n_past=n_past_entry, estimated=estimated)

        self._session_table.update_last_used(sess_id)
        decode_url = decode_worker.llama_url

        if item.request.get("stream", False):
            async def stream_restore():
                last_usage = None
                try:
                    async for chunk in proxy_completion_stream(decode_url, item.request, item.trace_id):
                        if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                            try:
                                data = json.loads(chunk[6:])
                                if "usage" in data:
                                    last_usage = data["usage"]
                            except Exception:
                                pass
                        yield chunk
                    await self._track_after_stream(sess_id, decode_url, last_usage, item)
                except Exception:
                    self._tracker.on_error(decode_worker.name)
                    raise
                else:
                    self._tracker.on_success(decode_worker.name)
                finally:
                    self._tracker.release(decode_worker.name)
                    self._worker_freed.set()

            item.future.set_result(StreamingResponse(
                stream_restore(),
                media_type="text/event-stream",
                headers={"X-Trace-Id": item.trace_id, "X-Hydra-Node": decode_worker.name},
            ))
        else:
            try:
                result = await proxy_completion(decode_url, item.request, item.trace_id)
            except httpx.TimeoutException as e:
                upstream_timeouts_total.inc()
                self._tracker.on_error(decode_worker.name)
                raise HTTPException(status_code=504, detail=f"Completion exceeded {self._config.llama_request_timeout_s}s")
            except Exception:
                self._tracker.on_error(decode_worker.name)
                raise
            else:
                self._tracker.on_success(decode_worker.name)
            finally:
                self._tracker.release(decode_worker.name)
                self._worker_freed.set()
            await self._track_after_completion(sess_id, decode_url, result, item)
            item.future.set_result(JSONResponse(
                content=result,
                headers={"X-Trace-Id": item.trace_id, "X-Hydra-Node": decode_worker.name},
            ))

    # ── 3a. Cold atomic path ────────────────────────────────────────────

    async def _execute_atomic(self, item: WorkItem, worker: WorkerNodeConfig):
        sess_id = item.session_id
        node_url = worker.llama_url
        log.info("cold_atomic",
                 trace_id=item.trace_id, session_id=sess_id, node=worker.name,
                 estimated_tokens=item.estimated_tokens,
                 estimated_new_tokens=item.estimated_new_tokens,
                 stream=item.request.get("stream", False),
                 elapsed_ms=self._elapsed_ms(item))

        await self._maybe_restore_prefix_checkpoint(item, worker)

        prefill_slot = self._health.get_idle_slot(worker.name) or await _pick_idle_slot(node_url, item.trace_id)

        self._session_table.register(
            sess_id, worker.name, prefill_slot, n_past=0, prefix_hash=item.prefix_hash,
        )

        if item.request.get("stream", False):
            async def stream_atomic():
                last_usage = None
                try:
                    async for chunk in proxy_completion_stream(node_url, item.request, item.trace_id):
                        if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                            try:
                                data = json.loads(chunk[6:])
                                if "usage" in data:
                                    last_usage = data["usage"]
                            except Exception:
                                pass
                        yield chunk
                    await self._track_after_stream(sess_id, node_url, last_usage, item)
                    if item.prefix_hash:
                        await self._maybe_save_prefix(item, worker)
                    await self._state.save_session(sess_id, worker.host, worker.rpc_port)
                except Exception:
                    self._tracker.on_error(worker.name)
                    raise
                else:
                    self._tracker.on_success(worker.name)
                finally:
                    self._tracker.release(worker.name)
                    self._worker_freed.set()

            item.future.set_result(StreamingResponse(
                stream_atomic(),
                media_type="text/event-stream",
                headers={"X-Trace-Id": item.trace_id, "X-Hydra-Node": worker.name},
            ))
        else:
            try:
                result = await proxy_completion(node_url, item.request, item.trace_id)
            except httpx.TimeoutException as e:
                upstream_timeouts_total.inc()
                self._tracker.on_error(worker.name)
                raise HTTPException(status_code=504, detail=f"Completion exceeded {self._config.llama_request_timeout_s}s")
            except Exception:
                self._tracker.on_error(worker.name)
                raise
            else:
                self._tracker.on_success(worker.name)
            finally:
                self._tracker.release(worker.name)
                self._worker_freed.set()
            # Use id_slot from response if available (llama-server fork), fallback to captured slot
            response_slot = result.get("id_slot")
            if response_slot is not None:
                entry = self._session_table.lookup(sess_id)
                if entry:
                    entry.slot_id = response_slot
            await self._track_after_completion(sess_id, node_url, result, item)
            if item.prefix_hash:
                await self._maybe_save_prefix(item, worker)
            await self._state.save_session(sess_id, worker.host, worker.rpc_port)
            item.future.set_result(JSONResponse(
                content=result,
                headers={"X-Trace-Id": item.trace_id, "X-Hydra-Node": worker.name},
            ))

    # ── 3b. Cold concurrency path ────────────────────────────────────────

    async def _execute_concurrency(self, item: WorkItem, prefill_worker: WorkerNodeConfig):
        sess_id = item.session_id
        prefill_url = prefill_worker.llama_url
        log.info("cold_concurrency_prefill",
                 trace_id=item.trace_id, session_id=sess_id, node=prefill_worker.name,
                 estimated_tokens=item.estimated_tokens,
                 estimated_new_tokens=item.estimated_new_tokens,
                 stream=item.request.get("stream", False),
                 elapsed_ms=self._elapsed_ms(item))

        await self._maybe_restore_prefix_checkpoint(item, prefill_worker)

        # Capture idle slot before prefill. May return None if all slots are busy —
        # the id_slot from the completion response (extracted below) will correct it.
        prefill_slot = self._health.get_idle_slot(prefill_worker.name) or await _pick_idle_slot(prefill_url, item.trace_id)
        log.info("cold_concurrency_slot",
                 trace_id=item.trace_id, session_id=sess_id,
                 node=prefill_worker.name, prefill_slot=prefill_slot)

        self._session_table.register(
            sess_id, prefill_worker.name, prefill_slot, n_past=0, prefix_hash=item.prefix_hash,
        )

        prefill_dict = {**item.request, "stream": False, "max_tokens": 1}
        try:
            prefill_result = await proxy_completion(prefill_url, prefill_dict, item.trace_id)
        except httpx.TimeoutException as e:
            self._tracker.release(prefill_worker.name)
            self._worker_freed.set()
            self._tracker.on_error(prefill_worker.name)
            upstream_timeouts_total.inc()
            raise HTTPException(status_code=504, detail=f"Prefill exceeded {self._config.llama_request_timeout_s}s")
        except Exception as e:
            self._tracker.release(prefill_worker.name)
            self._worker_freed.set()
            self._tracker.on_error(prefill_worker.name)
            raise HTTPException(status_code=503, detail=f"Prefill failed: {e}")

        # Use id_slot from response if available (llama-server fork), fallback to captured slot
        response_slot = prefill_result.get("id_slot")
        if response_slot is not None:
            entry = self._session_table.lookup(sess_id)
            if entry:
                entry.slot_id = response_slot
            log.debug("prefill_slot_from_response",
                      trace_id=item.trace_id, session_id=sess_id,
                      slot_id=response_slot)

        n_past_after = (
            prefill_result.get("usage", {}).get("total_tokens", 0)
            if isinstance(prefill_result.get("usage"), dict) else 0
        )
        if n_past_after > 0:
            self._session_table.update_n_past(sess_id, n_past_after)

        entry = self._session_table.lookup(sess_id)
        if entry and entry.slot_id is None:
            log.warning("slot_resolve_failed_no_prefill_slot",
                        trace_id=item.trace_id, session_id=sess_id,
                        node=prefill_worker.name, n_past=n_past_after,
                        elapsed_ms=self._elapsed_ms(item))

        self._tracker.release(prefill_worker.name)
        self._worker_freed.set()

        if item.prefix_hash:
            await self._maybe_save_prefix(item, prefill_worker)

        try:
            await self._state.save_session(sess_id, prefill_worker.host, prefill_worker.rpc_port)
        except Exception as e:
            self._tracker.on_error(prefill_worker.name)
            raise HTTPException(status_code=503, detail=f"KV save failed: {e}")

        self._session_table.mark_evicted(sess_id)
        if n_past_after > 0:
            self._session_table.update_n_past(sess_id, n_past_after)

        decode_worker = pick_best_decode_worker(
            self._config.workers, self._tracker, self._health,
            exclude=prefill_worker.name,
        ) or pick_best_decode_worker(
            self._config.workers, self._tracker, self._health,
        )
        if not decode_worker:
            free = self._tracker.free_workers()
            busy = self._tracker.busy_workers()
            log.warning("decode_worker_unavailable",
                         trace_id=item.trace_id, session_id=sess_id,
                         free_workers=free, busy_workers=busy,
                         elapsed_ms=self._elapsed_ms(item))
            raise HTTPException(status_code=503, detail="No decode worker available")
        if not self._tracker.acquire(decode_worker.name, "decode"):
            free = self._tracker.free_workers()
            busy = self._tracker.busy_workers()
            log.warning("decode_worker_acquire_failed",
                         trace_id=item.trace_id, session_id=sess_id,
                         worker=decode_worker.name, free_workers=free,
                         busy_workers=busy,
                         elapsed_ms=self._elapsed_ms(item))
            raise HTTPException(status_code=503, detail=f"Decode worker {decode_worker.name} busy")

        decode_url = decode_worker.llama_url

        try:
            await self._state.restore_session(sess_id, decode_worker.host, decode_worker.rpc_port, slot_id=0)
        except Exception as e:
            self._tracker.release(decode_worker.name)
            self._tracker.on_error(decode_worker.name)
            self._worker_freed.set()
            raise HTTPException(status_code=503, detail=f"KV restore failed: {e}")

        self._session_table.register(sess_id, decode_worker.name, None, n_past=n_past_after, prefix_hash=item.prefix_hash)
        log.info("cold_concurrency_decode",
                 trace_id=item.trace_id, session_id=sess_id, node=decode_worker.name,
                 n_past=n_past_after, elapsed_ms=self._elapsed_ms(item))

        async def stream_concurrency():
            last_usage = None
            try:
                async for chunk in proxy_completion_stream(decode_url, item.request, item.trace_id):
                    if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                        try:
                            data = json.loads(chunk[6:])
                            if "usage" in data:
                                last_usage = data["usage"]
                        except Exception:
                            pass
                    yield chunk
                await self._track_after_stream(sess_id, decode_url, last_usage, item)
                if item.prefix_hash:
                    await self._maybe_save_prefix(item, decode_worker)
            except Exception:
                self._tracker.on_error(decode_worker.name)
                raise
            else:
                self._tracker.on_success(decode_worker.name)
            finally:
                self._tracker.release(decode_worker.name)
                self._worker_freed.set()

        item.future.set_result(StreamingResponse(
            stream_concurrency(),
            media_type="text/event-stream",
            headers={
                "X-Trace-Id": item.trace_id,
                "X-Hydra-Node": decode_worker.name,
                "X-Hydra-Prefill-Node": prefill_worker.name,
            },
        ))

    # ── shared helpers ───────────────────────────────────────────────────

    async def _track_after_stream(self, sess_id: str, node_url: str, last_usage: Optional[dict], item: WorkItem):
        if not last_usage:
            return
        total = last_usage.get("total_tokens", 0) if isinstance(last_usage, dict) else 0
        if total > 0:
            self._session_table.update_n_past(sess_id, total)
        entry = self._session_table.lookup(sess_id)
        if entry and entry.slot_id is None and total > 0:
            self._resolve_slot_from_health(entry, total, item.trace_id)

    async def _track_after_completion(self, sess_id: str, node_url: str, result: dict, item: WorkItem):
        usage = result.get("usage", {})
        total = usage.get("total_tokens", 0) if isinstance(usage, dict) else 0
        if total > 0:
            self._session_table.update_n_past(sess_id, total)
        entry = self._session_table.lookup(sess_id)
        if entry and entry.slot_id is None and total > 0:
            self._resolve_slot_from_health(entry, total, item.trace_id)

    def _resolve_slot_from_health(self, entry, total: int, trace_id: str):
        for s in self._health.get_slots(entry.node_name):
            if s.get("n_past", 0) == total and not s.get("is_processing"):
                entry.slot_id = s["id"]
                log.info("slot_resolved_health", trace_id=trace_id,
                         session_id=entry.session_id, slot_id=s["id"], n_past=total)
                return

    async def _maybe_restore_prefix_checkpoint(self, item: WorkItem, worker: WorkerNodeConfig):
        if not self._config.prefix_checkpoint_enabled:
            return
        if not item.prefix_hash:
            return
        try:
            meta = await self._state.restore_prefix_checkpoint(
                item.prefix_hash,
                worker.host,
                worker.rpc_port,
                slot_id=0,
            )
        except Exception:
            meta = None

        if meta and meta.get("n_past", 0) > 0:
            self._session_table.update_n_past(item.session_id, meta["n_past"])
            log.info("prefix_restored",
                     trace_id=item.trace_id, session_id=item.session_id,
                     node=worker.name,
                     checkpoint=item.prefix_hash, n_past=meta["n_past"],
                     elapsed_ms=self._elapsed_ms(item))
            return
        log.warning("prefix_restore_failed",
                    trace_id=item.trace_id, session_id=item.session_id,
                    node=worker.name, checkpoint=item.prefix_hash,
                    elapsed_ms=self._elapsed_ms(item))


    async def _maybe_save_prefix(self, item: WorkItem, worker: WorkerNodeConfig):
        if not self._config.prefix_checkpoint_enabled:
            return
        if not item.prefix_hash:
            return
        prefix_key = f"{worker.name}:{item.prefix_hash}"
        if prefix_key in self._prefix_set:
            return
        self._prefix_set.add(prefix_key)
        try:
            entry = self._session_table.lookup(item.session_id)
            slot = entry.slot_id if entry and entry.slot_id is not None else None
            await self._state.save_prefix_checkpoint(
                item.prefix_hash,
                worker.host,
                worker.rpc_port,
                slot_id=slot,
            )
            log.info("prefix_saved",
                     trace_id=item.trace_id, session_id=item.session_id,
                     checkpoint=item.prefix_hash, node=worker.name,
                     elapsed_ms=self._elapsed_ms(item))
        except Exception:
            self._prefix_set.discard(prefix_key)
