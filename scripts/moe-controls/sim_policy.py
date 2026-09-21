#!/usr/bin/env python3
"""Offline replay of the MoE expert-cache policy on a GGML_CUDA_MOE_LEDGER trace (needs the ids columns).

Routing is policy-independent at temperature 0, so one trace can be replayed under any policy. Each layer group has its
own N-slot cache. Policies: kernel(hl) = the shipped plan kernel (validated against the recorded misses), lru,
gate(hl,T) = kernel victim rule but a miss installs only if its prior windowed sighting count >= T (else it is
"bypassed", i.e. served by another path), belady (mandatory install) and belady_bypass (optimal with bypass).

Usage: sim_policy.py <ledger.csv> [N=42] [--c-up 0.3003] [--c-cpu 0.105,0.19] [--warm 60] [--carry=1]
--carry=1 replays all requests as one stream per group (cache and frequency state survive request boundaries) instead of
a cold cache per request, which is what the engine does today. With repeated identical requests it is optimistic.
Exactness: residency-only policies (kernel, lru, belady mandatory) are exact replays; gate/bypass policies are approximate,
because a CPU-served expert can change accumulation order and so perturb later routing.
CSV row: t_ns,step,group,n_routes,n_unique,n_misses,n_evict,evict_freq_sum,mf0,mf1,mf2,mf3,miss_mask,id0,id1,...
"""
import sys, collections


def load(path):
    """-> list of requests; request = {group: [ (unique_ids, miss_mask, n_misses) per step ]}"""
    reqs, cur = [], None
    for line in open(path):
        if line.startswith('#'):
            cur = collections.defaultdict(list)
            reqs.append(cur)
            continue
        f = line.strip().split(',')
        if len(f) < 14 or cur is None:
            continue
        group, n_unique, n_misses, mask = int(f[2]), int(f[4]), int(f[5]), int(f[12])
        ids = [int(x) for x in f[13:13 + n_unique]]
        cur[group].append((int(f[1]), ids, mask, n_misses))
    for r in reqs:
        for g in r:
            r[g].sort(key=lambda x: x[0])
    return [r for r in reqs if r]


def eff(freq, ep, now):
    d = now - ep
    return freq >> d if d < 32 else 0


def sim_kernel(seq, n_slots, hl, gate_t=0, lru=False):
    """Exact plan-kernel policy for one group. Returns per-step (hits, installs, bypass, miss_ids)."""
    exp_for_slot = [-1] * n_slots
    last_used = [0] * n_slots
    slot_of = {}
    freq, fep = {}, {}
    clock = 0
    prev = None  # (installs[(expert,slot)], unique, epoch, clock_begin)
    out = []
    for si, (step, ids, _mask, _nm) in enumerate(seq):
        if prev is not None:
            installs, uniq, pep, cb = prev
            for e, s in installs:
                old = exp_for_slot[s]
                if old >= 0:
                    slot_of.pop(old, None)
                exp_for_slot[s] = e
                slot_of[e] = s
            for u, e in enumerate(uniq):
                if e in slot_of:
                    last_used[slot_of[e]] = cb + u + 1
                freq[e] = eff(freq.get(e, 0), fep.get(e, 0), pep) + 1
                fep[e] = pep
        epoch = si // hl
        routed = [False] * n_slots
        misses = []
        hits = 0
        for e in ids:
            if e in slot_of:
                routed[slot_of[e]] = True
                hits += 1
            else:
                misses.append(e)
        installs, bypass, miss_ids = [], 0, list(misses)
        for e in misses:
            if gate_t and eff(freq.get(e, 0), fep.get(e, 0), epoch) < gate_t:
                bypass += 1
                continue
            best, bs = None, -1
            for s in range(n_slots):
                if routed[s]:
                    continue
                res = exp_for_slot[s]
                fr = 0 if (res < 0 or lru) else eff(freq.get(res, 0), fep.get(res, 0), epoch)
                key = (fr, last_used[s], s)
                if best is None or key < best:
                    best, bs = key, s
            if bs < 0:
                bypass += 1
                continue
            routed[bs] = True
            installs.append((e, bs))
        out.append((hits, len(installs), bypass, miss_ids))
        prev = (installs, list(ids), epoch, clock)
        clock += 10
    return out


