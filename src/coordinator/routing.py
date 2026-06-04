import hashlib
import time
from dataclasses import dataclass
from typing import Optional

from coordinator.config import WorkerNodeConfig
from coordinator.session_table import SessionTable

WORKER_PREFILL = 1
WORKER_DECODE = 2
WORKER_MIXED = 3

# Incremented on every new-session routing decision so that ties in load are
# broken by rotating across nodes rather than always picking the first in sort
# order (which would always be the same node when priorities are equal).
_rr_counter: int = 0


@dataclass
class RoutingDecision:
    node_name: str
    node_config: WorkerNodeConfig
    slot_id: Optional[int] = None
    action: str = "route"  # "route" or "store_restore"
    session_id: Optional[str] = None
    session_found: bool = False
    n_past: int = 0


def derive_session_id(messages: list[dict]) -> str:
    key_parts = []
    for msg in messages:
        role = msg.get("role", "")
        content = msg.get("content", "")
        key_parts.append(f"{role}:{content}")
    raw = "\n".join(key_parts)
    return "sess_" + hashlib.sha256(raw.encode()).hexdigest()[:24]


def estimate_request_tokens(messages: list[dict], chars_per_token: float = 4.0) -> int:
    total_chars = 0
    for msg in messages:
        total_chars += len(str(msg.get("content", "")))
    return max(1, int(total_chars / chars_per_token))


def _load_fraction(
    worker_name: str,
    health_info: dict[str, dict],
    in_flight: Optional[dict[str, int]] = None,
) -> float:
    """Busy-slot fraction [0.0, 1.0]. Combines health-poll data with in-flight
    counter so concurrent requests see up-to-date load without waiting for the
    next poll (which can be up to health_poll_interval_s stale)."""
    info = health_info.get(worker_name, {})
    total = max(info.get("slots_total", 1), 1)
    idle = info.get("slots_idle", 0)
    inflight = (in_flight or {}).get(worker_name, 0)
    busy = min(total, (total - idle) + inflight)
    return busy / total


def _sort_key_prefill(
    worker: WorkerNodeConfig,
    health_info: dict[str, dict],
    in_flight: Optional[dict[str, int]],
) -> tuple:
    """Sort key for prefill worker selection: priority ASC, then load ASC."""
    return (worker.prefill_priority, _load_fraction(worker.name, health_info, in_flight))


def _sort_key_decode(
    worker: WorkerNodeConfig,
    health_info: dict[str, dict],
    in_flight: Optional[dict[str, int]],
) -> tuple:
    """Sort key for decode worker selection: priority ASC, then load ASC."""
    return (worker.decode_priority, _load_fraction(worker.name, health_info, in_flight))


def _has_capacity(
    worker_name: str,
    health_info: dict[str, dict],
    in_flight: Optional[dict[str, int]] = None,
) -> bool:
    info = health_info.get(worker_name, {})
    total = info.get("slots_total", 0)
    idle = info.get("slots_idle", 0)
    inflight = (in_flight or {}).get(worker_name, 0)
    return (total - idle + inflight) < total


def select_prefill_worker(
    workers: list[WorkerNodeConfig],
    health_info: dict[str, dict],
    in_flight: Optional[dict[str, int]] = None,
    exclude: Optional[str] = None,
) -> Optional[WorkerNodeConfig]:
    """Return the highest-priority healthy PREFILL-capable worker with capacity."""
    healthy_prefill = [
        w for w in workers
        if (w.worker_type & WORKER_PREFILL)
        and health_info.get(w.name, {}).get("healthy", False)
        and w.name != exclude
        and _has_capacity(w.name, health_info, in_flight)
    ]
    if not healthy_prefill:
        return None
    return min(healthy_prefill, key=lambda w: _sort_key_prefill(w, health_info, in_flight))


def select_decode_worker(
    workers: list[WorkerNodeConfig],
    health_info: dict[str, dict],
    in_flight: Optional[dict[str, int]] = None,
    exclude: Optional[str] = None,
) -> Optional[WorkerNodeConfig]:
    """Return the highest-priority healthy DECODE-capable worker with capacity."""
    healthy_decode = [
        w for w in workers
        if (w.worker_type & WORKER_DECODE)
        and health_info.get(w.name, {}).get("healthy", False)
        and w.name != exclude
        and _has_capacity(w.name, health_info, in_flight)
    ]
    if not healthy_decode:
        return None
    return min(healthy_decode, key=lambda w: _sort_key_decode(w, health_info, in_flight))


def route_request(
    request_messages: list[dict],
    session_table: SessionTable,
    workers: list[WorkerNodeConfig],
    health_info: dict[str, dict],
    chars_per_token: float = 4.0,
    long_prompt_threshold: int = 8192,
    session_id: Optional[str] = None,
    in_flight: Optional[dict[str, int]] = None,
) -> RoutingDecision:
    global _rr_counter

    # --- Session lookup ---
    if session_id:
        entry = session_table.lookup(session_id)
    else:
        entry = None

    if not entry and not session_id:
        derived_id = derive_session_id(request_messages)
        entry = session_table.lookup(derived_id)
        if entry:
            session_id = derived_id

    healthy_workers = {
        w.name: health_info[w.name]
        for w in workers
        if health_info.get(w.name, {}).get("healthy", False)
    }

    if not healthy_workers:
        raise RuntimeError("No healthy workers available")

    # --- Session affinity (with capacity check) ---
    if entry:
        if entry.node_name in healthy_workers:
            cfg = next((w for w in workers if w.name == entry.node_name), None)
            if cfg:
                load = _load_fraction(entry.node_name, health_info, in_flight)
                if load < 1.0:
                    return RoutingDecision(
                        node_name=entry.node_name,
                        node_config=cfg,
                        slot_id=entry.slot_id,
                        action="route",
                        session_id=entry.session_id,
                        session_found=True,
                        n_past=entry.n_past,
                    )

        if entry.has_store_state:
            # Session evicted to store — restore on least-loaded worker with capacity
            healthy_list = [
                w for w in workers
                if w.name in healthy_workers
                and _has_capacity(w.name, health_info, in_flight)
            ]
            if healthy_list:
                target = min(
                    healthy_list,
                    key=lambda w: _load_fraction(w.name, health_info, in_flight),
                )
                return RoutingDecision(
                    node_name=target.name,
                    node_config=target,
                    action="store_restore",
                    session_id=entry.session_id,
                    session_found=True,
                    n_past=entry.n_past,
                )

    # --- Long-prompt: prefer a PREFILL-capable worker ---
    estimated = estimate_request_tokens(request_messages, chars_per_token)
    if estimated >= long_prompt_threshold:
        prefill_worker = select_prefill_worker(workers, health_info, in_flight)
        if prefill_worker:
            return RoutingDecision(
                node_name=prefill_worker.name,
                node_config=prefill_worker,
                action="route",
                session_id=session_id,
            )

    # --- Least-loaded with round-robin tiebreak ---
    healthy_list = [w for w in workers if w.name in healthy_workers]
    if not healthy_list:
        raise RuntimeError("No healthy workers available")

    healthy_list.sort(key=lambda w: _load_fraction(w.name, health_info, in_flight))
    min_load = _load_fraction(healthy_list[0].name, health_info, in_flight)
    tied = [w for w in healthy_list if _load_fraction(w.name, health_info, in_flight) == min_load]
    target = tied[_rr_counter % len(tied)]
    _rr_counter += 1

    return RoutingDecision(
        node_name=target.name,
        node_config=target,
        action="route",
        session_id=session_id,
    )
