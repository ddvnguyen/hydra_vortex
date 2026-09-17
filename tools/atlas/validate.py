#!/usr/bin/env python3
"""LOP validation — offline port of Colibri c/tools/expert_atlas/validate.py.

Leave-one-run-out: for every category c and every held-out run h of c:
  1. build c's top-K specialist set from c's OTHER runs only (h excluded)
  2. do the same for all other categories (all their runs — they never saw h)
  3. on the held-out run, measure what share of routing selections land in
     each set
  4. the atlas works if c's own set wins on h. Chance = 1/C.

Draft-corpus caveat: with 2 categories and 1 run each, leave-one-run-out
leaves ZERO training runs for the held-out category — the protocol is not
executable as designed. This script therefore also reports the degenerate
"hold-nothing" version (sets built from all runs, evaluated on the same
runs) as a pipeline-correctness check only. With 2 categories the expected
separation is weak; the point is that the machinery is correct, not the
labels. Final numbers need the multi-domain probe corpus (design §7.3).
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict

import trace_io


def specialists(run, run_tot, cats, idxs, cat, exclude, K):
    """Top-K experts by base-rate-corrected affinity for `cat`, computed
    WITHOUT the excluded run (port of Colibri validate.py specialists)."""
    share = defaultdict(lambda: defaultdict(float))
    for c in cats:
        used = [i for i in idxs[c] if not (c == cat and i == exclude)]
        for i in used:
            for k, n in run[(c, i)].items():
                share[k][c] += n / max(1, run_tot[(c, i)]) / len(used)
    scored = []
    for k, per in share.items():
        s = sum(per.values())
        if s <= 0:
            continue
        if per[cat] > 0:
            scored.append((per[cat] / s, k))
    scored.sort(reverse=True)
    return {k for _, k in scored[:K]}


def lop(run, run_tot, K, allow_degenerate=False):
    cats = sorted({c for c, _ in run})
    idxs = {c: sorted(i for cc, i in run if cc == c) for c in cats}
    C = len(cats)
    mode = "leave-one-run-out"
    trials = []
    executable = all(len(idxs[c]) >= 2 for c in cats)
    if not executable:
        if not allow_degenerate:
            print(f"WARNING: {C} categories with run counts "
                  f"{ {c: len(idxs[c]) for c in cats} }; leave-one-run-out is not "
                  f"executable (held-out category has no training runs left).")
        mode = "hold-nothing (degenerate; pipeline check only)"
    for c in cats:
        for h in idxs[c]:
            exclude = h if executable else None
            if exclude is None and not allow_degenerate and not executable:
                continue
            sets = {cc: specialists(run, run_tot, cats, idxs, cc,
                                    exclude if cc == c else None, K)
                    for cc in cats}
            held = run[(c, h)]
            htot = max(1, run_tot[(c, h)])
            scores = {cc: sum(held.get(k, 0) for k in sets[cc]) / htot for cc in cats}
            win = max(scores, key=scores.get)
            own = 100 * scores[c]
            others = [v for cc, v in scores.items() if cc != c]
            best_other = 100 * max(others) if others else float("nan")
            trials.append({"category": c, "held": h, "mode": mode,
                           "own_pct": own, "best_other_pct": best_other,
                           "win": win, "hit": win == c,
                           "margins": {cc: round(100 * v, 2) for cc, v in scores.items()}})
    return {"mode": mode, "K": K, "categories": cats, "chance_pct": 100.0 / C,
            "trials": trials,
            "accuracy_pct": round(100 * sum(t["hit"] for t in trials)
                                  / max(1, len(trials)), 1) if trials else None}


def main(argv=None):
    ap = argparse.ArgumentParser(description="LOP validation of the Expert Atlas")
    ap.add_argument("--full", default="experts.json.atlas-full.json",
                    help="intermediate from analyze.py (reuses its run counts)")
    ap.add_argument("--k", type=int, default=200, help="specialists per category")
    ap.add_argument("--out", default="")
    args = ap.parse_args(argv)

    with open(args.full) as fh:
        full = json.load(fh)
    cats = full["categories"]
    # rebuild run counts from the trace paths recorded by analyze.py
    traces = [trace_io.load_trace(r["name"], r["path"], r["category"], r["index"])
              for r in full["runs"]]
    trace_io.check_geometry(traces)
    run, run_tot = analyze_counts(traces, full["decode_only"])

    print(f"LOP validation, {len(cats)} categories ({', '.join(cats)}), "
          f"top-{args.k} specialists per category")
    res = lop(run, run_tot, args.k, allow_degenerate=True)
    print(f"mode: {res['mode']}   chance = {res['chance_pct']:.1f}%\n")
    for t in res["trials"]:
        print(f"  {t['category']:<12} run {t['held']}  own-set {t['own_pct']:5.2f}%  "
              f"best-other {t['best_other_pct']:5.2f}%  -> "
              f"{'HIT ' if t['hit'] else 'MISS'} (predicted {t['win']})")
    print(f"\naccuracy: {sum(t['hit'] for t in res['trials'])}/{len(res['trials'])} "
          f"= {res['accuracy_pct']}%   (chance {res['chance_pct']:.1f}%)")
    if res["mode"].startswith("hold-nothing"):
        print("CAVEAT: degenerate protocol on the 2-run draft corpus — these numbers "
              "are in-sample pipeline checks, NOT generalisation claims. Expected "
              "separation with 2 categories is weak; final labels need the "
              "multi-domain probe corpus.")
    if args.out:
        with open(args.out, "w") as fh:
            json.dump(res, fh, indent=1)
        print(f"wrote {args.out}")


def analyze_counts(traces, decode_only):
    import analyze as analyze_mod
    return analyze_mod.run_counts(traces, decode_only)


if __name__ == "__main__":
    main()
