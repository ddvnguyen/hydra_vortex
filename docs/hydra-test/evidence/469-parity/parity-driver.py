#!/usr/bin/env python3
"""
#751 parity driver — live-GPU parity smoke for the #469 fork fix.

Verifies, on a REAL hybrid (Gated Delta Net + attention) model on the P100
test lane, that the fork's RPC PREFILL (0x42) checkpoint is HONEST at
runtime under fork HEAD 45c1435da, by driving one engine over BOTH:
  * HTTP  POST /v1/chat/completions   (arm A — reference)
  * RPC   0x42 PREFILL                (arm B — n_past honesty)
  * RPC   0x43 DECODE + GET /v1/decode/{id} (arm C — text/token parity)

The literal #469 signature asserted here:
    n_past (RPC PREFILL) == usage.prompt_tokens (HTTP)
and full decode parity:
    decoded text (RPC) == completion text (HTTP)
    completion_tokens (RPC) == completion_tokens (HTTP)
for two different prompt lengths (short ~10 tok, longer ~200 tok) so the
checkpoint-tail behavior is exercised at more than one length.

python3 stdlib only. The XXH3_64bits implementation below is embedded
verbatim from /home/ddv/scratch/w1j-697/xxh3_py.py and was fuzz-validated
against a compiled C reference (xxhash 0.13.1 @ 45c1435da) for lengths
0..512 + block boundaries + 3000 random vectors up to 4096 — 0 mismatches.

Wire protocol (from src/llama-cpp/tools/server/server-context.cpp @45c1435da):
  request  = [u16 0x4859 LE][u8 op][u8 flags=0][u16 keyLen][u64 payloadLen LE]
             [u16 traceLen] + key + trace + payload          (16B header)
  response = [u8 status][u24 metaLen LE][u64 payloadLen LE] + meta + payload (12B header)
  0x42 PREFILL payload  = OpenAI chat JSON; meta = JSON{n_past, ...}
  0x43 DECODE payload   = [u32 hdrLen][u64 hdrHash=XXH3_64(hdrJson) LE][hdrJson]
                          + promptSegment + kvSegment
      hdrJson = {v:3, model, kv_metadata, model_metadata, generation, segments[]}
      segments[] = [{id:"prompt",offset:0,len:PL,hash:"xxh3:HEX"},
                    {id:"kv",offset:PL,len:0,hash:"xxh3:HEX"}]
      (kv len 0 => DECODE continues from the slot's RPC-primed resident state)
      result retrieved via HTTP GET /v1/decode/{decode_request_id}

Usage:
    python3 parity-driver.py
Env overrides:
    HYDRA_HOST (default 127.0.0.1), HYDRA_HTTP_PORT (default 18086),
    HYDRA_RPC_PORT (default 19513), PARITY_LOG_DIR (default .)
"""
import json
import os
import socket
import struct
import sys
import time
import urllib.request

# ----------------------------------------------------------------------------
# Embedded XXH3_64bits (seedless) — fuzz-validated vs compiled C reference.
# ----------------------------------------------------------------------------
import struct

M64 = (1 << 64) - 1

P32_1 = 2654435761
P32_2 = 2246822519
P32_3 = 3266489917
P32_4 = 668265263
P32_5 = 374761393
P64_1 = 11400714785074694791
P64_2 = 14029467366897019727
P64_3 = 1609587929392839161
P64_4 = 9650029242287828579
P64_5 = 2870177450012600261
PRIME_MX1 = 0x165667919E3779F9
PRIME_MX2 = 0x9FB21C651E98DF25

# XXH3_kSecret (192 bytes), verbatim from xxhash.h @45c1435da
KSECRET = bytes([
    0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
    0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
    0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
    0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
    0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
    0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
    0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
    0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
    0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
    0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
    0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
    0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
])
assert len(KSECRET) == 192


def _le32(b, o):
    return struct.unpack_from('<I', b, o)[0]


def _le64(b, o):
    return struct.unpack_from('<Q', b, o)[0]


def _swap32(x):
    return ((x & 0xFF) << 24) | ((x & 0xFF00) << 8) | ((x >> 8) & 0xFF00) | ((x >> 24) & 0xFF)


def _swap64(x):
    return int.from_bytes(x.to_bytes(8, 'little'), 'big')