def sim_belady(seq, n_slots, bypass_ok):
    n = len(seq)
    nxt = {}  # expert -> sorted list of step indices
    for i, (_s, ids, _m, _n) in enumerate(seq):
        for e in ids:
            nxt.setdefault(e, []).append(i)
    ptr = {e: 0 for e in nxt}

    def next_use(e, i):
        lst, p = nxt[e], ptr[e]
        while p < len(lst) and lst[p] <= i:
            p += 1
        ptr[e] = p
        return lst[p] if p < len(lst) else 1 << 60

    resident = {}  # expert -> True
    out = []
    for i, (_s, ids, _m, _n) in enumerate(seq):
        hits = sum(1 for e in ids if e in resident)
        installs = bypass = 0
        cur = set(ids)
        for e in ids:
            if e in resident:
                continue
            if len(resident) < n_slots:
                resident[e] = True
                installs += 1
                continue
            cands = [r for r in resident if r not in cur]
            if not cands:
                bypass += 1
                continue
            v = max(cands, key=lambda r: next_use(r, i))
            if bypass_ok and next_use(e, i) >= next_use(v, i):
                bypass += 1
                continue
            del resident[v]
            resident[e] = True
            installs += 1
        out.append((hits, installs, bypass, []))
    return out


def summarize(per_group_out, warm):
    """-> (hits, installs, bypass) per token (mean over steps>=warm), summed over groups."""
    steps = max(len(v) for v in per_group_out.values())
    tot = [0, 0, 0]
    n = 0
    for s in range(warm, steps):
        for v in per_group_out.values():
            if s < len(v):
                tot[0] += v[s][0]; tot[1] += v[s][1]; tot[2] += v[s][2]
        n += 1
    return [x / max(n, 1) for x in tot]


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    opts = {a.split('=')[0]: a.split('=')[1] for a in sys.argv[1:] if a.startswith('--') and '=' in a}
    path = args[0]
    n_slots = int(args[1]) if len(args) > 1 else 42
    c_up = float(opts.get('--c-up', 0.3003))
    c_cpus = [float(x) for x in opts.get('--c-cpu', '0.105,0.19').split(',')]
    warm = int(opts.get('--warm', 60))
    reqs = load(path)
    if opts.get('--carry') == '1':
        merged = collections.defaultdict(list)
        for r in reqs:
            for g, seq in r.items():
                base = merged[g][-1][0] + 1 if merged[g] else 0
                merged[g].extend((base + i, ids, m, n) for i, (_st, ids, m, n) in enumerate(seq))
        reqs = [merged]
    print('requests', len(reqs), 'groups', [len(r) for r in reqs], 'steps', [max(len(v) for v in r.values()) for r in reqs])
    # validation: kernel hl=16 vs recorded misses, per request
    for k, r in enumerate(reqs):
        ok = tot = 0
        first_bad = None
        for g, seq in r.items():
            o = sim_kernel(seq, n_slots, 16)
            for si, (rec, sm) in enumerate(zip(seq, o)):
                tot += 1
                if len(sm[3]) == rec[3] and sum(1 << i for i, e in enumerate(rec[1]) if e in sm[3]) == rec[2]:
                    ok += 1
                elif first_bad is None:
                    first_bad = (g, si)
        print('validate req %d: kernel(hl=16) matches recorded miss set on %d/%d records (%.2f%%) first mismatch %s'
              % (k + 1, ok, tot, 100.0 * ok / max(tot, 1), first_bad))
    pols = [('kernel hl=16', lambda s: sim_kernel(s, n_slots, 16)),
            ('kernel hl=32', lambda s: sim_kernel(s, n_slots, 32)),
            ('kernel hl=64', lambda s: sim_kernel(s, n_slots, 64)),
            ('kernel hl=256', lambda s: sim_kernel(s, n_slots, 256)),
            ('lru', lambda s: sim_kernel(s, n_slots, 16, lru=True)),
            ('gate T=1 hl=16', lambda s: sim_kernel(s, n_slots, 16, gate_t=1)),
            ('gate T=2 hl=16', lambda s: sim_kernel(s, n_slots, 16, gate_t=2)),
            ('gate T=1 hl=64', lambda s: sim_kernel(s, n_slots, 64, gate_t=1)),
            ('gate T=2 hl=64', lambda s: sim_kernel(s, n_slots, 64, gate_t=2)),
            ('belady (mandatory install)', lambda s: sim_belady(s, n_slots, False)),
            ('belady + bypass (optimal)', lambda s: sim_belady(s, n_slots, True))]
    print('per token, steps >= %d, summed over groups; cost = up*%.4f + bypass*c_cpu ms' % (warm, c_up))
    hdr = '%-28s %8s %8s %8s %8s' % ('policy', 'hits', 'installs', 'bypass', 'M=i+b')
    for c in c_cpus:
        hdr += ' cost@%.3f' % c
    print(hdr)
    for name, fn in pols:
        agg = [0.0, 0.0, 0.0]
        for r in reqs:
            s = summarize({g: fn(seq) for g, seq in r.items()}, warm)
            agg = [a + b / len(reqs) for a, b in zip(agg, s)]
        line = '%-28s %8.1f %8.1f %8.1f %8.1f' % (name, agg[0], agg[1], agg[2], agg[1] + agg[2])
        for c in c_cpus:
            line += ' %9.1f' % (agg[1] * c_up + agg[2] * c)
        print(line)


if __name__ == '__main__':
    main()
