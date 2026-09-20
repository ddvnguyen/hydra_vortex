"""trace_io — parse llama.cpp route-trace `.route` files for the Expert Atlas.

Line format (one row per (position, routed layer)):

    <call> <pos> <layer> <expert>:<gate> ...

`pos` is a global position counter: 0..P-1 are prefill (prompt) tokens, P..
are decode tokens. P = `prefill_tokens` from the per-trace sidecar
(`<trace>.route.json`); per cnre-phase0/TRACE_SPEC.md the prefill/decode
split is positional and P must be exact. `gate` is the `1` placeholder and
is ignored.

Geometry contract (QWEN4EXP_PROFILE.md): 48 routed layers, 512 experts,
top-10, layers 0..n_layers-1 present at every position, no duplicate
(pos, layer) row, single top-K across rows. The parser asserts all of it.

This module is self-contained on purpose (the Atlas pipeline must not
depend on /tmp scratch), but its counting/ordering semantics are a faithful
port of cnre-phase0 `scripts/trace_lib.py` + `phase0_rank.py` so that
overlapping quantities reproduce `phase0_rank.json` exactly (most_common
tie order included: count desc, then first-encounter order).
"""
from __future__ import annotations

import json
import os
from collections import Counter, defaultdict
from dataclasses import dataclass


class TraceFormatError(ValueError):
    pass


@dataclass
class Trace:
    name: str          # short id, e.g. "coding"
    category: str      # atlas category / domain (same as name for the draft corpus)
    index: int         # run index within the category (0-based)
    path: str
    sidecar: dict
    positions: dict    # pos -> {layer -> [expert ids]}
    n_layers: int
    topk: int
    prefill_end: int   # P: first decode position

    @property
    def decode_lo(self) -> int:
        return self.prefill_end

    @property
    def decode_hi(self) -> int:
        return max(self.positions) + 1

    @property
    def n_gen(self) -> int:
        return self.decode_hi - self.decode_lo

    def window(self, decode_only: bool) -> tuple[int, int]:
        """Analysis window. Decode-only is the Atlas default (design doc §2:
        prefill routing is topic-generic; decode-only sharpens affinity)."""
        if decode_only:
            return self.decode_lo, self.decode_hi
        return 0, self.decode_hi


def parse_route(path: str) -> tuple[dict, int, int]:
    """Parse a .route file -> (positions, n_layers, topk).

    Same validation as trace_lib.load_trace: full layer coverage, no
    duplicate (pos, layer), single top-K across rows.
    """
    positions: dict[int, dict[int, list[int]]] = defaultdict(dict)
    topk_seen: set[int] = set()
    n_rows = 0
    with open(path, "r", encoding="utf-8") as fh:
        for lineno, raw in enumerate(fh, 1):
            fields = raw.split()
            if not fields:
                continue
            if len(fields) < 4:
                raise TraceFormatError(
                    f"{path}:{lineno}: expected call pos layer expert:gate ...")
            try:
                _call, pos, layer = map(int, fields[:3])
            except ValueError as error:
                raise TraceFormatError(f"{path}:{lineno}: invalid call/pos/layer") from error
            if pos < 0 or layer < 0:
                raise TraceFormatError(f"{path}:{lineno}: negative pos/layer")
            experts = []
            for value in fields[3:]:
                expert_text, sep, _gate = value.partition(":")
                if not sep:
                    raise TraceFormatError(f"{path}:{lineno}: invalid expert field {value!r}")
                try:
                    experts.append(int(expert_text))
                except ValueError as error:
                    raise TraceFormatError(
                        f"{path}:{lineno}: invalid expert id in {value!r}") from error
            if not experts:
                raise TraceFormatError(f"{path}:{lineno}: row selects no experts")
            if layer in positions[pos]:
                raise TraceFormatError(
                    f"{path}:{lineno}: duplicate (pos={pos}, layer={layer}) row")
            positions[pos][layer] = experts
            topk_seen.add(len(experts))
            n_rows += 1
    if not positions:
        raise TraceFormatError(f"{path}: trace contains no routing rows")
    if len(topk_seen) != 1:
        raise TraceFormatError(f"{path}: mixed top-K across rows: {sorted(topk_seen)}")
    layers_seen = {layer for per_layer in positions.values() for layer in per_layer}
    n_layers = max(layers_seen) + 1
    missing = sorted(set(range(n_layers)) - layers_seen)
    if missing:
        raise TraceFormatError(f"{path}: missing routed layers: {missing}")
    return dict(positions), n_layers, topk_seen.pop()


