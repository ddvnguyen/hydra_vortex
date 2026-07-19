#!/usr/bin/env python3
"""
Cross-node P/D mix-quant KV-transfer correctness test (needle oracle).

Reproduces the #469 failing path directly at the slot level, WITHOUT the Pi
agent or Hydra.Core routing in the loop:

  1. Prefill a needle-bearing prompt on the RTX 5060 Ti engine (Mini quant).
  2. Pull the raw KV state out via RPC STATE_GET (0x30).
  3. Push it into the P100 engine via RPC STATE_PUT (0x31) — a DIFFERENT quant
     (Balanced) and a DIFFERENT compiled build (sm_60 / CUDA 12.9 vs the RTX
     fat sm_120/sm_86 / CUDA 13.2 build).
  4. Decode on the P100 (HTTP /v1/chat/completions, temp=0, greedy).
  5. Assert the decoded text contains the needle. Correct KV transfer -> the
     model recalls the needle. Garbled/mis-deserialized KV -> fluent-but-
     unrelated output (the #469 symptom) -> the needle is absent -> FAIL.

This is a deterministic pass/fail oracle: no human judgement of "looks like
garbage", just "is the unique needle string present in the output".

Isolation:
  * Point --p100-* at a P100 running the SAME quant (Mini, via
    node-p100-mini.yaml -> models.json `moe-35b-pd-mini`) to isolate the
    cross-NODE / cross-ARCH transport from the cross-QUANT mixing. If that
    passes but Balanced fails, the fault is quant-mixing, not transport.
  * The RTX-only baseline (--baseline, on by default) prefills+decodes the
    same needle on the RTX alone, proving the prompt/model/sampling actually
    retrieve the needle in the good case before we blame the transfer.

Wire helpers (http_*, rpc_call) are reused verbatim from the validated
single-node test (tests/test_single_node_kv_roundtrip.py).

Usage:
  # Default: RTX (Mini) -> P100 (whatever quant is deployed there)
  python tests/test_cross_node_pd_needle.py

  # Larger context to approach the #469 repro size (~5K tokens)
  python tests/test_cross_node_pd_needle.py --pad 4000

  # Same-quant control (P100 running Mini)
  python tests/test_cross_node_pd_needle.py --label "RTX-Mini -> P100-Mini"
"""
import argparse, json, re, socket, struct, sys, time, urllib.request


# --------------------------------------------------------------------------
# Wire helpers (reused from tests/test_single_node_kv_roundtrip.py)
# --------------------------------------------------------------------------
def http_get(url, timeout=30):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read())


