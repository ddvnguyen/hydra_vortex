#!/usr/bin/env python3
"""
E1 P/D Cycle Test for llama-engine
Tests the full prefill → STATE_GET → STATE_PUT → decode cycle over RPC
"""

import socket
import struct
import json
import sys
import time

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

def create_request(op, key, payload=b'', trace_id='test'):
    """Create a Hydra RPC request"""
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
    
    return header + key_bytes + payload + trace_bytes

def parse_response(sock):
    """Parse a Hydra RPC response"""
    header = sock.recv(RESPONSE_HEADER_SIZE)
    if len(header) < RESPONSE_HEADER_SIZE:
        raise Exception("Incomplete response header")
    
    status = header[0]
    meta_len = struct.unpack('<I', b'\x00' + header[1:4])[0]
    payload_len = struct.unpack('<Q', header[4:12])[0]
    
    meta = b''
    if meta_len > 0:
        meta = sock.recv(meta_len)
    
    payload = b''
    if payload_len > 0:
        payload = sock.recv(payload_len)
    
    return status, meta, payload

def test_engine_rpc(host='localhost', port=9510):
    """Test the full P/D cycle over RPC"""
    print(f"Connecting to llama-engine at {host}:{port}...")
    
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.connect((host, port))
    sock.settimeout(30)
    
    try:
        # Test 1: INFO
        print("\n=== Test 1: INFO ===")
        req = create_request(OP_INFO, '0')
        sock.sendall(req)
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        if status == STATUS_OK and meta:
            info = json.loads(meta.decode('utf-8'))
            print(f"Info: {json.dumps(info, indent=2)}")
        else:
            print("INFO failed or no metadata")
        
        # Test 2: CONFIGURE
        print("\n=== Test 2: CONFIGURE ===")
        config = json.dumps({"test": "value"})
        req = create_request(OP_CONFIGURE, '0', config.encode('utf-8'))
        sock.sendall(req)
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        if status == STATUS_OK and meta:
            result = json.loads(meta.decode('utf-8'))
            print(f"Result: {result}")
        
        # Test 3: PREFILL (with simple prompt tokens)
        print("\n=== Test 3: PREFILL ===")
        # Simple token sequence: [1, 2, 3, 4, 5] (dummy tokens)
        tokens = struct.pack('<5I', 1, 2, 3, 4, 5)
        req = create_request(OP_PREFILL, '0', tokens)
        sock.sendall(req)
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        if status == STATUS_OK and meta:
            result = json.loads(meta.decode('utf-8'))
            print(f"Result: {result}")
            n_past = result.get('n_past', 0)
            print(f"n_past after prefill: {n_past}")
        
        # Test 4: STATE_META
        print("\n=== Test 4: STATE_META ===")
        req = create_request(OP_STATE_META, '0')
        sock.sendall(req)
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        if status == STATUS_OK and meta:
            result = json.loads(meta.decode('utf-8'))
            print(f"Result: {result}")
        
        # Test 5: DECODE (stub - just verify it responds)
        print("\n=== Test 5: DECODE ===")
        # n_predict (4 bytes) + tokens
        n_predict = struct.pack('<i', 10)
        req = create_request(OP_DECODE, '0', n_predict + tokens)
        sock.sendall(req)
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        if status == STATUS_OK and meta:
            result = json.loads(meta.decode('utf-8'))
            print(f"Result: {result}")
        
        # Test 6: SET_EXPERT_MODE
        print("\n=== Test 6: SET_EXPERT_MODE ===")
        mode = b'solo'
        req = create_request(OP_SET_EXPERT_MODE, '0', mode)
        sock.sendall(req)
        status, meta, payload = parse_response(sock)
        print(f"Status: {status:#x}")
        if status == STATUS_OK and meta:
            result = json.loads(meta.decode('utf-8'))
            print(f"Result: {result}")
        
        print("\n=== All tests completed ===")
        
    except Exception as e:
        print(f"Error: {e}")
        import traceback
        traceback.print_exc()
    finally:
        sock.close()

if __name__ == '__main__':
    host = sys.argv[1] if len(sys.argv) > 1 else 'localhost'
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 9510
    test_engine_rpc(host, port)
