import json
import os
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
    slot_freed: bool = False
    prefix_hash: Optional[str] = None
    created_at: float = field(default_factory=time.time)
    last_used: float = field(default_factory=time.time)


class SessionTable:
    def __init__(self):
        self._sessions: dict[str, SessionEntry] = {}

    def lookup(self, session_id: str) -> Optional[SessionEntry]:
        return self._sessions.get(session_id)

    def register(self, session_id: str, node_name: str, slot_id: int | None = None, n_past: int = 0, prefix_hash: str | None = None):
        prev = self._sessions.get(session_id)
        now = time.time()
        entry = SessionEntry(
            session_id=session_id,
            node_name=node_name,
            slot_id=slot_id,
            n_past=n_past,
            prefix_hash=prefix_hash,
            created_at=prev.created_at if prev else now,
            last_used=now,
        )
        if prev:
            entry.has_store_state = prev.has_store_state
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
            entry.slot_freed = True
            entry.has_store_state = True

    def get_sessions_on_node(self, node_name: str) -> list[SessionEntry]:
        return [s for s in self._sessions.values() if s.node_name == node_name]

    def get_lru_session(self, node_name: str) -> Optional[SessionEntry]:
        sessions = [s for s in self.get_sessions_on_node(node_name)
                    if s.slot_id is not None and not s.slot_freed]
        if not sessions:
            return None
        return min(sessions, key=lambda s: s.last_used)

    def active_count_on_node(self, node_name: str) -> int:
        return sum(1 for s in self._sessions.values() if s.node_name == node_name)

    def get_stale_session_ids(self, timeout_s: int) -> list[str]:
        now = time.time()
        return [sid for sid, entry in self._sessions.items()
                if now - entry.last_used > timeout_s]

    def evict_stale(self, timeout_s: int) -> int:
        stale = self.get_stale_session_ids(timeout_s)
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

    def save_to_file(self, path: str):
        persisted = [
            {
                "session_id": e.session_id,
                "n_past": e.n_past,
                "has_store_state": e.has_store_state,
                "prefix_hash": e.prefix_hash,
            }
            for e in self._sessions.values()
            if e.has_store_state
        ]
        if persisted:
            tmp = f"{path}.tmp"
            with open(tmp, "w") as f:
                json.dump(persisted, f)
            os.replace(tmp, path)

    def load_from_file(self, path: str):
        if not os.path.exists(path):
            return
        try:
            with open(path) as f:
                data = json.load(f)
        except (json.JSONDecodeError, OSError):
            return
        now = time.time()
        for d in data:
            sid = d.get("session_id", "")
            if not sid or sid in self._sessions:
                continue
            self._sessions[sid] = SessionEntry(
                session_id=sid,
                node_name="",
                slot_id=None,
                n_past=d.get("n_past", 0),
                has_store_state=d.get("has_store_state", True),
                prefix_hash=d.get("prefix_hash"),
                created_at=now,
                last_used=now,
            )
