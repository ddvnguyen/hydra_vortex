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
    DEFERRED per owner ruling 2026-09-24 (EAN fork defect: HYDRA_EAN_STATS
    aborts the server; diagnosis delivered, fix with fork owner): run with
    --skip-c1; C1 scoring resumes on a post-EAN-fix run against the routing
    captured here. B0/C2/warm thresholds below are UNCHANGED by the skip.
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
Outputs: experts-790.json, expert-ranks-790.json (+ REAP overlay file),
         tables.json, report printed to stdout.

AMENDMENT 2026-09-26 (hydra F4b, architect package d-9981fa1092, decision
recorded): output files renamed with the -790 suffix. The old names
(experts.json / expert-ranks.json) are shared with the UI atlas pipeline
(emit.py + served experts.json) — a ranking-tier experts.json served as the
UI atlas crashes Brain/Galaxy (entries lack label/affinity; F4c makes the
UI skip them, but the files must not collide in the first place).
Thresholds, endpoints and gates UNCHANGED.

AMENDMENT 2026-09-30 (leader, task-6becca4365 Ornith leg): geometry and
budgets are CLI args (no hardcoded model geometry; grid snapshots from
capture_57.py are asserted, trunk-only scoring, nextn rows excluded).
Without --b0-gate-lo/hi the structural uniform floor applies (STOP unless
B0 h@N beats N/n_experts). APEX runs reproduce the frozen design by passing
the documented APEX args. Method (LOPO, bootstrap, PASS bars) UNCHANGED.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import statistics
import sys
from collections import Counter

# ---------------- METHOD CONSTANTS (model-independent) ----------------
# Geometry (layers/experts/k) and budgets are CLI args (required) — NO
# hardcoded model geometry anywhere. APEX-I-Mini #790 pre-registration
# (frozen 2026-09-23) is reproduced by passing:
#   --n-layers 48 --n-experts 512 --top-k 10 --n-pins 53 --n-cont 38
#   --b0-gate-lo 0.33 --b0-gate-hi 0.43 --engine-id qwen38
# Ornith (2026-09-30): --n-layers 41 --n-experts 256 --top-k 8 --n-pins 160
#   --n-cont 38 --extra-ns 128 (no --b0-gate-lo/hi: uniform floor applies).
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


def grid_of(probes, n_layers, n_experts, top_k):
    """Fail-loud geometry assertion. All probes must share one grid whose
    trunk (moe_rows) matches the expected geometry; nextn/MTP rows (if any)
    are reported and EXCLUDED from scoring (MTP off during capture)."""
    grids = {}
    for p in probes:
        g = p.get("grid")
        if g is None:
            raise SystemExit(f"STOP: probe {p['name']} lacks a grid snapshot "
                             f"(re-capture with current capture_57.py)")
        grids[p["name"]] = (tuple(g["moe_rows"]), tuple(g["nextn_rows"]),
                            g["cols"], g["k"])
    distinct = set(grids.values())
    if len(distinct) != 1:
        raise SystemExit(f"STOP: mixed grids across probes: {distinct}")
    moe_rows, nextn_rows, cols, k = distinct.pop()
    if len(moe_rows) != n_layers or cols != n_experts or k != top_k:
        raise SystemExit(
            f"STOP: live grid moe_rows={len(moe_rows)} cols={cols} k={k} != "
            f"expected layers={n_layers} experts={n_experts} k={top_k}")
    if nextn_rows:
        print(f"grid: {len(moe_rows)} trunk layers + {len(nextn_rows)} "
              f"nextn rows {list(nextn_rows)} (excluded from scoring)")
    return moe_rows


