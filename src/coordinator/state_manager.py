import time

import httpx

from python_shared.log_config import get_logger, new_trace_id
from python_shared.rpc_client import RpcClient, OpCode
from coordinator.session_table import SessionTable
from coordinator.proxy import warmup_prefix
from coordinator.metrics import migrations_total, migration_latency

log = get_logger()


class StateManager:
    def __init__(
        self,
        session_table: SessionTable,
        store_host: str,
        store_port: int,
    ):
        self._session_table = session_table
        self._store_host = store_host
        self._store_port = store_port

    def _agent_client(self, host: str, port: int) -> RpcClient:
        return RpcClient(host, port)

    def _store_client(self) -> RpcClient:
        return RpcClient(self._store_host, self._store_port)

    async def save_session(self, session_id: str, node_host: str, node_port: int) -> dict:
        trace_id = new_trace_id()
        entry = self._session_table.lookup(session_id)
        slot_id = entry.slot_id if entry and entry.slot_id is not None else 0
        client = self._agent_client(node_host, node_port)
        try:
            resp = await client.request(OpCode.SaveStateChunked, f"{session_id}:{slot_id}", trace_id=trace_id)
            n_past = resp.meta.get("n_past", 0) if resp.meta else entry.n_past if entry else 0
            if entry:
                self._session_table.update_n_past(session_id, n_past)
                self._session_table.mark_evicted(session_id)
            log.info("state_saved", session_id=session_id, meta=resp.meta)
            return resp.meta or {}
        finally:
            await client.close()

    async def restore_session(
        self, session_id: str, target_host: str, target_port: int
    ) -> dict:
        trace_id = new_trace_id()
        entry = self._session_table.lookup(session_id)
        slot_id = entry.slot_id if entry and entry.slot_id is not None else 0
        client = self._agent_client(target_host, target_port)
        try:
            resp = await client.request(OpCode.RestoreStateChunked, f"{session_id}:{slot_id}", trace_id=trace_id)
            restored = resp.meta.get("restored", False) if resp.meta else False
            n_past = resp.meta.get("n_past", 0) if resp.meta else 0
            slot_id = resp.meta.get("slot_id", slot_id) if resp.meta else slot_id
            node_name = resp.meta.get("node_name", entry.node_name if entry else target_host) if resp.meta else (entry.node_name if entry else target_host)
            if entry:
                entry.node_name = node_name
                entry.slot_id = slot_id
                entry.n_past = n_past
                entry.has_store_state = not restored
            log.info("state_restored", session_id=session_id, slot_id=slot_id, n_past=n_past)
            return resp.meta or {}
        finally:
            await client.close()

    async def migrate_session(
        self,
        session_id: str,
        from_host: str,
        from_port: int,
        to_host: str,
        to_port: int,
        to_node_name: str,
        from_node_name: str = "",
    ) -> dict:
        t0 = time.monotonic()
        log.info("migrate_start", session_id=session_id, to=to_node_name)

        entry = self._session_table.lookup(session_id)
        slot_id = str(entry.slot_id) if entry and entry.slot_id is not None else ""

        save_meta = await self.save_session(session_id, from_host, from_port)

        erase_client = self._agent_client(from_host, from_port)
        try:
            trace_id = new_trace_id()
            await erase_client.request(OpCode.SlotErase, slot_id, trace_id=trace_id)
        finally:
            await erase_client.close()

        restore_meta = await self.restore_session(session_id, to_host, to_port)

        entry = self._session_table.lookup(session_id)
        if entry:
            entry.node_name = to_node_name

        elapsed = time.monotonic() - t0
        migrations_total.labels(from_node=from_node_name, to_node=to_node_name).inc()
        migration_latency.labels(from_node=from_node_name, to_node=to_node_name).observe(elapsed)

        result = {**save_meta, **restore_meta}
        log.info("migrate_done", session_id=session_id, to=to_node_name, meta=result)
        return result

    async def evict_lru(
        self, node_name: str, node_host: str, node_port: int
    ) -> int | None:
        entry = self._session_table.get_lru_session(node_name)
        if not entry or entry.slot_id is None:
            return None

        log.info("evict_lru", session_id=entry.session_id, node=node_name)
        slot_id = entry.slot_id

        await self.save_session(entry.session_id, node_host, node_port)

        erase_client = self._agent_client(node_host, node_port)
        try:
            sid = str(slot_id) if slot_id is not None else ""
            trace_id = new_trace_id()
            await erase_client.request(OpCode.SlotErase, sid, trace_id=trace_id)
        finally:
            await erase_client.close()

        return slot_id

    async def save_prefix_checkpoint(
        self,
        checkpoint_name: str,
        node_host: str,
        node_port: int,
        slot_id: int | None = None,
    ) -> dict:
        if slot_id is None:
            slot_id = 0

        trace_id = new_trace_id()
        client = self._agent_client(node_host, node_port)
        try:
            resp = await client.request(
                OpCode.SaveStateChunked,
                f"prefix/{checkpoint_name}:{slot_id}",
                trace_id=trace_id,
            )
            log.info(
                "prefix_checkpoint_saved",
                checkpoint=checkpoint_name,
                slot_id=slot_id,
                meta=resp.meta,
            )
            return resp.meta
        finally:
            await client.close()

    async def _resolve_warm_slot(
        self, llama_url: str, expected_n_past: int, trace_id: str
    ) -> int | None:
        """Find the llama-server slot whose n_past matches the warm-up result."""
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
            log.warning("resolve_warm_slot_failed", url=llama_url, error=str(exc))
            return None

        slots = data if isinstance(data, list) else data.get("slots", [])
        for slot in slots:
            if slot.get("n_past", 0) == expected_n_past:
                return slot.get("id")
        return None

    async def warmup_and_save_prefix(
        self,
        checkpoint_name: str,
        system_content: str,
        node_host: str,
        node_port: int,
        llama_url: str,
    ) -> int:
        """Prefill the system prompt only, then checkpoint that KV state.

        Saves n_past ~= system_tokens (not a full conversation), so a later
        restore + real request clears the n_past guard and reuses the prefix KV.
        Returns the warmed n_past.
        """
        trace_id = new_trace_id()
        n_past = await warmup_prefix(llama_url, system_content, trace_id)
        if n_past <= 0:
            raise RuntimeError("prefix warmup returned n_past=0")
        slot_id = await self._resolve_warm_slot(llama_url, n_past, trace_id)
        await self.save_prefix_checkpoint(
            checkpoint_name, node_host, node_port, slot_id=slot_id or 0
        )
        log.info(
            "prefix_warmed",
            checkpoint=checkpoint_name,
            n_past=n_past,
            slot_id=slot_id,
        )
        return n_past

    async def restore_prefix_checkpoint(
        self,
        checkpoint_name: str,
        target_host: str,
        target_port: int,
        slot_id: int | None = None,
    ) -> dict:
        if slot_id is None:
            slot_id = 0

        trace_id = new_trace_id()
        client = self._agent_client(target_host, target_port)
        try:
            resp = await client.request(
                OpCode.RestoreStateChunked,
                f"prefix/{checkpoint_name}:{slot_id}",
                trace_id=trace_id,
            )
            log.info(
                "prefix_checkpoint_restored",
                checkpoint=checkpoint_name,
                slot_id=slot_id,
                meta=resp.meta,
            )
            return resp.meta
        except Exception as e:
            log.warning(
                "prefix_checkpoint_restore_failed",
                checkpoint=checkpoint_name,
                error=str(e),
            )
            raise