def http_post(url, body=None, timeout=180):
    data = json.dumps(body).encode() if body else b'{}'
    req = urllib.request.Request(url, data=data, method='POST',
                                 headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def rpc_call(host, port, opcode, slot_id, data=b'', trace=b'test', timeout=180):
    s = socket.create_connection((host, port), timeout=timeout)
    key = str(slot_id).encode()
    hdr = struct.pack('<HBBHqH', 0x4859, opcode, 0, len(key), len(data), len(trace))
    s.sendall(hdr + key + trace + data)
    res_hdr = s.recv(12)
    status = res_hdr[0]
    meta_len = res_hdr[1] | (res_hdr[2] << 8) | (res_hdr[3] << 16)
    payload_len = struct.unpack('<Q', res_hdr[4:12])[0]
    meta = b''
    while len(meta) < meta_len:
        chunk = s.recv(min(65536, meta_len - len(meta)))
        if not chunk:
            break
        meta += chunk
    payload = b''
    while len(payload) < payload_len:
        chunk = s.recv(min(65536, payload_len - len(payload)))
        if not chunk:
            break
        payload += chunk
    s.close()
    return status, json.loads(meta) if meta else {}, payload


# --------------------------------------------------------------------------
# Needle prompt
# --------------------------------------------------------------------------
NEEDLE = "VIOLET-7734"


def build_messages(pad_chars=0):
    """A single user turn: unique needle + optional filler + a recall question.

    The needle sits at the top so it is far from the question once padded,
    exercising real context retrieval rather than a trivial adjacency.
    """
    filler = ""
    if pad_chars > 0:
        # Neutral, deterministic filler so the prompt is reproducible.
        unit = ("The following is background material with no bearing on the "
                "question. ")
        filler = "\n\n" + (unit * ((pad_chars // len(unit)) + 1))[:pad_chars]

    content = (
        f"Note: the internal build access code is {NEEDLE}. "
        f"Keep it in mind exactly as written.{filler}\n\n"
        "Question: What is the internal build access code? "
        "Reply with ONLY the code, nothing else."
    )
    return [{"role": "user", "content": content}]


def needle_present(text):
    """Case/format-tolerant needle match (ignores spaces, hyphens, case)."""
    def norm(s):
        return re.sub(r"[\s\-_]+", "", s).upper()
    return norm(NEEDLE) in norm(text or "")


def resident_model(base_url):
    """Best-effort: report which GGUF the engine actually has loaded."""
    for path in ("/props", "/v1/models"):
        try:
            j = http_get(f"{base_url}{path}")
            for key in ("model_path", "default_generation_settings"):
                if isinstance(j, dict) and key in j:
                    v = j[key]
                    if isinstance(v, dict):
                        v = v.get("model") or v.get("model_path")
                    if v:
                        return str(v)
            if isinstance(j, dict) and j.get("data"):
                return str(j["data"][0].get("id", "?"))
        except Exception:
            continue
    return "?"


# --------------------------------------------------------------------------
# Steps
# --------------------------------------------------------------------------
def prefill(base_url, messages, slot=0):
    """Process the full prompt into the given slot's KV (no generation).

    id_slot is pinned explicitly: engines configured with parallel>1 (e.g.
    node-rtx.yaml's parallel:2) auto-schedule an unpinned request onto
    whichever slot is free, which need not be slot 0 — the slot STATE_GET/
    STATE_PUT operate on. Without pinning, prefill can silently land on a
    different slot than the one whose KV gets pulled/pushed.
    """
    resp = http_post(f"{base_url}/v1/chat/completions", {
        "model": "test", "messages": messages, "id_slot": slot,
        "max_tokens": 0, "temperature": 0, "stream": False,
    })
    pt = resp.get("usage", {}).get("prompt_tokens", 0)
    assert pt > 0, "FAIL: prefill produced 0 prompt tokens"
    return pt


def decode(base_url, messages, slot=0):
    """Greedy-decode from the given slot's current KV, reusing the prefilled prefix.

    max_tokens=384: this engine runs in reasoning_format=deepseek (chain-of-
    thought before the final answer). At larger contexts the CoT preamble
    alone can exceed 64 tokens, truncating before the answer is ever emitted
    -- a false-negative on the needle oracle, not a real recall failure.
    """
    resp = http_post(f"{base_url}/v1/chat/completions", {
        "model": "test", "messages": messages, "id_slot": slot,
        "max_tokens": 384, "temperature": 0, "stream": False,
    })
    msg = resp["choices"][0]["message"]
    content = msg.get("content", "") or ""
    reasoning = msg.get("reasoning_content", "") or ""
    timings = resp.get("timings", {})
    return {
        "content": content,
        "reasoning": reasoning,
        "text": (reasoning + "\n" + content).strip(),
        "cache_n": timings.get("cache_n", 0),
        "prompt_ms": timings.get("prompt_ms", 0),
        "prompt_tokens": resp.get("usage", {}).get("prompt_tokens", 0),
    }


def run_baseline(rtx_base, messages, slot=0):
    print("=" * 64)
    print("BASELINE: RTX-only prefill + decode (no transfer)")
    print("=" * 64)
    pt = prefill(rtx_base, messages, slot)
    print(f"  prefill prompt_tokens={pt}")
    d = decode(rtx_base, messages, slot)
    print(f"  cache_n={d['cache_n']} prompt_ms={d['prompt_ms']:.0f}")
    print(f"  output: {d['text'][:200]!r}")
    ok = needle_present(d["text"])
    print(f"  needle '{NEEDLE}' present: {ok}")
    if not ok:
        print("  WARNING: baseline failed to retrieve the needle — the prompt/"
              "model/sampling is the problem, not the transfer. Fix this first.")
    print()
    return ok


def run_cross_node(rtx_base, rtx_rpc, p100_base, p100_rpc, messages, slot, label):
    print("=" * 64)
    print(f"CROSS-NODE P/D: {label}")
    print("=" * 64)
    print(f"  RTX   model: {resident_model(rtx_base)}")
    print(f"  P100  model: {resident_model(p100_base)}")

    print("\n[1/5] Prefill needle prompt on RTX...")
    t0 = time.time()
    pt = prefill(rtx_base, messages, slot)
    print(f"  prompt_tokens={pt} ({(time.time()-t0)*1000:.0f} ms)")

    print("\n[2/5] STATE_GET (0x30) KV blob from RTX...")
    t0 = time.time()
    status, meta, blob = rpc_call(rtx_rpc[0], rtx_rpc[1], 0x30, slot)
    assert status == 0x00, f"FAIL: STATE_GET status={status:#x}"
    assert len(blob) > 0, "FAIL: empty KV blob from RTX"
    print(f"  status={status:#x} bytes={len(blob)} ({(time.time()-t0)*1000:.0f} ms) meta={meta}")

    print("\n[3/5] Erase P100 slot then STATE_PUT (0x31) blob into P100...")
    try:
        http_post(f"{p100_base}/slots/{slot}?action=erase")
    except Exception as e:
        print(f"  (erase skipped: {e})")
    t0 = time.time()
    status, meta, _ = rpc_call(p100_rpc[0], p100_rpc[1], 0x31, slot, blob)
    assert status == 0x00, f"FAIL: STATE_PUT status={status:#x}"
    print(f"  status={status:#x} ({(time.time()-t0)*1000:.0f} ms) meta={meta}")

    print("\n[4/5] Verify P100 slot populated...")
    m = http_get(f"{p100_base}/slots/{slot}/state/meta")
    print(f"  n_past={m.get('n_past')} state_size={m.get('state_size')}")
    assert m.get("n_past", 0) > 0, "FAIL: P100 n_past==0 after STATE_PUT"

    print("\n[5/5] Decode on P100 (temp=0) and check needle...")
    t0 = time.time()
    d = decode(p100_base, messages, slot)
    print(f"  cache_n={d['cache_n']} prompt_ms={d['prompt_ms']:.0f} "
          f"decode_ms={(time.time()-t0)*1000:.0f}")
    print(f"  output: {d['text'][:300]!r}")

    kv_reused = d["cache_n"] > 0 or d["prompt_ms"] < 2000
    ok = needle_present(d["text"])
    print(f"\n  kv_reused={kv_reused}  needle '{NEEDLE}' present={ok}")
    if not kv_reused:
        print("  NOTE: KV did not appear reused (re-prefilled on P100) — the "
              "transfer/prefix-match failed before we even tested correctness.")
    print("=" * 64)
    print(f"CROSS-NODE RESULT: {'PASS' if ok else 'FAIL (hallucination / needle lost)'}")
    print("=" * 64)
    return ok


def main():
    ap = argparse.ArgumentParser(description="Cross-node P/D mix-quant needle test")
    ap.add_argument("--rtx-host", default="localhost")
    ap.add_argument("--rtx-port", type=int, default=8080)
    ap.add_argument("--rtx-rpc", type=int, default=9503)
    ap.add_argument("--p100-host", default="192.168.122.21")
    ap.add_argument("--p100-port", type=int, default=8086)
    ap.add_argument("--p100-rpc", type=int, default=9502)
    ap.add_argument("--slot", type=int, default=0)
    ap.add_argument("--pad", type=int, default=0,
                    help="Filler chars to grow the context toward the #469 repro size")
    ap.add_argument("--label", default="RTX-Mini prefill -> P100 decode")
    ap.add_argument("--no-baseline", action="store_true",
                    help="Skip the RTX-only needle sanity check")
    args = ap.parse_args()

    rtx_base = f"http://{args.rtx_host}:{args.rtx_port}"
    p100_base = f"http://{args.p100_host}:{args.p100_port}"
    messages = build_messages(args.pad)

    results = {}
    if not args.no_baseline:
        try:
            results["baseline_rtx"] = run_baseline(rtx_base, messages, args.slot)
        except Exception as e:
            print(f"BASELINE FAILED: {e}\n")
            results["baseline_rtx"] = False

    try:
        results["cross_node_pd"] = run_cross_node(
            rtx_base, (args.rtx_host, args.rtx_rpc),
            p100_base, (args.p100_host, args.p100_rpc),
            messages, args.slot, args.label)
    except Exception as e:
        print(f"\nCROSS-NODE FAILED: {e}")
        results["cross_node_pd"] = False

    print("\n" + "=" * 64)
    print("FINAL RESULTS")
    print("=" * 64)
    for name, ok in results.items():
        print(f"  {name}: {'PASS' if ok else 'FAIL'}")

    # The verdict that matters is the cross-node path.
    sys.exit(0 if results.get("cross_node_pd") else 1)


if __name__ == "__main__":
    main()
