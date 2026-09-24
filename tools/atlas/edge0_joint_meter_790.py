#!/usr/bin/env python3
"""edge0_joint_meter_790.py — Ruling-2 joint-solver meter (CPU-only).

Pre-registered predictor (unchanged): per (layer, fold), per-expert one-vs-rest
LogisticRegression(lbfgs, C, max_iter=1000) on StandardScaler-normalized
router-input hidden states, LOPO by prompt. n_experts inferred from data;
single-class-train folds score the majority vote (authorized fixes).

Solver change ONLY (per Architect Ruling 2, 2026-09-24): instead of 512 cold
fits per (layer, fold), fit ONE full-data OvR reference per layer, then run
each fold's 512 binaries warm-started (warm_start=True, init from the
reference coef_) to the same optimum (convex problem, same tol) — far fewer
lbfgs iterations per fit. Change the solver, not the predictor.

Pre-committed decision rule (fixed 2026-09-24, before any full-scale result):
project the metered per-(layer,fold) cost to 48 layers; report ALL 48 layers
if the projection is <= 24 wall-hours on the available cores, else report
EXACTLY layers {0,6,12,18,24,30,36,42,47}. No other subsets.

CPU policy: caller pins with taskset (cores 18-19 while any rig/P100 lane
runs; never during a measurement arm).

Outputs: timing table + accuracy-identity check (warm vs cold max abs acc
delta) + scope verdict. Small-scale validation (5 surviving sidecars) runs
anytime; full-scale metering needs the 57-probe sidecar corpus.
"""
from __future__ import annotations

import argparse
import json
import os
import time

import numpy as np
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler


def load_layer(sidecars_dir, files, layer):
    Xs, Ks = [], []
    for fn, (start, n) in files.items():
        dd = json.load(open(os.path.join(sidecars_dir, fn)))
        ld = dd["layers"][str(layer)]
        Xs.append(np.array(ld["hidden"], dtype=np.float32)[start:start + n])
        Ks.append(np.array(ld["topk"], dtype=np.int32)[start:start + n])
    return Xs, Ks


def ovr_matrix(Ks, n_exp):
    Y = []
    for K in Ks:
        m = np.zeros((len(K), n_exp), dtype=np.int8)
        for t, row in enumerate(K):
            m[t, row] = 1
        Y.append(m)
    return Y


def fit_binary(Xt, yt, seed, C, init=None):
    clf = LogisticRegression(C=C, max_iter=1000, solver="lbfgs",
                             random_state=seed,
                             warm_start=init is not None)
    if init is not None:
        clf.coef_ = init[0].copy()
        clf.intercept_ = init[1].copy()
        clf.classes_ = np.array([0, 1])
    clf.fit(Xt, yt)
    return clf


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Ruling-2 joint-solver meter")
    ap.add_argument("--sidecars", required=True)
    ap.add_argument("--slices", required=True, help="JSON {file: [start, n]}")
    ap.add_argument("--layer", type=int, default=0)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--C", type=float, default=1.0)
    ap.add_argument("--cores", type=int, default=2,
                    help="cores available (for the 24h projection)")
    args = ap.parse_args(argv)

    files = json.load(open(args.slices))
    Xs, Ks = load_layer(args.sidecars, files, args.layer)
    X = np.vstack(Xs)
    K = np.vstack(Ks)
    pids = np.concatenate([np.full(len(x), i) for i, x in enumerate(Xs)])
    n_exp = int(K.max()) + 1
    Y = ovr_matrix(Xs, n_exp)
    Yall = np.vstack(Y)
    folds = sorted(set(pids.tolist()))
    print(f"meter: layer={args.layer} tokens={len(X)} dim={X.shape[1]} "
          f"experts={n_exp} folds={len(folds)}")

    # full-data reference (cold), one binary per expert
    scaler0 = StandardScaler().fit(X)
    Xs0 = scaler0.transform(X)
    t0 = time.time()
    ref = []
    for e in range(n_exp):
        yt = Yall[:, e]
        if len(np.unique(yt)) < 2:
            ref.append(None)
            continue
        ref.append(fit_binary(Xs0, yt, args.seed, args.C))
    t_ref = time.time() - t0
    print(f"reference: {sum(r is not None for r in ref)} cold fits, "
          f"{t_ref:.1f}s")

    # per-fold: cold loop (status quo) vs warm-started joint (ruling 2)
    cold_t = warm_t = 0.0
    max_acc_delta = 0.0
    fold_times = []
    for held in folds:
        tr, te = pids != held, pids == held
        Xt, Xe = X[tr], X[te]
        Yt, Ye = Yall[tr], Yall[te]
        scaler = StandardScaler().fit(Xt)
        Xts, Xes = scaler.transform(Xt), scaler.transform(Xe)
        # cold
        t0 = time.time()
        cold_acc = []
        for e in range(n_exp):
            yt, ye = Yt[:, e], Ye[:, e]
            if yt.sum() == 0 and ye.sum() == 0:
                continue
            if len(np.unique(yt)) < 2:
                cold_acc.append(float(np.mean(ye == yt[0])))
                continue
            c = fit_binary(Xts, yt, args.seed, args.C)
            cold_acc.append(float(np.mean(c.predict(Xes) == ye)))
        cold_t += time.time() - t0
        # warm (init from reference)
        t0 = time.time()
        warm_acc = []
        for e, r in enumerate(ref):
            yt, ye = Yt[:, e], Ye[:, e]
            if yt.sum() == 0 and ye.sum() == 0:
                continue
            if len(np.unique(yt)) < 2 or r is None:
                warm_acc.append(float(np.mean(ye == (yt[0] if len(yt) else 0))))
                continue
            c = fit_binary(Xts, yt, args.seed, args.C,
                           init=(r.coef_, r.intercept_))
            warm_acc.append(float(np.mean(c.predict(Xes) == ye)))
        dt = time.time() - t0
        warm_t += dt
        fold_times.append(dt)
        max_acc_delta = max(max_acc_delta,
                            float(np.max(np.abs(np.array(cold_acc) -
                                                     np.array(warm_acc)))))
    print(f"cold loop total: {cold_t:.1f}s | warm joint total: {warm_t:.1f}s "
          f"| speedup: {cold_t / max(warm_t, 1e-9):.2f}x")
    print(f"accuracy identity: max abs acc delta (warm-cold) = "
          f"{max_acc_delta:.2e} (expect ~0: same optima)")
    per_lf = (t_ref / max(1, len(folds)) + warm_t / max(1, len(folds)))
    proj_cpu_h = per_lf * 48 * len(folds) / 3600
    proj_wall_h = proj_cpu_h / max(1, args.cores)
    print(f"projection (this scale): {proj_cpu_h:.1f} CPU-h, "
          f"{proj_wall_h:.1f} wall-h on {args.cores} cores")
    scope = ("ALL 48 layers" if proj_wall_h <= 24
             else "layers {0,6,12,18,24,30,36,42,47}")
    print(f"SCOPE RULE (<=24 wall-h -> all): {scope}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
