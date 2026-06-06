import json
import time

from python_shared.log_config import get_logger, new_trace_id
from python_shared.rpc_client import RpcClient, OpCode
from coordinator.session_table import SessionTable
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

    async def save_session(self, session_id: str, node_host: str, node_port: int, trace_id: str = "") -> dict:
        if not trace_id:
            trace_id = new_trace_id()
        entry = self._session_table.lookup(session_id)
        slot_id = entry.slot_id if entry and entry.slot_id is not None else 0
        client = self._agent_client(node_host, node_port)
        try:
            resp = await client.request(OpCode.SaveStateChunked, f"{session_id}:{slot_id}", trace_id=trace_id)

            # Also propagate trace to Store for cross-service correlation
            store_client = self._store_client()
            store_key = f"save:{session_id}"
            try:
                await store_client.request(OpCode.PutMeta, store_key, payload=json.dumps({"session_id": session_id, "trace_id": trace_id}).encode(), trace_id=trace_id)
            except Exception as e:
                log.warning("store_meta_put_failed", session_id=session_id, error=str(e))
            n_past = resp.meta.get("n_past", 0) if resp.meta else entry.n_past if entry else 0
            if entry:
                self._session_table.update_n_past(session_id, n_past)
                self._session_table.mark_evicted(session_id)
            log.info("state_saved", session_id=session_id, meta=resp.meta)
            return resp.meta or {}
        finally:
            await client.close()

    async def restore_session(
        self, session_id: str, target_host: str, target_port: int, trace_id: str = ""
    ) -> dict:
        if not trace_id:
            trace_id = new_trace_id()
        entry = self._session_table.lookup(session_id)
        slot_id = entry.slot_id if entry and entry.slot_id is not None else 0
        client = self._agent_client(target_host, target_port)
        try:
            resp = await client.request(OpCode.RestoreStateChunked, f"{session_id}:{slot_id}", trace_id=trace_id)

            # Also propagate trace to Store for cross-service correlation
            store_client = self._store_client()
            store_key = f"restore:{session_id}"
            try:
                resp = await store_client.request(OpCode.PutMeta, store_key, payload=json.dumps({"session_id": session_id, "trace_id": trace_id}).encode(), trace_id=trace_id)
            except Exception as e:
                log.warning("store_meta_put_failed", session_id=session_id, error=str(e))
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
        trace_id: str = "",
    ) -> dict:
        if not trace_id:
            trace_id = new_trace_id()
        t0 = time.monotonic()
        log.info("migrate_start", session_id=session_id, to=to_node_name)

        entry = self._session_table.lookup(session_id)
        slot_id = str(entry.slot_id) if entry and entry.slot_id is not None else ""

        save_meta = await self.save_session(session_id, from_host, from_port, trace_id=trace_id)

        erase_client = self._agent_client(from_host, from_port)
        try:
            await erase_client.request(OpCode.SlotErase, slot_id, trace_id=trace_id)
        finally:
            await erase_client.close()

        restore_meta = await self.restore_session(session_id, to_host, to_port, trace_id=trace_id)

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
        self, node_name: str, node_host: str, node_port: int, trace_id: str = ""
    ) -> int | None:
        entry = self._session_table.get_lru_session(node_name)
        if not entry or entry.slot_id is None:
            return None

        if not trace_id:
            trace_id = new_trace_id()

        log.info("evict_lru", session_id=entry.session_id, node=node_name)
        slot_id = entry.slot_id

        await self.save_session(entry.session_id, node_host, node_port, trace_id=trace_id)

        erase_client = self._agent_client(node_host, node_port)
        try:
            sid = str(slot_id) if slot_id is not None else ""
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
        trace_id: str = "",
    ) -> dict:
        if slot_id is None:
            slot_id = 0

        if not trace_id:
            trace_id = new_trace_id()
        client = self._agent_client(node_host, node_port)
        try:
            resp = await client.request(
                OpCode.SaveStateChunked,
                f"prefix/{checkpoint_name}:{slot_id}",
                trace_id=trace_id,
            )

            # Also propagate trace to Store for cross-service correlation
            store_client = self._store_client()
            store_key = f"prefix_save:{checkpoint_name}"
            try:
                await store_client.request(OpCode.PutMeta, store_key, payload=json.dumps({"checkpoint": checkpoint_name, "trace_id": trace_id}).encode(), trace_id=trace_id)
            except Exception as e:
                log.warning("store_meta_put_failed", session_id=checkpoint_name, error=str(e))

            log.info(
                "prefix_checkpoint_saved",
                checkpoint=checkpoint_name,
                slot_id=slot_id,
                meta=resp.meta,
            )
            return resp.meta
        finally:
            await client.close()

    async def restore_prefix_checkpoint(
        self,
        checkpoint_name: str,
        target_host: str,
        target_port: int,
        slot_id: int | None = None,
        trace_id: str = "",
    ) -> dict:
        if slot_id is None:
            slot_id = 0

        if not trace_id:
            trace_id = new_trace_id()
        client = self._agent_client(target_host, target_port)
        try:
            resp = await client.request(
                OpCode.RestoreStateChunked,
                f"prefix/{checkpoint_name}:{slot_id}",
                trace_id=trace_id,
            )

            # Also propagate trace to Store for cross-service correlation
            store_client = self._store_client()
            store_key = f"prefix_restore:{checkpoint_name}"
            try:
                await store_client.request(OpCode.PutMeta, store_key, payload=json.dumps({"checkpoint": checkpoint_name, "trace_id": trace_id}).encode(), trace_id=trace_id)
            except Exception as e:
                log.warning("store_meta_put_failed", session_id=checkpoint_name, error=str(e))

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
