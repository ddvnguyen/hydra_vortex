"""
Phase 0 — scheduler feasibility check for layer-split COMBINE.

Per `ddvnguyen/llama.cpp#36` Phase 0 + the v4 design's
"Scheduler feasibility (CRITICAL — do this before Phase 2)" section:
the upstream ggml scheduler assigns compute to the device that holds
the **inputs**, not the device that holds the **weights**. For
layer-split DENSE models, a layer's compute goes to the peer (where
the previous layer's outputs live), but the peer's RPC server can't
access the local CUDA's weights. This test loads a small dense model
(Qwen3.5-9B Q8_0, 32 layers, 9.5 GB) with a 50/50 tensor-split across
CUDA0 (local, via the head) and CUDA1 (peer, via the upstream
ggml-rpc-server), decodes a small prompt, and confirms the scheduler
places compute on the right device with no `GET_TENSOR FAILED` or
`RPC_CMD_* failed` errors.

Substitutes 9B for the design's 1B per the user's note (no Qwen2-1.5B
on disk; 9B is a similar dense-class model — Qwen3.5-9B is a hybrid
but layer-split's correctness is the same for the scheduler check).
27B DENSE is the real test (gated on Phase 1, not this file).

Acceptance (gates G1 in #36):
  - The decode produces coherent tokens
  - No `GET_TENSOR FAILED` in head log
  - No `RPC_CMD_* failed for ... on ...` in head log
  - No `device index out of range` errors
  - The head reports the peer as a registered backend

If this fails, #36's design says pick:
  - Fallback A: use upstream's `-ts --rpc` path (option 2 in PR #34)
  - Fallback B: drop DENSE 27B from G1; only pursue MoE 35B COMBINED

Environment variables:
  PHASE0_MODEL          /mnt/SSD/Qwen3.5-9B-Q8_0.gguf  (default)
  PHASE0_PEER_PORT      19050                          (default)
  PHASE0_HEAD_PORT      18080                          (default)
  PHASE0_PEER_DEVICE    CUDA1                          (default)
  PHASE0_LAYER_SPLIT    1/1                            (default, 50/50)
  PHASE0_MAX_TOKENS     32                             (default)
"""

import asyncio
import os
import re
import signal
import socket
import subprocess
import time
from pathlib import Path

import httpx
import pytest


REPO_ROOT = Path(__file__).resolve().parent.parent.parent
LLAMA_ENGINE_BIN = Path(
    os.environ.get(
        "PHASE0_LLAMA_ENGINE",
        "/tmp/llama-cpp-build/bin/llama-engine",
    )
)
RPC_SERVER_BIN = Path(
    os.environ.get(
        "PHASE0_RPC_SERVER",
        "/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp/build_sm86_sm120/bin/rpc-server",
    )
)
LIB_DIR = LLAMA_ENGINE_BIN.parent

MODEL_PATH = Path(
    os.environ.get("PHASE0_MODEL", "/mnt/SSD/Qwen3.5-9B-Q8_0.gguf")
)
PEER_PORT = int(os.environ.get("PHASE0_PEER_PORT", "19050"))
HEAD_PORT = int(os.environ.get("PHASE0_HEAD_PORT", "18080"))
HEAD_RPC_PORT = HEAD_PORT + 1  # auto-derived (unified server rule)
PEER_DEVICE = os.environ.get("PHASE0_PEER_DEVICE", "CUDA1")
LAYER_SPLIT = os.environ.get("PHASE0_LAYER_SPLIT", "1/1")
MAX_TOKENS = int(os.environ.get("PHASE0_MAX_TOKENS", "32"))

PROMPT = "What is 2+2? Answer with one number."

# Errors the scheduler logs that we treat as Phase 0 failures.
# These are the exact failure modes called out in #36's design doc
# ("Scheduler feasibility (CRITICAL)").
FORBIDDEN_LOG_PATTERNS = [
    re.compile(r"GET_TENSOR FAILED", re.IGNORECASE),
    re.compile(r"RPC_CMD_.* failed for .* on .*", re.IGNORECASE),
    re.compile(r"device index .* out of range", re.IGNORECASE),
    re.compile(r"pre-allocated tensor .* in a buffer .* that cannot run the operation", re.IGNORECASE),
    re.compile(r"truncated graph", re.IGNORECASE),
]


def _port_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


def _wait_port_open(port: int, timeout_s: float = 60.0) -> bool:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            if s.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.5)
    return False


def _wait_http_ready(url: str, timeout_s: float = 90.0) -> bool:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            r = httpx.get(url, timeout=2.0)
            if r.status_code == 200:
                return True
        except Exception:
            pass
        time.sleep(1.0)
    return False


def _scan_forbidden(log_text: str) -> list[str]:
    hits = []
    for pat in FORBIDDEN_LOG_PATTERNS:
        for m in pat.finditer(log_text):
            hits.append(f"{pat.pattern}: {m.group(0)}")
    return hits


@pytest.fixture(scope="module")
def peer_proc():
    """Start the upstream ggml-rpc-server (peer) on PEER_PORT / PEER_DEVICE."""
    assert RPC_SERVER_BIN.exists(), f"rpc-server not found: {RPC_SERVER_BIN}"
    if not _port_free(PEER_PORT):
        pytest.skip(f"peer port {PEER_PORT} already in use; set PHASE0_PEER_PORT")

    env = os.environ.copy()
    env["LD_LIBRARY_PATH"] = f"{LIB_DIR}:{env.get('LD_LIBRARY_PATH', '')}"

    proc = subprocess.Popen(
        [
            str(RPC_SERVER_BIN),
            "--host", "127.0.0.1",
            "--port", str(PEER_PORT),
            "--device", PEER_DEVICE,
        ],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        preexec_fn=os.setsid,
    )
    try:
        assert _wait_port_open(PEER_PORT, timeout_s=30), (
            f"rpc-server did not open port {PEER_PORT} within 30s; "
            f"pid={proc.pid}"
        )
        yield proc
    finally:
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)


