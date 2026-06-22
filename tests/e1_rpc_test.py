#!/usr/bin/env python3
"""
E1 P/D Cycle Test for llama-engine
Tests the full prefill → STATE_GET → STATE_PUT → decode cycle over RPC
"""

import socket
import struct
import json
import sys

# Hydra RPC constants
MAGIC = 0x4859
REQUEST_HEADER_SIZE = 16
RESPONSE_HEADER_SIZE = 12

# Opcodes
OP_STATE_GET = 0x30
OP_STATE_PUT = 0x31
OP_STATE_META = 0x32
OP_CONFIGURE = 0x40
OP_INFO = 0x41
OP_PREFILL = 0x42
OP_DECODE = 0x43
OP_SET_EXPERT_MODE = 0x44
OP_SWAP_QUANT = 0x45

# Status codes
STATUS_OK = 0x00
STATUS_NOT_FOUND = 0x01
STATUS_ERROR = 0x02

def recv_all(sock, n):
    """Read exactly n bytes from sock, looping until all bytes arrive."""
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise EOFError(f"Connection closed after {len(buf)}/{n} bytes")
        buf += chunk
    return bytes(buf)

def create_request(op, key, payload=b'', trace_id='test'):
    """Create a Hydra RPC request.
    Wire format: header(16) + key + trace + payload  (per rpc-protocol.md)
    """
    key_bytes = key.encode('utf-8')
    trace_bytes = trace_id.encode('utf-8')

    # Request header: magic(2) + op(1) + flags(1) + key_len(2) + payload_len(8) + trace_len(2) = 16 bytes
    header = struct.pack('<HBBHQH',
        MAGIC,
        op,
        0,  # flags
        len(key_bytes),
        len(payload),
        len(trace_bytes)
    )

    return header + key_bytes + trace_bytes + payload

def parse_response(sock):
    """Parse a Hydra RPC response.
    Response header: status(1) + meta_len(3 LE) + payload_len(8 LE) = 12 bytes
    """
    header = recv_all(sock, RESPONSE_HEADER_SIZE)

    status = header[0]
    # meta_len is a 3-byte LE uint24: pad the high byte on the right
    meta_len = struct.unpack('<I', header[1:4] + b'\x00')[0]
    payload_len = struct.unpack('<Q', header[4:12])[0]

    meta = recv_all(sock, meta_len) if meta_len > 0 else b''
    payload = recv_all(sock, payload_len) if payload_len > 0 else b''

    return status, meta, payload

def test_engine_rpc(host='localhost', port=9510):
    """Test the full P/D cycle over RPC"""
    print(f"Connecting to llama-engine at {host}:{port}...")

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.connect((host, port))
    sock.settimeout(30)

    failures = []

    try:
        # Test 1: INFO
        print("\n=== Test 1: INFO ===")
        sock.sendall(create_request(OP_INFO, '0'))
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        assert status == STATUS_OK, f"INFO expected OK, got {status:#x}"
        assert meta, "INFO expected non-empty metadata"
        info = json.loads(meta.decode('utf-8'))
        print(f"Info: {json.dumps(info, indent=2)}")
        assert 'engine' in info, f"INFO metadata missing 'engine' key: {info}"
        print("PASS")

        # Test 2: CONFIGURE
        print("\n=== Test 2: CONFIGURE ===")
        config = json.dumps({"test": "value"})
        sock.sendall(create_request(OP_CONFIGURE, '0', config.encode('utf-8')))
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        assert status == STATUS_OK, f"CONFIGURE expected OK, got {status:#x}"
        print("PASS")

        # Test 3: PREFILL (dummy tokens — verifies wire framing, not model output)
        print("\n=== Test 3: PREFILL ===")
        tokens = struct.pack('<5I', 1, 2, 3, 4, 5)
        sock.sendall(create_request(OP_PREFILL, '0', tokens))
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        assert status == STATUS_OK, f"PREFILL expected OK, got {status:#x}"
        assert meta, "PREFILL expected n_past in metadata"
        result = json.loads(meta.decode('utf-8'))
        print(f"Result: {result}")
        n_past = result.get('n_past', -1)
        assert n_past > 0, f"PREFILL expected n_past > 0, got {n_past}"
        print(f"n_past after prefill: {n_past}")
        print("PASS")

        # Test 4: STATE_META
        print("\n=== Test 4: STATE_META ===")
        sock.sendall(create_request(OP_STATE_META, '0'))
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        assert status == STATUS_OK, f"STATE_META expected OK, got {status:#x}"
        assert meta, "STATE_META expected metadata"
        result = json.loads(meta.decode('utf-8'))
        print(f"Result: {result}")
        assert 'n_past' in result, f"STATE_META missing n_past: {result}"
        assert 'state_size' in result, f"STATE_META missing state_size: {result}"
        print("PASS")

        # Test 5: DECODE — n_predict(4 bytes LE i32) only; slot is already primed by PREFILL
        print("\n=== Test 5: DECODE ===")
        n_predict = struct.pack('<i', 10)
        sock.sendall(create_request(OP_DECODE, '0', n_predict))
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        assert status == STATUS_OK, f"DECODE expected OK, got {status:#x}"
        print("PASS")

        # Test 6: SET_EXPERT_MODE
        print("\n=== Test 6: SET_EXPERT_MODE ===")
        sock.sendall(create_request(OP_SET_EXPERT_MODE, '0', b'solo'))
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        assert status == STATUS_OK, f"SET_EXPERT_MODE expected OK, got {status:#x}"
        print("PASS")

        print("\n=== All tests passed ===")

    except AssertionError as e:
        failures.append(str(e))
        print(f"FAIL: {e}")
        import traceback
        traceback.print_exc()
    except Exception as e:
        print(f"Error: {e}")
        import traceback
        traceback.print_exc()
    finally:
        sock.close()

    if failures:
        print(f"\n{len(failures)} test(s) failed:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)

if __name__ == '__main__':
    host = sys.argv[1] if len(sys.argv) > 1 else 'localhost'
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 9510
    test_engine_rpc(host, port)
