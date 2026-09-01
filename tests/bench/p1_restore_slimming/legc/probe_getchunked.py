#!/usr/bin/env python3
"""Raw GET_CHUNKED probe: what does the store emit on the wire, first frames?"""
import socket, struct, sys

HOST, PORT = "127.0.0.1", 19500
KEY = "kv/legc-20260901"
OP_GET_CHUNKED = 0x11

def build_request(op: int, key: bytes, payload: bytes, trace: bytes = b"probe") -> bytes:
    # RequestHeader: magic u16=0x4859, op u8, flags u8, keyLen u16,
    # payloadLen u64, traceLen u16  (16 bytes LE)
    # packed layout: Magic H@0, Op B@2, Flags B@3, KeyLen H@4, PayloadLen Q@6, TraceLen H@14
    hdr = struct.pack("<HBBHQH", 0x4859, op, 0, len(key), len(payload), len(trace))
    return hdr + key + trace + payload

def recv_exact(s: socket.socket, n: int) -> bytes:
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            raise ConnectionError(f"EOF after {len(buf)}/{n}")
        buf += chunk
    return buf

s = socket.create_connection((HOST, PORT), timeout=30)
s.sendall(build_request(OP_GET_CHUNKED, KEY.encode(), b"[]"))

# response: status u8, metaLen u24(3B LE), payloadLen u64  (12 bytes total)
hdr = recv_exact(s, 12)
status = hdr[0]
metaLen = int.from_bytes(hdr[1:4], "little")
payloadLen = int.from_bytes(hdr[4:12], "little")
meta = recv_exact(s, metaLen) if metaLen else b""
print(f"status={status:#x} metaLen={metaLen} payloadLen={payloadLen} meta={meta.decode(errors='replace')[:120]}")

# expected framing length = bodies + 8*n
# read first N frames
n = int(sys.argv[1]) if len(sys.argv) > 1 else 5
for i in range(n):
    hdr8 = recv_exact(s, 8)
    idx, size = struct.unpack("<ii", hdr8)
    body = recv_exact(s, min(size, 16))
    print(f"frame {i}: idx={idx} size={size} body16={body.hex()}")
s.close()
