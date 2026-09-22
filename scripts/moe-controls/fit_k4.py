#!/usr/bin/env python3
"""Fit D(k) = ms per layer moved GPU->CPU from the static timing arms (design note section 20). TIMING ONLY for k != 10.
Usage: fit_k4.py <out_dir_with_pass*/results-k4-timing.jsonl> [--I 43.2] [--F2 1.057]
Governing estimator: pooled OLS slope of decode ms/token vs layers-on-CPU (44/46/48), warm reps 2.. of every boot."""
import glob, json, math, sys, collections

LAYERS = {"ncm4": 44, "ncm2": 46, "allhost": 48}


def ols(xs, ys):
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx
    icpt = my - slope * mx
    rss = sum((y - icpt - slope * x) ** 2 for x, y in zip(xs, ys))
    se = math.sqrt(rss / (n - 2) / sxx) if n > 2 else float("nan")
    return slope, se, icpt


def main():
    out = sys.argv[1]
    opts = {sys.argv[i]: float(sys.argv[i + 1]) for i in range(2, len(sys.argv) - 1, 2)}
    I, F2 = opts.get("--I", 43.2), opts.get("--F2", 1.057)
    pts = collections.defaultdict(list)  # (k, pass) -> [(layers, ms_per_token, arm, boot)]
    for f in sorted(glob.glob(f"{out}/pass*/results-k4-timing.jsonl")):
        ps = f.split("/")[-2]
        for line in open(f):
            r = json.loads(line)
            if r.get("void"):
                print("VOID", ps, r.get("tag"), r["void"])
                continue
            for rep in r["reps"]:
                if rep["rep"] < 2 or rep.get("void") or not rep.get("predicted_n"):
                    continue
                pts[(r["k_used"], ps)].append((LAYERS[r["arm"]], rep["decode_ms_server"] / rep["predicted_n"], r["arm"], r["tag"]))
    res = {}
    for k in sorted({k for k, _ in pts}):
        allp = [p for (kk, _), v in pts.items() if kk == k for p in v]
        print(f"\n== k={k}{'  (TIMING ONLY, k override)' if k != 10 else ''} ==")
        for arm in ("ncm4", "ncm2", "allhost"):
            v = [p[1] for p in allp if p[2] == arm]
            if v:
                m = sum(v) / len(v)
                sd = math.sqrt(sum((x - m) ** 2 for x in v) / (len(v) - 1)) if len(v) > 1 else float("nan")
                print(f"  {arm:8s} n={len(v)} mean {m:7.3f} ms/token  sd {sd:.3f} ({100 * sd / m:.2f}%)  = {1000 / m:.3f} tok/s")
        s, se, ic = ols([p[0] for p in allp], [p[1] for p in allp])
        print(f"  pooled D(k) = {s:.4f} +- {se:.4f} (1 SE) ms/layer   intercept@48 layers {ic + 48 * s:.2f} ms")
        res[k] = (s, se)
        for ps in sorted({p for kk, p in pts if kk == k}):
            v = pts[(k, ps)]
            if len({x[0] for x in v}) >= 2:
                s2, se2, _ = ols([p[0] for p in v], [p[1] for p in v])
                print(f"    {ps}: D = {s2:.4f} +- {se2:.4f}")
        means = {arm: sum(p[1] for p in allp if p[2] == arm) / max(1, sum(1 for p in allp if p[2] == arm)) for arm in LAYERS}
        if all(any(p[2] == a for p in allp) for a in LAYERS):
            print(f"    pairwise ncm4->ncm2 {(means['ncm2'] - means['ncm4']) / 2:.4f}   ncm2->allhost {(means['allhost'] - means['ncm2']) / 2:.4f}"
                  f"   ncm4->allhost {(means['allhost'] - means['ncm4']) / 4:.4f}")
    if 4 in res:
        d4, se4 = res[4]
        thr = (1000 / 13.75 - I) / (48 * F2)
        for name, d in (("point", d4), ("D-1SE", d4 - se4), ("D+1SE", d4 + se4)):
            print(f"B_worst[{name}] = {1000 / (I + 48 * d * F2):.2f} tok/s   (D={d:.4f}, threshold D >= {thr:.4f} -> shelve)")
        print("BRANCH:", "SHELVE (B_worst <= 13.75)" if 1000 / (I + 48 * d4 * F2) <= 13.75 else "BOUND SURVIVES (B_worst > 13.75), not build authorisation")
    if 4 in res and 10 in res:
        d4, d10 = res[4][0], res[10][0]
        w = (d10 - d4) / 6.0
        K = d4 - 4 * w
        print(f"K_e = {K:.4f} ms/call  w_e = {w:.4f} ms/expert   48*K_e = {48 * K:.2f} ms/token   (bench K 0.081, w 0.0769)")


main()