def load_sidecar(trace_path: str) -> dict:
    """Sidecar JSON: <trace>.route.json next to the trace."""
    side = trace_path + ".json"
    if not os.path.exists(side):
        raise TraceFormatError(
            f"{trace_path}: sidecar {side} missing (TRACE_SPEC requires exact "
            f"prefill_tokens for the decode split)")
    with open(side, "r", encoding="utf-8") as fh:
        return json.load(fh)


def load_trace(name: str, path: str, category: str | None = None,
               index: int = 0) -> Trace:
    """Load one trace + sidecar; validate the decode split."""
    positions, n_layers, topk = parse_route(path)
    sidecar = load_sidecar(path)
    P = sidecar.get("prefill_tokens")
    if not isinstance(P, int) or P < 0:
        raise TraceFormatError(f"{path}: sidecar prefill_tokens missing/not an int")
    n_gen_actual = sidecar.get("n_gen_actual")
    n_gen = max(positions) + 1 - P
    if isinstance(n_gen_actual, int) and n_gen != n_gen_actual:
        raise TraceFormatError(
            f"{path}: decode positions {n_gen} != sidecar n_gen_actual {n_gen_actual}")
    return Trace(name=name, category=category or name, index=index, path=path,
                 sidecar=sidecar, positions=positions, n_layers=n_layers,
                 topk=topk, prefill_end=P)


def check_geometry(traces: list[Trace], expect_layers: int | None = 48) -> None:
    """Cross-trace geometry agreement (all traces must describe one model)."""
    seen: set[tuple[int, int]] = {(t.n_layers, t.topk) for t in traces}
    if len(seen) != 1:
        raise TraceFormatError(f"traces disagree on (n_layers, topk): {sorted(seen)}")
    if expect_layers is not None:
        (nl, _), = seen
        if nl != expect_layers:
            raise TraceFormatError(f"expected {expect_layers} routed layers, got {nl}")
    for t in traces:
        lo, hi = t.window(decode_only=True)
        if hi <= lo:
            raise TraceFormatError(f"{t.path}: empty decode window (P={t.prefill_end})")


# ---- counting / hit-rate primitives (port of phase0_rank.py, exact semantics) ----

def layer_counts(positions, n_layers: int, lo: int, hi: int) -> dict[int, Counter]:
    """Per-layer expert selection Counter over positions [lo, hi).

    Iteration order (pos asc, experts in row order) matches trace_lib, so
    Counter.most_common tie order matches phase0_rank.py bit-for-bit.
    """
    counts: dict[int, Counter] = {layer: Counter() for layer in range(n_layers)}
    for pos in range(lo, hi):
        per_layer = positions.get(pos)
        if per_layer is None:
            raise TraceFormatError(f"position {pos} absent from trace")
        for layer in range(n_layers):
            try:
                experts = per_layer[layer]
            except KeyError as error:
                raise TraceFormatError(f"position {pos} missing layer {layer}") from error
            counts[layer].update(experts)
    return counts


def topn_pins(counts: dict[int, Counter], n: int) -> dict[int, set[int]]:
    """Top-N expert sets per layer (most_common ordering, ties by encounter)."""
    return {l: {e for e, _ in c.most_common(n)} for l, c in counts.items()}


def hit_rate_micro(positions, n_layers: int, lo: int, hi: int, pins) -> float:
    hits = total = 0
    for pos in range(lo, hi):
        per = positions[pos]
        for l in range(n_layers):
            r = per[l]
            total += len(r)
            s = pins[l]
            hits += sum(1 for e in r if e in s)
    return hits / total


def hit_rate_per_layer(positions, n_layers: int, lo: int, hi: int, pins) -> list[float]:
    out = []
    for l in range(n_layers):
        hits = total = 0
        for pos in range(lo, hi):
            r = positions[pos][l]
            total += len(r)
            s = pins[l]
            hits += sum(1 for e in r if e in s)
        out.append(hits / total)
    return out