def heat_of(probes, moe_rows):
    """name -> list of per-layer Counters over decode routed calls.

    Turn-routing cells are keyed by GRID ROW; map row -> moe_rows[row]
    (trunk layer id). Cells on non-trunk rows must be empty (MTP off);
    any count there is a STOP (measurement confound)."""
    row2layer = {r: il for r, il in enumerate(moe_rows)}
    n_layers = len(moe_rows)
    out = {}
    for p in probes:
        layers = [Counter() for _ in range(n_layers)]
        for key, n in p["routing"].items():
            r_s, e_s = key.split(":")
            r, e = int(r_s), int(e_s)
            if r not in row2layer:
                if n:
                    raise SystemExit(
                        f"STOP: probe {p['name']} has {n} routed calls on "
                        f"non-trunk grid row {r} (MTP leak?)")
                continue
            layers[row2layer[r]][e] += n
        out[p["name"]] = layers
    return out


def totals_of(probes):
    return {p["name"]: sum(p["routing"].values()) for p in probes}


def check_decode_only(probes, moe_rows, top_k):
    """Stage-A counters are decode-only by design. The prompt-eval batch also
    yields the first emitted token, so each PRESENT trunk layer must total
    exactly forwards*k. Layers absent in ALL probes are a systematic engine
    gap (reported by the SCOPE note); a layer present in SOME probes only,
    or a present layer with a wrong total, is a STOP. Returns absent layers.
    """
    from collections import Counter as _C
    absent_sets = []
    bad = []
    for p in probes:
        per = _C()
        for key, n in p["routing"].items():
            r_s, e_s = key.split(":")
            per[int(r_s)] += n
        expect = p["forwards"] * top_k
        for r, tot in per.items():
            if tot != expect:
                bad.append((p["name"], r, tot, expect))
        present = {moe_rows[r] for r in per if r < len(moe_rows)}
        absent_sets.append(frozenset(moe_rows) - present)
    if len(set(absent_sets)) != 1:
        bad.append(("MIXED-ABSENT-LAYERS", sorted(map(sorted, set(absent_sets)))[:3], "", ""))
    return bad, sorted(absent_sets[0]) if absent_sets else []


def flat_pins(train_heat, names, n, n_layers):
    """Per-layer top-n by summed training heat."""
    pins = {}
    for layer in range(n_layers):
        agg = Counter()
        for nm in names:
            agg.update(train_heat[nm][layer])
        pins[layer] = {e for e, _ in agg.most_common(n)}
    return pins


def waterfill_pins(train_heat, names, budget, n_layers):
    """Top-`budget` (layer, expert) cells by training heat (greedy marginal-h)."""
    agg = Counter()
    for nm in names:
        for layer in range(n_layers):
            for e, n in train_heat[nm][layer].items():
                agg[(layer, e)] += n
    pins = {layer: set() for layer in range(n_layers)}
    for (layer, e), _ in agg.most_common(budget):
        pins[layer].add(e)
    return pins


def ean_flat_pins(saliency, n, n_layers):
    """C1: per-layer top-n by EAN saliency; unobserved experts rank last."""
    by_layer: dict = {}
    for (layer, e), s in saliency.items():
        by_layer.setdefault(layer, []).append((s, e))
    pins = {}
    for layer in range(n_layers):
        ranked = sorted(by_layer.get(layer, []), reverse=True)
        pins[layer] = {e for _, e in ranked[:n]}
    return pins


