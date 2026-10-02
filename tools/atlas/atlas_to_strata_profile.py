#!/usr/bin/env python3
"""Convert an Atlas expert-ranks JSON into a Strata expert profile (STRP v1, 48 x 512).

Strata's `--expert-cache auto` fills VRAM slots in profile order, so the order IS the ranking under test.
Order written: every (layer, expert) the atlas saw, by global heat descending (ties: layer, expert), then every
unseen pair interleaved across layers (as tools/make_profile.py does). `--shuffle SEED` writes a same-membership
random control (identical file size, ranking destroyed). Format mirrors Strata tools/make_profile.py write_profile.
Usage: atlas_to_strata_profile.py RANKS.json OUT.bin [--shuffle SEED]
"""
import argparse, json, random, struct

N_LAYER, N_EXPERT, MAGIC, VERSION = 48, 512, b"STRP", 1

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ranks"); ap.add_argument("out"); ap.add_argument("--shuffle", type=int, default=None)
    a = ap.parse_args()
    d = json.load(open(a.ranks))
    heat = {(int(l), e["id"]): e["heat"] for l, v in d["layers"].items() for e in v["experts"]}
    seen = sorted(heat, key=lambda p: (-heat[p], p))
    seen_set = set(seen)
    missing = [(l, e) for e in range(N_EXPERT) for l in range(N_LAYER) if (l, e) not in seen_set]
    ranked = seen + missing
    if a.shuffle is not None:
        random.Random(a.shuffle).shuffle(ranked)
    assert len(ranked) == N_LAYER * N_EXPERT and len(set(ranked)) == len(ranked)
    table = [[-1] * N_EXPERT for _ in range(N_LAYER)]
    for slot, (l, e) in enumerate(ranked):
        table[l][e] = slot
    with open(a.out, "wb") as f:
        f.write(MAGIC + struct.pack("<5I", VERSION, N_LAYER, N_EXPERT, len(ranked), len(ranked)))
        for l, e in ranked:
            f.write(struct.pack("<HH", l, e))
        for l in range(N_LAYER):
            f.write(struct.pack("<%di" % N_EXPERT, *table[l]))
    print(f"{a.out}: {len(ranked)} pairs, {len(seen)} ranked by atlas heat, {len(missing)} appended"
          + (f", shuffled seed {a.shuffle}" if a.shuffle is not None else ""))

if __name__ == "__main__":
    main()
