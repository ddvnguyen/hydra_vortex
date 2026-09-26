#!/usr/bin/env python3
"""Expert Atlas analysis — offline port of Colibri c/tools/expert_atlas/analyze.py.

Methodology (verbatim from the Colibri reference, #175):
  - Routing selections inside one run are heavily autocorrelated: the same
    context routes to the same experts token after token. An expert firing N
    times in one prompt is ONE effective observation.
  - So specialisation is only claimed when it REPLICATES across the
    independent runs (prompts) of a category:
      * per-category mean share across runs (a single hot run cannot carry it)
      * replication gate: the expert must fire in >= min_runs runs of its
        top category (Colibri default 2/3)
  - base-rate correction: p(c|e) = mean_share_c(e) / sum_c' mean_share_c'(e)
  - spec = 1 - H(p)/log(C)  (C = number of categories)
  - reliability = fired_runs(top) / runs_of(top)

  - Input is route-trace `.route` files, not `.coli_usage` stats txt; the
    per-(layer, expert) run counts come from tools/atlas/trace_io.py.
  - Decode-only is the default analysis window (design doc §2: prefill
    routing is topic-generic; decode-only sharpened every affinity number).
  - The draft corpus has only 2 categories with ONE run each, so the
    replication gate (>=2 runs) would drop everything; the pipeline gate is
    parameterized and set OFF (min_runs=1) for this draft. Draft-grade.
"""
from __future__ import annotations

from collections import Counter, defaultdict


import argparse
import json
import math
import sys
from collections import defaultdict

import trace_io


# ---- analysis core (pure; shared with validate.py) ----

def run_counts(traces, decode_only=True):
    """run[(cat, idx)][(layer, expert)] = count, run_tot[(cat, idx)] = total.

    Counts are aggregated across all 48 layers per run — matching the
    Colibri reference, which keys its stats by (layer, expert) but computes
    share/entropy over the whole expert population, not per layer.
    """
    run, run_tot = defaultdict(dict), {}
    for t in traces:
        lo, hi = t.window(decode_only)
        agg = defaultdict(int)
        for layer, counter in trace_io.layer_counts(t.positions, t.n_layers, lo, hi).items():
            for e, n in counter.items():
                agg[(layer, e)] = n
        run[(t.category, t.index)] = dict(agg)
        run_tot[(t.category, t.index)] = sum(agg.values())
    return run, run_tot


def build_atlas(run, run_tot, *, min_count=30, min_runs=1):
    """Per-expert affinity table. Port of Colibri analyze.py main loop."""
    cats = sorted({c for c, _ in run})
    runs_of = {c: sorted(i for cc, i in run if cc == c) for c in cats}
    C = len(cats)
    if C < 2:
        raise SystemExit("need >= 2 categories for spec = 1 - H/log C")

    experts = {k for d in run.values() for k in d}
    atlas, dropped_sparse, dropped_unrepl = [], 0, 0
    for key in experts:
        total = sum(run[r].get(key, 0) for r in run)
        if total < min_count:
            dropped_sparse += 1
            continue
        mean_share, fired_runs = {}, {}
        for c in cats:
            shares, fired = [], 0
            for i in runs_of[c]:
                n = run[(c, i)].get(key, 0)
                shares.append(n / max(1, run_tot[(c, i)]))
                if n > 0:
                    fired += 1
            mean_share[c] = sum(shares) / len(shares)
            fired_runs[c] = fired
        s = sum(mean_share.values())
        if s <= 0:
            continue
        p = {c: mean_share[c] / s for c in cats}
        top = max(cats, key=lambda c: p[c])
        if fired_runs[top] < min_runs:
            dropped_unrepl += 1
            continue
        H = -sum(v * math.log(v) for v in p.values() if v > 0)
        atlas.append({
            "layer": key[0], "expert": key[1], "total": total,
            "spec": round(1.0 - H / math.log(C), 4),
            "top_topic": top,
            "top_lift": round(p[top] * C, 2),
            "reliability": f"{fired_runs[top]}/{len(runs_of[top])}",
            "p": {c: round(p[c], 4) for c in cats},
        })
    atlas.sort(key=lambda r: (-r["spec"], -r["total"]))
    return {"categories": cats, "runs_of": runs_of, "atlas": atlas,
            "dropped_sparse": dropped_sparse, "dropped_unrepl": dropped_unrepl}