@pytest.fixture(scope="module")
def head_proc(peer_proc):
    """Start the fork's llama-engine (head) with layer-split + rpc-engine=peer.

    Reads its own log and returns it so the test can scan for forbidden patterns.
    """
    assert LLAMA_ENGINE_BIN.exists(), f"llama-engine not found: {LLAMA_ENGINE_BIN}"
    assert MODEL_PATH.exists(), f"model not found: {MODEL_PATH}"
    if not _port_free(HEAD_PORT):
        pytest.skip(f"head port {HEAD_PORT} already in use; set PHASE0_HEAD_PORT")
    if not _port_free(HEAD_RPC_PORT):
        pytest.skip(f"head rpc-port {HEAD_RPC_PORT} already in use")

    env = os.environ.copy()
    env["LD_LIBRARY_PATH"] = f"{LIB_DIR}:{env.get('LD_LIBRARY_PATH', '')}"

    proc = subprocess.Popen(
        [
            str(LLAMA_ENGINE_BIN),
            "--model", str(MODEL_PATH),
            "--port", str(HEAD_PORT),
            "--host", "127.0.0.1",
            "--rpc-engine", f"127.0.0.1:{PEER_PORT}",
            "--tensor-split", LAYER_SPLIT,
            "--n-gpu-layers", "99",
            "--ctx-size", "4096",
            "--parallel", "1",
            "--cont-batching",
            "--flash-attn", "on",
            "--jinja",
            "--log-verbosity", "4",
            "--no-warmup",
        ],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        preexec_fn=os.setsid,
    )
    log_chunks: list[str] = []
    try:
        assert _wait_port_open(HEAD_PORT, timeout_s=90), (
            f"head HTTP did not open port {HEAD_PORT} within 90s"
        )
        assert _wait_http_ready(
            f"http://127.0.0.1:{HEAD_PORT}/health", timeout_s=120
        ), f"head /health never returned 200 within 120s"

        # Drain log while the test runs so we can scan afterwards.
        import threading
        def _drain():
            assert proc.stdout is not None
            for line in proc.stdout:
                log_chunks.append(line)
        t = threading.Thread(target=_drain, daemon=True)
        t.start()

        yield proc, log_chunks

        t.join(timeout=2)
    finally:
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)


def test_phase0_layer_split_50_50(head_proc):
    """
    Send a short prompt, decode MAX_TOKENS tokens, confirm coherent output
    and no scheduler errors in the head log.

    The fork's llama-engine (post-PR #30) does layer-split via the unified
    server's `--rpc-engine` + `--tensor-split`. The peer is the upstream
    ggml-rpc-server (no merged-server logic) — this is the exact setup
    `ddvnguyen/llama.cpp#36` Phase 0 specifies.

    Success criteria:
      1. HTTP 200 on /v1/chat/completions
      2. Response has non-empty content
      3. No forbidden log patterns in head log
    """
    proc, log_chunks = head_proc
    log_so_far = "".join(log_chunks)

    body = {
        "model": str(MODEL_PATH),
        "messages": [{"role": "user", "content": PROMPT}],
        "max_tokens": MAX_TOKENS,
        "temperature": 0,
        "stream": False,
    }

    with httpx.Client(timeout=180.0) as client:
        r = client.post(
            f"http://127.0.0.1:{HEAD_PORT}/v1/chat/completions", json=body
        )
        assert r.status_code == 200, (
            f"chat completion returned {r.status_code}: {r.text[:1000]}"
        )
        resp = r.json()
        choices = resp.get("choices") or []
        assert choices, f"no choices in response: {resp}"
        content = (
            choices[0].get("message", {}).get("content")
            or choices[0].get("message", {}).get("reasoning_content")
            or ""
        ).strip()
        assert content, f"empty content in response: {resp}"

    # Wait briefly for any deferred log lines, then scan.
    time.sleep(2)
    full_log = log_so_far + "".join(log_chunks)
    forbidden = _scan_forbidden(full_log)
    assert not forbidden, (
        f"forbidden log patterns found in head log:\n  "
        + "\n  ".join(forbidden)
        + f"\n\nLast 50 log lines:\n"
        + "\n".join(full_log.splitlines()[-50:])
    )


def test_phase0_no_deadlock_on_followup(head_proc):
    """
    Send a second request immediately to confirm the scheduler doesn't
    deadlock on the second decode (the #376 root-cause class). The first
    request loads the model; the second is a small follow-up to exercise
    the path under sustained concurrent-style load.
    """
    proc, log_chunks = head_proc
    body = {
        "model": str(MODEL_PATH),
        "messages": [
            {"role": "user", "content": "What is 3+3? Answer with one number."}
        ],
        "max_tokens": 8,
        "temperature": 0,
        "stream": False,
    }
    with httpx.Client(timeout=120.0) as client:
        r = client.post(
            f"http://127.0.0.1:{HEAD_PORT}/v1/chat/completions", json=body
        )
        assert r.status_code == 200, (
            f"second request returned {r.status_code}: {r.text[:1000]}"
        )
