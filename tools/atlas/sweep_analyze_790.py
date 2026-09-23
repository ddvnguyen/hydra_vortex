#!/usr/bin/env python3
"""sweep_analyze_790.py — PRE-REGISTERED analysis for architect #790 (track t-d1cf43b723).

Frozen 2026-09-23 BEFORE any capture run. Do not edit thresholds after the run
starts; any change requires a new script version + decision record.

Design (binding #790, verbatim thresholds):
  - Primary endpoint: out-of-sample h at N = 53 experts/layer (3060 budget),
    LOPO split (rank on 56 prompts, score h on held-out one), decode-only, k=10.
    Also report h@38 for continuity.
  - B0 baseline: global heat, recomputed in the same sweep on the same corpus.
    SANITY GATE: B0 h@38 median must land in [0.33, 0.43]; else STOP (exit 3).
  - C1: REAP rank by mean gate weight x expert output norm (live-EAN overlay).
    NOTE (disclosed leak): EAN accumulators have no per-probe reset endpoint,
    so C1 uses the corpus-global EAN ranking in every fold (held-out prompt
    contributes ~1/57 of EAN mass). Bias is pro-C1 and negligible vs the
    +0.05 bar; a C1 PASS under this leak would need a leak-ablation follow-up.
  - C2: global heat water-filled across layers — top TOTAL_SLABS (layer, expert)
    cells by training heat (greedy marginal-h allocation), variable N_l/layer.
  - PASS for C1/C2 (ALL THREE required):
      median paired Dh >= +0.05; Dh positive in >= 2/3 of held-out prompts;
      bootstrap (B=10000, seed=786) 95% lower bound on median Dh > 0.
  - Secondary (reported, not gating): Dh vs the warm ranking on v4 turns 2+.
    Warm ranking for turn t = flat-53 heat rank from the SAME session's prior
    turns' fresh decode counts.
  - Edge0 endpoint (separate): probe minus last-token-repeat gain >= +0.10
    counts as "predictable". Characterization only. Computed from sidecars;
    this script reports the LTR column + a probe pilot (see edge0 section).

Inputs: --capture DIR (per-probe JSONs from capture_57.py + experts_final.json),
        --ean OVERLAY (live-EAN overlay JSON, reap_observer from-ean shape).
Outputs: experts.json, expert-ranks.json (+ REAP overlay file), tables.json,
         report printed to stdout.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import statistics
import sys
from collections import Counter

# ---------------- PRE-REGISTERED CONSTANTS (frozen 2026-09-23) ----------------
N_LAYERS = 48
N_PER_LAYER = 53          # 3060 budget: flat pins/layer for B0 and C1
N_CONT = 38               # continuity point
TOTAL_SLABS = N_PER_LAYER * N_LAYERS  # 2544, C2 water-fill budget
B0_GATE_LO, B0_GATE_HI = 0.33, 0.43   # B0 h@38 sanity band
PASS_DH = 0.05
PASS_FRAC = 2.0 / 3.0
BOOT_B = 10000
BOOT_SEED = 786
ENGINE_ID = "qwen38"
# ------------------------------------------------------------------------------


def load_capture(capdir):
    probes = []
    for fn in sorted(os.listdir(capdir)):
        if not fn.endswith(".json") or fn == "experts_final.json":
            continue
        with open(os.path.join(capdir, fn)) as fh:
            probes.append(json.load(fh))
    probes.sort(key=lambda r: r["name"])
    if len(probes) < 2:
        raise SystemExit("need >= 2 probes for LOPO")
    return probes


def heat_of(probes):
    """name -> list of per-layer Counters over decode routed calls."""
    out = {}
    for p in probes:
        layers = [Counter() for _ in range(N_LAYERS)]
        for key, n in p["routing"].items():
            l_s, e_s = key.split(":")
            layers[int(l_s)][int(e_s)] += n
        out[p["name"]] = layers
    return out


def totals_of(probes):
    return {p["name"]: sum(p["routing"].values()) for p in probes}


def check_decode_only(probes):
    """Stage-A counters are decode-only by design. The prompt-eval batch also
    yields the first emitted token, so routed decode calls per turn equal
    forwards*48*10 with forwards == n_gen - 1 (verified on smoke turn)."""
    bad = []
    for p in probes:
        expect = p["forwards"] * N_LAYERS * 10
        if sum(p["routing"].values()) != expect:
            bad.append((p["name"], sum(p["routing"].values()), expect))
    return bad


def flat_pins(train_heat, names, n, key):
    """Per-layer top-n by summed training heat. key selects rank value fn."""
    pins = {}
    for layer in range(N_LAYERS):
        agg = Counter()
        for nm in names:
            agg.update(train_heat[nm][layer])
        pins[layer] = {e for e, _ in agg.most_common(n)}
    return pins


def waterfill_pins(train_heat, names, budget):
    """Top-`budget` (layer, expert) cells by training heat (greedy marginal-h)."""
    agg = Counter()
    for nm in names:
        for layer in range(N_LAYERS):
            for e, n in train_heat[nm][layer].items():
                agg[(layer, e)] += n
    pins = {layer: set() for layer in range(N_LAYERS)}
    for (layer, e), _ in agg.most_common(budget):
        pins[layer].add(e)
    return pins


def ean_flat_pins(saliency, n):
    """C1: per-layer top-n by EAN saliency; unobserved experts rank last."""
    by_layer: dict = {}
    for (layer, e), s in saliency.items():
        by_layer.setdefault(layer, []).append((s, e))
    pins = {}
    for layer in range(N_LAYERS):
        ranked = sorted(by_layer.get(layer, []), reverse=True)
        pins[layer] = {e for _, e in ranked[:n]}
    return pins


def hit_rate(layers, pins):
    hits = tot = 0
    for layer in range(N_LAYERS):
        s = pins[layer]
        for e, n in layers[layer].items():
            tot += n
            if e in s:
                hits += n
    return hits / tot if tot else 0.0


def bootstrap_lb(deltas, b=BOOT_B, seed=BOOT_SEED):
    import random
    rng = random.Random(seed)
    n = len(deltas)
    meds = []
    for _ in range(b):
        sample = [deltas[rng.randrange(n)] for _ in range(n)]
        meds.append(statistics.median(sample))
    meds.sort()
    return meds[int(0.025 * b)]


def judge(tag, deltas):
    med = statistics.median(deltas)
    frac = sum(1 for d in deltas if d > 0) / len(deltas)
    lb = bootstrap_lb(deltas)
    ok = med >= PASS_DH and frac >= PASS_FRAC and lb > 0
    print(f"{tag}: median Dh={med:+.4f} (bar {PASS_DH:+.2f})  "
          f"pos_frac={frac:.3f} (bar {PASS_FRAC:.3f})  "
          f"boot95_LB={lb:+.4f} (bar >0)  -> {'PASS' if ok else 'FAIL'}")
    return {"median_dh": med, "pos_frac": frac, "boot_lb": lb,
            "pass": ok, "deltas": deltas}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="pre-registered #790 sweep analysis")
    ap.add_argument("--capture", required=True)
    ap.add_argument("--ean", required=True, help="live-EAN overlay JSON")
    ap.add_argument("--outdir", required=True)
    ap.add_argument("--model", default="")
    args = ap.parse_args(argv)

    os.makedirs(args.outdir, exist_ok=True)
    probes = load_capture(args.capture)
    names = [p["name"] for p in probes]
    print(f"#790 analysis: {len(names)} probes: {names[0]} .. {names[-1]}")

    bad = check_decode_only(probes)
    if bad:
        print(f"STOP: decode-only check failed for {len(bad)} probes "
              f"(total != n_gen*48*10), e.g. {bad[:3]}", file=sys.stderr)
        return 3
    print("decode-only check: all probe totals == forwards*48*10")

    heat = heat_of(probes)
    with open(args.ean) as fh:
        overlay = json.load(fh)
    saliency = {}
    for key, val in overlay.get("saliency", {}).items():
        l_s, e_s = key.split(":")
        saliency[(int(l_s), int(e_s))] = float(val)
    print(f"EAN overlay: {len(saliency)} cells "
          f"(method={overlay.get('provenance', {}).get('method')})")

    c1_pins = ean_flat_pins(saliency, N_PER_LAYER)
    c1_38 = ean_flat_pins(saliency, N_CONT)

    rows = []
    for i, held in enumerate(names):
        train = [n for n in names if n != held]
        b0 = flat_pins(heat, train, N_PER_LAYER, "heat")
        b0_38 = flat_pins(heat, train, N_CONT, "heat")
        c2 = waterfill_pins(heat, train, TOTAL_SLABS)
        h_b0 = hit_rate(heat[held], b0)
        h_b0_38 = hit_rate(heat[held], b0_38)
        h_c1 = hit_rate(heat[held], c1_pins)
        h_c1_38 = hit_rate(heat[held], c1_38)
        h_c2 = hit_rate(heat[held], c2)
        rows.append({"held": held, "h_b0": h_b0, "h_b0_38": h_b0_38,
                     "h_c1": h_c1, "h_c1_38": h_c1_38, "h_c2": h_c2,
                     "d_c1": h_c1 - h_b0, "d_c2": h_c2 - h_b0})

    med_b0_38 = statistics.median(r["h_b0_38"] for r in rows)
    print(f"B0 h@38 median = {med_b0_38:.4f} (gate band "
          f"[{B0_GATE_LO}, {B0_GATE_HI}])")
    if not (B0_GATE_LO <= med_b0_38 <= B0_GATE_HI):
        print("STOP: B0 sanity gate FAILED — corpus or measurement is off; "
              "candidates do not get scored.", file=sys.stderr)
        return 3

    res_c1 = judge("C1 REAP", [r["d_c1"] for r in rows])
    res_c2 = judge("C2 waterfill", [r["d_c2"] for r in rows])

    # ---- secondary: warm ranking on v4 turns 2+ (reported, not gating) ----
    warm_rows = []
    sess_turns: dict = {}
    for p in probes:
        if p["name"].startswith("v4/"):
            rest = p["name"][3:]
            base, turn = rest.rsplit("_T", 1)
            sess_turns.setdefault(base, []).append((int(turn), p["name"]))
    for base, turns in sorted(sess_turns.items()):
        turns.sort()
        for k in range(1, len(turns)):
            prior = [nm for _, nm in turns[:k]]
            cur = turns[k][1]
            warm = flat_pins(heat, prior, N_PER_LAYER, "heat")
            h_w = hit_rate(heat[cur], warm)
            train = [n for n in names if n != cur]
            h_b = hit_rate(heat[cur], flat_pins(heat, train, N_PER_LAYER, "heat"))
            h_1 = hit_rate(heat[cur], c1_pins)
            h_2 = hit_rate(heat[cur], waterfill_pins(heat, train, TOTAL_SLABS))
            warm_rows.append({"turn": cur, "h_warm": h_w, "h_b0": h_b,
                              "h_c1": h_1, "h_c2": h_2})
    if warm_rows:
        for tag, key in (("B0", "h_b0"), ("C1", "h_c1"), ("C2", "h_c2")):
            ds = [r[key] - r["h_warm"] for r in warm_rows]
            print(f"warm-2+ {tag}-warm: median Dh={statistics.median(ds):+.4f} "
                  f"n={len(ds)} (secondary, not gating)")

    # ---- artifacts ----
    all_names = names
    full_heat = Counter()
    for nm in all_names:
        for layer in range(N_LAYERS):
            for e, n in heat[nm][layer].items():
                full_heat[(layer, e)] += n
    b0_full = flat_pins(heat, all_names, N_PER_LAYER, "heat")
    c2_full = waterfill_pins(heat, all_names, TOTAL_SLABS)
    prov = {
        "model": args.model, "engine_id": ENGINE_ID,
        "generated_at": datetime.datetime.now(datetime.timezone.utc).strftime(
            "%Y-%m-%dT%H:%M:%SZ"),
        "corpus": {"kind": "57-trace v2+v4 replay, fresh capture",
                   "n_probes": len(names), "decode_only": True,
                   "n_gen": sorted({p['n_gen'] for p in probes})},
        "analysis": "sweep_analyze_790.py (pre-registered 2026-09-23)",
        "gates": {"b0_h38_band": [B0_GATE_LO, B0_GATE_HI],
                  "pass_dh": PASS_DH, "pass_frac": PASS_FRAC,
                  "bootstrap": {"b": BOOT_B, "seed": BOOT_SEED}},
        "c1_leak_note": "C1 uses corpus-global EAN in every LOPO fold "
                        "(no per-probe EAN reset endpoint); held-out prompt "
                        "contributes ~1/57 of EAN mass.",
    }
    experts = {}
    for (layer, e), cnt in sorted(full_heat.items()):
        key = f"{layer}:{e}"
        experts[key] = {
            "heat": cnt,
            "in_b0_53": e in b0_full[layer],
            "in_c2": e in c2_full[layer],
            "reap": saliency.get((layer, e)),
            "edge0": None,
        }
    with open(os.path.join(args.outdir, "experts.json"), "w") as fh:
        json.dump({"experts": experts, "provenance": prov}, fh, indent=1)
    layers = {}
    for layer in range(N_LAYERS):
        agg = Counter()
        for nm in all_names:
            agg.update(heat[nm][layer])
        layers[str(layer)] = {"experts": [
            {"id": e, "heat": n, "reap_saliency": saliency.get((layer, e))}
            for e, n in agg.most_common()]}
    with open(os.path.join(args.outdir, "expert-ranks.json"), "w") as fh:
        json.dump({"version": 1, "engine_id": ENGINE_ID,
                   "model_hash": args.model, "provenance": prov,
                   "layers": layers}, fh, indent=1)
    with open(os.path.join(args.outdir, "tables.json"), "w") as fh:
        json.dump({"rows": rows, "c1": res_c1, "c2": res_c2,
                   "warm_rows": warm_rows,
                   "b0_h38_median": med_b0_38}, fh, indent=1)
    print(f"wrote experts.json ({len(experts)} experts), expert-ranks.json, "
          f"tables.json -> {args.outdir}")
    print(f"VERDICT-790: C1 {'PASS' if res_c1['pass'] else 'FAIL'} / "
          f"C2 {'PASS' if res_c2['pass'] else 'FAIL'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
