namespace Tests.Shared;

/// <summary>
/// Unit tests for <see cref="PromHelpers"/> — the Prometheus text-exposition parser.
///
/// Ported from Python <c>tests/system/test_prom_helpers.py</c>.
/// These tests run in the normal unit gate (no live stack required).
/// </summary>
public class PromHelpersTests
{
    private const string SampleBody = """
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
        """;

    [Fact]
    public void ParseSkipsHelpAndTypeLines()
    {
        var samples = PromHelpers.ParsePromLines(SampleBody);
        var names = samples.Select(s => s.Name).ToList();

        Assert.Contains("hydra_cross_model_kv_proceeded_total", names);
        // Ensure no HELP/TYPE lines leaked in.
        Assert.All(names, n => Assert.False(n.StartsWith('#')));
    }

    [Fact]
    public void ParseLabeledCounter()
    {
        var samples = PromHelpers.ParsePromLines(SampleBody);

        var rtxProceeded = samples
            .Where(s => s.Name == "hydra_cross_model_kv_proceeded_total"
                     && s.Labels.TryGetValue("worker", out var w) && w == "rtx")
            .ToList();

        Assert.Single(rtxProceeded);
        Assert.Equal(1.0, rtxProceeded[0].Value);
    }

    [Fact]
    public void ParseUnlabeledCounter()
    {
        var samples = PromHelpers.ParsePromLines(SampleBody);

        var unlabeled = samples
            .Where(s => s.Name == "hydra_unlabeled_total")
            .ToList();

        Assert.Single(unlabeled);
        Assert.Empty(unlabeled[0].Labels);
        Assert.Equal(42.0, unlabeled[0].Value);
    }

    [Fact]
    public void ParseGaugeWithLabel()
    {
        var samples = PromHelpers.ParsePromLines(SampleBody);

        var gauge = samples
            .Where(s => s.Name == "hydra_gauge"
                     && s.Labels.TryGetValue("worker", out var w) && w == "rtx")
            .ToList();

        Assert.Single(gauge);
        Assert.Equal(0.95, gauge[0].Value);
    }

    [Fact]
    public void SumCounterUnlabeledReturnsTotalAcrossWorkers()
    {
        // The system test in PR #296 reviews against `proceeded_total` with no
        // labels — this must return the total across all workers, not just one.
        var samples = PromHelpers.ParsePromLines(SampleBody);

        var total = PromHelpers.SumCounter(samples, "hydra_cross_model_kv_proceeded_total");

        Assert.Equal(1.0, total); // rtx=1, p100=0
    }

    [Fact]
    public void SumCounterWithLabelFilter()
    {
        var samples = PromHelpers.ParsePromLines(SampleBody);

        var rtxOnly = PromHelpers.SumCounter(
            samples, "hydra_cross_model_kv_aborted_total",
            new Dictionary<string, string> { ["worker"] = "rtx" });

        var p100Only = PromHelpers.SumCounter(
            samples, "hydra_cross_model_kv_aborted_total",
            new Dictionary<string, string> { ["worker"] = "p100" });

        Assert.Equal(0.0, rtxOnly);
        Assert.Equal(2.0, p100Only);
    }

    [Fact]
    public void SumCounterAbsentReturnsZero()
    {
        var samples = PromHelpers.ParsePromLines(SampleBody);
        Assert.Equal(0.0, PromHelpers.SumCounter(samples, "nonexistent_counter"));
    }

    [Fact]
    public void ParseEmptyBody()
    {
        Assert.Empty(PromHelpers.ParsePromLines(""));
    }

    [Fact]
    public void ParseMalformedLineSkipped()
    {
        // Garbage lines (e.g. truncated exposition) must not crash the parser.
        const string body = "hydra_ok_total 5\nthis is not a valid metric line\nhydra_other_total 1\n";
        var samples = PromHelpers.ParsePromLines(body);
        var names = samples.Select(s => s.Name).ToList();

        Assert.Contains("hydra_ok_total", names);
        Assert.Contains("hydra_other_total", names);
        // The garbage line is silently dropped (no exception, no entry).
        Assert.Equal(2, samples.Count);
    }
}