def _xorshift64(v, s):
    return v ^ (v >> s)


def _avalanche64(h):
    h = (h ^ (h >> 33)) & M64
    h = (h * P64_2) & M64
    h = (h ^ (h >> 29)) & M64
    h = (h * P64_3) & M64
    h = (h ^ (h >> 32)) & M64
    return h


def _avalanche3(h):
    # XXH64_avalanche applied to a 32-bit value promoted to 64 (C: xxh_u64 arg)
    return _avalanche64(h)


def _xxh3_avalanche(h):
    # XXH3_avalanche (xxhash.h): fast avalanche used by the XXH3 64-bit paths
    # (len 9..16, 17..128, 129..240, long merge). NOT XXH64_avalanche.
    h = _xorshift64(h, 37)
    h = (h * PRIME_MX1) & M64
    h = _xorshift64(h, 32)
    return h


def _mul128_fold64(a, b):
    p = a * b
    return (p & M64) ^ (p >> 64)


def _rrmxmx(h, ln):
    h ^= (((h << 49) | (h >> 15)) & M64) ^ (((h << 24) | (h >> 40)) & M64)
    h = (h * PRIME_MX2) & M64
    h = (h ^ ((h >> 35) + ln)) & M64
    h = (h * PRIME_MX2) & M64
    return _xorshift64(h, 28)


def _mix16b(inp, ip, sec, sp):
    return _mul128_fold64(
        _le64(inp, ip) ^ (_le64(sec, sp) + 0),
        _le64(inp, ip + 8) ^ (_le64(sec, sp + 8) - 0),
    )


def _len_0to16(inp, n):
    if n > 8:  # 9..16
        bf1 = (_le64(KSECRET, 24) ^ _le64(KSECRET, 32))
        bf2 = (_le64(KSECRET, 40) ^ _le64(KSECRET, 48))
        lo = _le64(inp, 0) ^ bf1
        hi = _le64(inp, n - 8) ^ bf2
        acc = (n + _swap64(lo) + hi + _mul128_fold64(lo, hi)) & M64
        return _xxh3_avalanche(acc)
    if n >= 4:  # 4..8
        i1 = _le32(inp, 0)
        i2 = _le32(inp, n - 4)
        bf = (_le64(KSECRET, 8) ^ _le64(KSECRET, 16))
        val = (i2 + (i1 << 32)) & M64
        return _rrmxmx(val ^ bf, n)
    if n:  # 1..3
        c1, c2, c3 = inp[0], inp[n >> 1], inp[n - 1]
        combined = ((c1 << 16) | (c2 << 24) | (c3 << 0) | (n << 8)) & 0xFFFFFFFF
        bf = (_le32(KSECRET, 0) ^ _le32(KSECRET, 4))
        return _avalanche64((combined ^ bf) & M64)
    # 0
    return _avalanche64(_le64(KSECRET, 56) ^ _le64(KSECRET, 64))


def _len_17to128(inp, n):
    acc = (n * P64_1) & M64
    i = (n - 1) // 32
    while True:
        acc = (acc + _mix16b(inp, 16 * i, KSECRET, 32 * i)) & M64
        acc = (acc + _mix16b(inp, n - 16 * (i + 1), KSECRET, 32 * i + 16)) & M64
        if i == 0:
            break
        i -= 1
    return _xxh3_avalanche(acc)


def _len_129to240(inp, n):
    acc = (n * P64_1) & M64
    nb_rounds = n // 16
    for i in range(8):
        acc = (acc + _mix16b(inp, 16 * i, KSECRET, 16 * i)) & M64
    # secret + XXH3_SECRET_SIZE_MIN - XXH3_MIDSIZE_LASTOFFSET.
    # NOTE: in xxhash 0.13.1 (vendored @45c1435da) XXH3_SECRET_SIZE_MIN == 136, not 192.
    acc_end = _mix16b(inp, n - 16, KSECRET, 136 - 17)
    acc = _xxh3_avalanche(acc)
    for i in range(8, nb_rounds):
        acc_end = (acc_end + _mix16b(inp, 16 * i, KSECRET, 16 * (i - 8) + 3)) & M64
    return _xxh3_avalanche((acc + acc_end) & M64)


