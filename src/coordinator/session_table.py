import time
from dataclasses import dataclass, field
from typing import Optional


@dataclass
class SessionEntry:
    session_id: str
    node_name: str
    slot_id: Optional[int]
    n_past: int = 0
    has_store_state: bool = False
    created_at: float = field(default_factory=time.time)
    last_used: float = field(default_factory=time.time)


class SessionTable:
    def __init__(self):
        self._sessions: dict[str, SessionEntry] = {}

    def lookup(self, session_id: str) -> Optional[SessionEntry]:
        return self._sessions.get(session_id)

    def register(self, session_id: str, node_name: str, slot_id: int, n_past: int = 0):
        now = time.time()
        entry = SessionEntry(
            session_id=session_id,
            node_name=node_name,
            slot_id=slot_id,
            n_past=n_past,
            created_at=now,
            last_used=now,
        )
        self._sessions[session_id] = entry
        return entry

    def update_last_used(self, session_id: str):
        entry = self._sessions.get(session_id)
        if entry:
            entry.last_used = time.time()

    def update_n_past(self, session_id: str, n_past: int):
        entry = self._sessions.get(session_id)
        if entry:
            entry.n_past = n_past

    def mark_evicted(self, session_id: str):
        entry = self._sessions.get(session_id)
        if entry:
            entry.slot_id = None
            entry.has_store_state = True

    def get_sessions_on_node(self, node_name: str) -> list[SessionEntry]:
        return [s for s in self._sessions.values() if s.node_name == node_name]

    def get_lru_session(self, node_name: str) -> Optional[SessionEntry]:
        sessions = self.get_sessions_on_node(node_name)
        if not sessions:
            return None
        return min(sessions, key=lambda s: s.last_used)

    def active_count_on_node(self, node_name: str) -> int:
        return sum(1 for s in self._sessions.values() if s.node_name == node_name)

    def evict_stale(self, timeout_s: int) -> int:
        now = time.time()
        stale = [sid for sid, entry in self._sessions.items()
                 if now - entry.last_used > timeout_s]
        for sid in stale:
            del self._sessions[sid]
        return len(stale)

    def remove(self, session_id: str):
        self._sessions.pop(session_id, None)

    @property
    def active_count(self) -> int:
        return len(self._sessions)

    @property
    def all_sessions(self) -> dict[str, SessionEntry]:
        return dict(self._sessions)
