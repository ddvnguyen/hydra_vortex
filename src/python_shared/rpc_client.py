import asyncio
import json
import struct
from enum import IntEnum
from typing import AsyncIterator, Optional


MAGIC = 0x4859
REQUEST_HEADER_SIZE = 16
RESPONSE_HEADER_SIZE = 12


class OpCode(IntEnum):
    Put = 0x01
    Get = 0x02
    Del = 0x03
    Stat = 0x04
    List = 0x05
    PutChunked = 0x10
    GetChunked = 0x11
    SyncPlan = 0x12
    PushChunks = 0x13
    SaveState = 0x20
    RestoreState = 0x21
    SlotStatus = 0x22
    SlotErase = 0x23
    NodeHealth = 0x24
    Completion = 0x25
    SaveStateChunked = 0x26
    RestoreStateChunked = 0x27
    StateGet = 0x30
    StatePut = 0x31
    StateMeta = 0x32


class StatusCode(IntEnum):
    Ok = 0x00
    NotFound = 0x01
    Error = 0x02
    Partial = 0x03
    Busy = 0x04


class RpcError(Exception):
    def __init__(self, status: int, meta: str):
        self.status = status
        self.meta = meta
        super().__init__(f"RPC error (status=0x{status:02X}): {meta}")


class RpcResponse:
    def __init__(self, status: int, meta: Optional[str], payload: bytes):
        self.status = status
        self.meta = json.loads(meta) if meta else {}
        self.payload = payload


