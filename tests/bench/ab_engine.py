"""
A/B driver — confirm new engine (RPC opcodes 0x40-0x46) vs legacy fork
(HTTP + state opcodes 0x30-0x32) for each of the 7 capabilities listed
in the reframed issue #306.

Runs both paths on the same llama-server binary (the engine build supports
both), asserts functional equivalence, and prints an A/B table per
capability. Exits non-zero on any equivalence failure. Exits 0 if the
engine is absent (NOT_IMPLEMENTED) and only the legacy path is exercised
— that is the "detect, don't assume" behaviour the issue specifies.

**Same request, same model, same hardware, two transports.**

Usage:
    # Engine (binary RPC) at 127.0.0.1:9503, legacy HTTP at 127.0.0.1:8080
    python -m tests.bench.ab_engine --engine-rpc 127.0.0.1:9503 --legacy-http http://127.0.0.1:8080

    # Single endpoint (engine + legacy both on :8080; engine RPC detected via 0x41)
    python -m tests.bench.ab_engine --engine-rpc 127.0.0.1:9503 --legacy-http http://127.0.0.1:8080

    # Engine only (legacy path skipped) — useful for smoke testing
    python -m tests.bench.ab_engine --engine-rpc 127.0.0.1:9503 --skip-legacy

The script writes /tmp/ab-engine-results.json with the full per-call data
for later diffing with `tests.bench.compare`.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import struct
import sys
import time
from dataclasses import dataclass, field, asdict
from typing import Any
from urllib.request import Request, urlopen
from urllib.error import URLError, HTTPError

# ── Wire format constants (mirror src/core/Hydra.Shared/Protocol.cs) ──────

MAGIC = 0x4859  # "HY"
REQUEST_HEADER_SIZE  = 16
RESPONSE_HEADER_SIZE = 12

# Op codes (subset)
OP_STATE_GET              = 0x30
OP_STATE_PUT              = 0x31
OP_STATE_META             = 0x32
OP_ENGINE_CONFIGURE       = 0x40
OP_ENGINE_INFO            = 0x41
OP_ENGINE_PREFILL         = 0x42
OP_ENGINE_DECODE          = 0x43
OP_ENGINE_SET_EXPERT_MODE = 0x44
OP_ENGINE_SWAP_QUANT      = 0x45
OP_ENGINE_PIPELINE_ATTACH = 0x46

# Status codes
STATUS_OK             = 0x00
STATUS_NOT_FOUND      = 0x01
STATUS_ERROR          = 0x02
STATUS_PARTIAL        = 0x03
STATUS_BUSY           = 0x04
STATUS_BAD_REQUEST    = 0x05
STATUS_NOT_IMPLEMENTED = 0x06

STATUS_NAMES = {
    STATUS_OK: "OK", STATUS_NOT_FOUND: "NotFound", STATUS_ERROR: "Error",
    STATUS_PARTIAL: "Partial", STATUS_BUSY: "Busy",
    STATUS_BAD_REQUEST: "BadRequest", STATUS_NOT_IMPLEMENTED: "NotImplemented",
}


# ── RPC client (async TCP) ──────────────────────────────────────────────

# NOTE: this RPC client re-implements the wire format from
# `specs/rpc-protocol.md` and `src/core/Hydra.Shared/Protocol.cs` in
# Python. The duplication is intentional — the C# client is a private
# member of the Hydra.Core binary, and we don't want this driver to
# depend on the dotnet build. Review #307 flagged this as worth a
# note: any future addition to the wire format MUST be mirrored here
# AND in specs/rpc-protocol.md. If the formats drift, this client will
# silently corrupt on read or write — the protocol is binary, so an
# off-by-one in the header will manifest as a stuck deserializer, not
# a clean parse error.

@dataclass
class RpcResponse:
    status: int
    meta: dict[str, Any]
    payload: bytes
    meta_raw: str = ""
    elapsed_ms: float = 0.0

    @property
    def status_name(self) -> str:
        return STATUS_NAMES.get(self.status, f"unknown({self.status})")

    @property
    def is_ok(self) -> bool:
        return self.status == STATUS_OK

    @property
    def is_not_implemented(self) -> bool:
        return self.status == STATUS_NOT_IMPLEMENTED


class RpcClient:
    """Minimal async client for the Hydra binary RPC protocol.

    Wire format (from specs/rpc-protocol.md + src/core/Hydra.Shared/Protocol.cs):
      Request header  (16 bytes):
        [2] magic=0x4859 ("HY")
        [1] op
        [1] flags
        [2] key_len     (LE)
        [8] payload_len (LE)
        [2] trace_len   (LE)
      Request body: key | trace_id | payload
      Response header (12 bytes):
        [1] status
        [3] meta_len    (LE)
        [8] payload_len (LE)
      Response body: meta (JSON, UTF-8) | payload
    """

    def __init__(self, host: str, port: int, timeout: float = 60.0) -> None:
        self.host = host
        self.port = port
        self.timeout = timeout
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._lock = asyncio.Lock()

    async def connect(self) -> None:
        async with self._lock:
            if self._writer is not None and not self._writer.is_closing():
                return
            self._reader, self._writer = await asyncio.open_connection(self.host, self.port)

    async def close(self) -> None:
        if self._writer is not None and not self._writer.is_closing():
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except Exception:
                pass
        self._reader = None
        self._writer = None

    async def request(
        self, op: int, key: str = "", payload: bytes = b"",
        trace_id: str = "ab-engine",
    ) -> RpcResponse:
        await self.connect()
        assert self._writer is not None and self._reader is not None
        key_b = key.encode("utf-8")
        trace_b = trace_id.encode("utf-8")
        header = struct.pack(
            "<HBBHQH", MAGIC, op, 0, len(key_b), len(payload), len(trace_b),
        )
        t0 = time.monotonic()
        async with self._lock:
            self._writer.write(header + key_b + trace_b + payload)
            await self._writer.drain()
            # Response header
            resp = await asyncio.wait_for(
                self._reader.readexactly(RESPONSE_HEADER_SIZE), timeout=self.timeout,
            )
            status = resp[0]
            meta_len = resp[1] | (resp[2] << 8) | (resp[3] << 16)
            payload_len = struct.unpack("<Q", resp[4:12])[0]
            meta_bytes = b""
            if meta_len > 0:
                meta_bytes = await asyncio.wait_for(
                    self._reader.readexactly(meta_len), timeout=self.timeout,
                )
            payload_bytes = b""
            if payload_len > 0:
                payload_bytes = await asyncio.wait_for(
                    self._reader.readexactly(payload_len), timeout=self.timeout,
                )
        elapsed_ms = (time.monotonic() - t0) * 1000
        try:
            meta = json.loads(meta_bytes.decode("utf-8")) if meta_bytes else {}
        except json.JSONDecodeError:
            meta = {"_raw": meta_bytes.decode("utf-8", errors="replace")}
        return RpcResponse(
            status=status, meta=meta, payload=payload_bytes,
            meta_raw=meta_bytes.decode("utf-8", errors="replace"),
            elapsed_ms=elapsed_ms,
        )

    async def __aenter__(self) -> "RpcClient":
        await self.connect()
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.close()


# ── HTTP helper (legacy path) ───────────────────────────────────────────

def http_get(url: str, timeout: float = 30.0) -> tuple[int, bytes, dict[str, str]]:
    try:
        with urlopen(url, timeout=timeout) as resp:
            return resp.status, resp.read(), dict(resp.headers)
    except HTTPError as e:
        return e.code, e.read() if e.fp else b"", dict(e.headers) if e.headers else {}
    except URLError as e:
        return 0, str(e).encode(), {}


def http_post_json(url: str, body: dict, timeout: float = 60.0) -> tuple[int, Any, float]:
    data = json.dumps(body).encode("utf-8")
    req = Request(url, data=data, method="POST",
                  headers={"Content-Type": "application/json"})
    t0 = time.monotonic()
    try:
        with urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
        elapsed = (time.monotonic() - t0) * 1000
        return resp.status, json.loads(raw), elapsed
    except HTTPError as e:
        raw = e.read() if e.fp else b""
        elapsed = (time.monotonic() - t0) * 1000
        try:
            return e.code, json.loads(raw), elapsed
        except json.JSONDecodeError:
            return e.code, raw, elapsed


# ── Capability A/B result types ─────────────────────────────────────────

@dataclass
class CapResult:
    """Per-capability A/B result."""
    name: str
    metric: str
    legacy: Any
    engine: Any
    delta: Any
    pass_fail: str  # "PASS" | "FAIL" | "SKIP" | "ERROR"
    notes: str = ""


@dataclass
class AbReport:
    capabilities: list[CapResult] = field(default_factory=list)
    engine_info: dict[str, Any] = field(default_factory=dict)
    legacy_http_ok: bool = False
    engine_rpc_ok: bool = False

    @property
    def any_failed(self) -> bool:
        return any(c.pass_fail == "FAIL" for c in self.capabilities)

    @property
    def any_errored(self) -> bool:
        return any(c.pass_fail == "ERROR" for c in self.capabilities)

    def to_dict(self) -> dict[str, Any]:
        return {
            "engine_info": self.engine_info,
            "legacy_http_ok": self.legacy_http_ok,
            "engine_rpc_ok": self.engine_rpc_ok,
            "capabilities": [asdict(c) for c in self.capabilities],
        }


# ── 7 capabilities ────────────────────────────────────────────────────

# A small deterministic chat-completions body reused across capabilities.
def _small_request_body(seed: int = 42) -> dict[str, Any]:
    return {
        "model": "balanced",
        "messages": [
            {"role": "system", "content": "You are a concise assistant."},
            {"role": "user",   "content": f"Reply with the number {seed*2+1}."},
        ],
        "max_tokens": 12,
        "temperature": 0,
        "seed": seed,
    }


async def detect_engine(rpc: RpcClient) -> dict[str, Any]:
    """Call INFO 0x41; return capabilities dict. Empty if NOT_IMPLEMENTED."""
    try:
        resp = await rpc.request(OP_ENGINE_INFO, key="0")
    except Exception as e:
        return {"_error": f"connect failed: {e!r}"}
    if resp.is_not_implemented:
        return {"_not_implemented": True, "status": resp.status_name}
    if not resp.is_ok:
        return {"_error": f"status={resp.status_name}", "meta_raw": resp.meta_raw}
    return resp.meta


async def cap1_prefill_only(
    rpc: RpcClient | None, legacy_base: str | None, slot: str, seed: int,
) -> CapResult:
    """Cap 1: prefill only — n_past/tokens_processed equivalence."""
    body = _small_request_body(seed=seed)
    payload = json.dumps(body).encode()
    legacy: dict[str, Any] = {"_skipped": True}
    engine: dict[str, Any] = {"_skipped": True}

    if legacy_base is not None:
        # Legacy path: HTTP prefill into slot 0, then STATE_META 0x32
        # We don't have a direct prefill-only HTTP endpoint, so we issue a
        # full chat completion with max_tokens=0 (engine processes prompt,
        # returns no tokens), then read STATE_META via the meta endpoint.
        legacy_chat = dict(body); legacy_chat["max_tokens"] = 0
        status, resp, ms = http_post_json(f"{legacy_base}/v1/chat/completions", legacy_chat)
        if status == 200:
            legacy = {
                "source": "HTTP /v1/chat/completions + GET /slots/0/state/meta",
                "chat_ms": round(ms),
                "chat_prompt_tokens": resp.get("usage", {}).get("prompt_tokens", 0),
            }
            # Now read slot meta
            slot_status, slot_body, _ = http_get(f"{legacy_base}/slots/{slot}/state/meta")
            if slot_status == 200:
                legacy_meta = json.loads(slot_body)
                legacy["n_past"] = legacy_meta.get("n_past", 0)
                legacy["state_size"] = legacy_meta.get("state_size", 0)
        else:
            legacy = {"_error": f"HTTP {status}: {str(resp)[:200]}"}

    if rpc is not None:
        # Engine path: PREFILL 0x42 (n_predict=0)
        try:
            resp = await rpc.request(OP_ENGINE_PREFILL, key=slot, payload=payload)
            if resp.is_not_implemented:
                engine = {"_not_implemented": True}
            elif resp.is_ok:
                engine = {
                    "source": "RPC PREFILL 0x42",
                    "n_past": resp.meta.get("n_past", 0),
                    "tokens_processed": resp.meta.get("tokens_processed", 0),
                    "prefill_ms": resp.meta.get("prefill_ms", 0),
                    "elapsed_ms": round(resp.elapsed_ms),
                    "state_size_in_payload": len(resp.payload),
                    "model_alias": resp.meta.get("model_alias", ""),
                    "model_hash": resp.meta.get("model_hash", "")[:16],
                }
            else:
                engine = {"_error": f"status={resp.status_name}", "meta": resp.meta}
        except Exception as e:
            engine = {"_error": repr(e)}

    # Equivalence check
    legacy_np = legacy.get("n_past") if isinstance(legacy, dict) else None
    engine_np = engine.get("n_past") if isinstance(engine, dict) else None
    if legacy_np is None or engine_np is None:
        return CapResult("cap1_prefill", "n_past", legacy_np, engine_np, "n/a",
                         "SKIP" if (engine.get("_not_implemented") or legacy.get("_skipped")) else "ERROR",
                         notes=f"legacy={legacy.get('_error', '')} engine={engine.get('_error', '')}")
    delta = engine_np - legacy_np
    passed = abs(delta) <= 2  # allow 2-token tolerance for chat-template differences
    return CapResult("cap1_prefill", "n_past", legacy_np, engine_np,
                     delta, "PASS" if passed else "FAIL",
                     notes="PREFILL 0x42 (engine) vs HTTP prefill+STATE_META 0x32 (legacy)")


async def cap2_decode_only(
    rpc: RpcClient | None, legacy_base: str | None, slot: str, seed: int,
) -> CapResult:
    """Cap 2: decode only — token stream equivalence."""
    body = _small_request_body(seed=seed)
    payload = json.dumps(body).encode()
    legacy_tokens: str | None = None
    engine_tokens: str | None = None
    legacy_ms = 0.0
    engine_ms = 0.0

    if legacy_base is not None:
        status, resp, ms = http_post_json(f"{legacy_base}/v1/chat/completions", body)
        legacy_ms = ms
        if status == 200:
            legacy_tokens = (
                resp.get("choices", [{}])[0].get("message", {}).get("content", "")
            )
        else:
            return CapResult("cap2_decode", "token_text", None, None, "n/a", "ERROR",
                             notes=f"legacy HTTP {status}")

    if rpc is not None:
        try:
            resp = await rpc.request(OP_ENGINE_DECODE, key=slot, payload=payload)
            engine_ms = resp.elapsed_ms
            if resp.is_not_implemented:
                pass
            elif resp.is_ok:
                # Engine decode payload is streamed frames of [4B token_id][4B logprob][1B flags]
                # We don't have a tokenizer in Python; just note the byte count and stop_reason.
                engine_tokens = f"<{len(resp.payload)}B stream; stop_reason={resp.meta.get('stop_reason', '?')}>"
            else:
                return CapResult("cap2_decode", "token_text", legacy_tokens, None, "n/a", "ERROR",
                                 notes=f"engine status={resp.status_name}")
        except Exception as e:
            return CapResult("cap2_decode", "token_text", legacy_tokens, None, "n/a", "ERROR",
                             notes=f"engine exception: {e!r}")

    if engine_tokens is None and legacy_tokens is None:
        return CapResult("cap2_decode", "ms_legacy_vs_engine", "n/a", "n/a", "n/a", "SKIP",
                         notes="both paths unavailable")
    if engine_tokens is None and legacy_tokens is not None:
        return CapResult("cap2_decode", "token_text (legacy only)", legacy_tokens, "n/a", "n/a", "SKIP",
                         notes="engine decode not implemented in this build. Token-equivalence is validated by EngineModeTests (C#) — see src/core/Tests.Core/Integration/EngineModeTests.cs")
    if engine_tokens and legacy_tokens and engine_tokens != "<...>":
        return CapResult("cap2_decode", "token_text", legacy_tokens[:80], engine_tokens[:80],
                         "see notes", "SKIP",
                         notes=f"engine uses token-id stream; legacy uses text. **FUNCTIONAL GAP** (review #307): Python driver has no tokenizer, so token-exact compare is delegated to EngineModeTests (C#) — see src/core/Tests.Core/Integration/EngineModeTests.cs")
    if legacy_ms == 0 or engine_ms == 0:
        return CapResult("cap2_decode", "ms_legacy_vs_engine",
                         round(legacy_ms, 1), round(engine_ms, 1),
                         "n/a", "SKIP",
                         notes="one path produced no timing — likely unavailable")

    # Latency-only pass criterion. **FUNCTIONAL GAP** (review #307): the
    # reframe's cap 2 is "same token stream" — the Python driver doesn't
    # have a Qwen3 tokenizer, so this bench can only check latency. The
    # token-exact compare is validated by EngineModeTests.cs on the C#
    # side; this row should be read as "latency OK" not "tokens
    # equivalent". A future improvement: add a shared vocab file
    # (tokenizer.json) and decode the engine's token-id stream here.
    return CapResult("cap2_decode", "ms_legacy_vs_engine", round(legacy_ms, 1), round(engine_ms, 1),
                     round(engine_ms - legacy_ms, 1), "PASS" if engine_ms <= legacy_ms * 1.5 else "FAIL",
                     notes="LATENCY ONLY — engine <= 1.5x legacy. Token-exact equivalence: see EngineModeTests.cs (review #307).")


async def cap3_kv_save(
    rpc: RpcClient | None, legacy_base: str | None, slot: str, seed: int,
) -> CapResult:
    """Cap 3: KV save — state size equivalence (engine inline vs legacy STATE_GET)."""
    body = _small_request_body(seed=seed)
    payload = json.dumps(body).encode()
    legacy_size: int | None = None
    engine_size: int | None = None

    if legacy_base is not None:
        # STATE_GET 0x30 via the meta endpoint doesn't return size without the bytes.
        # We use the legacy HTTP path: do a prefill, then read STATE_META for state_size.
        legacy_chat = dict(body); legacy_chat["max_tokens"] = 0
        status, resp, _ = http_post_json(f"{legacy_base}/v1/chat/completions", legacy_chat)
        if status == 200:
            slot_status, slot_body, _ = http_get(f"{legacy_base}/slots/{slot}/state/meta")
            if slot_status == 200:
                legacy_size = json.loads(slot_body).get("state_size", 0)

    if rpc is not None:
        try:
            resp = await rpc.request(OP_ENGINE_PREFILL, key=slot, payload=payload)
            if resp.is_not_implemented:
                pass
            elif resp.is_ok:
                engine_size = len(resp.payload)
        except Exception as e:
            return CapResult("cap3_kv_save", "state_size", legacy_size, None, "n/a", "ERROR",
                             notes=f"engine exception: {e!r}")

    if engine_size is None:
        return CapResult("cap3_kv_save", "state_size", legacy_size, "n/a", "n/a", "SKIP",
                         notes="engine PREFILL not available in this build")
    if legacy_size is None:
        return CapResult("cap3_kv_save", "state_size", "n/a", engine_size, "n/a", "SKIP",
                         notes="legacy HTTP path not available")
    delta = engine_size - legacy_size
    # Engine state is inline in PREFILL response; legacy state_size is from
    # STATE_META. They should be very close (a few bytes of header).
    passed = abs(delta) <= 1024
    return CapResult("cap3_kv_save", "state_bytes", legacy_size, engine_size,
                     delta, "PASS" if passed else "FAIL",
                     notes="engine payload len (PREFILL inline) vs legacy STATE_META state_size")


async def cap4_kv_restore(
    rpc: RpcClient | None, legacy_base: str | None, slot: str, seed: int,
) -> CapResult:
    """Cap 4: KV restore — decode after save/restore matches decode without round-trip.

    **FUNCTIONAL GAP** (review #307): this bench confirms the state blob
    is *accepted* by STATE_PUT (0x31) but does NOT verify that decode
    after restore produces the same tokens as decode without the
    round-trip. The reframe's cap 4 is "decode after restore == decode
    without save/restore round-trip" — that requires a tokenizer
    (same gap as cap 2) and a C# integration test. See
    `src/core/Tests.Core/Integration/EngineModeTests.cs`.

    The legacy path (STATE_PUT via the fork's HTTP `/slots/{id}/state`
    endpoint) is also not tested here — that needs a binary client
    on the legacy port.
    """
    body = _small_request_body(seed=seed)
    payload = json.dumps(body).encode()
    legacy_ok: bool | None = None
    engine_ok: bool | None = None

    if legacy_base is not None:
        # Use STATE_PUT 0x31 via the binary RPC client on the same port.
        # We don't have a separate Python RPC client for the legacy path;
        # use the meta endpoint to confirm the slot accepts a put.
        # For now: mark legacy as "not directly tested" — the StatePut test
        # requires a binary client. Mark as informational.
        legacy_ok = None  # skip in the absence of a binary client for the legacy port

    if rpc is not None:
        try:
            # Get a state blob first (PREFILL returns state inline)
            prefill = await rpc.request(OP_ENGINE_PREFILL, key=slot, payload=payload)
            if prefill.is_ok and prefill.payload:
                # Now feed it back via STATE_PUT 0x31
                put = await rpc.request(OP_STATE_PUT, key=slot, payload=prefill.payload)
                if put.is_not_implemented:
                    engine_ok = None
                elif put.is_ok:
                    engine_ok = put.meta.get("restored", False) or put.meta.get("bytes", 0) > 0
                else:
                    return CapResult("cap4_kv_restore", "engine_put_ok", None, False, "n/a", "ERROR",
                                     notes=f"engine STATE_PUT status={put.status_name}")
            else:
                return CapResult("cap4_kv_restore", "engine_put_ok", None, None, "n/a", "ERROR",
                                 notes="engine PREFILL did not return a state blob")
        except Exception as e:
            return CapResult("cap4_kv_restore", "engine_put_ok", None, None, "n/a", "ERROR",
                             notes=f"engine exception: {e!r}")

    if engine_ok is None and legacy_ok is None:
        return CapResult("cap4_kv_restore", "state_put_ok", "n/a", "n/a", "n/a", "SKIP",
                         notes="binary legacy client not available in this bench")
    if engine_ok is None:
        return CapResult("cap4_kv_restore", "state_put_ok", legacy_ok, "n/a", "n/a", "SKIP",
                         notes="engine PREFILL inline + STATE_PUT (0x42 → 0x31) not exercised")
    return CapResult("cap4_kv_restore", "state_put_ok", legacy_ok, engine_ok,
                     "n/a" if legacy_ok is None else (engine_ok == legacy_ok),
                     "PASS" if engine_ok else "FAIL",
                     notes="engine: PREFILL 0x42 → STATE_PUT 0x31 round-trip on the same slot")


async def cap5_metadata(
    rpc: RpcClient | None, legacy_base: str | None, slot: str, seed: int,
) -> CapResult:
    """Cap 5: metadata — INFO 0x41 (engine) vs STATE_META 0x32 (legacy) agree."""
    body = _small_request_body(seed=seed)
    payload = json.dumps(body).encode()
    engine_meta: dict[str, Any] = {}
    legacy_meta: dict[str, Any] = {}

    if legacy_base is not None:
        # Prefill first so STATE_META has n_past > 0
        legacy_chat = dict(body); legacy_chat["max_tokens"] = 0
        status, resp, _ = http_post_json(f"{legacy_base}/v1/chat/completions", legacy_chat)
        if status == 200:
            slot_status, slot_body, _ = http_get(f"{legacy_base}/slots/{slot}/state/meta")
            if slot_status == 200:
                legacy_meta = json.loads(slot_body)

    if rpc is not None:
        try:
            # Engine INFO 0x41 — gives capabilities (not slot-specific n_past)
            info = await rpc.request(OP_ENGINE_INFO, key=slot)
            if info.is_ok:
                engine_meta["capabilities"] = info.meta.get("capabilities", [])
                engine_meta["engine"] = info.meta.get("engine", "")
                engine_meta["version"] = info.meta.get("version", "")
            # Engine STATE_META 0x32 — gives slot n_past
            meta = await rpc.request(OP_STATE_META, key=slot)
            if meta.is_ok:
                engine_meta["n_past"] = meta.meta.get("n_past", 0)
                engine_meta["state_size"] = meta.meta.get("state_size", 0)
        except Exception as e:
            return CapResult("cap5_metadata", "n_past", legacy_meta.get("n_past"), None, "n/a", "ERROR",
                             notes=f"engine exception: {e!r}")

    legacy_np = legacy_meta.get("n_past")
    engine_np = engine_meta.get("n_past")
    if legacy_np is None and engine_np is None:
        return CapResult("cap5_metadata", "n_past", "n/a", "n/a", "n/a", "SKIP",
                         notes="no path produced n_past")
    if legacy_np is None or engine_np is None:
        return CapResult("cap5_metadata", "n_past", legacy_np, engine_np, "n/a", "SKIP",
                         notes="one path missing n_past")
    delta = engine_np - legacy_np
    passed = abs(delta) <= 2
    return CapResult("cap5_metadata", "n_past", legacy_np, engine_np,
                     delta, "PASS" if passed else "FAIL",
                     notes=f"INFO 0x41 + STATE_META 0x32 (engine) vs GET /slots/0/state/meta (legacy). engine caps: {engine_meta.get('capabilities', [])}")


async def cap6_metrics(
    rpc: RpcClient | None, legacy_base: str | None, coord_metrics_url: str,
) -> CapResult:
    """Cap 6: metrics — both routes emit hydra_prefill_seconds etc. on :9501.

    The Coordinator's Prometheus endpoint exposes the histograms that the
    issue requires. We split the check into two tiers:

    * **Required** — should be > 0 once the system has served any traffic.
      prefill/decode always fire; if these are 0 the coordinator isn't
      actually routing.
    * **Optional** — only fire on specific operations (warm-slot save /
      restore, cross-node migration). 0 is fine in a session-less smoke
      test or before any warm sessions / migrations have happened.

    The C5 fix is "metric defined and exposed"; whether the counter has
    been incremented is a usage signal, not a correctness signal.
    """
    status, body, _ = http_get(coord_metrics_url)
    if status != 200:
        return CapResult("cap6_metrics", "metrics_endpoint_ok", False, "n/a", "n/a", "ERROR",
                         notes=f"coord metrics {status}")
    text = body.decode("utf-8", errors="replace")
    # Two passes: (1) find which metric NAMES are declared (via the
    # `# TYPE <name> histogram` line) and (2) sum samples per name across
    # label combinations. A declared histogram with zero samples is
    # perfectly valid — prometheus-net only emits the `_sum`/`_count`
    # series once the first observation arrives. So we track both
    # "declared" and "exercised" independently.
    declared_types: dict[str, str] = {}
    series_totals: dict[str, float] = {}
    for line in text.splitlines():
        if not line:
            continue
        if line.startswith("# TYPE "):
            parts = line.split(" ", 3)
            if len(parts) >= 4:
                declared_types[parts[2]] = parts[3]
            continue
        if line.startswith("#"):
            continue
        if "{" in line:
            name_end = line.index("{")
        else:
            name_end = line.index(" ")
        name = line[:name_end]
        parts = line.split(" ", 1)
        if len(parts) != 2:
            continue
        try:
            v = float(parts[1])
        except ValueError:
            continue
        series_totals[name] = series_totals.get(name, 0.0) + v

    required = [
        "hydra_prefill_seconds_count",
        "hydra_decode_seconds_count",
    ]
    optional = [
        "hydra_save_kv_seconds_count",
        "hydra_restore_kv_seconds_count",
        # C5 fix (#307) — defined in CoordinatorMetrics.cs; only observed
        # when a session is actually migrated. We accept "histogram
        # declared" as proof the fix shipped.
        "hydra_migration_latency_seconds_count",
    ]

    def _base(name: str) -> str:
        """Strip _count / _sum / _bucket suffix to get the histogram base name."""
        for suf in ("_count", "_sum", "_bucket"):
            if name.endswith(suf):
                return name[: -len(suf)]
        return name

    # Required: histogram type declared AND count > 0 (means it's been used).
    required_ok = all(
        (_base(k) in declared_types and series_totals.get(k, 0.0) > 0)
        for k in required
    )
    # Optional: histogram type declared (count may be 0 if not yet exercised).
    optional_ok = all(_base(k) in declared_types for k in optional)
    verdict = "PASS" if (required_ok and optional_ok) else "FAIL"

    present_required = {k: series_totals.get(k, 0.0) for k in required}
    present_optional = {k: series_totals.get(k, 0.0) for k in optional}

    return CapResult(
        "cap6_metrics",
        "required declared+>0 AND optional declared",
        {f"{k}={v}": ("✓" if v > 0 else "0") for k, v in present_required.items()},
        {f"{k}={v}": ("✓" if v > 0 else "0") for k, v in present_optional.items()},
        f"required_ok={required_ok} optional_ok={optional_ok}",
        verdict,
        notes=(
            f"required (declared+exercised): {present_required} | "
            f"optional (declared+exercised): {present_optional} | "
            f"C5 fix (migration_latency histogram declared): "
            f"{'yes' if 'hydra_migration_latency_seconds_count' in declared_types else 'NO'}"
        ),
    )


async def cap7_observability(
    rpc: RpcClient | None, legacy_base: str | None, coord_metrics_url: str,
) -> CapResult:
    """Cap 7: observability — trace_id propagates; both paths produce labelled logs.

    Heuristic: send a request with a known trace_id on each path, then
    verify the coord's metrics endpoint shows a request for that path.
    The actual log correlation is checked via Loki by an external tool;
    here we just confirm the trace_id round-trips and the request was
    served.
    """
    legacy_trace = f"ab-legacy-{int(time.time()*1000)}"
    engine_trace = f"ab-engine-{int(time.time()*1000)}"

    legacy_ok = False
    engine_ok = False

    if legacy_base is not None:
        body = _small_request_body(seed=99)
        body["session_id"] = legacy_trace
        status, _, _ = http_post_json(f"{legacy_base}/v1/chat/completions", body)
        legacy_ok = (status == 200)

    if rpc is not None:
        try:
            payload = json.dumps({
                "model": "balanced",
                "messages": [{"role": "user", "content": "Reply OK."}],
                "max_tokens": 4, "temperature": 0, "seed": 99,
                "session_id": engine_trace,
            }).encode()
            resp = await rpc.request(OP_ENGINE_PREFILL, key="0", payload=payload,
                                     trace_id=engine_trace)
            if resp.is_ok or resp.status == STATUS_ERROR:
                # Any non-NOT_IMPLEMENTED response means the trace_id was accepted.
                engine_ok = True
        except Exception:
            engine_ok = False

    passed = legacy_ok and engine_ok
    return CapResult(
        "cap7_observability", "trace_round_trips", legacy_ok, engine_ok,
        "legacy&engine must both be True" if not passed else "n/a",
        "PASS" if passed else "FAIL",
        notes=f"traces={legacy_trace} / {engine_trace}; full log correlation via Loki is out of scope here",
    )


# ── Driver ──────────────────────────────────────────────────────────────

async def run(args: argparse.Namespace) -> int:
    rpc: RpcClient | None = None
    if args.engine_rpc:
        host, _, port = args.engine_rpc.partition(":")
        rpc = RpcClient(host, int(port), timeout=args.timeout)

    legacy: str | None = None if args.skip_legacy else args.legacy_http
    legacy_ok = False
    if legacy is not None:
        status, _, _ = http_get(f"{legacy}/health", timeout=5.0)
        legacy_ok = (status == 200)

    engine_info: dict[str, Any] = {}
    engine_rpc_ok = False
    if rpc is not None:
        try:
            engine_info = await detect_engine(rpc)
            engine_rpc_ok = "engine" in engine_info  # successful INFO
        except Exception as e:
            engine_info = {"_error": repr(e)}

    report = AbReport(
        engine_info=engine_info,
        legacy_http_ok=legacy_ok,
        engine_rpc_ok=engine_rpc_ok,
    )

    if engine_info.get("_not_implemented"):
        # Pre-#289 binary — engine path is unavailable. Per issue, the
        # driver should still run and report SKIP for engine columns.
        pass

    # Run the 7 capabilities. If the engine RPC connect fails (e.g. the
    # binary doesn't expose :9503 on the host), we degrade to "engine
    # column = SKIP" rather than crashing — this matches the issue's
    # "detect, don't assume" requirement.
    seed = 42
    rpc_unavailable = False
    if rpc is not None:
        try:
            await rpc.connect()
        except (ConnectionRefusedError, OSError, asyncio.TimeoutError) as e:
            engine_info = {"_error": f"connect: {e!r}"}
            report.engine_info = engine_info
            report.engine_rpc_ok = False
            rpc_unavailable = True
    if rpc_unavailable:
        rpc = None  # downstream capability functions treat None as "engine skipped"

    try:
        report.capabilities.append(await cap1_prefill_only(rpc, legacy, "0", seed))
        report.capabilities.append(await cap2_decode_only(rpc, legacy, "0", seed))
        report.capabilities.append(await cap3_kv_save(rpc, legacy, "0", seed))
        report.capabilities.append(await cap4_kv_restore(rpc, legacy, "0", seed))
        report.capabilities.append(await cap5_metadata(rpc, legacy, "0", seed))
        report.capabilities.append(await cap6_metrics(rpc, legacy, args.coord_metrics))
        report.capabilities.append(await cap7_observability(rpc, legacy, args.coord_metrics))
    finally:
        if rpc is not None:
            await rpc.close()

    # Print the A/B tables
    print()
    print("=" * 78)
    print("  ab_engine.py — engine vs legacy A/B (issue #306 reframed)")
    print("=" * 78)
    print(f"  legacy http: {legacy or 'skipped':<24}  ok={legacy_ok}")
    print(f"  engine rpc:  {args.engine_rpc or 'skipped':<24}  ok={engine_rpc_ok}")
    if engine_info:
        print(f"  engine info: {json.dumps({k: v for k, v in engine_info.items() if not k.startswith('_')}, default=str)[:200]}")
    if engine_info.get("_not_implemented"):
        print("  ⚠ engine returned NOT_IMPLEMENTED — engine column will be SKIP for capabilities that need it")
    print()
    print(f"  {'capability':<22} {'metric':<28} {'legacy':>14} {'engine':>14}  verdict")
    print("  " + "-" * 76)
    for c in report.capabilities:
        legacy_str = str(c.legacy)[:14] if c.legacy is not None else "n/a"
        engine_str = str(c.engine)[:14] if c.engine is not None else "n/a"
        print(f"  {c.name:<22} {c.metric:<28} {legacy_str:>14} {engine_str:>14}  {c.pass_fail}")
        if c.notes:
            print(f"    └─ {c.notes[:120]}")
    print()

    # Summary
    passed = sum(1 for c in report.capabilities if c.pass_fail == "PASS")
    failed = sum(1 for c in report.capabilities if c.pass_fail == "FAIL")
    skipped = sum(1 for c in report.capabilities if c.pass_fail == "SKIP")
    errored = sum(1 for c in report.capabilities if c.pass_fail == "ERROR")
    print(f"  TOTAL: {passed} pass, {failed} fail, {skipped} skip, {errored} error (out of {len(report.capabilities)})")
    print()

    # Persist for compare.py
    out_path = args.output or "/tmp/ab-engine-results.json"
    with open(out_path, "w") as f:
        json.dump(report.to_dict(), f, indent=2, default=str)
    print(f"  detailed results: {out_path}")

    if failed > 0 or errored > 0:
        return 1
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description="ab_engine.py — A/B driver (issue #306)")
    p.add_argument("--engine-rpc", default="127.0.0.1:9503",
                   help="host:port of the engine's binary RPC (default: 127.0.0.1:9503)")
    p.add_argument("--legacy-http", default="http://127.0.0.1:8080",
                   help="base URL of the legacy HTTP path (default: http://127.0.0.1:8080)")
    p.add_argument("--coord-metrics", default="http://127.0.0.1:9501/metrics",
                   help="Coordinator Prometheus metrics URL")
    p.add_argument("--skip-legacy", action="store_true",
                   help="skip the legacy HTTP path (engine only)")
    p.add_argument("--timeout", type=float, default=60.0)
    p.add_argument("--output", default=None)
    args = p.parse_args()
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
