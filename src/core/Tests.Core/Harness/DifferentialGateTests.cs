using Xunit;
using Xunit.Abstractions;

namespace Tests.Core.Harness;

/// <summary>
/// DIFFERENTIAL GATE (epic #591 WP3): runs the SAME catalog scenarios against
/// the v2 scheduler (<see cref="V2ScenarioDriver"/>) and compares the
/// normalized trace byte-for-byte against the legacy goldens under
/// <c>Harness/Goldens/</c>.
///
/// <para>Only scenarios listed in <see cref="ExpectedParity"/> are ASSERTED;
/// everything else is reported as SKIP in the test output (a growing parity
/// matrix). As v2 ports close behavior gaps, move scenarios from the skip
/// bucket into <c>ExpectedParity</c> — a byte-identical trace is the merge gate
/// for every strangler swap.</para>
///
/// <para>Known divergence: <c>ExpectedParity</c> starts empty because v2's
/// current pipeline (EnginePrefill → Put → Get → StatePut → HTTP decode) does
/// not yet reproduce the legacy wire stream (EngineConfigure payloads, same-node
/// skip-restore, BgSave StateGet+Put, ledger registration, warm-lease
/// retention). This test documents the current state — the matrix in the output
/// is the A/B parity scoreboard.</para>
/// </summary>
[Collection("HydraHarnessTests")]
public sealed class DifferentialGateTests
{
    /// <summary>Scenarios the v2 scheduler must byte-match today. Grows with parity.</summary>
    private static readonly HashSet<string> ExpectedParity = new()
    {
        // Tranche 1 (single-node, HTTP-decode): filled as v2 ports land.
    };

    private readonly ITestOutputHelper _output;

    public DifferentialGateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task V2_Traces_Match_Legacy_Goldens_For_InScope_Scenarios()
    {
        Directory.CreateDirectory(ScenarioCatalog.GoldensDirectory);

        var failures = new List<string>();
        var matched = 0;
        var skipped = 0;
        var matrix = new List<string>();

        foreach (var spec in ScenarioCatalog.All)
        {
            if (spec.LegacyOnly)
            {
                skipped++;
                matrix.Add($"{spec.Id,-28} SKIP  (legacy-only direct-drive seams)");
                continue;
            }

            ScenarioRunResult result;
            await using (var driver = new V2ScenarioDriver(spec.Options, "sess_h"))
            {
                result = await SchedulerScenarioRunner.ExecuteOnAsync(driver, spec);
            }

            var goldenPath = Path.Combine(ScenarioCatalog.GoldensDirectory, $"{spec.Id}.json");
            if (!File.Exists(goldenPath))
            {
                skipped++;
                matrix.Add($"{spec.Id,-28} SKIP  (no golden on disk)");
                continue;
            }

            var expected = File.ReadAllText(goldenPath);
            var actual = SchedulerScenarioRunner.SerializeGolden(
                new GoldenTrace(spec.Id, spec.Description, 1, result.Trace));
            var matches = expected == actual;

            if (!ExpectedParity.Contains(spec.Id))
            {
                skipped++;
                matrix.Add($"{spec.Id,-28} SKIP  (not in ExpectedParity)  v2_outcome={result.Outcome,-8} rpc={result.Trace.Rpc.Count,2} match={matches}");
                continue;
            }

            if (matches)
            {
                matched++;
                matrix.Add($"{spec.Id,-28} MATCH");
            }
            else
            {
                failures.Add($"{spec.Id}: TRACE DRIFT vs golden — outcome={result.Outcome}, " +
                             $"rpc={result.Trace.Rpc.Count}, busy={string.Join(",", result.Trace.BusySeconds.Select(kv => $"{kv.Key}={kv.Value}"))}, " +
                             $"final={result.Trace.FinalState}");
            }
        }

        // Always report the matrix (the parity scoreboard) even on success.
        _output.WriteLine("PARITY MATRIX (v2 vs legacy goldens):\n" + string.Join("\n", matrix));
        var matrixText = string.Join("\n", matrix);
        Assert.True(failures.Count == 0,
            $"Differential gate: {matched} matched, {skipped} skipped, {failures.Count} drifted.\n" +
            $"PARITY MATRIX:\n{matrixText}\n" +
            (failures.Count == 0 ? "" : "DRIFT:\n" + string.Join("\n", failures)));

        if (ExpectedParity.Count > 0)
            Assert.Equal(ExpectedParity.Count, matched);
    }
}
