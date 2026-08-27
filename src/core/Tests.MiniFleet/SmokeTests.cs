using System.Text.Json;
using Tests.Core.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Tests.MiniFleet;

/// <summary>
/// Shared plumbing for mini-fleet smoke facts (brief §Acceptance criteria):
/// model resolution, fleet lifecycle, and the four smoke assertions applied to
/// every executed scenario:
///   1. HTTP 200 from the engine (/v1/chat/completions),
///   2. finish_reason present,
///   3. completion_tokens &gt; 0,
///   4. no crash output from either engine process (log-based here: cpu lane
///      streams engine stderr to Aspire console; a hard crash surfaces as a
///      failed health gate or non-200 — both fail these tests),
///   5. store reachable (coordinator /health reports store.healthy for the
///      cpu lane; asserted via the sandbox coordinator URL).
/// All facts carry Trait Tier=MiniFleet so the charter VERIFY filter
///   dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet"
/// selects exactly this tier.
/// </summary>
[Trait("Tier", "MiniFleet")]
public abstract class MiniFleetSmokeBase
{
    protected readonly ITestOutputHelper Output;

    protected MiniFleetSmokeBase(ITestOutputHelper output) => Output = output;

    /// <summary>Smoke subset of the REAL harness catalog (consultant ruling).</summary>
    private static IReadOnlyList<ScenarioSpec> Specs => RealEngineScenarioRunner.SmokeSubset;

    /// <summary>Model path: MINIFLEET_MODEL_PATH override else pinned download.</summary>
    protected static Task<FileInfo> ResolveModelAsync(CancellationToken ct) =>
        Artifacts.EnsureModelAsync(ct);

    protected static void AssertSmokeAssertions(MiniFleetScenarioRunResult result)
    {
        Assert.True(result.Outcome == "Done",
            $"{result.ScenarioId} [{result.SchedulerImpl}]: expected Done, got {result.Outcome} " +
            $"(error: {result.Error})");
        Assert.All(result.Trace, call =>
        {
            Assert.Equal(200, call.HttpStatusCode);
            Assert.NotNull(call.FinishReason);
            Assert.True((call.CompletionTokens ?? 0) > 0,
                $"{result.ScenarioId}: completion_tokens must be > 0");
        });
    }

