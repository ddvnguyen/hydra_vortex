import json, pathlib, sys
import numpy as np

L, E = 48, 512
N = 38                      # production operating point
ROOT = pathlib.Path('/tmp/opencode/trace-collect/traces')

def parse(route, P, ngen):
    """Return (prefill_counts, decode_counts) as [L,E] int arrays."""
    pre = np.zeros((L, E), dtype=np.int64)
    dec = np.zeros((L, E), dtype=np.int64)
    with open(route) as fh:
        for line in fh:
            f = line.split()
            if len(f) < 4:
                continue
            pos = int(f[1]); lay = int(f[2])
            if lay >= L:
                continue
            tgt = pre if pos < P else dec
            for tok in f[3:]:
                e = int(tok.split(':', 1)[0])
                if e < E:
                    tgt[lay, e] += 1
    return pre, dec

def topn(counts, n):
    """Boolean [L,E] mask of top-n experts per layer."""
    m = np.zeros((L, E), dtype=bool)
    for l in range(L):
        idx = np.argpartition(-counts[l], n - 1)[:n]
        m[l, idx] = True
    return m

def hit(mask, dec):
    tot = dec.sum()
    return (dec * mask).sum() / tot if tot else float('nan')

traces = []
for j in sorted(ROOT.rglob('*.json')):
    r = j.with_suffix('.route')
    if not r.exists():
        continue
    d = json.load(open(j))
    P, g = d.get('prefill_tokens'), d.get('n_gen_actual')
    if not P or not g:
        continue
    pre, dec = parse(r, P, g)
    if dec.sum() == 0:
        continue
    traces.append({
        'name': str(j.relative_to(ROOT)).replace('.json', ''),
        'P': P, 'pre': pre, 'dec': dec,
    })
    print(f"  parsed {traces[-1]['name']:<26} P={P:<5} decode_routes={dec.sum()}", file=sys.stderr)

json.dump({'n': len(traces)}, open('/tmp/opencode/prefill_poc_meta.json', 'w'))
np.save('/tmp/opencode/prefill_poc_pre.npy', np.stack([t['pre'] for t in traces]))
np.save('/tmp/opencode/prefill_poc_dec.npy', np.stack([t['dec'] for t in traces]))
json.dump([{'name': t['name'], 'P': t['P']} for t in traces],
          open('/tmp/opencode/prefill_poc_index.json', 'w'), indent=1)
print(f"\nparsed {len(traces)} traces -> cached", file=sys.stderr)
