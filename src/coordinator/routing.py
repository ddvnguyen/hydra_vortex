import hashlib
from typing import Optional

import httpx

from coordinator.config import WorkerNodeConfig
from coordinator.worker_tracker import WorkerTracker
from coordinator.health import HealthMonitor
from coordinator.session_table import SessionEntry

WORKER_PREFILL = 1
WORKER_DECODE = 2
WORKER_MIXED = 3


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


def compute_prefix_hash(messages: list[dict]) -> Optional[str]:
    system_msg = next((m for m in messages if m.get("role") == "system"), None)
    if not system_msg:
        return None
    content = str(system_msg.get("content", ""))
    return hashlib.sha256(content.encode()).hexdigest()[:16]


def compute_full_prefix_hash(messages: list[dict]) -> Optional[str]:
    system_msg = next((m for m in messages if m.get("role") == "system"), None)
    first_user = next((m for m in messages if m.get("role") == "user"), None)
    parts = []
    if system_msg:
        parts.append(f"system:{system_msg.get('content', '')}")
    if first_user:
        parts.append(f"user:{first_user.get('content', '')}")
    if not parts:
        return None
    raw = "\n".join(parts)
    return hashlib.sha256(raw.encode()).hexdigest()[:16]


async def resolve_slot_id(llama_url: str, expected_n_past: int, trace_id: str) -> Optional[int]:
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
    except Exception:
        return None

    slots = data if isinstance(data, list) else data.get("slots", [])
    for slot in slots:
        if slot.get("n_past", 0) == expected_n_past:
            return slot.get("id")
    return None


async def verify_warm_slot(
    worker: WorkerNodeConfig,
    entry: SessionEntry,
    trace_id: str,
    http_client: Optional[httpx.AsyncClient] = None,
) -> bool:
    """Check that the worker's slot for this session is genuinely warm.

    Returns True only if all hold:
    1. A slot with id == entry.slot_id exists.
    2. It is not stuck (not is_processing == true && n_remain == 0).
    3. slot.n_past >= entry.n_past (resident KV covers the session).
    4. prefix_hash matches (guards slot-id reuse by another session).
    """
    client_provided = http_client is not None
    client = http_client or httpx.AsyncClient(timeout=5)
    try:
        resp = await client.get(
            f"{worker.llama_url.rstrip('/')}/slots",
            headers={"X-Trace-Id": trace_id},
        )
        if resp.status_code != 200:
            return False
        data = resp.json()
    except Exception:
        return False
    finally:
        if not client_provided:
            await client.aclose()

    slots = data if isinstance(data, list) else data.get("slots", [])
    for slot in slots:
        if slot.get("id") != entry.slot_id:
            continue
        if slot.get("is_processing") and slot.get("n_remain", 1) == 0:
            return False
        if slot.get("n_past", 0) < (entry.n_past or 0):
            return False
        if entry.prefix_hash:
            slot_prefix = slot.get("prefix_hash") or slot.get("prompt_prefix_hash")
            if slot_prefix and slot_prefix != entry.prefix_hash:
                return False
        return True
    return False


def _eligible_prefill_workers(
    workers: list[WorkerNodeConfig],
    tracker: WorkerTracker,
    health: HealthMonitor,
    max_tokens: Optional[int] = None,
    exclude: Optional[str] = None,
) -> list[WorkerNodeConfig]:
    eligible = []
    for w in workers:
        if not (w.worker_type & WORKER_PREFILL):
            continue
        if w.name == exclude:
            continue
        if not health.is_healthy(w.name):
            continue
        if not tracker.is_free(w.name):
            continue
        if max_tokens is not None and w.max_prefill_tokens != -1:
            if max_tokens > w.max_prefill_tokens:
                continue
        eligible.append(w)
    return eligible


def _eligible_decode_workers(
    workers: list[WorkerNodeConfig],
    tracker: WorkerTracker,
    health: HealthMonitor,
    exclude: Optional[str] = None,
) -> list[WorkerNodeConfig]:
    eligible = []
    for w in workers:
        if not (w.worker_type & WORKER_DECODE):
            continue
        if w.name == exclude:
            continue
        if not health.is_healthy(w.name):
            continue
        if not tracker.is_free(w.name):
            continue
        eligible.append(w)
    return eligible


def pick_best_prefill_worker(
    workers: list[WorkerNodeConfig],
    tracker: WorkerTracker,
    health: HealthMonitor,
    max_tokens: Optional[int] = None,
    exclude: Optional[str] = None,
) -> Optional[WorkerNodeConfig]:
    eligible = _eligible_prefill_workers(workers, tracker, health, max_tokens, exclude)
    if not eligible:
        return None
    return min(eligible, key=lambda w: (w.prefill_priority, w.name))


def pick_best_decode_worker(
    workers: list[WorkerNodeConfig],
    tracker: WorkerTracker,
    health: HealthMonitor,
    exclude: Optional[str] = None,
) -> Optional[WorkerNodeConfig]:
    eligible = _eligible_decode_workers(workers, tracker, health, exclude)
    if not eligible:
        return None
    return min(eligible, key=lambda w: (w.decode_priority, w.name))


def pick_best_mixed_worker(
    workers: list[WorkerNodeConfig],
    tracker: WorkerTracker,
    health: HealthMonitor,
    max_tokens: Optional[int] = None,
    exclude: Optional[str] = None,
) -> Optional[WorkerNodeConfig]:
    eligible = [
        w for w in workers
        if (w.worker_type & WORKER_PREFILL) and (w.worker_type & WORKER_DECODE)
        and w.name != exclude
        and health.is_healthy(w.name)
        and tracker.is_free(w.name)
        and (max_tokens is None or w.max_prefill_tokens == -1 or max_tokens <= w.max_prefill_tokens)
    ]
    if not eligible:
        return None
    return min(eligible, key=lambda w: (w.prefill_priority, w.decode_priority, w.name))
