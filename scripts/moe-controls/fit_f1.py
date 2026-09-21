#!/usr/bin/env python3
"""Compose the CPU-serve cost from moe_cpu_bench BENCH lines (sections 130-132). Usage: fit_f1.py f1f2.txt

Split execution calls the CPU once per layer (48/token) and serves j = M/48 experts per call, so j and M are one parameter:
    CPU ms/token = 48 * T(M/48),  T(j) = per-call time.   With T = K + w*j this is 48K + M*w.   B = 1000/(I + 48*T_e(M/48)*F2).
T(j) is taken two ways and the MORE CONSERVATIVE (lower B) governs: the K+w fit over the whole sweep, and the directly
measured per-call time interpolated at j = M/48. T_e is the bench T scaled to in-situ (c_meas = 0.099-0.116 ms/expert at
j=10, whole layers on CPU, static arms):  uniform = s*T with s = c_meas/c_bench(10);  K-absorbing = T + (10*c_meas - T(10)),
i.e. ALL of the engine-minus-bench gap is a fixed per-call cost (upper bound).
F2_pure = c(4, GPU decode running)/c(4, idle); F2_hybrid adds a 6 GB/s host-memory reader (only meaningful for a design that
still uploads). The pre-registered rule consumes F2_pure, worst cell I=43.2 M=190, c_meas at its upper bound.
"""
import re, sys, collections

SHELVE_B = 13.75
runs = collections.defaultdict(dict)
for line in open(sys.argv[1]):
    m = re.match(r'BENCH (\S+) j=(\d+) .*median_ms=([\d.]+)', line)
    if m:
        runs[m.group(1)][int(m.group(2))] = float(m.group(3))


def group(regex):
    labels = sorted(l for l in runs if re.fullmatch(regex, l))
    js = sorted(set.intersection(*[set(runs[l]) for l in labels])) if labels else []
    return labels, {j: sum(runs[l][j] for l in labels) / len(labels) for j in js}


def fit(T):
    js = sorted(T)
    mx, my = sum(js) / len(js), sum(T[j] for j in js) / len(js)
    w = sum((j - mx) * (T[j] - my) for j in js) / sum((j - mx) ** 2 for j in js)
    return my - w * mx, w


def interp(T, j):
    js = sorted(T)
    for a, b in zip(js, js[1:]):
        if a <= j <= b:
            return T[a] + (T[b] - T[a]) * (j - a) / (b - a)
    return T[js[0]] if j < js[0] else T[js[-1]]


groups = {n: group(r) for n, r in (('idle', r'idle\d'), ('busy', r'busy\d'), ('hog', r'busyhog\d'))}
if not groups['idle'][0] or not groups['busy'][0]:
    sys.exit('need idleN and busyN labels')
Ti, Tb = groups['idle'][1], groups['busy'][1]
Th = groups['hog'][1] if groups['hog'][0] else None

print('per-run fits (K, w scatter shows how well the split is identified; c(4) is the stable quantity)')
for l in sorted(runs):
    if len(runs[l]) >= 6:
        k, w = fit(runs[l])
        print('  %-12s K=%.3f w=%.4f  c4_meas=%.4f c10_meas=%.4f' % (l, k, w, runs[l][4] / 4, runs[l][10] / 10))

print('\npooled per-call T(j) ms (mean of run medians) and pooled-fit residual %')
Ki, wi = fit(Ti)
print('  j    idle    busy    hog    | F2_pure  F2_hybrid | idle fit resid%')
for j in sorted(Ti):
    print('  %-3d %6.4f  %6.4f  %6s | %6.3f   %6s   | %+5.1f' % (j, Ti[j], Tb[j], '%.4f' % Th[j] if Th else '-', Tb[j] / Ti[j],
          '%.3f' % (Th[j] / Ti[j]) if Th else '-', (Ti[j] - (Ki + wi * j)) / (Ki + wi * j) * 100))
F1_raw = (Ti[4] / 4) / (Ti[10] / 10)
F1_fit = (Ki / 4 + wi) / (Ki / 10 + wi)
print('\nidle: K=%.3f ms  w=%.4f ms   F1_fit=%.3f  F1_raw(c4/c10)=%.3f' % (Ki, wi, F1_fit, F1_raw))
Kb, wb = fit(Tb)
print('busy: K=%.3f ms  w=%.4f ms' % (Kb, wb))
if Th:
    Kh, wh = fit(Th)
    print('busy+hog: K=%.3f ms  w=%.4f ms' % (Kh, wh))