class RpcClient:
    def __init__(self, host: str, port: int, retry_delays: list[int] | None = None):
        self._host = host
        self._port = port
        self._retry_delays = retry_delays or [100, 500, 2000]
        self._reader: Optional[asyncio.StreamReader] = None
        self._writer: Optional[asyncio.StreamWriter] = None
        self._lock = asyncio.Lock()
        self._connected = False

    async def _ensure_connected(self):
        if self._connected and self._writer and not self._writer.is_closing():
            return
        self._reader, self._writer = await asyncio.open_connection(
            self._host, self._port
        )
        self._connected = True

    async def _reconnect(self):
        self._connected = False
        if self._writer and not self._writer.is_closing():
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except Exception:
                pass
        self._reader, self._writer = await asyncio.open_connection(
            self._host, self._port
        )
        self._connected = True

    # Request header layout (16 bytes, little-endian, matches specs/rpc-protocol.md):
    #   magic:uint16  op:uint8  flags:uint8  key_len:uint16  payload_len:int64  trace_len:uint16
    _REQUEST_FMT = "<HBBHqH"  # 2+1+1+2+8+2 = 16 bytes

    async def request(
        self, op: OpCode, key: str, payload: bytes = b"", trace_id: str = ""
    ) -> RpcResponse:
        async with self._lock:
            attempts = 0
            while True:
                try:
                    await self._ensure_connected()
                    await self._send_request(op, key, payload, trace_id)
                    return await self._read_response()
                except (ConnectionError, OSError, asyncio.IncompleteReadError):
                    attempts += 1
                    if attempts <= len(self._retry_delays):
                        await asyncio.sleep(self._retry_delays[attempts - 1] / 1000)
                        await self._reconnect()
                    else:
                        raise

    async def request_stream_body(
        self,
        op: OpCode,
        key: str,
        body: asyncio.StreamReader,
        body_len: int,
        trace_id: str = "",
    ) -> RpcResponse:
        async with self._lock:
            attempts = 0
            while True:
                try:
                    await self._ensure_connected()
                    key_bytes = key.encode("utf-8") if key else b""
                    trace_bytes = trace_id.encode("utf-8") if trace_id else b""

                    # Wire format: magic(2) op(1) flags(1) key_len(2) payload_len(8) trace_len(2)
                    header = struct.pack(
                        self._REQUEST_FMT,
                        MAGIC,
                        int(op),
                        0,                    # flags
                        len(key_bytes),       # key_len: uint16
                        body_len,             # payload_len: int64 (up to 4 GB)
                        len(trace_bytes),     # trace_len: uint16
                    )
                    self._writer.write(header)
                    if key_bytes:
                        self._writer.write(key_bytes)
                    if trace_bytes:
                        self._writer.write(trace_bytes)
                    await self._writer.drain()

                    remaining = body_len
                    while remaining > 0:
                        chunk = await body.read(min(65536, remaining))
                        if not chunk:
                            raise ConnectionError("Stream ended early")
                        self._writer.write(chunk)
                        remaining -= len(chunk)
                    await self._writer.drain()

                    return await self._read_response()
                except (ConnectionError, OSError, asyncio.IncompleteReadError):
                    attempts += 1
                    if attempts <= len(self._retry_delays):
                        await asyncio.sleep(self._retry_delays[attempts - 1] / 1000)
                        await self._reconnect()
                    else:
                        raise

    async def request_stream_response(
        self, op: OpCode, key: str, payload: bytes = b"", trace_id: str = ""
    ) -> AsyncIterator[bytes]:
        async with self._lock:
            await self._ensure_connected()
            await self._send_request(op, key, payload, trace_id)

            header_buf = await self._read_exact(RESPONSE_HEADER_SIZE)
            status, meta_len, payload_len = self._parse_response_header(header_buf)

            if status != StatusCode.Ok:
                meta = None
                if meta_len > 0:
                    meta_bytes = await self._read_exact(meta_len)
                    meta = meta_bytes.decode("utf-8")
                raise RpcError(status, meta or "")

            if meta_len > 0:
                await self._read_exact(meta_len)

            remaining = payload_len
            while remaining > 0:
                to_read = min(65536, remaining)
                chunk = await self._read_exact(to_read)
                remaining -= len(chunk)
                yield chunk

    async def _send_request(
        self, op: OpCode, key: str, payload: bytes, trace_id: str
    ):
        key_bytes = key.encode("utf-8") if key else b""
        trace_bytes = trace_id.encode("utf-8") if trace_id else b""

        # Wire format: magic(2) op(1) flags(1) key_len(2) payload_len(8) trace_len(2) = 16 bytes
        header = struct.pack(
            self._REQUEST_FMT,
            MAGIC,
            int(op),
            0,                   # flags
            len(key_bytes),      # key_len: uint16
            len(payload),        # payload_len: int64
            len(trace_bytes),    # trace_len: uint16
        )
        self._writer.write(header)
        if key_bytes:
            self._writer.write(key_bytes)
        if trace_bytes:
            self._writer.write(trace_bytes)
        if payload:
            self._writer.write(payload)
        await self._writer.drain()

    async def _read_response(self) -> RpcResponse:
        header_buf = await self._read_exact(RESPONSE_HEADER_SIZE)
        status, meta_len, payload_len = self._parse_response_header(header_buf)

        meta = None
        if meta_len > 0:
            meta_bytes = await self._read_exact(meta_len)
            meta = meta_bytes.decode("utf-8")

        payload = b""
        if payload_len > 0:
            payload = await self._read_exact(payload_len)

        if status != StatusCode.Ok:
            raise RpcError(status, meta or "")

        return RpcResponse(status, meta, payload)

    async def _read_exact(self, n: int) -> bytes:
        return await self._reader.readexactly(n)

    @staticmethod
    def _parse_response_header(buf: bytes) -> tuple[int, int, int]:
        status = buf[0]
        meta_len = int.from_bytes(buf[1:4], "little")
        payload_len = int.from_bytes(buf[4:12], "little")
        return status, meta_len, payload_len

    async def close(self):
        async with self._lock:
            self._connected = False
            if self._writer and not self._writer.is_closing():
                self._writer.close()
                try:
                    await self._writer.wait_closed()
                except Exception:
                    pass
