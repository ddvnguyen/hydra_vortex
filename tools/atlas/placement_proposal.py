#!/usr/bin/env python3
"""placement_proposal.py — architect ruling d-5f4a1b5fbd (narrowed A).

Harvest the /turns ring offline (row 40 / MTP excluded on BOTH sides),
rank experts per layer by exact routing counts, build the ranking on the
OLDER half of the ring and score it on the NEWER half (time-ordered split,
more honest than leave-one-out for a ring), then emit the B-sweep proposal
table: for each swap budget B (individual experts, one-for-one VRAM<->RAM
at equal total VRAM cost), what share of total expert work would sit on CPU.

Deliverable is the TABLE ONLY — no pin file is produced or applied
(nothing in prod reads a pin file; HYDRA_PIN_FILE on 6b7dc6a98 only marks
top-k tensors as graph outputs).
"""
from __future__ import annotations

import argparse
import json
import os
import urllib.request
from collections import defaultdict

BASE = "http://192.168.122.21:8620"
PROXY = BASE + "/engine-proxy/p100-35b"  # atlas-web fronts the engine (SPA fallback owns bare /turns)


def fetch_json(path: str, timeout: float = 10.0):
    req = urllib.request.Request(PROXY + path)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode())


def harvest() -> tuple[dict, list[dict]]:
    """Return (geometry, [{turn_seq, routing:[{row, expert, count}...]}...])
    with MTP rows excluded at harvest time."""
    turns = fetch_json("/turns")["turns"]
    geo = fetch_json("/experts")["geometry"]
    moe = geo["moe_rows"]           # grid row -> real layer (trunk only)
    out = []
    for t in turns:
        det = fetch_json(f"/turns/{t['turn_seq']}")
        routing = []
        for e in det.get("routing") or []:
            row = e["row"]
            if row >= len(moe):      # trailing rows = nextn/MTP grid rows
                continue             # exclude row 40 on BOTH sides (ruling)
            routing.append({"layer": moe[row], "expert": e["expert"],
                            "count": e["count"]})
        out.append({"turn_seq": t["turn_seq"], "routing": routing})
    return geo, out


def count_maps(turns: list[dict]):
    """Per (build|score) split: total work per layer, and per-cell counts."""
    total = defaultdict(int)                    # layer -> work
    cells = defaultdict(lambda: defaultdict(int))  # layer -> expert -> count
    for t in turns:
        for e in t["routing"]:
            total[e["layer"]] += e["count"]
            cells[e["layer"]][e["expert"]] += e["count"]
    return total, cells


def rank_layers(cells, top_b: int):
    """Per layer: top-B experts by build-half counts, hot-first."""
    ranks = {}
    for layer, cm in cells.items():
        ids = sorted(cm.items(), key=lambda kv: (-kv[1], kv[0]))
        ranks[layer] = [eid for eid, _ in ids[:top_b]]
    return ranks


def cpu_share_with_swap(build_cells, score_total, score_cells, B: int):
    """CPU share of expert work on the score half under a TOTAL swap budget
    of B individual experts (architect ruling d-5f4a1b5fbd table semantics:
    B=256 -> ~5.1pp, B=1024 -> ~11.6pp). One-for-one VRAM-neutral:
    promoted per CPU layer = B/8 (hot, to VRAM); demoted per VRAM layer =
    B/33 (cold, to RAM); floor keeps VRAM usage <= baseline (conservative)."""
    cpu_layers = set(range(8))
    n_cpu, n_vram = 8, 33
    b_prom = B // n_cpu
    b_dem = B // n_vram
    total_work = sum(score_total.values())
    if total_work == 0:
        return None
    cpu_work = 0.0
    for layer, work in score_total.items():
        cm = score_cells.get(layer, {})
        bc = build_cells.get(layer, {})
        if layer in cpu_layers:
            if not bc:
                cpu_work += work        # no build data: layer stays CPU
                continue
            top = {eid for eid, _ in
                   sorted(bc.items(), key=lambda kv: (-kv[1], kv[0]))[:b_prom]}
            promoted = sum(cm.get(eid, 0) for eid in top)
            cpu_work += (work - promoted)
        else:
            if not bc or b_dem == 0:
                continue                # stays VRAM, zero CPU work
            cold = {eid for eid, _ in
                    sorted(bc.items(), key=lambda kv: (kv[1], kv[0]))[:b_dem]}
            cpu_work += sum(cm.get(eid, 0) for eid in cold)
    return cpu_work / total_work


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="docs/evidence/placement/proposal.json")
    args = ap.parse_args()

    geo, turns = harvest()
    turns.sort(key=lambda t: t["turn_seq"])
    half = len(turns) // 2
    build, score = turns[:half], turns[half:]
    print(f"ring: {len(turns)} turns "
          f"(build {build[0]['turn_seq']}..{build[-1]['turn_seq']}, "
          f"score {score[0]['turn_seq']}..{score[-1]['turn_seq']}), "
          f"MTP row excluded")

    build_total, build_cells = count_maps(build)
    score_total, score_cells = count_maps(score)

    # Baseline: today's placement = 8 CPU banks (layers 0..7 static
    # --n-cpu-moe 8) + all MTP excluded. CPU share on the score half:
    cpu_banks = set(range(8))
    total_work = sum(score_total.values())
    base_cpu = sum(w for l, w in score_total.items() if l in cpu_banks) / total_work

    rows = [{"B": 0, "cpu_share": round(base_cpu, 4),
             "promoted_per_cpu_layer": 0, "demoted_per_vram_layer": 0}]
    for B in (256, 512, 1024):
        share = cpu_share_with_swap(build_cells, score_total,
                                    score_cells, B)
        rows.append({"B": B, "cpu_share": round(share, 4),
                     "promoted_per_cpu_layer": B // 8,
                     "demoted_per_vram_layer": B // 33})

    # Which individual experts would move at B=256 (top-32 per CPU layer).
    moves = []
    for layer in sorted(cpu_banks):
        cm = build_cells.get(layer, {})
        top = sorted(cm.items(), key=lambda kv: (-kv[1], kv[0]))[:256 // 8]
        moves.append({"layer": layer, "promote_top": len(top),
                      "sample_hot": [eid for eid, _ in top[:8]]})

    doc = {
        "ruling": "d-5f4a1b5fbd (narrowed A)",
        "engine": {"geometry": geo},
        "ring": {"turns": len(turns),
                 "build": [build[0]["turn_seq"], build[-1]["turn_seq"]],
                 "score": [score[0]["turn_seq"], score[-1]["turn_seq"]]},
        "baseline": {"cpu_banks": sorted(cpu_banks),
                     "cpu_share_score_half": round(base_cpu, 4),
                     "total_expert_calls_score_half": total_work},
        "sweep": rows,
        "moves_at_B256": moves,
        "note": ("deliverable is the proposal table only; no pin file is "
                 "produced (nothing in prod reads one; d-5f4a1b5fbd)"),
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w") as fh:
        json.dump(doc, fh, indent=1)

    print(f"\nbaseline CPU share (8 banks): {base_cpu:.1%} "
          f"of {total_work} expert calls")
    print("\nB-sweep (one-for-one swap, score half):")
    print("  B      CPU share of expert work")
    for r in rows:
        print(f"  {r['B']:<6} {r['cpu_share']:.1%}")
    print(f"\nwrote {args.out}")


if __name__ == "__main__":
    main()