def _acc512(acc, data, d, sec, s):
    for lane in range(8):
        dv = _le64(data, d + lane * 8)
        dk = dv ^ _le64(sec, s + lane * 8)
        acc[lane ^ 1] = (acc[lane ^ 1] + dv) & M64
        # XXH_mult32to64_add64(dk & 0xFFFFFFFF, dk >> 32, acc[lane])
        acc[lane] = (acc[lane] + ((dk & 0xFFFFFFFF) * (dk >> 32))) & M64


def _scramble(acc, sec, s):
    for lane in range(8):
        k = _le64(sec, s + lane * 8)
        a = _xorshift64(acc[lane], 47) ^ k
        acc[lane] = (a * P32_1) & M64


def _hash_long(inp, n):
    acc = [P32_3, P64_1, P64_2, P64_3, P64_4, P32_2, P64_5, P32_1]
    nb_stripes_per_block = (192 - 64) // 8   # 16
    block_len = 64 * nb_stripes_per_block   # 1024
    nb_blocks = (n - 1) // block_len
    for blk in range(nb_blocks):
        for j in range(nb_stripes_per_block):
            _acc512(acc, inp, blk * block_len + j * 64, KSECRET, j * 8)
        _scramble(acc, KSECRET, 192 - 64)
    # last partial block
    off = nb_blocks * block_len
    nb_stripes = ((n - 1) - off) // 64
    for j in range(nb_stripes):
        _acc512(acc, inp, off + j * 64, KSECRET, j * 8)
    # last stripe
    _acc512(acc, inp, n - 64, KSECRET, 192 - 64 - 7)  # XXH_SECRET_LASTACC_START
    # merge
    result = (n * P64_1) & M64
    for i in range(4):
        result = (result + _mul128_fold64(
            acc[2 * i] ^ _le64(KSECRET, 11 + 16 * i),
            acc[2 * i + 1] ^ _le64(KSECRET, 11 + 16 * i + 8))) & M64
    return _xxh3_avalanche(result)


def xxh3_64bits(data: bytes) -> int:
    n = len(data)
    if n <= 16:
        return _len_0to16(data, n)
    if n <= 128:
        return _len_17to128(data, n)
    if n <= 240:
        return _len_129to240(data, n)
    return _hash_long(data, n)




# ----------------------------------------------------------------------------
# Configuration
# ----------------------------------------------------------------------------
HOST        = os.environ.get("HYDRA_HOST", "127.0.0.1")
HTTP_PORT   = int(os.environ.get("HYDRA_HTTP_PORT", "18086"))
RPC_PORT    = int(os.environ.get("HYDRA_RPC_PORT", "19513"))
SLOT_KEY    = "0"
SEED        = 42
TEMPERATURE = 0.0
MAX_TOKENS  = 16

OP_INFO    = 0x41
OP_PREFILL = 0x42
OP_DECODE  = 0x43
MAGIC      = 0x4859
STATUS_OK  = 0x00

# Two prompts: short (~10 tok) and longer (~200 tok) so the checkpoint-tail
# behavior is exercised at more than one length.
SHORT_PROMPT = "The capital of France is"
LONG_PROMPT = (
    "Explain in clear, concrete terms how a modern relational database engine "
    "turns a single SQL SELECT statement into a sequence of physical operations "
    "on disk and in memory. Start with how the parser reads the query and builds "
    "an abstract syntax tree, then describe how the planner walks that tree to "
    "choose among possible access paths such as a sequential table scan, an index "
    "range scan, or a nested loop versus a hash join. Explain why the planner "
    "depends on statistics like row counts, distinct values, and histogram "
    "buckets, and how stale statistics can lead to a bad plan. Finish by "
    "describing how the executor streams rows through the chosen operators, "
    "materializes intermediate results only when necessary, and respects memory "
    "limits by spilling to temporary storage. Keep the explanation accessible to "
    "an experienced backend engineer who is new to database internals."
)

PROMPTS = [
    ("short-~10tok", SHORT_PROMPT),
    ("long-~200tok", LONG_PROMPT),
]

LOG_LINES = []
def log(msg):
    line = "%s %s" % (time.strftime("%H:%M:%S"), msg)
    print(line, flush=True)
    LOG_LINES.append(line)


