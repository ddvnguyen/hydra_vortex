import json
import numpy as np

L, E, B = 48, 512, 48 * 38          # fixed pin budget = 1824
dec = np.load('/tmp/opencode/prefill_poc_dec.npy')   # [T,L,E]
idx = json.load(open('/tmp/opencode/prefill_poc_index.json'))
T = len(idx)

def subject(n):
    b = n.split('/')[-1]
    for s in ('coding', 'reasoning', 'essay'):
        if b.startswith(s): return s
    return 'coding' if b.startswith('c') else 'general'
subs = [subject(t['name']) for t in idx]
tot = dec.sum(axis=0)

def mask_flat(train, n=38):
    m = np.zeros((L, E), bool)
    for l in range(L):
        m[l, np.argpartition(-train[l], n-1)[:n]] = True
    return m

def mask_greedy(train, budget=B):
    """Optimal per-layer N: globally rank all (layer,expert) by train freq.
    Valid because every layer sees identical route counts, and within-layer
    marginal gain is monotone decreasing."""
    flat = train.reshape(-1)
    keep = np.argpartition(-flat, budget-1)[:budget]
    m = np.zeros(L*E, bool); m[keep] = True
    return m.reshape(L, E)

def h(m, d): return (d*m).sum()/d.sum()

res = []
for i in range(T):
    d = dec[i]
    train_glob = tot - d
    same = [j for j in range(T) if subs[j]==subs[i] and j!=i]
    train_subj = dec[same].sum(axis=0)
    r = dict(name=idx[i]['name'], sub=subs[i],
             flat_g   = h(mask_flat(train_glob),   d),
             greedy_g = h(mask_greedy(train_glob), d),
             flat_s   = h(mask_flat(train_subj),   d),
             greedy_s = h(mask_greedy(train_subj), d),
             oracle   = h(mask_flat(d), d))
    res.append(r)

def S(x, a=0.545, OD=0.1298): return 1.0/(1.0 - a*x + OD)

print("=== PER-LAYER N (greedy, fit on train only) vs FLAT N=38 — budget fixed at 1824 pins ===\n")
print(f"{'arm':<26}{'median h':>10}{'mean':>9}{'S(med)':>9}")
for k, lab in (('flat_g','flat N=38, global'), ('greedy_g','per-layer N, global'),
               ('flat_s','flat N=38, subject'), ('greedy_s','per-layer N, subject'),
               ('oracle','oracle flat N=38')):
    v = np.array([r[k] for r in res])
    print(f"{lab:<26}{np.median(v):>10.4f}{v.mean():>9.4f}{S(np.median(v)):>9.3f}")

print("\n=== PAIRED greedy - flat (the thing that matters) ===")
for tr, lab in (('g','global'), ('s','subject')):
    d_ = np.array([r[f'greedy_{tr}'] - r[f'flat_{tr}'] for r in res])
    print(f"  {lab:<9} median {np.median(d_):+.4f}  mean {d_.mean():+.4f}  "
          f"positive {int((d_>0).sum())}/{T}  max {d_.max():+.4f}")

# what the allocation actually looks like
alloc = mask_greedy(tot).sum(axis=1)
print(f"\n=== greedy per-layer N (fit on ALL traces) ===")
print(f"  min {alloc.min()}  p10 {np.percentile(alloc,10):.0f}  median {np.median(alloc):.0f}  "
      f"p90 {np.percentile(alloc,90):.0f}  max {alloc.max()}  (flat would be 38)")
print(f"  layers with N<=20: {(alloc<=20).sum()}/48   N>=60: {(alloc>=60).sum()}/48")

# per-layer transfer heterogeneity, to check the leader's 6-29% claim shape
pl = []
for i in range(T):
    d = dec[i]; m = mask_flat(tot - d)
    pl.append([(d[l]*m[l]).sum()/d[l].sum() for l in range(L)])
pl = np.array(pl)
med = np.median(pl, axis=0)
print(f"\n=== per-layer LOSO transfer h (median over {T} traces) ===")
print(f"  min {med.min():.3f}  p10 {np.percentile(med,10):.3f}  median {np.median(med):.3f}  "
      f"p90 {np.percentile(med,90):.3f}  max {med.max():.3f}   chance=0.0742")
print(f"  layers below chance: {(med<0.0742).sum()}/48")
