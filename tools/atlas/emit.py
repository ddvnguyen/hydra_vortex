#!/usr/bin/env python3
"""emit.py — write the two Stage-C atlas artifacts (design doc §4, owner ruling
2026-09-17: two tiers, two separated files).

1. experts.json — observability tier (Colibri dashboard shape + spec/reliability):
     {"categories": [...],
      "experts": {"<realLayer>:<expert>": {"affinity": {...}, "entropy": H,
                  "top": cat, "label": "specialist: <top>" | "generalist",
                  "spec": s, "reliability": "f/R"}}}
   plus provenance block {model, engine_id, commit, probe_set, generated_at}.

2. expert-ranks.json — ranking tier for the future placement-prior consumer:
     {"version", "engine_id", "model_hash", "provenance",
      "layers": {"<il>": {"experts": [{"id", "heat", "p": {cat: score}, ...}
                                     ordered hot-first]}}}
   `reap_saliency` is null until a REAP overlay is merged (--reap-overlay):
   live EAN snapshots (fork HYDRA_EAN_STATS, prong A) or the offline observer
   (tools/atlas/reap_observer.py, prong B) both feed the same slot —
   gate x output-norm means over selected decode tokens (design doc §9).

Both files carry the mandatory provenance block (#1078 lesson: the shipped
GLM atlas had undocumented corpus provenance).
"""
from __future__ import annotations

import argparse
import datetime
import json

import trace_io

ENGINE_ID = "qwen38"  # owner-designated engine id for this qwen4exp model


def load_full(path):
    with open(path) as fh:
        return json.load(fh)


def provenance(full):
    runs = full["runs"]
    return {
        "model": runs[0]["sidecar"].get("model_shard1", "unknown"),
        "engine_id": ENGINE_ID,
        "commit": runs[0]["sidecar"].get("fork_commit", "unknown"),
        "probe_set": [r["name"] for r in runs],
        "generated_at": datetime.datetime.now(datetime.timezone.utc)
                        .strftime("%Y-%m-%dT%H:%M:%SZ"),
        "corpus": {
            "kind": "cnre-phase0 route traces",
            "quality": "draft-grade" if len({r["category"] for r in runs}) < 3
                       else "multi-domain",
            "decode_only": full["decode_only"],
            "min_runs_gate": full["min_runs"],
            "runs": [{"name": r["name"], "category": r["category"],
                      "path": r["path"], "n_gen": r["n_gen"],
                      "prefill_end": r["prefill_end"]} for r in runs],
        },
    }


def emit_experts(full, prov):
    cats, atlas = full["categories"], full["atlas"]
    experts = {}
    for r in atlas:
        aff = {c: v for c, v in r["p"].items() if v > 0}
        H = -sum(v * _log2(v) for v in aff.values())
        experts[f"{r['layer']}:{r['expert']}"] = {
            "affinity": aff,
            "entropy": round(H, 2) or 0.0,
            "top": r["top_topic"],
            "label": f"specialist: {r['top_topic']}" if r["spec"] >= 0.5 else "generalist",
            "spec": r["spec"],
            "reliability": r["reliability"],
        }
    doc = {"categories": cats, "experts": experts, "provenance": prov}
    return doc


def load_overlay(path):
    """REAP overlay from either prong (live EAN via reap_observer from-ean,
    or offline observer output): {"saliency": {"L:E": s}, "provenance": {...}}
    with mandatory model/engine_id/commit/probe_set provenance."""
    with open(path) as fh:
        doc = json.load(fh)
    sal = doc.get("saliency")
    if not isinstance(sal, dict):
        raise ValueError(f"{path}: overlay needs a 'saliency' object")
    parsed = {}
    for key, val in sal.items():
        try:
            layer_text, expert_text = key.split(":")
            parsed[(int(layer_text), int(expert_text))] = float(val)
        except (ValueError, AttributeError) as error:
            raise ValueError(f"{path}: bad saliency key {key!r}") from error
    prov = doc.get("provenance") or {}
    for field in ("model", "engine_id", "commit", "probe_set"):
        if field not in prov:
            raise ValueError(f"{path}: overlay provenance lacks {field!r}")
    if not isinstance(prov["probe_set"], list):
        raise ValueError(f"{path}: overlay provenance probe_set must be a list")
    return parsed, prov


