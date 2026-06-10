# Data Models

## Shared Models (hydra/shared/models.py)

```python
from pydantic import BaseModel
from enum import IntEnum

class OpCode(IntEnum):
    PUT = 0x01
    GET = 0x02
    DEL = 0x03
    STAT = 0x04
    LIST = 0x05
    PUT_CHUNKED = 0x10
    GET_CHUNKED = 0x11
    SYNC_PLAN = 0x12
    PUSH_CHUNKS = 0x13
    PUT_META = 0x14
    # 0x20–0x27 retired (was Agent RPC; Agent merged into Hydra.Core C# — PR #203)
    # 0x20 SAVE_STATE, 0x21 RESTORE_STATE, 0x22 SLOT_STATUS,
    # 0x23 SLOT_ERASE, 0x24 NODE_HEALTH, 0x25 COMPLETION,
    # 0x26 SAVE_STATE_CHUNKED, 0x27 RESTORE_STATE_CHUNKED
    GET_MANIFEST = 0x33

class StatusCode(IntEnum):
    OK = 0x00
    NOT_FOUND = 0x01
    ERROR = 0x02
    PARTIAL = 0x03
    BUSY = 0x04

class RpcRequest(BaseModel):
    op: OpCode
    flags: int = 0
    key: str = ""
    trace_id: str = ""
    payload_len: int = 0

class RpcResponse(BaseModel):
    status: StatusCode
    meta: dict = {}
    payload_len: int = 0

class SlotInfo(BaseModel):
    id: int
    n_past: int = 0
    is_processing: bool = False

class NodeInfo(BaseModel):
    name: str
    healthy: bool
    slots: list[SlotInfo]
    slots_total: int
    slots_idle: int
    url: str  # llama-server RPC address (Agent removed — PR #203)

class SessionEntry(BaseModel):
    session_id: str
    node_name: str
    slot_id: int | None = None
    n_past: int = 0
    has_store_state: bool = False
    created_at: float
    last_used_at: float

class SaveResult(BaseModel):
    session_id: str
    n_past: int
    size_bytes: int
    duration_ms: int

class RestoreResult(BaseModel):
    session_id: str
    slot_id: int
    n_past: int
    duration_ms: int

# --- Coordinator HTTP models ---

class ChatMessage(BaseModel):
    role: str  # system, user, assistant
    content: str

class ChatCompletionRequest(BaseModel):
    model: str = "darwin"
    messages: list[ChatMessage]
    max_tokens: int = 512
    temperature: float = 0.86
    top_p: float = 0.95
    top_k: int = 20
    repeat_penalty: float = 1.06
    stream: bool = True
    session_id: str | None = None  # client-provided, or auto-derived

class RoutingDecision(BaseModel):
    node_name: str
    reason: str  # cache_hit, store_restore, long_prompt_rtx, least_loaded
    session_id: str
    slot_id: int | None = None
```
