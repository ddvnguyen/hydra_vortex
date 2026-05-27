from python_shared.log_config import get_logger, new_trace_id
from python_shared.rpc_client import RpcClient, OpCode
from coordinator.session_table import SessionTable

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
        client = self._agent_client(node_host, node_port)
        try:
            resp = await client.request(OpCode.SaveState, session_id, trace_id=trace_id)
            entry = self._session_table.lookup(session_id)
            if entry:
                n_past = resp.meta.get("n_past", entry.n_past)
                self._session_table.update_n_past(session_id, n_past)
                self._session_table.mark_evicted(session_id)
            log.info("state_saved", session_id=session_id, meta=resp.meta)
            return resp.meta
        finally:
            await client.close()

    async def restore_session(
        self, session_id: str, target_host: str, target_port: int
    ) -> dict:
        trace_id = new_trace_id()
        client = self._agent_client(target_host, target_port)
        try:
            resp = await client.request(OpCode.RestoreState, session_id, trace_id=trace_id)
            slot_id = resp.meta.get("slot_id")
            n_past = resp.meta.get("n_past", 0)
            entry = self._session_table.lookup(session_id)
            if entry:
                entry.node_name = resp.meta.get("node_name", entry.node_name)
                entry.slot_id = slot_id
                entry.n_past = n_past
                entry.has_store_state = False
            log.info("state_restored", session_id=session_id, slot_id=slot_id, n_past=n_past)
            return resp.meta
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
    ) -> dict:
        log.info("migrate_start", session_id=session_id, to=to_node_name)

        save_meta = await self.save_session(session_id, from_host, from_port)

        erase_client = self._agent_client(from_host, from_port)
        try:
            entry = self._session_table.lookup(session_id)
            slot_id = str(entry.slot_id) if entry and entry.slot_id is not None else ""
            trace_id = new_trace_id()
            await erase_client.request(OpCode.SlotErase, slot_id, trace_id=trace_id)
        finally:
            await erase_client.close()

        restore_meta = await self.restore_session(session_id, to_host, to_port)

        entry = self._session_table.lookup(session_id)
        if entry:
            entry.node_name = to_node_name

        result = {**save_meta, **restore_meta}
        log.info("migrate_done", session_id=session_id, to=to_node_name, meta=result)
        return result

    async def evict_lru(
        self, node_name: str, node_host: str, node_port: int
    ) -> int | None:
        entry = self._session_table.get_lru_session(node_name)
        if not entry:
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