# ----------------------------------------------------------------------------
# HTTP helpers
# ----------------------------------------------------------------------------
def http_json(path, body=None, timeout=300):
    url = "http://%s:%d%s" % (HOST, HTTP_PORT, path)
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers,
                                 method=("POST" if body is not None else "GET"))
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
        return r.status, raw.decode("utf-8", "replace")


def wait_for_health(timeout=180):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            st, txt = http_json("/health", timeout=5)
            log("health: %s %s" % (st, txt.strip()[:80]))
            return True
        except Exception as e:
            last = e
            time.sleep(2)
    log("health: TIMED OUT: %r" % (last,))
    return False


# ----------------------------------------------------------------------------
# RPC helpers
# ----------------------------------------------------------------------------
def _recv_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("RPC connection closed early (got %d of %d)" % (len(buf), n))
        buf += chunk
    return buf


def rpc_call(op, key, payload, trace=b"", timeout=300, drain_payload=True):
    """Send one Hydra RPC request, return (status, meta_bytes, payload_bytes)."""
    key = key.encode("utf-8") if isinstance(key, str) else key
    if isinstance(payload, (dict, list)):
        payload = json.dumps(payload).encode("utf-8")
    elif isinstance(payload, str):
        payload = payload.encode("utf-8")
    s = socket.create_connection((HOST, RPC_PORT), timeout=timeout)
    try:
        # Wire format (server-rpc.h / hydra_handle_connection @45c1435da):
        # [u16 magic][u8 op][u8 flags][u16 keyLen][u64 payloadLen][u16 traceLen]
        hdr = struct.pack("<HBBHQH", MAGIC, op, 0, len(key), len(payload), len(trace))
        s.sendall(hdr + key + trace + payload)
        rhdr = _recv_exact(s, 12)
        status = rhdr[0]
        meta_len = rhdr[1] | (rhdr[2] << 8) | (rhdr[3] << 16)
        pay_len = struct.unpack("<Q", rhdr[4:12])[0]
        meta = _recv_exact(s, meta_len) if meta_len else b""
        pay = _recv_exact(s, pay_len) if (pay_len and drain_payload) else b""
        if pay_len and not drain_payload:
            # do not block on a huge KV blob; just report its size
            pay = b""
        return status, meta, pay, pay_len
    finally:
        s.close()


def rpc_prefill(chat_json):
    status, meta, pay, pay_len = rpc_call(OP_PREFILL, SLOT_KEY, chat_json)
    return status, json.loads(meta.decode("utf-8")), pay_len


def rpc_decode(kv_meta, model_meta, generation, prompt_seg_bytes):
    """Build and send a 0x43 DECODE frame that continues from the RPC-primed slot."""
    prompt_len = len(prompt_seg_bytes)
    kv_len = 0
    segments = [
        {"id": "prompt", "offset": 0, "len": prompt_len,
         "hash": "xxh3:%016x" % xxh3_64bits(prompt_seg_bytes)},
        {"id": "kv", "offset": prompt_len, "len": kv_len,
         "hash": "xxh3:%016x" % xxh3_64bits(b"")},
    ]
    hdr = {
        "v": 3,
        "model": model_meta.get("model", ""),
        "kv_metadata": kv_meta,
        "model_metadata": model_meta,
        "generation": generation,
        "segments": segments,
    }
    hdr_bytes = json.dumps(hdr).encode("utf-8")
    frame = struct.pack("<I", len(hdr_bytes))
    frame += struct.pack("<Q", xxh3_64bits(hdr_bytes))
    frame += hdr_bytes
    frame += prompt_seg_bytes          # prompt segment
    frame += b""                       # kv segment (len 0)
    status, meta, pay, pay_len = rpc_call(OP_DECODE, SLOT_KEY, frame, drain_payload=False)
    return status, json.loads(meta.decode("utf-8"))


def poll_decode_result(decode_request_id, timeout=300):
    """Poll GET /v1/decode/{id} (non-SSE) until DONE, return parsed JSON."""
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            st, txt = http_json("/v1/decode/%d" % decode_request_id, timeout=10)
            doc = json.loads(txt)
            if st == 202 or doc.get("state") in ("loading", "restoring"):
                last = doc
                time.sleep(0.5)
                continue
            return st, doc
        except Exception as e:
            last = e
            time.sleep(0.5)
    raise TimeoutError("decode result %d not ready after %ds (last=%r)" % (decode_request_id, timeout, last))