def emit_ranks(full, prov, top_n=None, reap=None):
    """Per-layer ordered hot-first lists for the placement-prior consumer.

    Population: ALL specialists that fired at that layer in the decode
    window (the raw heat ranking, independent of the observability tier's
    min_count/replication filters). heat = raw decode selection count;
    p = base-rate-corrected affinity for the subset
    that passed the observability filter, {} for heat-only entries.
    Ordering hot-first with the most_common tie semantics of
    phase0_rank.py, so the exported pin order agrees with the phase0
    full-decode top-N lists.
    reap = (saliency, overlay_provenance) from load_overlay(), or None
    (reap_saliency stays null — never fabricated).
    """
    cats, atlas = full["categories"], full["atlas"]
    p_by_key = {(r["layer"], r["expert"]): r["p"] for r in atlas}
    sal, reap_prov = reap if reap else ({}, None)
    out_layers = {}
    for il, pairs in sorted(full["rank_heat"].items(), key=lambda kv: int(kv[0])):
        rows = pairs if top_n is None else pairs[:top_n]
        out_layers[il] = {"experts": [
            {"id": eid, "heat": heat,
             "p": p_by_key.get((int(il), eid), {}),
             "reap_saliency": sal.get((int(il), eid))}
            for eid, heat in rows]}
    if reap_prov is not None:
        prov = dict(prov)
        prov["reap"] = {
            "method": reap_prov.get("method", "unknown"),
            "model": reap_prov["model"],
            "engine_id": reap_prov["engine_id"],
            "commit": reap_prov["commit"],
            "probe_set": reap_prov["probe_set"],
            "decode_only": reap_prov.get("decode_only", True),
            "mtp_excluded": reap_prov.get("mtp_excluded", True),
            "cells": sum(1 for v in sal.values() if v is not None),
        }
    return {"version": 1, "engine_id": ENGINE_ID,
            "model_hash": prov["model"], "provenance": prov,
            "layers": out_layers}


def _log2(v):
    import math
    return math.log2(v)


def main(argv=None):
    ap = argparse.ArgumentParser(description="emit atlas artifacts")
    ap.add_argument("--full", default="experts.json.atlas-full.json",
                    help="intermediate from analyze.py")
    ap.add_argument("--experts-out", default="experts.json")
    ap.add_argument("--ranks-out", default="expert-ranks.json")
    ap.add_argument("--top-n", type=int, default=None,
                    help="cap experts per layer in expert-ranks.json (default all)")
    ap.add_argument("--reap-overlay", default=None,
                    help="REAP overlay JSON (reap_observer output): fills "
                         "reap_saliency where present, null elsewhere")
    args = ap.parse_args(argv)

    full = load_full(args.full)
    prov = provenance(full)
    reap = load_overlay(args.reap_overlay) if args.reap_overlay else None
    experts_doc = emit_experts(full, prov)
    ranks_doc = emit_ranks(full, prov, top_n=args.top_n, reap=reap)
    with open(args.experts_out, "w") as fh:
        json.dump(experts_doc, fh, indent=1)
    with open(args.ranks_out, "w") as fh:
        json.dump(ranks_doc, fh, indent=1)
    n = len(experts_doc["experts"])
    nl = len(ranks_doc["layers"])
    print(f"wrote {args.experts_out} ({n} experts, {len(experts_doc['categories'])} categories)")
    print(f"wrote {args.ranks_out} ({nl} layers)")
    print(f"corpus quality: {prov['corpus']['quality']} "
          f"(categories: {', '.join(prov['probe_set'])})")


if __name__ == "__main__":
    main()
