import time
from dataclasses import dataclass, field
from typing import Optional


@dataclass
class WorkerTracker:
    _states: dict[str, str] = field(default_factory=dict)
    _roles: dict[str, str] = field(default_factory=dict)
    _busy_since: dict[str, float] = field(default_factory=dict)
    _errors: dict[str, int] = field(default_factory=dict)
    _healthy: dict[str, bool] = field(default_factory=dict)
    _error_threshold: int = 3

    def init_worker(self, name: str) -> None:
        if name not in self._states:
            self._states[name] = "free"
            self._healthy[name] = True
            self._errors[name] = 0

    def acquire(self, name: str, role: str = "decode") -> bool:
        if self._states.get(name) != "free":
            return False
        if not self._healthy.get(name, True):
            return False
        self._states[name] = role
        self._roles[name] = role
        self._busy_since[name] = time.time()
        return True

    def release(self, name: str) -> None:
        self._states[name] = "free"
        self._roles.pop(name, None)
        self._busy_since.pop(name, None)

    def on_error(self, name: str) -> None:
        self._errors[name] = self._errors.get(name, 0) + 1
        if self._errors[name] >= self._error_threshold:
            self._healthy[name] = False

    def on_success(self, name: str) -> None:
        self._errors[name] = 0
        self._healthy[name] = True

    def mark_unhealthy(self, name: str) -> None:
        self._healthy[name] = False

    def mark_healthy(self, name: str) -> None:
        self._healthy[name] = True
        self._errors[name] = 0

    def free_workers(self) -> list[str]:
        return [n for n, s in self._states.items() if s == "free" and self._healthy.get(n, True)]

    def busy_workers(self) -> list[str]:
        return [n for n, s in self._states.items() if s != "free"]

    def is_free(self, name: str) -> bool:
        return self._states.get(name) == "free" and self._healthy.get(name, True)

    def status(self, name: str) -> str:
        return self._states.get(name, "unknown")

    def busy_since(self, name: str) -> Optional[float]:
        return self._busy_since.get(name)

    def elapsed_seconds(self, name: str) -> float:
        since = self._busy_since.get(name)
        if since is None:
            return 0.0
        return time.time() - since

    @property
    def all_workers(self) -> list[str]:
        return list(self._states.keys())