# ----------------------------------------------------------------------------
# Per-prompt parity run
# ----------------------------------------------------------------------------
def canon_text(message):
    """Canonical generated text for parity, applied identically to both arms.
    Qwen3.5 is a thinking model: the HTTP arm reports the 16 thinking tokens
    in reasoning_content with content='', while the /v1/decode endpoint
    reports the SAME tokens in BOTH content and reasoning_content
    (completion_tokens=16 on both arms proves the tokens are not doubled,
    only the text fields are). Rule: if both fields are non-empty and one
    is a prefix of the other (endpoint duplication), take the longer once;
    otherwise concatenate in generation order (reasoning then content)."""
    c = message.get("content") or ""
    r = message.get("reasoning_content") or ""
    if c and r and (c.startswith(r) or r.startswith(c)):
        return c if len(c) >= len(r) else r
    return r + c


def raw_fields(message):
    return "content=%r reasoning_content=%r" % (
        message.get("content"), message.get("reasoning_content"))


def run_parity(label, prompt):
    log("=" * 76)
    log("[%s] prompt (%d chars): %s" % (label, len(prompt), prompt[:60] + ("..." if len(prompt) > 60 else "")))
    results = {}

    # Qwen3.5 is a thinking model: without /no_think all 16 completion tokens
    # land in reasoning_content and message.content is '' (empty-text parity is
    # weak evidence). /no_think is part of the prompt for ALL arms so
    # tokenization stays identical across HTTP / RPC-0x42 / RPC-0x43.
    chat_msgs = {"messages": [{"role": "user", "content": prompt + "/no_think"}],
                 "temperature": TEMPERATURE, "seed": SEED}

    # ---- Arm A: HTTP reference ------------------------------------------------
    http_body = dict(chat_msgs)
    http_body["max_tokens"] = MAX_TOKENS
    st, txt = http_json("/v1/chat/completions", http_body, timeout=300)
    http_doc = json.loads(txt)
    http_msg = http_doc["choices"][0]["message"]
    http_text = canon_text(http_msg)
    log("[%s] HTTP  raw %s" % (label, raw_fields(http_msg)))
    http_prompt_tokens = http_doc["usage"]["prompt_tokens"]
    http_completion_tokens = http_doc["usage"]["completion_tokens"]
    log("[%s] HTTP  prompt_tokens=%d completion_tokens=%d" % (label, http_prompt_tokens, http_completion_tokens))
    log("[%s] HTTP  text=%r" % (label, http_text))

    # ---- Arm B: RPC PREFILL ---------------------------------------------------
    prefill_body = dict(chat_msgs)  # no max_tokens — prefill only
    p_status, p_meta, p_pay_len = rpc_prefill(prefill_body)
    rpc_n_past = p_meta.get("n_past")
    log("[%s] RPC 0x42 PREFILL status=0x%02x n_past=%s state_size=%s logits_size=%s payload=%dB"
        % (label, p_status, rpc_n_past, p_meta.get("state_size"), p_meta.get("logits_size"), p_pay_len))

    ok_prefill_status = (p_status == STATUS_OK)
    # The literal #469 signature:
    ok_npast = (rpc_n_past == http_prompt_tokens)
    results["B_prefill_status_ok"] = ok_prefill_status
    results["B_n_past_eq_http_prompt_tokens"] = ok_npast
    log("[%s] ASSERT n_past(%s) == usage.prompt_tokens(%s) -> %s"
        % (label, rpc_n_past, http_prompt_tokens, "PASS" if ok_npast else "FAIL"))

    # ---- Arm C: RPC DECODE from the RPC-primed slot ---------------------------
    identity = {
        "tokenizer": p_meta.get("tokenizer", ""),
        "model_name": p_meta.get("model_name", ""),
        "model_quant": p_meta.get("model_quant", ""),
        "model_capabilities": p_meta.get("model_capabilities", 0),
    }
    model_meta = dict(identity)
    model_meta["model"] = p_meta.get("model_alias", "")
    kv_meta = dict(identity)
    generation = {"n_predict": MAX_TOKENS, "sampling": {"temperature": TEMPERATURE, "seed": SEED}}
    prompt_seg = json.dumps({"messages": chat_msgs["messages"]}).encode("utf-8")

    d_status, d_meta = rpc_decode(kv_meta, model_meta, generation, prompt_seg)
    decode_request_id = d_meta.get("decode_request_id")
    log("[%s] RPC 0x43 DECODE status=0x%02x valid=%s request_id=%s n_past_after_restore=%s match=%s"
        % (label, d_status, d_meta.get("valid"), decode_request_id,
           d_meta.get("n_past_after_restore"),
           json.dumps(d_meta.get("match", {}), sort_keys=True)))

    ok_decode_status = (d_status == STATUS_OK and d_meta.get("valid") is True)
    results["C_decode_status_ok"] = ok_decode_status

    st, doc = poll_decode_result(decode_request_id)
    rpc_msg = doc.get("choices", [{}])[0].get("message", {})
    rpc_text = canon_text(rpc_msg)
    log("[%s] RPC   raw %s" % (label, raw_fields(rpc_msg)))
    rpc_completion_tokens = doc.get("usage", {}).get("completion_tokens")
    log("[%s] RPC decode result http_status=%s completion_tokens=%s" % (label, st, rpc_completion_tokens))
    log("[%s] RPC  text=%r" % (label, rpc_text))

    ok_ctok = (rpc_completion_tokens == http_completion_tokens)
    ok_text = (rpc_text == http_text)
    results["C_completion_tokens_eq_http"] = ok_ctok
    results["C_text_eq_http"] = ok_text
    log("[%s] ASSERT completion_tokens(%s) == HTTP(%s) -> %s"
        % (label, rpc_completion_tokens, http_completion_tokens, "PASS" if ok_ctok else "FAIL"))
    log("[%s] ASSERT decoded text == HTTP text -> %s" % (label, "PASS" if ok_text else "FAIL"))

    return label, results, {
        "http_prompt_tokens": http_prompt_tokens, "http_completion_tokens": http_completion_tokens,
        "http_text": http_text, "rpc_n_past": rpc_n_past,
        "rpc_completion_tokens": rpc_completion_tokens, "rpc_text": rpc_text,
    }


