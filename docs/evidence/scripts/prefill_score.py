import json
import numpy as np

L, E, N = 48, 512, 38
pre = np.load('/tmp/opencode/prefill_poc_pre.npy')   # [T,L,E]
dec = np.load('/tmp/opencode/prefill_poc_dec.npy')
idx = json.load(open('/tmp/opencode/prefill_poc_index.json'))
T = len(idx)

def topn_mask(c, n=N):
    m = np.zeros((L, E), dtype=bool)
    for l in range(L):
        m[l, np.argpartition(-c[l], n - 1)[:n]] = True
    return m

def h(mask, d):
    return (d * mask).sum() / d.sum()

def subject(name):
    b = name.split('/')[-1]
    if b.startswith('coding') or b.startswith('c'): return 'coding'
    if b.startswith('reasoning'):                   return 'reasoning'
    if b.startswith('essay'):                       return 'essay'
    return 'general'

subs = [subject(t['name']) for t in idx]
dec_tot = dec.sum(axis=0)

rows = []
for i, t in enumerate(idx):
    d = dec[i]
    h_pre = h(topn_mask(pre[i]), d)                       # runtime-realizable
    h_los = h(topn_mask(dec_tot - d), d)                  # deployable global table (LOSO)
    same  = [j for j in range(T) if subs[j] == subs[i] and j != i]
    h_sub = h(topn_mask(dec[same].sum(axis=0)), d) if same else float('nan')
    h_orc = h(topn_mask(d), d)                            # ceiling
    rows.append(dict(name=t['name'], P=t['P'], sub=subs[i],
                     prefill=h_pre, loso=h_los, subj=h_sub, oracle=h_orc))

def S(x, alpha=0.545, OD=0.1298):
    return 1.0 / (1.0 - alpha * x + OD)

print(f"{'trace':<24}{'P':>6} {'h_prefill':>10}{'h_global':>10}{'h_subj':>9}{'h_oracle':>10}   {'S(pre)':>7}")
print('-' * 84)
for r in sorted(rows, key=lambda r: (r['sub'], r['P'])):
    print(f"{r['name']:<24}{r['P']:>6} {r['prefill']:>10.4f}{r['loso']:>10.4f}"
          f"{r['subj']:>9.4f}{r['oracle']:>10.4f}   {S(r['prefill']):>7.3f}")

print('\n=== AGGREGATE (N=38) ===')
print(f"{'arm':<14}{'median':>9}{'mean':>9}{'min':>9}{'max':>9}{'S(median)':>11}")
for k in ('prefill', 'loso', 'subj', 'oracle'):
    v = np.array([r[k] for r in rows], dtype=float)
    v = v[~np.isnan(v)]
    print(f"{k:<14}{np.median(v):>9.4f}{v.mean():>9.4f}{v.min():>9.4f}{v.max():>9.4f}{S(np.median(v)):>11.3f}")

print('\n=== BY SUBJECT: median h_prefill vs h_global ===')
for s in sorted(set(subs)):
    p = np.array([r['prefill'] for r in rows if r['sub'] == s])
    g = np.array([r['loso']    for r in rows if r['sub'] == s])
    print(f"  {s:<10} n={len(p):<3} prefill {np.median(p):.4f}   global {np.median(g):.4f}   delta {np.median(p-g):+.4f}")

print('\n=== PAIRED prefill - global ===')
d_ = np.array([r['prefill'] - r['loso'] for r in rows])
print(f"  median {np.median(d_):+.4f}   mean {d_.mean():+.4f}   positive {int((d_>0).sum())}/{len(d_)}")

print('\n=== REFERENCE LINES ===')
print(f"  live-measured global static   h=0.1400   S={S(0.14):.3f}  (LOSES)")
print(f"  breakeven                     h=0.2382   S=1.000")
print(f"  SHIP (S>=1.10)                h=0.4050   S=1.100")

json.dump(rows, open('/tmp/opencode/prefill_poc_results.json', 'w'), indent=1)
