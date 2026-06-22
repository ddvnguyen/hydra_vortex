"""
Unit tests for the Prometheus text-exposition parser.

These tests run in the normal unit gate (no live stack required).
They were added to prevent the regression found in PR #296 review
where the system test's `_get_counter` regex required whitespace
immediately after the metric name, so it never matched labeled series
like `hydra_cross_model_kv_proceeded_total{worker="rtx"} 1`.

The fix: a permissive regex that accepts both `name ` and `name{...} `
forms, plus a `sum_counter` helper that aggregates across all matching
series (since system tests want the total across workers, not just one).
"""

from __future__ import annotations

from prom_helpers import parse_prom_lines, sum_counter


SAMPLE_BODY = """\
# HELP hydra_cross_model_kv_proceeded_total Cross-model check passed
# TYPE hydra_cross_model_kv_proceeded_total counter
hydra_cross_model_kv_proceeded_total{worker="rtx"} 1
hydra_cross_model_kv_proceeded_total{worker="p100"} 0
# HELP hydra_cross_model_kv_aborted_total Cross-model check aborted
# TYPE hydra_cross_model_kv_aborted_total counter
hydra_cross_model_kv_aborted_total{worker="rtx"} 0
hydra_cross_model_kv_aborted_total{worker="p100"} 2
# HELP hydra_unlabeled_total A counter with no labels
# TYPE hydra_unlabeled_total counter
hydra_unlabeled_total 42
# HELP hydra_gauge A gauge
# TYPE hydra_gauge gauge
hydra_gauge{worker="rtx"} 0.95
"""


def test_parse_skips_help_and_type_lines():
    samples = parse_prom_lines(SAMPLE_BODY)
    names = [s[0] for s in samples]
    assert "hydra_cross_model_kv_proceeded_total" in names
    # Ensure no HELP/TYPE lines leaked in
    assert all(not n.startswith("#") for n in names)


def test_parse_labeled_counter():
    samples = parse_prom_lines(SAMPLE_BODY)
    rtx_proceeded = [
        s for s in samples
        if s[0] == "hydra_cross_model_kv_proceeded_total"
        and s[1].get("worker") == "rtx"
    ]
    assert len(rtx_proceeded) == 1
    assert rtx_proceeded[0][2] == 1.0


def test_parse_unlabeled_counter():
    samples = parse_prom_lines(SAMPLE_BODY)
    unlabeled = [s for s in samples if s[0] == "hydra_unlabeled_total"]
    assert len(unlabeled) == 1
    assert unlabeled[0][1] == {}
    assert unlabeled[0][2] == 42.0


def test_parse_gauge_with_label():
    samples = parse_prom_lines(SAMPLE_BODY)
    gauge = [
        s for s in samples
        if s[0] == "hydra_gauge" and s[1].get("worker") == "rtx"
    ]
    assert gauge[0][2] == 0.95


def test_sum_counter_unlabeled_returns_total_across_workers():
    """The system test in PR #296 reviews against `proceeded_total` with no
    labels — this must return the total across all workers, not just one."""
    samples = parse_prom_lines(SAMPLE_BODY)
    total = sum_counter(samples, "hydra_cross_model_kv_proceeded_total")
    assert total == 1.0  # rtx=1, p100=0


def test_sum_counter_with_label_filter():
    samples = parse_prom_lines(SAMPLE_BODY)
    rtx_only = sum_counter(
        samples, "hydra_cross_model_kv_aborted_total", labels={"worker": "rtx"}
    )
    p100_only = sum_counter(
        samples, "hydra_cross_model_kv_aborted_total", labels={"worker": "p100"}
    )
    assert rtx_only == 0.0
    assert p100_only == 2.0


def test_sum_counter_absent_returns_zero():
    samples = parse_prom_lines(SAMPLE_BODY)
    assert sum_counter(samples, "nonexistent_counter") == 0.0


def test_parse_empty_body():
    assert parse_prom_lines("") == []


def test_parse_malformed_line_skipped():
    """Garbage lines (e.g. truncated exposition) must not crash the parser."""
    body = "hydra_ok_total 5\nthis is not a valid metric line\nhydra_other_total 1\n"
    samples = parse_prom_lines(body)
    names = [s[0] for s in samples]
    assert "hydra_ok_total" in names
    assert "hydra_other_total" in names
    # The garbage line is silently dropped (no exception, no entry).
    assert len(samples) == 2
