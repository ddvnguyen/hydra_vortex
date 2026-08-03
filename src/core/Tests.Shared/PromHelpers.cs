using System.Globalization;
using System.Text.RegularExpressions;

namespace Tests.Shared;

/// <summary>
/// Helpers for parsing the Hydra.Core Prometheus text-exposition endpoint.
///
/// Ported from Python <c>tests/system/prom_helpers.py</c>. Extracted so the
/// regex parsing can be unit-tested without a live stack.
///
/// Background: prometheus-net always renders labeled counters with their
/// label set, so <c>hydra_cross_model_kv_proceeded_total</c> shows up as
/// <code>
///   hydra_cross_model_kv_proceeded_total{worker="rtx"} 1
///   hydra_cross_model_kv_proceeded_total{worker="p100"} 0
/// </code>
/// A naive <c>^name\\s+...</c> regex (whitespace immediately after the name)
/// fails to match both. A correct reader must:
///   1. Accept both labeled (<c>name{...}</c>) and unlabeled (<c>name </c>) forms.
///   2. Sum across all label series when the caller asks for the total.
/// </summary>
public static class PromHelpers
{
    /// <summary>
    /// A single parsed Prometheus sample.
    /// </summary>
    public sealed record Sample(
        string Name,
        IReadOnlyDictionary<string, string> Labels,
        double Value);

    // Anchored at start-of-line. The `(?:\{...\}|\\s+)` allows either
    // `{labels}` (labeled series) or whitespace (unlabeled series) after
    // the metric name. The value is the standard Prometheus float grammar
    // (with optional exponent) but we use a permissive `[0-9.eE+\-]+`
    // so that NaN, Inf, and other special values parse as 0.0.
    private static readonly Regex LinePattern = new(
        @"^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{(?<labels>[^}]*)\})?\s+(?<value>[0-9.eE+\-]+)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a Prometheus text-exposition body into a list of
    /// <see cref="Sample"/> records. Skips blank lines and
    /// <c># HELP</c> / <c># TYPE</c> comment lines silently.
    ///
    /// Labels is an empty dictionary for unlabeled series. The value is
    /// a double (NaN and Inf become 0.0 via the permissive regex — callers
    /// that need to distinguish can check <see cref="double.IsNaN"/> /
    /// <see cref="double.IsInfinity"/> on the result).
    /// </summary>
    public static IReadOnlyList<Sample> ParsePromLines(string body)
    {
        var outList = new List<Sample>();

        foreach (var line in body.Split('\n'))
        {
            // Trim for robustness (Python splitlines also strips trailing \r).
            var trimmed = line.TrimEnd('\r');

            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var m = LinePattern.Match(trimmed);
            if (!m.Success)
                continue;

            var name = m.Groups["name"].Value;
            var labelsStr = m.Groups["labels"].Value;
            var labels = new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(labelsStr))
            {
                foreach (var kv in labelsStr.Split(','))
                {
                    var eqIdx = kv.IndexOf('=');
                    if (eqIdx < 0) continue;

                    var k = kv[..eqIdx].Trim();
                    var v = kv[(eqIdx + 1)..].Trim();

                    // Strip surrounding quotes from the value.
                    if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                        v = v[1..^1];

                    labels[k] = v;
                }
            }

            double value = 0.0;
            if (double.TryParse(
                    m.Groups["value"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                value = parsed;
            }

            outList.Add(new Sample(name, labels, value));
        }

        return outList;
    }

    /// <summary>
    /// Sum a counter across all series matching <paramref name="name"/>
    /// (and optionally the label subset <paramref name="labels"/>).
    /// Returns 0.0 when no series match.
    ///
    /// When <paramref name="labels"/> is <c>null</c>, sums every series
    /// with the matching name regardless of labels — this is the "total
    /// across all workers" query that system tests use to assert a guard
    /// fired at least once.
    ///
    /// When <paramref name="labels"/> is provided, filters to series whose
    /// label set is a superset of <paramref name="labels"/> (Prometheus
    /// label matching semantics).
    /// </summary>
    public static double SumCounter(
        IReadOnlyList<Sample> samples,
        string name,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        var total = 0.0;

        foreach (var sample in samples)
        {
            if (sample.Name != name)
                continue;

            if (labels is not null)
            {
                var match = true;
                foreach (var kv in labels)
                {
                    if (!sample.Labels.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    {
                        match = false;
                        break;
                    }
                }
                if (!match)
                    continue;
            }

            total += sample.Value;
        }

        return total;
    }
}
