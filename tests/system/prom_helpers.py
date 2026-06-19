"""
Helpers for parsing the Hydra.Core Prometheus metrics endpoint.

Extracted from `test_mix_precision_p_d_system.py` so the regex parsing
can be unit-tested without a live stack (the system tests need a running
Coordinator + llama-server, which CI doesn't have). See
`test_prom_helpers.py` for the unit tests.

Background: prometheus-net always renders labeled counters with their
label set, so `hydra_cross_model_kv_proceeded_total` shows up as

    hydra_cross_model_kv_proceeded_total{worker="rtx"} 1
    hydra_cross_model_kv_proceeded_total{worker="p100"} 0

A naive `^name\\s+...` regex (whitespace immediately after the name)
fails to match both — the gap before this fix was that the test asked
for the unlabeled total, got 0.0, and asserted a positive number, so
the test would have failed even when the guard was working correctly.

A correct reader must:
  1. Accept both labeled (`name{...}`) and unlabeled (`name `) forms.
  2. Sum across all label series when the caller asks for the total
     of a multi-label counter (or filter to a specific label set).
"""

from __future__ import annotations

import re
from typing import Mapping


# Anchor at the start of a line. The `(?:\s+|{)` allows either whitespace
# (unlabeled series) or `{` (labeled series) after the metric name. The
# value at the end is the standard Prometheus float grammar (with
# optional exponent) but we use a permissive `[0-9.eE+\-]+` so that
# NaN, Inf, and other special values parse as 0.0 in downstream code.
_LINE_PATTERN = re.compile(
    r"^(?P<name>[a-zA-Z_:][a-zA-Z0-9_:]*)"
    r"(?:\{(?P<labels>[^}]*)\})?"
    r"\s+(?P<value>[0-9.eE+\-]+)\s*$"
)


def parse_prom_lines(body: str) -> list[tuple[str, Mapping[str, str], float]]:
    """
    Parse a Prometheus text-exposition body into a list of
    (name, labels, value) tuples. Skip `# HELP` / `# TYPE` / blank
    lines silently.

    `labels` is an empty mapping for unlabeled series. The value is
    a float (NaN and Inf become 0.0 via `float()`'s parser — callers
    that need to distinguish can use `math.isnan` / `math.isinf` on
    the result).
    """
    out: list[tuple[str, Mapping[str, str], float]] = []
    for line in body.splitlines():
        if not line or line.startswith("#"):
            continue
        m = _LINE_PATTERN.match(line)
        if not m:
            continue
        name = m.group("name")
        labels_str = m.group("labels") or ""
        labels: dict[str, str] = {}
        if labels_str:
            for kv in labels_str.split(","):
                k, _, v = kv.partition("=")
                # Strip surrounding quotes from the value.
                v = v.strip()
                if v.startswith('"') and v.endswith('"'):
                    v = v[1:-1]
                labels[k.strip()] = v
        try:
            value = float(m.group("value"))
        except ValueError:
            value = 0.0
        out.append((name, labels, value))
    return out


def sum_counter(
    samples: list[tuple[str, Mapping[str, str], float]],
    name: str,
    labels: Mapping[str, str] | None = None,
) -> float:
    """
    Sum a counter across all series matching `name` (and optionally the
    label subset `labels`). Returns 0.0 when no series match.

    When `labels` is None, sums every series with the matching name
    regardless of labels — this is the "total across all workers"
    query that system tests use to assert a guard fired at least once.

    When `labels` is provided, filters to series whose label set is
    a superset of `labels` (Prometheus label matching semantics —
    `labels={worker="rtx"}` matches `{worker="rtx",extra="x"}` too,
    but for our counters the only label is `worker` so the
    distinction doesn't matter).
    """
    total = 0.0
    for sample_name, sample_labels, value in samples:
        if sample_name != name:
            continue
        if labels is not None and not all(
            sample_labels.get(k) == v for k, v in labels.items()
        ):
            continue
        total += value
    return total
