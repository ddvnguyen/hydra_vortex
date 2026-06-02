import hashlib
from dataclasses import dataclass
from typing import Optional

from coordinator.config import NodeConfig
from coordinator.session_table import SessionTable

# Incremented on every new-session routing decision so that ties in load are
# broken by rotating across nodes rather than always picking the first in dict
# order (which would always be RTX, starving P100 when both are idle).
_rr_counter: int = 0


@dataclass
class RoutingDecision:
    node_name: str
    node_config: NodeConfig
    slot_id: Optional[int] = None
    action: str = "route"  # "route" or "store_restore"
    session_id: Optional[str] = None
    session_found: bool = False
    n_past: int = 0


def derive_session_id(messages: list[dict]) -> str:
    # Anchor on system prompt + first user message so all turns of the same
    # conversation hash to the same ID regardless of how many assistant/user
    # exchanges have accumulated. Hashing the full history would generate a new
    # ID on every turn, making affinity routing impossible.
    sys_content = next(
        (m.get("content", "") for m in messages if m.get("role") == "system"), ""
    )
    first_user_content = next(
        (m.get("content", "") for m in messages if m.get("role") == "user"), ""
    )
    raw = f"sys:{sys_content}\nuser:{first_user_content}"
    return "sess_" + hashlib.sha256(raw.encode()).hexdigest()[:24]


def estimate_request_tokens(messages: list[dict], chars_per_token: float = 4.0) -> int:
    total_chars = 0
    for msg in messages:
        total_chars += len(str(msg.get("content", "")))
    return max(1, int(total_chars / chars_per_token))


def route_request(
    request_messages: list[dict],
    session_table: SessionTable,
    nodes: list[NodeConfig],
    health_info: dict[str, dict],
    chars_per_token: float = 4.0,
    long_prompt_threshold: int = 4096,
    session_id: Optional[str] = None,
) -> RoutingDecision:
    if session_id:
        entry = session_table.lookup(session_id)
    else:
        entry = None

    # Only fall back to derived session_id when caller did NOT provide one.
    # When caller provides session_id and it's not found, keep it as-is so
    # the caller can register it as a new session (without being overridden
    # by a derived ID that may collide with a different session on the same
    # message content).
    if not entry and not session_id:
        derived_id = derive_session_id(request_messages)
        entry = session_table.lookup(derived_id)
        if entry:
            session_id = derived_id

    healthy_nodes = {
        name: info
        for name, info in health_info.items()
        if info.get("healthy", False)
    }

    if not healthy_nodes:
        raise RuntimeError("No healthy nodes available")

    rtx_nodes = {
        name: info
        for name, info in healthy_nodes.items()
        if info.get("gpu_type") == "rtx5060ti"
    }

    def load_fraction(node_name: str) -> float:
        """Busy-slot fraction [0.0, 1.0]. Normalises by capacity so RTX (2 slots)
        and P100 (1 slot) are compared fairly: 1 busy slot on each = 0.5 vs 1.0."""
        info = healthy_nodes.get(node_name, {})
        total = max(info.get("slots_total", 1), 1)
        idle = info.get("slots_idle", 0)
        return (total - idle) / total

    if entry:
        if entry.node_name in healthy_nodes:
            node_cfg = next((n for n in nodes if n.name == entry.node_name), None)
            if node_cfg:
                return RoutingDecision(
                    node_name=entry.node_name,
                    node_config=node_cfg,
                    slot_id=entry.slot_id,
                    action="route",
                    session_id=entry.session_id,
                    session_found=True,
                    n_past=entry.n_past,
                )

        if entry.has_store_state:
            sorted_healthy = sorted(
                (n for n in nodes if n.name in healthy_nodes),
                key=lambda n: load_fraction(n.name),
            )
            target = sorted_healthy[0] if sorted_healthy else None
            if target:
                return RoutingDecision(
                    node_name=target.name,
                    node_config=target,
                    action="store_restore",
                    session_id=entry.session_id,
                    session_found=True,
                    n_past=entry.n_past,
                )

    estimated = estimate_request_tokens(request_messages, chars_per_token)
    if estimated >= long_prompt_threshold and rtx_nodes:
        target_name = next(iter(rtx_nodes))
        node_cfg = next(n for n in nodes if n.name == target_name)
        return RoutingDecision(
            node_name=target_name,
            node_config=node_cfg,
            action="route",
            session_id=session_id,
        )

    sorted_healthy = sorted(healthy_nodes.keys(), key=load_fraction)

    if sorted_healthy:
        global _rr_counter
        min_load = load_fraction(sorted_healthy[0])
        # Collect all nodes tied at the minimum load and rotate between them so
        # P100 gets a turn even when both nodes are fully idle.
        tied = [n for n in sorted_healthy if load_fraction(n) == min_load]
        target_name = tied[_rr_counter % len(tied)]
        _rr_counter += 1

        node_cfg = next(n for n in nodes if n.name == target_name)
        return RoutingDecision(
            node_name=target_name,
            node_config=node_cfg,
            action="route",
            session_id=session_id,
        )

    raise RuntimeError("No healthy nodes available")
