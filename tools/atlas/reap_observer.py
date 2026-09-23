#!/usr/bin/env python3
"""reap_observer.py — offline REAP saliency observer, prong B of #787 S-C3.

REAP (CerebrasResearch/reap, arXiv:2510.13999) saliency per MoE layer/expert:

    S_j = mean over tokens where j is top-k-selected of ( g_j(x) * ||f_j(x)||_2 )

g_j = renormalized router gate weight, f_j = UNWEIGHTED expert output
(SwiGLU MLP). The live engine computes the same quantity via the env-gated
EAN path (HYDRA_EAN_STATS, fork Stage C); this script is the offline twin:
it fills the same `reap_saliency` schema slot from recorded snapshots or
from a NumPy expert-MLP forward when weights + hidden states are available.

Subcommands (CPU-only, no engine, no GPU):

  inventory --gguf PATH
    Validate the qwen4exp arch map (the REAP MODEL_ATTRS-equivalent: tensor
    name templates for gate/up/down + shared-expert exclusion) against a real
    GGUF. Reports per-MoE-layer presence, shapes, and quants. No weights are
    read (metadata only). Exit nonzero on any map miss.
  synthetic --out OVERLAY [--seed N --layers L --experts E --topk K --toks T]
    Self-test: build a tiny random MoE, run calibration tokens through the
    exact SwiGLU expert math, verify the observer against an independent
    brute-force reference (asserts bit-exact agreement), and emit a schema
    overlay. Provenance is stamped method=synthetic-validation — NEVER merge
    this into production ranks; it exercises the schema path only.
  from-ean --ean SNAP --out OVERLAY --commit C [--probe-set P ...]
    Convert a live EAN snapshot (GET /experts "ean" section, saved to SNAP)
    into a schema overlay with full provenance. This is the production path
    for live data; the observer math is identical (mean of g*norm over
    selected decode tokens, MTP/draft excluded at the source).

Overlay format (consumed by emit.py --reap-overlay):
  {"saliency": {"<layer>:<expert>": float},
   "provenance": {"method", "model", "engine_id", "commit", "probe_set",
                  "decode_only": true, "mtp_excluded": true, ...}}
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys

import numpy as np

# ---- qwen4exp arch map (REAP MODEL_ATTRS-equivalent, GGUF namespace) ----
# Routed-expert projections per layer il; shared-expert (*_shexp*) tensors
# are dense (always execute) and NEVER enter the saliency population.
ARCH = "qwen4exp"
T_GATE_T = "blk.{il}.ffn_gate_exps.weight"
T_UP_T = "blk.{il}.ffn_up_exps.weight"
T_DOWN_T = "blk.{il}.ffn_down_exps.weight"
ROUTER_T = "blk.{il}.ffn_gate_inp.weight"
SHARED_PREFIXES = ("ffn_up_shexp", "ffn_gate_shexp", "ffn_down_shexp",
                   "ffn_gate_inp_shexp")
FFN_ACT = "silu"      # SwiGLU: silu(x@G) * (x@U) @ D
ROUTER_NORM = True    # norm_w: top-k gates renormalized to sum 1


def _utcnow() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---- observer math (NumPy; mirrors the engine EAN path) ----

def silu(x: np.ndarray) -> np.ndarray:
    return x / (1.0 + np.exp(-x))


def softmax(x: np.ndarray) -> np.ndarray:
    e = np.exp(x - x.max(axis=-1, keepdims=True))
    return e / e.sum(axis=-1, keepdims=True)


def expert_forward(h: np.ndarray, G: np.ndarray, U: np.ndarray,
                   D: np.ndarray) -> np.ndarray:
    """Unweighted expert output f_j(x) for one token. h: [d], G/U: [d, ff],
    D: [ff, d]."""
    return (silu(h @ G) * (h @ U)) @ D


def observe_layer(hiddens: np.ndarray, W_router: np.ndarray,
                  G: np.ndarray, U: np.ndarray, D: np.ndarray,
                  topk: int, decode_mask: np.ndarray | None = None,
                  renorm: bool = ROUTER_NORM) -> dict[int, tuple[float, int]]:
    """REAP saliency accumulation for one MoE layer.

    hiddens: [T, d] layer-input states; W_router: [d, E]; G/U: [E, d, ff]
    (per-expert), D: [E, ff, d]. Returns {expert: (gxn_sum, n_sel)} over
    decode tokens only (prefill excluded, #175 confound discipline).
    """
    T, d = hiddens.shape
    E = G.shape[0]
    mask = np.ones(T, dtype=bool) if decode_mask is None else decode_mask
    acc: dict[int, list] = {}
    logits = hiddens @ W_router                      # [T, E]
    probs = softmax(logits)
    top_idx = np.argpartition(-probs, topk - 1, axis=1)[:, :topk]  # [T, K]
    for t in range(T):
        if not mask[t]:
            continue
        sel = top_idx[t]
        g = probs[t, sel]
        if renorm:
            g = g / g.sum()
        h = hiddens[t]
        for s, j in enumerate(int(x) for x in sel):
            f = expert_forward(h, G[j], U[j], D[j])
            acc.setdefault(j, [0.0, 0])[0] += float(g[s]) * float(np.linalg.norm(f))
            acc[j][1] += 1
    return {j: (v[0], v[1]) for j, v in acc.items()}


def saliency_mean(acc: dict[int, tuple[float, int]]) -> dict[int, float]:
    return {j: s / n for j, (s, n) in acc.items() if n > 0}


def brute_force(hiddens, W_router, G, U, D, topk, decode_mask) -> dict[int, float]:
    """Independent reference: explicit per-token records, then average."""
    T = hiddens.shape[0]
    recs: dict[int, list[float]] = {}
    for t in range(T):
        if not decode_mask[t]:
            continue
        p = softmax(hiddens[t] @ W_router)
        sel = np.argsort(-p)[:topk]
        g = p[sel] / p[sel].sum()
        for s, j in enumerate(int(x) for x in sel):
            f = (silu(hiddens[t] @ G[j]) * (hiddens[t] @ U[j])) @ D[j]
            recs.setdefault(j, []).append(float(g[s]) * float(np.sqrt((f * f).sum())))
    return {j: sum(v) / len(v) for j, v in recs.items()}


# ---- subcommand: inventory ----

def cmd_inventory(args) -> int:
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))), "..", "src", "llama-cpp", "gguf-py"))
    import gguf  # noqa: E402  (fork-vendored reader, metadata only)

    reader = gguf.GGUFReader(args.gguf)
    names = {t.name for t in reader.tensors}
    by_name = {t.name: t for t in reader.tensors}
    # MoE layers = layers carrying routed gate experts (shared-expert-only
    # layers are dense and excluded, same rule as the engine geometry).
    ils = sorted({int(n.split(".")[1]) for n in names
                  if ".ffn_gate_exps.weight" in n})
    report = {"arch": ARCH, "gguf": os.path.basename(args.gguf),
              "tensor_templates": {"gate": T_GATE_T, "up": T_UP_T,
                                   "down": T_DOWN_T, "router": ROUTER_T},
              "n_moe_layers": len(ils), "layers": [], "mismatches": []}
    ok = True
    for il in ils:
        row = {"layer": il, "tensors": {}}
        for role, tmpl in (("gate", T_GATE_T), ("up", T_UP_T),
                           ("down", T_DOWN_T), ("router", ROUTER_T)):
            nm = tmpl.format(il=il)
            t = by_name.get(nm)
            if t is None:
                row["tensors"][role] = None
                report["mismatches"].append(nm)
                ok = False
            else:
                row["tensors"][role] = {
                    "shape": [int(x) for x in t.shape],
                    "quant": str(t.tensor_type).split(".")[-1],
                }
        report["layers"].append(row)
    # shared-expert quarantine check: no *_shexp* tensor may match a routed slot
    routed = {T_GATE_T.format(il=il) for il in ils} | \
             {T_UP_T.format(il=il) for il in ils} | \
             {T_DOWN_T.format(il=il) for il in ils}
    leak = [n for n in names if "shexp" in n and n in routed]
    report["shared_expert_leak"] = leak
    if leak:
        ok = False
    # expert-count agreement: trailing dim of gate/up/down (E) must equal
    # the router's trailing dim. Non-expert dims legitimately differ
    # (gate/up [ff, d, E], down [d, ff, E], router [d, E]).
    for row in report["layers"]:
        tt = row["tensors"]
        if all(tt[r] for r in ("gate", "up", "down", "router")):
            counts = {r: tt[r]["shape"][-1] for r in ("gate", "up", "down", "router")}
            if len(set(counts.values())) > 1:
                report["mismatches"].append(
                    f"layer {row['layer']}: expert counts {counts}")
                ok = False
            row["n_experts"] = counts["gate"]
    print(json.dumps(report, indent=1))
    if not ok:
        print("INVENTORY FAIL: arch map mismatches listed above", file=sys.stderr)
        return 1
    print(f"INVENTORY OK: {len(ils)} MoE layers, gate/up/down/router present, "
          f"no shared-expert leak", file=sys.stderr)
    return 0


# ---- subcommand: synthetic ----

def cmd_synthetic(args) -> int:
    rng = np.random.default_rng(args.seed)
    L, E, K, T, d, ff = (args.layers, args.experts, args.topk, args.toks,
                         args.dim, args.ff)
    n_prefill = T // 3  # first third = prefill (must be excluded)
    layers = []
    for _ in range(L):
        layers.append({
            "W_router": rng.normal(0, 0.1, (d, E)),
            "G": rng.normal(0, 0.1, (E, d, ff)),
            "U": rng.normal(0, 0.1, (E, d, ff)),
            "D": rng.normal(0, 0.1, (E, ff, d)),
            "h": rng.normal(0, 1.0, (T, d)),
        })
    decode_mask = np.zeros(T, dtype=bool)
    decode_mask[n_prefill:] = True
    saliency: dict[str, float] = {}
    for il, lay in enumerate(layers):
        acc = observe_layer(lay["h"], lay["W_router"], lay["G"], lay["U"],
                            lay["D"], K, decode_mask)
        ref = brute_force(lay["h"], lay["W_router"], lay["G"], lay["U"],
                          lay["D"], K, decode_mask)
        got = saliency_mean(acc)
        assert set(got) == set(ref), f"layer {il}: population mismatch"
        for j in ref:
            assert abs(got[j] - ref[j]) < 1e-9, \
                f"layer {il} expert {j}: {got[j]} != {ref[j]}"
        for j, s in got.items():
            saliency[f"{il}:{j}"] = s
    # decode-only proof on a prefill-only window: must be empty
    empty = observe_layer(layers[0]["h"], layers[0]["W_router"], layers[0]["G"],
                          layers[0]["U"], layers[0]["D"], K,
                          np.zeros(T, dtype=bool))
    assert empty == {}, "empty decode window must yield no saliency"
    overlay = {
        "saliency": {k: round(v, 6) for k, v in sorted(saliency.items())},
        "provenance": {
            "method": "synthetic-validation",
            "model": f"synthetic-moe(L={L},E={E},K={K})",
            "engine_id": "numpy-observer",
            "commit": "n/a",
            "probe_set": [f"synthetic-seed-{args.seed}"],
            "decode_only": True,
            "mtp_excluded": True,
            "generated_at": _utcnow(),
            "warning": ("VALIDATION ONLY — random weights, not a model. "
                        "Never merge into production expert-ranks."),
        },
    }
    with open(args.out, "w") as fh:
        json.dump(overlay, fh, indent=1)
    print(f"synthetic OK: {len(saliency)} saliencies, observer == brute force; "
          f"wrote {args.out}", file=sys.stderr)
    return 0


# ---- subcommand: from-ean ----

def cmd_from_ean(args) -> int:
    with open(args.ean) as fh:
        snap = json.load(fh)
    ean = snap.get("ean")
    if not ean or not ean.get("saliency"):
        print(f"{args.ean}: no counted EAN saliency (run with HYDRA_EAN_STATS "
              f"set and HYDRA_EXPERT_META=1, generate decode tokens first)",
              file=sys.stderr)
        return 1
    overlay = {
        "saliency": {k: float(v) for k, v in ean["saliency"].items()},
        "provenance": {
            "method": "live-EAN",
            "model": ean.get("model", "unknown"),
            "engine_id": ean.get("engine_id", "unknown"),
            "commit": args.commit,
            "probe_set": args.probe_set,
            "decode_only": True,
            "mtp_excluded": True,
            "source_cells": ean.get("cells"),
            "generated_at": _utcnow(),
        },
    }
    with open(args.out, "w") as fh:
        json.dump(overlay, fh, indent=1)
    print(f"from-ean OK: {len(overlay['saliency'])} saliencies; wrote {args.out}",
          file=sys.stderr)
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="offline REAP saliency observer")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p_inv = sub.add_parser("inventory", help="validate arch map vs a GGUF")
    p_inv.add_argument("--gguf", required=True)
    p_inv.set_defaults(fn=cmd_inventory)
    p_syn = sub.add_parser("synthetic", help="self-test on a random MoE")
    p_syn.add_argument("--out", required=True)
    p_syn.add_argument("--seed", type=int, default=787)
    p_syn.add_argument("--layers", type=int, default=4)
    p_syn.add_argument("--experts", type=int, default=16)
    p_syn.add_argument("--topk", type=int, default=4)
    p_syn.add_argument("--toks", type=int, default=24)
    p_syn.add_argument("--dim", type=int, default=32)
    p_syn.add_argument("--ff", type=int, default=48)
    p_syn.set_defaults(fn=cmd_synthetic)
    p_ean = sub.add_parser("from-ean", help="EAN snapshot -> overlay")
    p_ean.add_argument("--ean", required=True)
    p_ean.add_argument("--out", required=True)
    p_ean.add_argument("--commit", required=True)
    p_ean.add_argument("--probe-set", nargs="+", default=[])
    p_ean.set_defaults(fn=cmd_from_ean)
    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    raise SystemExit(main())
