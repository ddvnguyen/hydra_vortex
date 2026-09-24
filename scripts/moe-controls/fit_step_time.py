#!/usr/bin/env python3
"""Within-run fit  step_ms = I + slope * misses  from a GGML_CUDA_MOE_LEDGER trace (needs the '#' request markers).

Per-step wall time is the gap between consecutive last-group (47) plan records; misses are summed over all groups of the
step. Step 0 (480 misses, cold fill) is excluded. Identifies the fixed cost I from the variation of misses inside one run,
so it does not depend on between-run confounders (link state, page cache, thermals).
Usage: fit_step_time.py <ledger.csv> [--last-group 47] [--min-step 4]
"""
import sys, collections


def load(path, last_group):
    reqs, cur = [], None
    for line in open(path):
        if line.startswith('#'):
            cur = {'t': {}, 'm': collections.defaultdict(int)}
            reqs.append(cur)
            continue
        f = line.split(',')
        if len(f) < 12 or cur is None:
            continue
        step, group = int(f[1]), int(f[2])
        cur['m'][step] += int(f[5])
        if group == last_group:
            cur['t'][step] = int(f[0])
    return [r for r in reqs if len(r['t']) > 10]


def fit(x, y):
    n = len(x)
    mx, my = sum(x) / n, sum(y) / n
    sxx = sum((a - mx) ** 2 for a in x)
    b = sum((a - mx) * (c - my) for a, c in zip(x, y)) / sxx
    a = my - b * mx
    res = [c - (a + b * d) for d, c in zip(x, y)]
    se = (sum(r * r for r in res) / (n - 2) / sxx) ** 0.5
    r2 = 1 - sum(r * r for r in res) / sum((c - my) ** 2 for c in y)
    return a, b, se, r2


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    opts = dict(a.split('=') for a in sys.argv[1:] if a.startswith('--') and '=' in a)
    last_group = int(opts.get('--last-group', 47))
    min_step = int(opts.get('--min-step', 4))
    for k, r in enumerate(load(args[0], last_group)):
        steps = [s for s in sorted(r['t']) if s >= max(min_step, 1) and s - 1 in r['t']]
        xs = [r['m'][s] for s in steps]
        ys = [(r['t'][s] - r['t'][s - 1]) / 1e6 for s in steps]
        a, b, se, r2 = fit(xs, ys)
        print('req %d n=%d misses/step %d-%d  I=%.1f ms  slope=%.3f +-%.3f ms/miss  R2=%.3f  X=1000/I=%.1f tok/s  mean misses %.1f mean ms %.0f'
              % (k + 1, len(xs), min(xs), max(xs), a, b, se, r2, 1000 / a, sum(xs) / len(xs), sum(ys) / len(ys)))


if __name__ == '__main__':
    main()
