#!/usr/bin/env python3
"""t0006 A/B bench harness: strata (S-arms) vs llama.cpp fork (F-arms), RTX 3060 GPU1.

Streams /v1/chat/completions for every cell in prompts/manifest.json and records
per rep: TTFT, prompt tok/s, decode tok/s (streamed, post-first-token), wall,
prompt/completion tokens, raw usage, request window (ISO) for engine-log
alignment, and a first-64-char snippet. Median/min/max per cell.

Link/VRAM/RSS sampling is deliberately NOT here: the arm script owns one
sampler that covers engine load AND bench (sample_link.sh), so arm validity
comes from a single file covering the whole arm.

Exit: 0 = all cells OK; 1 = partial JSON written with status=error, exit 1.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import statistics
import sys
import time
import urllib.request

def iso_now():
    return dt.datetime.now().astimezone().isoformat(timespec="milliseconds")

def stream_chat(url, model, messages, max_tokens, timeout):
    """One streamed chat request. Returns metrics dict; raises RuntimeError on
    transport/server error (caller writes partial JSON and exits 1)."""
    body = json.dumps({
        "model": model,
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": 0,
        "stream": True,
        "stream_options": {"include_usage": True},
    }).encode()
    req = urllib.request.Request(url, body, {"content-type": "application/json"})
    t_start_iso = iso_now()
    t0 = time.time()
    tf = None            # first content/reasoning token
    t_last = None        # last content/reasoning token (loop end is authoritative)
    usage = None
    content = []
    reasoning = []
    finish = None
    error = None
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            for line in r:
                line = line.decode("utf-8", "replace").strip()
                if not line.startswith("data:"):
                    continue
                payload = line[5:].strip()
                if payload == "[DONE]":
                    break
                try:
                    j = json.loads(payload)
                except json.JSONDecodeError:
                    continue
                if j.get("usage"):
                    usage = j["usage"]
                err = j.get("error")
                if err:
                    raise RuntimeError("server error: %s" % (err if isinstance(err, str) else json.dumps(err)))
                ch = j.get("choices") or []
                if not ch:
                    continue
                delta = ch[0].get("delta") or {}
                c = delta.get("content")
                rc = delta.get("reasoning_content")
                if c:
                    content.append(c)
                if rc:
                    reasoning.append(rc)
                if c or rc:
                    if tf is None:
                        tf = time.time()
                    t_last = time.time()
                if ch[0].get("finish_reason"):
                    finish = ch[0]["finish_reason"]
        t1 = time.time()
    except Exception as e:
        t1 = time.time()
        error = "%s: %s" % (type(e).__name__, e)

    text = "".join(content)
    n = (usage or {}).get("completion_tokens")
    prompt_tokens = (usage or {}).get("prompt_tokens")
    cached = ((usage or {}).get("prompt_tokens_details") or {}).get("cached_tokens")
    ttft = round(tf - t0, 3) if tf else None
    decode_tps = round((n - 1) / (t1 - tf), 2) if (tf and n and n > 1 and t1 > tf) else None
    rec = {
        "start": t_start_iso,
        "end": iso_now(),
        "ttft_s": ttft,
        "prefill_tps": round(prompt_tokens / ttft, 2) if (prompt_tokens and ttft) else None,
        "decode_tps": decode_tps,
        "wall_s": round(t1 - t0, 3),
        "prompt_tokens": prompt_tokens,
        "completion_tokens": n,
        "cached_tokens": cached,
        "finish_reason": finish,
        "usage": usage,
        "snippet": (text[:64] or "".join(reasoning)[:64]),
        "error": error,
    }
    # keep full text only for prefix-cell assistant filler; caller pops it
    rec["_text"] = text
    rec["_reasoning"] = "".join(reasoning)
    return rec

def stats_of(vals):
    vals = [v for v in vals if v is not None]
    if not vals:
        return None
    return {"median": round(statistics.median(vals), 2),
            "min": round(min(vals), 2), "max": round(max(vals), 2), "n": len(vals)}

def drop_internal(rec):
    rec = dict(rec)
    rec.pop("_text", None)
    rec.pop("_reasoning", None)
    return rec

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--engine", choices=["strata", "fork"], required=True)
    ap.add_argument("--arm", required=True, help="arm id, e.g. S0 / F1")
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--prompts", required=True, help="dir with manifest.json + cells")
    ap.add_argument("--outdir", required=True, help="dir for <arm>.bench.json")
    ap.add_argument("--model", default="gsq-rco-iq3_s")
    ap.add_argument("--reps", type=int, default=3)
    ap.add_argument("--max-tokens", type=int, default=256)
    ap.add_argument("--prefix-max-tokens", type=int, default=256)
    ap.add_argument("--warmup", action="store_true", help="one unrecorded 64-tok request first")
    ap.add_argument("--base", default="http://127.0.0.1")
    ap.add_argument("--timeout", type=float, default=1800)
    ap.add_argument("--warmup-tokens", type=int, default=64)
    a = ap.parse_args()

    url = "%s:%d/v1/chat/completions" % (a.base, a.port)
    out_path = os.path.join(a.outdir, "%s.bench.json" % a.arm)
    manifest_path = os.path.join(a.prompts, "manifest.json")
    with open(manifest_path) as f:
        manifest = json.load(f)
    sha_path = os.path.join(a.prompts, "sha256sums.txt")
    prompts_sha = hashlib.sha256(open(sha_path, "rb").read()).hexdigest() if os.path.exists(sha_path) else None

    result = {
        "arm": a.arm, "engine": a.engine, "port": a.port, "model": a.model,
        "reps": a.reps, "max_tokens": a.max_tokens,
        "manifest_sha256_of_sha256sums": prompts_sha,
        "started": iso_now(), "finished": None, "status": "running",
        "warmup": None, "cells": [], "requests_total": 0,
    }

    def flush(status, exit_code):
        result["finished"] = iso_now()
        result["status"] = status
        os.makedirs(a.outdir, exist_ok=True)
        tmp = out_path + ".tmp"
        with open(tmp, "w") as f:
            json.dump(result, f, indent=1)
        os.replace(tmp, out_path)
        return exit_code

    def abort(reason):
        print("ABORT %s: %s" % (a.arm, reason), file=sys.stderr, flush=True)
        sys.exit(flush("error: " + reason, 1))

    if a.warmup:
        # independent content: warm-up must NOT prime a measured cell
        warm_file = os.path.join(a.prompts, "warmup.txt")
        if not os.path.exists(warm_file):
            abort("warmup.txt missing from prompts dir")
        rec = stream_chat(url, a.model, [{"role": "user",
                          "content": open(warm_file).read()}],
                          a.warmup_tokens, a.timeout)
        result["warmup"] = drop_internal(rec)
        result["requests_total"] += 1
        if rec["error"]:
            abort("warmup failed: " + rec["error"])

    for cell in manifest["cells"]:
        cid, kind = cell["id"], cell["kind"]
        crec = {"id": cid, "kind": kind, "reps": []}
        result["cells"].append(crec)
        if kind == "prefill_decode":
            prompt = open(os.path.join(a.prompts, cell["file"])).read()
            for rep in range(a.reps):
                rec = stream_chat(url, a.model, [{"role": "user", "content": prompt}],
                                  a.max_tokens, a.timeout)
                rec["rep"] = rep
                crec["reps"].append(drop_internal(rec))
                result["requests_total"] += 1
                print("  %s rep%d ttft=%s prefill=%s decode=%s err=%s" % (
                    cid, rep, rec["ttft_s"], rec["prefill_tps"], rec["decode_tps"], rec["error"]),
                    flush=True)
                if rec["error"]:
                    abort("%s rep%d failed: %s" % (cid, rep, rec["error"]))
            for key in ("ttft_s", "prefill_tps", "decode_tps", "wall_s"):
                crec[key + "_stats"] = stats_of([r[key] for r in crec["reps"]])
        elif kind == "prefix":
            turn_files = cell["files"]
            messages = []
            for i, tf_name in enumerate(turn_files, 1):
                messages.append({"role": "user",
                                 "content": open(os.path.join(a.prompts, tf_name)).read()})
                rec = stream_chat(url, a.model, messages, a.prefix_max_tokens, a.timeout)
                rec["turn"] = i
                crec["reps"].append(drop_internal(rec))
                result["requests_total"] += 1
                print("  %s turn%d ttft=%s prefill=%s prompt=%s err=%s" % (
                    cid, i, rec["ttft_s"], rec["prefill_tps"], rec["prompt_tokens"], rec["error"]),
                    flush=True)
                if rec["error"]:
                    abort("%s turn%d failed: %s" % (cid, i, rec["error"]))
                filler = rec["_text"][:1000] or rec["_reasoning"][:1000]
                if i < len(turn_files):
                    messages.append({"role": "assistant", "content": filler})
            for key in ("ttft_s", "prefill_tps", "decode_tps", "wall_s"):
                crec[key + "_stats"] = stats_of([r[key] for r in crec["reps"]])
        else:
            abort("unknown cell kind: %r" % kind)

    sys.exit(flush("ok", 0))

if __name__ == "__main__":
    main()
