import json
import numpy as np

L, E = 48, 512
MIB_PER_EXPERT = 2.148
dec = np.load('/tmp/opencode/prefill_poc_dec.npy')
idx = json.load(open('/tmp/opencode/prefill_poc_index.json'))
T = len(idx)

def subject(n):
    b = n.split('/')[-1]
    for s in ('coding','reasoning','essay'):
        if b.startswith(s): return s
    return 'coding' if b.startswith('c') else 'general'
subs = [subject(t['name']) for t in idx]
tot = dec.sum(axis=0)

def topn(c, n):
    m = np.zeros((L,E), bool)
    for l in range(L): m[l, np.argpartition(-c[l], n-1)[:n]] = True
    return m
def h(m,d): return (d*m).sum()/d.sum()
def S(x, OD): return 1.0/(1.0 - 0.545*x + OD)

print(f"{'N':>5}{'VRAM MiB':>10} | {'h_glob':>7}{'h_subj':>7}{'h_orac':>7} | "
      f"{'S_glob':>7}{'S_subj':>7}{'S_orac':>7} | {'S_orac':>7}")
print(f"{'':>5}{'':>10} | {'':>7}{'':>7}{'':>7} | {'--- O/D=0.1298 ---':>23} | {'O/D=0':>7}")
print('-'*82)
for N in (19, 28, 38, 43, 57, 76, 96, 128, 160, 192, 256):
    hg, hs, ho = [], [], []
    for i in range(T):
        d = dec[i]
        hg.append(h(topn(tot-d, N), d))
        same = [j for j in range(T) if subs[j]==subs[i] and j!=i]
        hs.append(h(topn(dec[same].sum(axis=0), N), d))
        ho.append(h(topn(d, N), d))
    hg, hs, ho = np.median(hg), np.median(hs), np.median(ho)
    vram = N*L*MIB_PER_EXPERT
    print(f"{N:>5}{vram:>10.0f} | {hg:>7.4f}{hs:>7.4f}{ho:>7.4f} | "
          f"{S(hg,.1298):>7.3f}{S(hs,.1298):>7.3f}{S(ho,.1298):>7.3f} | {S(ho,0):>7.3f}")

print("\nVRAM budget: GPU0 free @64K = 4441 MiB -> N_max = %d  (N=42 measured unsafe, 111 MiB margin)"
      % int(4441/(L*MIB_PER_EXPERT)))
print("SHIP line S>=1.10 needs h>=0.405 at O/D=0.1298;  h>=0.183 at O/D=0")