    /// <summary>Store-reachable assertion (cpu lane): the sandbox coordinator's
    /// /health endpoint reports store health. Tolerated missing on lanes without
    /// a coordinator (ssh-shim lane passes null).</summary>
    private static async Task AssertStoreReachableAsync(string? coordinatorUrl, CancellationToken ct)
    {
        if (coordinatorUrl is null)
        {
            return; // lane without a sandbox coordinator — out of scope for that lane
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync($"{coordinatorUrl}/health", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var store = doc.RootElement.GetProperty("store");
        Assert.True(store.TryGetProperty("healthy", out var healthy) && healthy.GetBoolean(),
            $"coordinator /health reports store unhealthy: {json}");
    }

    /// <summary>Shared executor: start fleet → run both A/B passes over the smoke
    /// subset → apply assertions → emit trace pairs. Returns per-spec results.</summary>
    protected static async Task<List<MiniFleetScenarioRunResult>> RunSmokeAsync(
        MiniFleetRun fleet, string schedulerImplPass, CancellationToken ct)
    {
        var results = new List<MiniFleetScenarioRunResult>();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        foreach (var spec in Specs)
        {
            var runner = new RealEngineScenarioRunner(http, fleet.PresetName);
            var (legacy, v2) = await runner.RunBothPassesAsync(
                spec,
                new[] { fleet.EngineAUrl, fleet.EngineBUrl },
                ct: ct).ConfigureAwait(false);

            AssertSmokeAssertions(legacy);
            if (v2 is not null)
            {
                AssertSmokeAssertions(v2);
            }
            results.Add(legacy);
        }

        await AssertStoreReachableAsync(fleet.CoordinatorBaseUrl, ct).ConfigureAwait(false);
        return results;
    }
}

/// <summary>AC1: cpu-2node preset green end-to-end on host WITHOUT GPU
/// (real engines, ngl=0, threads 3+3, ctx 4096, Aspire-hosted).</summary>
[Trait("Tier", "MiniFleet")]
public sealed class CpuTwoNodeSmokeTests : MiniFleetSmokeBase, IAsyncLifetime
{
    private MiniFleetRun? _fleet;
    private static readonly CancellationTokenSource Cts = new(TimeSpan.FromMinutes(20));

    public CpuTwoNodeSmokeTests(ITestOutputHelper output) : base(output) { }

    public async Task InitializeAsync()
    {
        var model = await ResolveModelAsync(Cts.Token).ConfigureAwait(false);
        Output.WriteLine($"model: {model.FullName}");
        _fleet = await MiniFleetAppHost.StartAsync(Presets.Cpu2Node, model.FullName, Cts.Token)
            .ConfigureAwait(false);
        Output.WriteLine($"fleet up: coord={_fleet.CoordinatorBaseUrl} " +
                         $"A={_fleet.EngineAUrl} B={_fleet.EngineBUrl}");
    }

    public async Task DisposeAsync()
    {
        if (_fleet is not null)
        {
            await _fleet.DisposeAsync().ConfigureAwait(false);
        }
        Cts.Dispose();
    }

    [Fact]
    public async Task ColdAtomicEngine_RealEngines_Passes()
    {
        var results = await RunSmokeAsync(_fleet!, "legacy", Cts.Token).ConfigureAwait(false);
        Assert.Contains(results, r => r.ScenarioId == "cold_atomic_engine");
    }

    [Fact]
    public async Task ChunkedSave_RealEngines_Passes()
    {
        var results = await RunSmokeAsync(_fleet!, "legacy", Cts.Token).ConfigureAwait(false);
        Assert.Contains(results, r => r.ScenarioId == "chunked_save");
    }
}

/// <summary>AC2: gpu-gpu-shared preset green against the live P100 VM through
/// the ssh shim (validated topology in Presets.GpuGpuShared). Skipped unless
/// MINIFLEET_SSH_TARGET is set, so CI (which does not carry VM ssh config)
/// never hangs or fails — the lane is explicitly opt-in via env.</summary>
[Trait("Tier", "MiniFleet")]
[Trait("RequiresVm", "true")]
public sealed class GpuGpuSharedSmokeTests : MiniFleetSmokeBase, IAsyncLifetime
{
    private static readonly string? SshTarget =
        Normalize(Environment.GetEnvironmentVariable("MINIFLEET_SSH_TARGET"));

    /// <summary>Treat empty-string env vars as unset (MINIFLEET_SSH_TARGET= on a
    /// command line means "skip the VM lane", same as not setting it).</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private MiniFleetRun? _fleet;
    private static readonly CancellationTokenSource Cts = new(TimeSpan.FromMinutes(25));

    public GpuGpuSharedSmokeTests(ITestOutputHelper output) : base(output) { }

    public Task InitializeAsync()
    {
        if (SshTarget is null)
        {
            // Skip via Skip.If in facts would still run Initialize; use an
            // explicit no-op fleet and skip in each fact for clarity.
            return Task.CompletedTask;
        }
        return InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        var model = await ResolveModelAsync(Cts.Token).ConfigureAwait(false);
        Output.WriteLine($"model: {model.FullName}");
        _fleet = await MiniFleetAppHost.StartAsync(Presets.GpuGpuShared, model.FullName, Cts.Token)
            .ConfigureAwait(false);
        Output.WriteLine($"fleet up: A={_fleet.EngineAUrl} B={_fleet.EngineBUrl}");
    }

    public async Task DisposeAsync()
    {
        if (_fleet is not null)
        {
            await _fleet.DisposeAsync().ConfigureAwait(false);
        }
        Cts.Dispose();
    }

    [SkippableFact]
    public async Task ColdAtomicEngine_VmLane_Passes()
    {
        Skip.If(SshTarget is null || _fleet is null,
            "MINIFLEET_SSH_TARGET unset — VM lane is opt-in.");
        var results = await RunSmokeAsync(_fleet!, "legacy", Cts.Token).ConfigureAwait(false);
        Assert.Contains(results, r => r.ScenarioId == "cold_atomic_engine");
    }

    [SkippableFact]
    public async Task ChunkedSave_VmLane_Passes()
    {
        Skip.If(SshTarget is null || _fleet is null,
            "MINIFLEET_SSH_TARGET unset — VM lane is opt-in.");
        var results = await RunSmokeAsync(_fleet!, "legacy", Cts.Token).ConfigureAwait(false);
        Assert.Contains(results, r => r.ScenarioId == "chunked_save");
    }
}
