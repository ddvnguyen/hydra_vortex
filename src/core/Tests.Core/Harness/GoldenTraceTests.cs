using Xunit;

namespace Tests.Core.Harness;

/// <summary>
/// Harness scenarios serialize among themselves: the runner mutates shared
/// statics (ModelRegistry registrations, ChunkEngine.CHUNK_SIZE) per scenario,
/// so no two harness scenarios may overlap.
/// </summary>
[CollectionDefinition("HydraHarnessTests", DisableParallelization = true)]
public sealed class HydraHarnessTestCollection { }

/// <summary>
/// Golden-event-trace gate (epic #591 WP0): runs every catalog scenario against
/// the LEGACY scheduler through the real evaluator loop and compares the
/// normalized trace byte-for-byte against the checked-in goldens under
/// <c>Harness/Goldens/</c>.
///
/// Regenerate mode: set <c>HYDRA_HARNESS_REGEN=1</c> (or pass
/// <c>--environment HYDRA_HARNESS_REGEN=1</c> to dotnet test) — the tests then
/// WRITE the goldens instead of comparing. The differential gate (WP3+) re-runs
/// this exact catalog against the v2 scheduler; a byte-identical trace is the
/// merge gate for every strangler swap.
/// </summary>
[Collection("HydraHarnessTests")]
public sealed class GoldenTraceTests
{
    [Fact]
    public async Task All_Catalog_Scenarios_Match_Their_Goldens()
    {
        var regen = Environment.GetEnvironmentVariable(ScenarioCatalog.RegenerateEnvVar) == "1";
        Directory.CreateDirectory(ScenarioCatalog.GoldensDirectory);

        var failures = new List<string>();
        var passed = 0;

        foreach (var spec in ScenarioCatalog.All)
        {
            var result = await SchedulerScenarioRunner.ExecuteAsync(spec);

            if (result.Outcome != spec.ExpectedOutcome)
            {
                failures.Add($"{spec.Id}: outcome {result.Outcome} != expected {spec.ExpectedOutcome}" +
                             $"{(result.Error is null ? "" : $" — {result.Error.GetType().Name}: {result.Error.Message}")}");
                continue;
            }

            var golden = new GoldenTrace(spec.Id, spec.Description, 1, result.Trace);
            var json = SchedulerScenarioRunner.SerializeGolden(golden);
            var path = Path.Combine(ScenarioCatalog.GoldensDirectory, $"{spec.Id}.json");

            if (regen)
            {
                File.WriteAllText(path, json);
                passed++;
                continue;
            }

            if (!File.Exists(path))
            {
                failures.Add($"{spec.Id}: golden missing — run with {ScenarioCatalog.RegenerateEnvVar}=1 to create {path}");
                continue;
            }

            var expected = File.ReadAllText(path);
            if (expected == json)
            {
                passed++;
            }
            else
            {
                failures.Add($"{spec.Id}: TRACE DRIFT — legacy behavior changed. " +
                             $"Diff against {path} and either fix the regression or re-baseline " +
                             $"({ScenarioCatalog.RegenerateEnvVar}=1).");
            }
        }

        Assert.True(failures.Count == 0,
            $"Golden trace gate: {passed}/{ScenarioCatalog.All.Count} matched.{(failures.Count == 0 ? "" : "\n" + string.Join("\n", failures))}");
        Assert.Equal(ScenarioCatalog.All.Count, passed);
    }
}