p0 = [l for l in runs if l == 'ctl-poll0']
if p0:
    Kp, wp = fit(runs['ctl-poll0'])
    print('control poll=0 (workers sleep): K=%.3f w=%.4f c4=%.4f  -> vs idle K=%.3f c4=%.4f' %
          (Kp, wp, runs['ctl-poll0'][4] / 4, Ki, Ti[4] / 4))
for l in ('ctl-sleepgap', 'ctl-gap1500'):
    if l in runs:
        k, w = fit(runs[l])
        print('control %-12s K=%.3f w=%.4f c4=%.4f c10=%.4f' % (l, k, w, runs[l][4] / 4, runs[l][10] / 10))

F2_pure = max(Tb[4] / Ti[4], 1.0)
F2_hyb = max(Th[4] / Ti[4], 1.0) if Th else float('nan')
print('\nF2_pure = %.3f (c(4) busy/idle)   F2_hybrid = %.3f   K share of a token: 48*K = %.1f ms (idle bench)' %
      (F2_pure, F2_hyb, 48 * Ki))

print('\nB = 1000/(I + 48*T_e(M/48)*F2_pure); rule cell = I=43.2, M=190, c_meas=0.116, more conservative of fit/raw')
cells = []
for cm in (0.099, 0.116):
    s = cm / (Ti[10] / 10)
    dk = 10 * cm - Ti[10]
    for method in ('uniform', 'K-absorbing'):
        for M in (190, 137):
            j = M / 48.0
            row = []
            for src in ('fit', 'raw'):
                t = (Ki + wi * j) if src == 'fit' else interp(Ti, j)
                te = s * t if method == 'uniform' else t + dk
                cpu = 48 * te * F2_pure
                row.append((src, cpu, [1000 / (I + cpu) for I in (43.2, 38.9)]))
            worst = min(row, key=lambda r: r[2][0])
            cells.append((cm, method, M, j, s, dk, row, worst))
            print(' c_meas=%.3f %-11s M=%d j=%.2f (s=%.2f dK=%.3f): ' % (cm, method, M, j, s, dk) +
                  ' | '.join('%s cpu=%.1f ms B(43.2)=%.1f B(38.9)=%.1f' % (r[0], r[1], r[2][0], r[2][1]) for r in row) +
                  '  => governs: %s %s' % (worst[0], 'PASS' if worst[2][0] > SHELVE_B else 'FAIL'))
rule = [c for c in cells if c[0] == 0.116 and c[1] == 'uniform' and c[2] == 190][0]
print('\nRULE CELL (uniform, c_meas 0.116, I=43.2, M=190, F2_pure): governing %s, cpu %.1f ms, B = %.2f -> %s (line %.2f)' %
      (rule[7][0], rule[7][1], rule[7][2][0], 'PASS' if rule[7][2][0] > SHELVE_B else 'FAIL', SHELVE_B))
ub = [c for c in cells if c[0] == 0.116 and c[1] == 'K-absorbing' and c[2] == 190][0]
print('UPPER-BOUND cell (K-absorbing, c_meas 0.116): governing %s, cpu %.1f ms, B = %.2f -> %s' %
      (ub[7][0], ub[7][1], ub[7][2][0], 'PASS' if ub[7][2][0] > SHELVE_B else 'FAIL'))
if Th:
    M, j = 190, 190 / 48.0
    mc = 0.9 * M
    jc = mc / 48.0
    cm = 0.116
    s = cm / (Ti[10] / 10)
    te = s * interp(Ti, jc)
    cpu = 48 * te * F2_hyb
    up = 0.1 * M * 0.3003
    print('INDICATIVE hybrid (90%% CPU-served, 10%% uploaded at c_up 0.3003, F2_hybrid %.3f, uniform c_meas 0.116, I=43.2): '
          'cpu %.1f + upload %.1f ms -> B = %.2f' % (F2_hyb, cpu, up, 1000 / (43.2 + cpu + up)))
