#!/usr/bin/env python3
"""capture_57.py — fresh expert-routing capture for track t-d1cf43b723 task-6becca4365.

Replays the prompts behind the 57 v2+v4 traces (corpus of the 0.39 /
prefill-ranking results) against a live llama-server with the atlas surface
(HYDRA_EXPERT_META=1, HYDRA_EXPERT_STATS=1, HYDRA_EAN_STATS=1,
HYDRA_EXPERT_CAPTURE=1 on the 5060 Ti capture host), and records per-probe:

  - routing: ABSOLUTE per-(layer, expert) decode selection counts from
    GET /turns/:seq (NOT the 63-saturated EMAP delta path of sweep.sh)
  - ean: the cumulative live-EAN section of GET /experts (means only; the
    final probe's snapshot is the corpus-global C1 signal)
  - sidecar: hidden-state + topk stream via POST /capture/flush (Edge0 input)

Confound controls (same as sweep.sh): temperature 0, top_p 1.0, top_k 0,
min_p 0, MTP/spec off (server booted --spec-type none), cache_prompt false
(no KV reuse across probes), one probe per turn (parallel=1).

Prompt bytes:
  - anchors c01/c02/g01 and v2/*: <file>.txt framed as
    <|im_start|>user\\n{TEXT}<|im_end|>\\n<|im_start|>assistant\\n<think>\\n
    (framing replicated from the ground-truth v4 T1 .pref on disk)
  - v4/*: traces/v4/<rest>.pref bytes VERBATIM via /completion (exact replay
    of the fed prefill, including prior-turn decodes + template framing)

All requests use POST /completion with n_predict=N_GEN (uniform).
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import sys
import urllib.request

TRACE_COLLECT = "/tmp/opencode/trace-collect"
INDEX_PATH = "/tmp/opencode/prefill_poc_index.json"
N_GEN_DEFAULT = 256

FR_PRE = b"<|im_start|>user\n"
FR_SUF = b"<|im_end|>\n<|im_start|>assistant\n<think>\n"


def prompt_bytes(name: str) -> bytes:
    if name.startswith("v4/"):
        rest = name.split("/", 1)[1]
        path = os.path.join(TRACE_COLLECT, "traces", "v4", rest + ".pref")
        with open(path, "rb") as fh:
            return fh.read()
    if "/" in name:  # v2/c02 style
        sub, pid = name.split("/", 1)
        path = os.path.join(TRACE_COLLECT, "prompts", sub, pid + ".txt")
    else:  # anchors c01 c02 g01
        path = os.path.join(TRACE_COLLECT, "prompts", name + ".txt")
    with open(path, "rb") as fh:
        text = fh.read()
    return FR_PRE + text + FR_SUF


def http(base: str, method: str, path: str, payload=None, timeout=600):
    """HTTP helper; always returns (status, dict) — raises on empty body."""
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        status = resp.status
        body = resp.read().decode()
    if not body:
        raise RuntimeError(f"{method} {path}: empty response body")
    doc = json.loads(body)
    if not isinstance(doc, dict):
        raise RuntimeError(f"{method} {path}: expected JSON object")
    return status, doc


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="57-prompt atlas capture driver")
    ap.add_argument("--server", required=True, help="e.g. http://127.0.0.1:18441")
    ap.add_argument("--out", required=True, help="output dir for per-probe JSONs")
    ap.add_argument("--sidecars", default=None, help="expected HYDRA_CAPTURE_OUTDIR (for flush validation)")
    ap.add_argument("--n-gen", type=int, default=N_GEN_DEFAULT)
    ap.add_argument("--probes", default=None, help="comma-separated subset of names (default: all 57)")
    ap.add_argument("--recheck", default=None, help="re-run named probes and diff counts (determinism spot-check)")
    ap.add_argument("--tag", default="", help="run tag recorded in each probe file")
    args = ap.parse_args(argv)

    names = [e["name"] for e in json.load(open(INDEX_PATH))]
    if args.probes:
        want = set(args.probes.split(","))
        names = [n for n in names if n in want]
    os.makedirs(args.out, exist_ok=True)
    recheck = set(args.recheck.split(",")) if args.recheck else set()

    # engine reachability + geometry
    _, meta = http(args.server, "GET", "/experts", timeout=15)
    if not meta.get("telemetry_enabled"):
        print("capture: telemetry OFF on engine — refusing (honest OFF state)", file=sys.stderr)
        return 2
    g = meta["geometry"]
    print(f"capture: engine {g['engine_id']} model={g['model_hash']} "
          f"moe_rows={len(g['moe_rows'])} cols={g['cols']} k={g['n_expert_used']}")

    for i, name in enumerate(names):
        safe = name.replace("/", "_")
        dst = os.path.join(args.out, safe + ".json")
        if os.path.exists(dst) and name not in recheck:
            print(f"  [{i + 1}/{len(names)}] {name} (cached)")
            continue
        prompt = prompt_bytes(name).decode("utf-8")
        sha = hashlib.sha256(prompt.encode()).hexdigest()[:16]
        _, comp = http(args.server, "POST", "/completion", {
            "prompt": prompt, "n_predict": args.n_gen,
            "temperature": 0.0, "top_p": 1.0, "top_k": 0, "min_p": 0.0,
            "cache_prompt": False,
        }, timeout=1200)
        n_dec = comp.get("tokens_predicted", args.n_gen)
        _, turns = http(args.server, "GET", "/turns", timeout=15)
        turn_list = turns.get("turns", [])
        if not turn_list:
            raise RuntimeError("GET /turns returned no turns")
        seq = max(t["turn_seq"] for t in turn_list)
        _, turn = http(args.server, "GET", f"/turns/{seq}", timeout=30)
        routing = {}
        for cell in turn.get("routing", []):
            routing[f"{cell['row']}:{cell['expert']}"] = cell["count"]
        _, experts = http(args.server, "GET", "/experts", timeout=30)
        ean = (experts.get("ean") or {})
        sidecar_path = None
        cat, idx = (name.split("/", 1) + ["0"])[:2] if "/" in name else (name, "0")
        try:
            _, flush = http(args.server, "POST",
                            f"/capture/flush?cat={cat}&idx={idx}", {}, timeout=120)
            sidecar_path = (flush or {}).get("path")
        except Exception as exc:  # capture disabled -> delta-only mode
            print(f"  [{i + 1}/{len(names)}] {name} flush unavailable: {exc}")
        rec = {
            "name": name, "tag": args.tag, "prompt_sha16": sha,
            "prefill_tokens": comp.get("tokens_evaluated"),
            "n_gen": n_dec, "turn_seq": seq, "routing": routing,
            "ean_cells": ean.get("cells"), "sidecar": sidecar_path,
            "generated_at": datetime.datetime.now(datetime.timezone.utc).strftime(
                "%Y-%m-%dT%H:%M:%SZ"),
        }
        if name in recheck and os.path.exists(dst):
            old = json.load(open(dst))
            same = old.get("routing") == routing
            print(f"  [{i + 1}/{len(names)}] {name} RECHECK "
                  f"{'IDENTICAL' if same else 'DIFFERS!!'} "
                  f"(old_turn={old.get('turn_seq')} new_turn={seq})")
            rec["recheck_identical"] = same
            rec["recheck_against_turn"] = old.get("turn_seq")
        with open(dst, "w") as fh:
            json.dump(rec, fh)
        total = sum(routing.values())
        print(f"  [{i + 1}/{len(names)}] {name} done "
              f"prefill={rec['prefill_tokens']} dec={n_dec} "
              f"routed_calls={total} ean_cells={rec['ean_cells']} "
              f"sidecar={'yes' if sidecar_path else 'no'}")
    # final corpus-global EAN snapshot (the C1 signal)
    _, experts = http(args.server, "GET", "/experts", timeout=30)
    with open(os.path.join(args.out, "experts_final.json"), "w") as fh:
        json.dump(experts, fh)
    print("capture: wrote experts_final.json "
          f"(ean_cells={(experts.get('ean') or {}).get('cells')})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
