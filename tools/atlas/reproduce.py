#!/usr/bin/env python3
"""reproduce.py — reproduction check against phase0_rank.json (track decision
d-bb2f0d3b29: exact reproduction of overlapping quantities).

Where phase0_rank.py already computes an overlapping quantity, this script
recomputes it with tools/atlas/trace_io.py and compares against the stored
phase0_rank.json:
  1. per-layer decode [P, P+256) selection counts (via layer_counts) -> the
     full_counts_top42 hot lists must match in ORDER (top-N sets and the
     "L <il> <ids hot-first>" pin order both depend on most_common order)
  2. self-heldout h@N (train=decode[P,P+50), eval=decode[P+100,P+200)) for
     N in (20, 30, 42) must match to machine precision
  3. train top-42 Jaccard coding-vs-general per layer
"""
from __future__ import annotations

import json
import sys

import trace_io

REF = "/tmp/opencode/phase0-rank/phase0_rank.json"
NS = (20, 30, 42)


def main():
    ref = json.load(open(REF))
    meta = ref["meta"]
    traces = [trace_io.load_trace(name, path)
              for name, path in sorted(meta["traces"].items())]
    trace_io.check_geometry(traces, expect_layers=meta["n_layers"])
    pos = {t.name: t.positions for t in traces}
    P = {t.name: t.prefill_end for t in traces}
    nl = meta["n_layers"]

    failures = 0

    # --- 1. h@N self-heldout ---
    print("== self-heldout h@N (train=decode[P,P+50), eval=decode[P+100,P+200)) ==")
    for t in traces:
        name = t.name
        p = P[name]
        tr_lo, tr_hi = p, p + 50
        ev_lo, ev_hi = p + 100, p + 200
        train_counts = trace_io.layer_counts(pos[name], nl, tr_lo, tr_hi)
        for n in NS:
            pins = trace_io.topn_pins(train_counts, n)
            h = trace_io.hit_rate_micro(pos[name], nl, ev_lo, ev_hi, pins)
            want = ref["self_heldout"][name]["h"][str(n)]
            ok = h == want
            failures += not ok
            print(f"  {name} h@{n}: ours={h!r} ref={want!r} "
                  f"{'OK' if ok else 'MISMATCH'}")

    # --- 2. full-decode per-layer top-42 order ---
    print("\n== full-decode [P, P+256) top-42 hot order per layer ==")
    bad = []
    for t in traces:
        name = t.name
        p = P[name]
        full_counts = trace_io.layer_counts(pos[name], nl, p, p + 256)
        ref42 = ref["self_heldout"][name]["full_counts_top42"]
        for l in range(nl):
            ours = [e for e, _ in full_counts[l].most_common(42)]
            if ours != ref42[str(l)]:
                bad.append((name, l, ours[:5], ref42[str(l)][:5]))
    if bad:
        failures += 1
        print(f"  MISMATCH on {len(bad)} (trace, layer) pairs, first 5:")
        for name, l, a, b in bad[:5]:
            print(f"    {name} L{l}: ours={a} ref={b}")
    else:
        print(f"  OK: {len(traces)} x {nl} layers, top-42 order identical")

    # --- 3. Jaccard train top-42 ---
    print("\n== train top-42 Jaccard coding-vs-general ==")
    tr_c = trace_io.layer_counts(pos["coding"], nl, P["coding"], P["coding"] + 50)
    tr_g = trace_io.layer_counts(pos["general"], nl, P["general"], P["general"] + 50)
    jac = []
    for l in range(nl):
        a = {e for e, _ in tr_c[l].most_common(42)}
        b = {e for e, _ in tr_g[l].most_common(42)}
        jac.append(len(a & b) / len(a | b))
    ok = jac == ref["jaccard_train_top42"]
    failures += not ok
    mean = sum(jac) / len(jac)
    ref_mean = sum(ref["jaccard_train_top42"]) / 48
    print(f"  mean ours={mean:.6f} ref={ref_mean:.6f} per-layer exact={'OK' if ok else 'MISMATCH'}")

    print(f"\nREPRODUCTION: {'PASS' if failures == 0 else f'FAIL ({failures})'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
