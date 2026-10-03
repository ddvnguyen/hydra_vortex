#!/usr/bin/env python3
"""t0006 engine-log parser for one arm (strata or llama.cpp fork).

Reads the engine log of a completed arm, extracts per-request timing lines and
arm-level facts (strata expert-cache fill / hit rates / suffix drafts /
decode-timing dt_draft; fork print_timing blocks + moe-cache aggregates),
joins request order with the harness bench JSON, and writes <arm>.logparse.json.

Alignment is by sequential order: both engines serve one request at a time
(strata FIFO, fork --parallel 1), and the harness request order equals log
order (warmup first when used).
"""
import argparse
import json
import os
import re
import sys
from typing import Any

STRATA_SERVE = re.compile(
    r"strata serve: prompt (\d+) tokens = (\d+) reused \+ (\d+) read in ([0-9.]+) ms "
    r"\(([0-9.]+) tok/s\), (\d+) generated in ([0-9.]+) ms \(([0-9.]+) tok/s\)"
    r"(?:, drafts accepted (\d+) of (\d+))?(?:, (\d+) checkpoints)?")
STRATA_HIT = re.compile(
    r"strata serve: decode expert cache hit rate: ([0-9.]+)% \((\d+) hits / (\d+) lookups\)")
STRATA_SUFFIX = re.compile(
    r"strata serve: suffix drafts: (\d+) windows, (\d+) of (\d+) drafts accepted")
STRATA_FILL = re.compile(r"strata generate: expert cache (\d+) slots, ([0-9.]+) GiB of VRAM")
STRATA_FILL_AUTO = re.compile(r"expert cache auto: .* -> (\d+) slots")
STRATA_FILL_RESERVE = re.compile(r"expert cache auto: ([0-9.]+) GiB free")
STRATA_CHUNK = re.compile(r"strata serve: prompt chunk auto: (\d+) tokens")
STRATA_BORROW = re.compile(r"the prompt path borrows (\d+) cache slots \(([0-9.]+) GiB\)")
STRATA_DECODE_TIMING = re.compile(
    r"strata decode timing: (\d+) windows, avg T ([0-9.]+), ([0-9.]+) tokens/window, "
    r"([0-9.]+) ms/window.*commit/emit ([0-9.]+) \+ draft ([0-9.]+);")
STRATA_PREFILL = re.compile(
    r"strata prefill timing: (\d+) tokens, GPU timeline ([0-9.]+) ms, wall ([0-9.]+) ms")

FORK_PE = re.compile(r"slot print_timing:.*\| prompt eval time =\s*([0-9.]+) ms /\s*(\d+) tokens")
FORK_EV = re.compile(r"slot print_timing:.*\|\s+eval time =\s*([0-9.]+) ms /\s*(\d+) tokens")
FORK_TO = re.compile(r"slot print_timing:.*\|\s+total time =\s*([0-9.]+) ms /\s*(\d+) tokens")
FORK_GR = re.compile(r"slot print_timing:.*graphs reused =\s*(\d+)")
FORK_TASK = re.compile(r"slot print_timing: id\s+\d+ \| task (\d+)")
FORK_PHASE = re.compile(
    r"moe-cache-phase: phase=(\w+) .*?l1_hits=(\d+) l1_misses=(\d+).*?h2d_mib=([0-9.]+)")
FORK_EXPERTS = re.compile(
    r"moe-cache-experts: tensors=(\d+) experts=(\d+) unique=(\d+).*?accesses=(\d+)")

