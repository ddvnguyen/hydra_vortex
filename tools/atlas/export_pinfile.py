#!/usr/bin/env python3
"""export_pinfile.py — expert-ranks.json -> in-tree pin-file format.

Format (llama-context.cpp:2334-2363 hydra_cpu_init; identical parser in
ggml-cuda.cu:1939-1970):
  - '#' lines and blank lines are skipped
  - each pin line: "L <il> <ids...>" — sscanf "L %d", il must be in [0, 256)
  - ids are ints after the second space, whitespace-separated
  - ORDER IS SIGNIFICANT: consumers treat the ids hot-first (the design doc
    §4 contract), so export preserves expert-ranks.json's hot-first order
  - line buffer is 8192 chars in hydra_cpu_init: 512 ids of <=4 chars + "L il"
    fits, but the exporter still wraps long layers into continuation lines
    "L <il> ..." is NOT re-readable mid-line; instead we emit one line per
    layer capped at --wrap ids per line, repeating the header (the parser
    appends: multiple lines for the same il concatenate their ids).

Example:
    # expert-ranks qwen38 (engine_id=qwen38, model=...)
    L 0 93 0 270 129 ...
    L 1 17 402 ...
"""
from __future__ import annotations

import argparse
import json


def layer_ids(ranks, il):
    return [e["id"] for e in ranks["layers"][str(il)]["experts"]]


def main(argv=None):
    ap = argparse.ArgumentParser(description="expert-ranks.json -> pin file")
    ap.add_argument("--ranks", default="expert-ranks.json")
    ap.add_argument("--out", default="experts.pin")
    ap.add_argument("--top-n", type=int, default=None,
                    help="export only the top-N hottest per layer (default all)")
    ap.add_argument("--wrap", type=int, default=500,
                    help="max ids per line (parser line buffer is 8192 chars)")
    args = ap.parse_args(argv)

    with open(args.ranks) as fh:
        ranks = json.load(fh)
    if ranks.get("engine_id") != "qwen38":
        # engine-id refusal discipline (route_trace.h): never mix engines
        raise SystemExit(f"refusing: ranks engine_id={ranks.get('engine_id')!r} "
                         f"is not qwen38")

    lines = [f"# expert-ranks pin export  engine_id={ranks['engine_id']}  "
             f"model={ranks.get('model_hash')}  version={ranks.get('version')}"]
    if ranks.get("provenance", {}).get("corpus", {}).get("quality") == "draft-grade":
        lines.append("# DRAFT-GRADE corpus (2-domain); re-export after multi-domain probes")
    n_layers = n_ids = 0
    for il in sorted(ranks["layers"], key=int):
        ids = layer_ids(ranks, il)
        if args.top_n is not None:
            ids = ids[: args.top_n]
        if not ids:
            continue
        n_layers += 1
        for chunk_start in range(0, len(ids), args.wrap):
            chunk = ids[chunk_start: chunk_start + args.wrap]
            lines.append("L " + str(il) + " " + " ".join(map(str, chunk)))
            n_ids += len(chunk)
    with open(args.out, "w") as fh:
        fh.write("\n".join(lines) + "\n")
    print(f"wrote {args.out}: {n_layers} layers, {n_ids} pin ids "
          f"(top-n={args.top_n or 'all'})")


if __name__ == "__main__":
    main()