def main():
    t0 = time.time()
    log("parity-driver start host=%s http=%d rpc=%d slot=%s seed=%d temp=%s max_tokens=%d"
        % (HOST, HTTP_PORT, RPC_PORT, SLOT_KEY, SEED, TEMPERATURE, MAX_TOKENS))

    if not wait_for_health(timeout=60):
        log("FATAL: engine not healthy; aborting")
        finish_log(1)
        return 1

    all_results = {}
    details = {}
    for label, prompt in PROMPTS:
        try:
            label, results, det = run_parity(label, prompt)
        except Exception as e:
            log("[%s] EXCEPTION: %r" % (label, e))
            results = {"exception": False}
            det = {"exception": repr(e)}
        all_results[label] = results
        details[label] = det

    # ---- Summary --------------------------------------------------------------
    log("=" * 76)
    log("SUMMARY")
    overall = True
    for label, results in all_results.items():
        for k, v in results.items():
            mark = "PASS" if v else "FAIL"
            if not v:
                overall = False
            log("  %-28s %-36s %s" % (label, k, mark))
    log("OVERALL: %s" % ("PASS" if overall else "FAIL"))
    log("elapsed %.1fs" % (time.time() - t0))

    out = {
        "host": HOST, "http_port": HTTP_PORT, "rpc_port": RPC_PORT,
        "seed": SEED, "temperature": TEMPERATURE, "max_tokens": MAX_TOKENS,
        "overall": overall, "results": all_results, "details": details,
    }
    finish_log(0 if overall else 1, out)
    return 0 if overall else 1


def finish_log(rc, summary_obj=None):
    logdir = os.environ.get("PARITY_LOG_DIR", ".")
    try:
        os.makedirs(logdir, exist_ok=True)
    except Exception:
        logdir = "."
    path = os.path.join(logdir, "parity-run-%s.log" % time.strftime("%H%M%S"))
    with open(path, "w") as f:
        f.write("\n".join(LOG_LINES) + "\n")
        if summary_obj is not None:
            f.write("\nSUMMARY_JSON:\n" + json.dumps(summary_obj, indent=2) + "\n")
    print("log written: %s" % path, flush=True)


if __name__ == "__main__":
    sys.exit(main())