def parse_strata(log_path):
    out = {"serve": [], "hit": [], "suffix": [], "decode_timing": [], "prefill_timing": [],
           "fill": None, "fill_auto_slots": None, "fill_reserve_gib": None,
           "chunk_tokens": None, "borrow_slots": None, "borrow_gib": None}
    with open(log_path, errors="replace") as f:
        for line in f:
            m = STRATA_SERVE.search(line)
            if m:
                out["serve"].append({
                    "prompt_tokens": int(m.group(1)), "reused": int(m.group(2)),
                    "read": int(m.group(3)), "read_ms": float(m.group(4)),
                    "read_tps": float(m.group(5)), "generated": int(m.group(6)),
                    "decode_ms": float(m.group(7)), "decode_tps": float(m.group(8)),
                    "drafts_accepted": int(m.group(9)) if m.group(9) else None,
                    "drafts_offered": int(m.group(10)) if m.group(10) else None,
                    "checkpoints": int(m.group(11)) if m.group(11) else None})
                continue
            m = STRATA_HIT.search(line)
            if m:
                out["hit"].append({"pct": float(m.group(1)), "hits": int(m.group(2)),
                                   "lookups": int(m.group(3))})
                continue
            m = STRATA_SUFFIX.search(line)
            if m:
                out["suffix"].append({"windows": int(m.group(1)), "accepted": int(m.group(2)),
                                      "offered": int(m.group(3))})
                continue
            m = STRATA_DECODE_TIMING.search(line)
            if m:
                out["decode_timing"].append({
                    "windows": int(m.group(1)), "avg_T": float(m.group(2)),
                    "tok_per_window": float(m.group(3)), "ms_per_window": float(m.group(4)),
                    "commit_emit_ms": float(m.group(5)), "draft_ms": float(m.group(6))})
                continue
            m = STRATA_PREFILL.search(line)
            if m:
                out["prefill_timing"].append({"tokens": int(m.group(1)),
                                              "gpu_ms": float(m.group(2)), "wall_ms": float(m.group(3))})
                continue
            m = STRATA_FILL.search(line)
            if m:
                out["fill"] = {"slots": int(m.group(1)), "gib": float(m.group(2))}
            m = STRATA_FILL_AUTO.search(line)
            if m:
                out["fill_auto_slots"] = int(m.group(1))
            m = STRATA_FILL_RESERVE.search(line)
            if m:
                out["fill_reserve_gib"] = float(m.group(1))
            m = STRATA_CHUNK.search(line)
            if m:
                out["chunk_tokens"] = int(m.group(1))
            m = STRATA_BORROW.search(line)
            if m:
                out["borrow_slots"], out["borrow_gib"] = int(m.group(1)), float(m.group(2))
    return out