def hit_rate(layers, pins, n_layers):
    hits = tot = 0
    for layer in range(n_layers):
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
    ap.add_argument("--ean", default=None, help="live-EAN overlay JSON")
    ap.add_argument("--skip-c1", action="store_true",
                    help="defer C1 (owner ruling 2026-09-24, EAN defect)")
    ap.add_argument("--outdir", required=True)
    ap.add_argument("--model", default="")
    ap.add_argument("--engine-id", default=ENGINE_ID,
                    help="provenance engine_id override (accepted engine "
                         "spellings: 16-hex FNV geometry id or short "
                         "$arch:$basename[:$size]; hydra F4a)")
    # Geometry + budget: REQUIRED, no hardcoded model geometry anywhere.
    # APEX-I-Mini #790: --n-layers 48 --n-experts 512 --top-k 10 --n-pins 53
    #   --n-cont 38 --b0-gate-lo 0.33 --b0-gate-hi 0.43
    # Ornith 2026-09-30: --n-layers 41 --n-experts 256 --top-k 8 --n-pins 160
    #   --n-cont 38 --extra-ns 128 (uniform floor gate, no band).
    ap.add_argument("--n-layers", type=int, required=True)
    ap.add_argument("--n-experts", type=int, required=True)
    ap.add_argument("--top-k", type=int, required=True)
    ap.add_argument("--n-pins", type=int, required=True,
                    help="primary N: flat pins/layer for B0 (3060 budget)")
    ap.add_argument("--n-cont", type=int, default=38,
                    help="continuity N (reported, not gated)")
    ap.add_argument("--extra-ns", default="",
                    help="comma-separated extra Ns, reported only (e.g. 128)")
    ap.add_argument("--b0-gate-lo", type=float, default=None)
    ap.add_argument("--b0-gate-hi", type=float, default=None,
                    help="explicit B0 h@cont sanity band (APEX form). If "
                    "omitted, the structural floor applies: STOP unless B0 "
                    "h@N median strictly beats uniform N/n_experts.")
    args = ap.parse_args(argv)
    if not args.skip_c1 and not args.ean:
        ap.error("--ean is required unless --skip-c1 is given")
    n_layers, n_exp, top_k = args.n_layers, args.n_experts, args.top_k
    n_pins, n_cont = args.n_pins, args.n_cont
    extra_ns = [int(x) for x in args.extra_ns.split(",") if x.strip()]
    total_slabs = n_pins * n_layers
    uniform = n_pins / n_exp

    os.makedirs(args.outdir, exist_ok=True)
    probes = load_capture(args.capture)
    names = [p["name"] for p in probes]
    print(f"#790 analysis: {len(names)} probes, geometry "
          f"{n_layers}x{n_exp} k={top_k}, N={n_pins} "
          f"(uniform baseline {uniform:.4f}): {names[0]} .. {names[-1]}")

    moe_rows = grid_of(probes, n_layers, n_exp, top_k)
    bad, absent = check_decode_only(probes, moe_rows, top_k)
    if bad:
        print(f"STOP: decode-only check failed: e.g. {bad[:3]}",
              file=sys.stderr)
        return 3
    print(f"decode-only check: every present trunk layer == forwards*k "
          f"(absent in all probes: {absent if absent else 'none'})")

    heat = heat_of(probes, moe_rows)
    # Scope note: trunk layers with zero corpus-wide heat are unscored
    # (e.g. Ornith layer 40 absent from engine turn-routing: suspected
    # fork off-by-one, reported; relative B0/C2/warm comparisons stay
    # fair, emitted ranks cover scored layers only).
    zero_layers = [l for l in range(n_layers)
                   if sum(heat[nm][l].total() for nm in names) == 0]
    if zero_layers:
        print(f"SCOPE: {len(zero_layers)}/{n_layers} trunk layers have zero "
              f"corpus heat and are unscored: {zero_layers}")
    saliency: dict = {}
    c1_pins = c1_cont = None
    if args.skip_c1:
        print("C1 DEFERRED; scoring B0/C2/warm only")
    else:
        with open(args.ean) as fh:
            overlay = json.load(fh)
        for key, val in overlay.get("saliency", {}).items():
            l_s, e_s = key.split(":")
            saliency[(int(l_s), int(e_s))] = float(val)
        print(f"EAN overlay: {len(saliency)} cells "
              f"(method={overlay.get('provenance', {}).get('method')})")
        c1_pins = ean_flat_pins(saliency, n_pins, n_layers)
        c1_cont = ean_flat_pins(saliency, n_cont, n_layers)

    rows = []
    for i, held in enumerate(names):
        train = [n for n in names if n != held]
        b0 = flat_pins(heat, train, n_pins, n_layers)
        b0_cont = flat_pins(heat, train, n_cont, n_layers)
        c2 = waterfill_pins(heat, train, total_slabs, n_layers)
        h_b0 = hit_rate(heat[held], b0, n_layers)
        h_b0_cont = hit_rate(heat[held], b0_cont, n_layers)
        h_c2 = hit_rate(heat[held], c2, n_layers)
        row = {"held": held, "h_b0": h_b0, "h_b0_cont": h_b0_cont,
               "h_c2": h_c2, "d_c2": h_c2 - h_b0}
        for nx in extra_ns:
            bx = flat_pins(heat, train, nx, n_layers)
            row[f"h_b0_N{nx}"] = hit_rate(heat[held], bx, n_layers)
        if not args.skip_c1:
            assert c1_pins is not None and c1_cont is not None
            h_c1 = hit_rate(heat[held], c1_pins, n_layers)
            h_c1_cont = hit_rate(heat[held], c1_cont, n_layers)
            row.update({"h_c1": h_c1, "h_c1_cont": h_c1_cont,
                        "d_c1": h_c1 - h_b0})
        rows.append(row)

    # Architect ruling 2731baf3 (2026-09-30): report tok/s alongside h@N.
    # These are routing-phase engine timings (observational). Whether
    # placement hits convert to throughput is a MECHANISM question this
    # sweep cannot answer (t0002: N=64->128 raised h 61%->79% with decode
    # flat ~2.2 tok/s). Stated explicitly: no throughput claim here.
    tps_d = sorted(p["tps_decode"] for p in probes if p.get("tps_decode"))
    tps_p = sorted(p["tps_prefill"] for p in probes if p.get("tps_prefill"))
    med_tps_d = statistics.median(tps_d) if tps_d else None
    med_tps_p = statistics.median(tps_p) if tps_p else None
    print(f"engine tok/s (routing phase, n={len(tps_d)}/{len(tps_p)}): "
          f"decode {med_tps_d if med_tps_d is None else round(med_tps_d, 2)} "
          f"prefill {med_tps_p if med_tps_p is None else round(med_tps_p, 2)} "
          f"(observational — hits->throughput NOT measured)")
    med_b0 = statistics.median(r["h_b0"] for r in rows)
    med_b0_cont = statistics.median(r["h_b0_cont"] for r in rows)
    print(f"B0 h@{n_pins} median = {med_b0:.4f} (uniform baseline "
          f"{uniform:.4f}, margin {med_b0 - uniform:+.4f})")
    print(f"B0 h@{n_cont} median = {med_b0_cont:.4f} (continuity)")
    if args.b0_gate_lo is not None and args.b0_gate_hi is not None:
        if not (args.b0_gate_lo <= med_b0_cont <= args.b0_gate_hi):
            print("STOP: B0 sanity gate FAILED — corpus or measurement is "
                  "off; candidates do not get scored.", file=sys.stderr)
            return 3
        print(f"B0 band gate [{args.b0_gate_lo}, {args.b0_gate_hi}]: PASS")
    else:
        if not med_b0 > uniform:
            print("STOP: B0 h@N median does not beat the uniform baseline — "
                  "measurement is broken; candidates do not get scored.",
                  file=sys.stderr)
            return 3
        print("B0 uniform-floor gate: PASS (median strictly above uniform)")

    res_c1 = None
    if not args.skip_c1:
        res_c1 = judge("C1 REAP", [r["d_c1"] for r in rows])
    else:
        print("C1 REAP: DEFERRED (no scoring)")
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
            warm = flat_pins(heat, prior, n_pins, n_layers)
            h_w = hit_rate(heat[cur], warm, n_layers)
            train = [n for n in names if n != cur]
            h_b = hit_rate(heat[cur], flat_pins(heat, train, n_pins, n_layers),
                           n_layers)
            h_2 = hit_rate(heat[cur],
                           waterfill_pins(heat, train, total_slabs, n_layers),
                           n_layers)
            wrow = {"turn": cur, "h_warm": h_w, "h_b0": h_b, "h_c2": h_2}
            if not args.skip_c1:
                assert c1_pins is not None
                wrow["h_c1"] = hit_rate(heat[cur], c1_pins, n_layers)
            warm_rows.append(wrow)
    if warm_rows:
        pairs = [("B0", "h_b0"), ("C2", "h_c2")]
        if not args.skip_c1:
            pairs.append(("C1", "h_c1"))
        for tag, key in pairs:
            ds = [r[key] - r["h_warm"] for r in warm_rows]
            print(f"warm-2+ {tag}-warm: median Dh={statistics.median(ds):+.4f} "
                  f"n={len(ds)} (secondary, not gating)")

    # ---- artifacts ----
    all_names = names
    full_heat = Counter()
    for nm in all_names:
        for layer in range(n_layers):
            for e, n in heat[nm][layer].items():
                full_heat[(layer, e)] += n
    b0_full = flat_pins(heat, all_names, n_pins, n_layers)
    c2_full = waterfill_pins(heat, all_names, total_slabs, n_layers)
    prov = {
        "model": args.model, "engine_id": args.engine_id,
        "generated_at": datetime.datetime.now(datetime.timezone.utc).strftime(
            "%Y-%m-%dT%H:%M:%SZ"),
        "corpus": {"kind": "57-trace v2+v4 replay, fresh capture",
                   "n_probes": len(names), "decode_only": True,
                   "n_gen": sorted({p['n_gen'] for p in probes})},
        "geometry": {"n_layers": n_layers, "n_experts": n_exp, "top_k": top_k,
                     "moe_rows": list(moe_rows)},
        "analysis": "sweep_analyze_790.py (pre-registered 2026-09-23; "
                    "multi-geometry args 2026-09-30, thresholds unchanged)",
        "gates": {"b0_band": [args.b0_gate_lo, args.b0_gate_hi],
                  "uniform_floor": uniform,
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
            "in_b0": e in b0_full[layer],
            "in_c2": e in c2_full[layer],
            "reap": saliency.get((layer, e)),
            "edge0": None,
        }
    with open(os.path.join(args.outdir, "experts-790.json"), "w") as fh:
        json.dump({"experts": experts, "provenance": prov}, fh, indent=1)
    layers = {}
    for layer in range(n_layers):
        agg = Counter()
        for nm in all_names:
            agg.update(heat[nm][layer])
        layers[str(layer)] = {"experts": [
            {"id": e, "heat": n, "reap_saliency": saliency.get((layer, e))}
            for e, n in agg.most_common()]}
    with open(os.path.join(args.outdir, "expert-ranks-790.json"), "w") as fh:
        json.dump({"version": 1, "engine_id": args.engine_id,
                   "model_hash": args.model, "provenance": prov,
                   "layers": layers}, fh, indent=1)
    with open(os.path.join(args.outdir, "tables.json"), "w") as fh:
        json.dump({"rows": rows, "c1": res_c1, "c2": res_c2,
                   "warm_rows": warm_rows,
                   "b0_hN_median": med_b0, "b0_hcont_median": med_b0_cont,
                   "uniform_baseline": uniform,
                   "tps_decode_median": med_tps_d,
                   "tps_prefill_median": med_tps_p}, fh, indent=1)
    print(f"wrote experts-790.json ({len(experts)} experts), "
          f"expert-ranks-790.json, tables.json -> {args.outdir}")
    c1v = "DEFERRED" if res_c1 is None else ("PASS" if res_c1["pass"] else "FAIL")
    print(f"VERDICT-790: C1 {c1v} / "
          f"C2 {'PASS' if res_c2['pass'] else 'FAIL'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