# ---- CLI ----

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--traces", nargs="+", required=True,
                    help=".route files as name=path or path (category = basename)")
    ap.add_argument("--min-count", type=int, default=30)
    ap.add_argument("--min-runs", type=int, default=1,
                    help="replication gate: fire in >= this many runs of the top "
                         "category. Colibri default is 2; the draft corpus has one "
                         "run per category so the pipeline default is 1 (gate off).")
    ap.add_argument("--include-prefill", action="store_true",
                    help="include prefill positions (default decode-only)")
    ap.add_argument("--out", default="experts.json")
    ap.add_argument("--ranks", default="",
                    help="(unused, kept for compat) ranking artifacts are "
                         "emitted by emit.py as expert-ranks-<engine_id>.json")
    ap.add_argument("--report", default="")
    args = ap.parse_args(argv)

    traces = []
    for spec in args.traces:
        if "=" in spec:
            name, path = spec.split("=", 1)
        else:
            path = spec
            name = trace_io_name(path)
        traces.append(trace_io.load_trace(name, path))
    trace_io.check_geometry(traces)
    decode_only = not args.include_prefill

    run, run_tot = run_counts(traces, decode_only)
    res = build_atlas(run, run_tot, min_count=args.min_count, min_runs=args.min_runs)

    # Ranking tier heat: raw per-layer decode selection counts over ALL
    # traces (summed in run order), independently of the observability-tier
    # min_count filter. Ordered hottest-first with the same most_common
    # semantics phase0_rank.py uses, so hot order agrees with the phase0
    # full-decode top-N lists (reproduce.py verifies).
    rank_heat = {l: Counter() for l in range(traces[0].n_layers)}
    for t in traces:
        lo, hi = t.window(decode_only)
        for l, counter in trace_io.layer_counts(t.positions, t.n_layers, lo, hi).items():
            rank_heat[l].update(counter)
    rank_layers = {l: counter.most_common() for l, counter in rank_heat.items()}
    cats, atlas = res["categories"], res["atlas"]

    # ---- console report (port of the Colibri printout) ----
    print(f"categories ({len(cats)}): " +
          ", ".join(f"{c}[{len(res['runs_of'][c])}]" for c in cats))
    print(f"experts seen: {len({k for d in run.values() for k in d}):,}")
    print(f"dropped {res['dropped_sparse']:,} sparse (<{args.min_count} sel)")
    print(f"dropped {res['dropped_unrepl']:,} UNREPLICATED (fired in <{args.min_runs} runs of their top topic)")
    print(f"kept    {len(atlas):,} experts")

    print("\n=== most specialised ===")
    print(f"{'layer':>5} {'exp':>4} {'sel':>6} {'spec':>6} {'lift':>6} {'repl':>5}  topic")
    for r in atlas[:20]:
        print(f"{r['layer']:>5} {r['expert']:>4} {r['total']:>6} {r['spec']:>6.3f} "
              f"{r['top_lift']:>6.2f} {r['reliability']:>5}  {r['top_topic']}")

    by_layer = defaultdict(list)
    for r in atlas:
        by_layer[r["layer"]].append(r["spec"])
    ls = sorted(by_layer)
    print("\n=== specialisation vs layer depth ===")
    for L in ls[::max(1, len(ls) // 13)]:
        v = by_layer[L]
        print(f"  layer {L:>3}  n={len(v):>4}  spec {sum(v)/len(v):.3f}")

    own = defaultdict(int)
    for r in atlas:
        own[r["top_topic"]] += 1
    print("\n=== experts owned per category ===")
    for c in sorted(own, key=lambda x: -own[x]):
        print(f"  {c:<14} {own[c]:>5}")

    strong = [r for r in atlas if r["spec"] >= 0.5]
    print(f"\nstrong specialists (spec >= 0.5): {len(strong):,} / {len(atlas):,} "
          f"({100*len(strong)/max(1, len(atlas)):.1f}%)")

    # ---- emit.py consumers ----
    payload = {
        "decode_only": decode_only,
        "min_count": args.min_count,
        "min_runs": args.min_runs,
        "runs": [{"name": t.name, "category": t.category, "index": t.index,
                  "path": t.path, "prefill_end": t.prefill_end,
                  "n_gen": t.n_gen, "sidecar": t.sidecar} for t in traces],
        "rank_heat": {str(l): pairs for l, pairs in rank_layers.items()},
        **res,
    }
    with open(args.out + ".atlas-full.json", "w") as fh:
        json.dump(payload, fh, indent=1)
    print(f"\nwrote {args.out}.atlas-full.json (intermediate for emit.py/validate.py)")


def trace_io_name(path):
    base = path.rsplit("/", 1)[-1]
    for suffix in (".route", ".route.json"):
        if base.endswith(suffix):
            base = base[: -len(suffix)]
            break
    return base or path


if __name__ == "__main__":
    main()