def parse_fork(log_path):
    out = {"timing": [], "phase": {}, "experts": None, "draft_lines": []}
    cur: Any = None
    with open(log_path, errors="replace") as f:
        for line in f:
            m = FORK_TASK.search(line)
            if m and "prompt eval time" in line:
                if cur:
                    out["timing"].append(cur)
                cur = {"task": int(m.group(1))}
                m2 = FORK_PE.search(line)
                if m2:
                    cur["prompt_eval_ms"] = float(m2.group(1))
                    cur["prompt_eval_tokens"] = int(m2.group(2))
                continue
            if cur is not None:
                m2 = FORK_EV.search(line)
                if m2:
                    cur["eval_ms"] = float(m2.group(1))
                    cur["eval_tokens"] = int(m2.group(2))
                    continue
                m2 = FORK_TO.search(line)
                if m2:
                    cur["total_ms"] = float(m2.group(1))
                    cur["total_tokens"] = int(m2.group(2))
                    continue
                m2 = FORK_GR.search(line)
                if m2:
                    cur["graphs_reused"] = int(m2.group(1))
                    continue
            m = FORK_PHASE.search(line)
            if m:  # lifetime aggregate printed at exit: last line wins
                out["phase"][m.group(1)] = {"l1_hits": int(m.group(2)), "l1_misses": int(m.group(3)),
                                            "h2d_mib": float(m.group(4))}
                continue
            m = FORK_EXPERTS.search(line)
            if m:
                out["experts"] = {"tensors": int(m.group(1)), "experts": int(m.group(2)),
                                  "unique": int(m.group(3)), "accesses": int(m.group(4))}
                continue
            if "draft" in line and "print_timing" in line:
                out["draft_lines"].append(line.strip())
    if cur:
        out["timing"].append(cur)
    return out

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--engine", choices=["strata", "fork"], required=True)
    ap.add_argument("--arm", required=True)
    ap.add_argument("--log", required=True, help="engine log file")
    ap.add_argument("--bench", required=True, help="<arm>.bench.json from bench3060.py")
    ap.add_argument("--out", required=True, help="output <arm>.logparse.json")
    a = ap.parse_args()

    with open(a.bench) as f:
        bench = json.load(f)
    parsed = parse_strata(a.log) if a.engine == "strata" else parse_fork(a.log)

    n_req = bench.get("requests_total", 0)
    align = {"requests": n_req}
    if a.engine == "strata":
        align["serve_lines"] = len(parsed["serve"])
        align["hit_lines"] = len(parsed["hit"])
        align["suffix_lines"] = len(parsed["suffix"])
        align["decode_timing_lines"] = len(parsed["decode_timing"])
        align["prefill_timing_lines"] = len(parsed["prefill_timing"])
        align["match"] = (len(parsed["serve"]) == n_req)
        # join order-wise: harness reps (warmup + cells) vs serve lines
        h_records = []
        if bench.get("warmup"):
            h_records.append(dict(bench["warmup"], tag="warmup"))
        for cell in bench["cells"]:
            for r in cell["reps"]:
                tag = cell["id"] + (":rep%d" % r.get("rep", r.get("turn", 0)))
                h_records.append(dict(r, tag=tag))
        joined = []
        for i, h in enumerate(h_records):
            j = {"tag": h.get("tag"), "harness": {k: h.get(k) for k in
                 ("ttft_s", "prefill_tps", "decode_tps", "prompt_tokens", "completion_tokens",
                  "cached_tokens")}}
            if i < len(parsed["serve"]):
                s = parsed["serve"][i]
                j["serve"] = s
                j["log_decode_tps"] = s["decode_tps"]
                j["log_read_tps"] = s["read_tps"]
                j["reused_frac"] = round(s["reused"] / s["prompt_tokens"], 4) if s["prompt_tokens"] else None
            if i < len(parsed["hit"]):
                j["hit"] = parsed["hit"][i]
            if i < len(parsed["suffix"]):
                j["suffix"] = parsed["suffix"][i]
            if i < len(parsed["decode_timing"]):
                j["decode_timing"] = parsed["decode_timing"][i]
            joined.append(j)
        parsed["joined"] = joined
        if parsed["decode_timing"]:
            d = parsed["decode_timing"]
            parsed["draft_cost"] = {
                "avg_draft_ms_per_window": round(sum(x["draft_ms"] for x in d) / len(d), 3),
                "avg_commit_emit_ms_per_window": round(sum(x["commit_emit_ms"] for x in d) / len(d), 3),
                "avg_ms_per_window": round(sum(x["ms_per_window"] for x in d) / len(d), 3),
                "avg_T": round(sum(x["avg_T"] for x in d) / len(d), 3),
                "n": len(d)}
    else:
        align["print_timing_blocks"] = len(parsed["timing"])
        align["match"] = (len(parsed["timing"]) == n_req)
        h_records = []
        if bench.get("warmup"):
            h_records.append(dict(bench["warmup"], tag="warmup"))
        for cell in bench["cells"]:
            for r in cell["reps"]:
                tag = cell["id"] + (":rep%d" % r.get("rep", r.get("turn", 0)))
                h_records.append(dict(r, tag=tag))
        joined = []
        for i, h in enumerate(h_records):
            j = {"tag": h.get("tag"), "harness": {k: h.get(k) for k in
                 ("ttft_s", "prefill_tps", "decode_tps", "prompt_tokens", "completion_tokens",
                  "cached_tokens")}}
            if i < len(parsed["timing"]):
                t = parsed["timing"][i]
                j["timing"] = t
                # n_prompt_processed in print_timing = evaluated (uncached) tokens
                cached = h.get("cached_tokens")
                if cached is None and "prompt_eval_tokens" in t and h.get("prompt_tokens") is not None:
                    cached = h["prompt_tokens"] - t["prompt_eval_tokens"]
                j["derived_cached_tokens"] = cached
                j["log_decode_tps"] = round((t["eval_tokens"] - 1) / (t["eval_ms"] / 1000.0), 2) \
                    if t.get("eval_tokens", 0) > 1 and t.get("eval_ms", 0) > 0 else None
            joined.append(j)
        parsed["joined"] = joined
        for ph, v in parsed["phase"].items():
            tot = v["l1_hits"] + v["l1_misses"]
            v["l1_hit_rate_pct"] = round(100.0 * v["l1_hits"] / tot, 2) if tot else None

    result = {"arm": a.arm, "engine": a.engine, "log": a.log, "bench": a.bench,
              "align": align, "parsed": parsed}
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    tmp = a.out + ".tmp"
    with open(tmp, "w") as f:
        json.dump(result, f, indent=1)
    os.replace(tmp, a.out)
    print("%s logparse written: %s (align match=%s)" % (a.arm, a.out, align["match"]))
    if not align["match"]:
        print("WARNING: request/line count mismatch", file=sys.stderr)
    sys.exit(0 if align["match"] else 2)

if __name__ == "__main__":
    main()
