#!/usr/bin/env python3
"""edge0_pilot_790.py — OFFLINE Edge0 pilot for #790 (CPU-only, no GPU, no server).

Uses the already-captured sidecars in /mnt/WorkDisk/atlas-790/sidecars/:
  1. LTR baseline per layer (capture-free definition, computed from the
     flushed topk streams): fraction of decode tokens whose topk set is a
     subset of the previous token's topk set, per layer. Exact and cheap.
  2. Timed single-layer linear-probe pilot (1 layer x 512 experts x LOPO
     folds) to measure per-fit cost and extrapolate the full
     48 layers x 512 experts x 57 folds budget.
  3. Evidence for the analyze_edge0.py n_experts=256 defect on this
     512-expert model: share of routed expert ids >= 256 in the streams.

Sidecar accumulation note (verified 2026-09-23): /capture/flush does not
clear the hidden-state buffer, so sidecar files after the first probe are
cumulative. --slices JSON maps sidecar file -> [start_row, n_rows] to
recover exact per-probe decode streams (append order, single slot).
"""
from __future__ import annotations

import argparse
import json
import os
import time

import numpy as np


def load_topk(sidecars_dir, slices, layer=None):
    """probe_name -> {layer: topk int array [T, k]} (sliced)."""
    out = {}
    for fn, (start, n) in slices.items():
        d = json.load(open(os.path.join(sidecars_dir, fn)))
        name = f"{d.get('category')}_{d.get('idx')}"
        layers = {}
        for ls, ld in d["layers"].items():
            if layer is not None and int(ls) != layer:
                continue
            t = np.array(ld["topk"], dtype=np.int32)[start:start + n]
            assert len(t) == n, (fn, ls, len(t), n)
            layers[int(ls)] = t
        out[name] = layers
    return out


def ltr_per_layer(streams):
    """streams: list of [T, k] arrays (one per probe). Returns per-layer LTR."""
    ltr = {}
    for layer in sorted(streams[0]):
        hits = tot = 0
        for s in (p[layer] for p in streams):
            for t in range(1, len(s)):
                tot += 1
                if set(s[t]).issubset(set(s[t - 1])):
                    hits += 1
        ltr[layer] = hits / tot if tot else 0.0
    return ltr


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="offline Edge0 pilot")
    ap.add_argument("--sidecars", required=True)
    ap.add_argument("--slices", required=True, help="JSON {file: [start, n]}")
    ap.add_argument("--pilot-layer", type=int, default=0)
    ap.add_argument("--seed", type=int, default=42)
    args = ap.parse_args(argv)

    slices = json.load(open(args.slices))
    topo = load_topk(args.sidecars, slices)
    probes = sorted(topo)
    print(f"pilot: {len(probes)} probes {probes}")

    streams0 = [topo[p] for p in probes]
    ltr = ltr_per_layer(streams0)
    vals = list(ltr.values())
    print(f"LTR: layers={len(vals)} mean={np.mean(vals):.4f} "
          f"min={min(vals):.4f} max={max(vals):.4f}")
    print("LTR per-layer (first 8):",
          " ".join(f"{l}:{ltr[l]:.3f}" for l in sorted(ltr)[:8]))

    # 256-bug evidence: routed ids >= 256 in the streams
    tot = ge = 0
    for p in probes:
        for _, t in topo[p].items():
            tot += t.size
            ge += (t >= 256).sum()
    print(f"routed ids >= 256: {ge}/{tot} = {ge / tot:.3f} "
          f"(analyze_edge0.py loops range(256): misses this mass)")

    # timed single-layer probe pilot: LOPO over probes, all 512 experts
    from sklearn.linear_model import LogisticRegression
    from sklearn.preprocessing import StandardScaler

    fn0 = next(iter(slices))
    d = json.load(open(os.path.join(args.sidecars, fn0)))
    print(f"loading hidden states layer {args.pilot_layer} "
          f"({len(slices)} files) ...")
    Xs, Ks = [], []
    for fn, (start, n) in slices.items():
        dd = json.load(open(os.path.join(args.sidecars, fn)))
        ld = dd["layers"][str(args.pilot_layer)]
        Xs.append(np.array(ld["hidden"], dtype=np.float32)[start:start + n])
        Ks.append(np.array(ld["topk"], dtype=np.int32)[start:start + n])
    X = np.vstack(Xs)
    K = np.vstack(Ks)
    pids = np.concatenate([np.full(len(x), i) for i, x in enumerate(Xs)])
    print(f"pilot design matrix: {X.shape}, topk {K.shape}, "
          f"prompts={len(Xs)}")
    n_exp = int(K.max()) + 1
    print(f"expert population in data: {n_exp} (model: 512)")

    t0 = time.time()
    n_fits = 0
    accs = []
    folds = sorted(set(pids.tolist()))
    for held in folds:
        tr, te = pids != held, pids == held
        Xt, Xe = X[tr], X[te]
        Kt, Ke = K[tr], K[te]
        scaler = StandardScaler().fit(Xt)
        Xts, Xes = scaler.transform(Xt), scaler.transform(Xe)
        for e in range(n_exp):
            yt = np.array([e in row for row in Kt], dtype=int)
            ye = np.array([e in row for row in Ke], dtype=int)
            if yt.sum() == 0 and ye.sum() == 0:
                continue
            if len(np.unique(yt)) < 2:
                # single-class train: no fit possible; majority vote.
                # (analyze_edge0.py lacks this guard and would raise here.)
                accs.append(float(np.mean(ye == yt[0])))
                continue
            clf = LogisticRegression(C=1.0, max_iter=1000, solver="lbfgs",
                                     random_state=args.seed)
            clf.fit(Xts, yt)
            accs.append(float(np.mean(clf.predict(Xes) == ye)))
            n_fits += 1
    dt = time.time() - t0
    print(f"pilot: {n_fits} fits in {dt:.1f}s "
          f"({dt / max(1, n_fits) * 1000:.0f} ms/fit), "
          f"mean binary acc={np.mean(accs):.4f}")
    full = 48 * 512 * 57
    print(f"extrapolated full Edge0 (48 layers x 512 experts x 57 folds = "
          f"{full} fits): {full * dt / max(1, n_fits) / 3600:.1f} CPU-hours "
          f"(single-thread equivalent)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
