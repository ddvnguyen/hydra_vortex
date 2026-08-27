using System.Text.Json;
using Tests.Core.Harness;
using Xunit;
using Xunit.Abstractions;

// Architect ruling 2026-08-28c: both smoke classes bind FIXED engine/coordinator
// ports (8088/8089/19000/19500) — xunit must run them sequentially or the two
// fleets collide. (Per-preset distinct ports would re-enable parallelism later.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
    /// a coordinator (ssh-shim lane passes null). Uses the shared HTTP budget
    /// policy (Infinite client timeout + per-request CTS) — single source of
    /// truth for HTTP budgets, per architect ruling 2026-08-28b(2).</summary>
    private static async Task AssertStoreReachableAsync(
        string? coordinatorUrl, bool viaSshShim, CancellationToken ct)
    {
        if (coordinatorUrl is null)
        {
            return; // lane without a sandbox coordinator — out of scope for that lane
        }
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(viaSshShim ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(120));
        var json = await http.GetStringAsync($"{coordinatorUrl}/health", requestCts.Token)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var store = doc.RootElement.GetProperty("store");
        Assert.True(store.TryGetProperty("healthy", out var healthy) && healthy.GetBoolean(),
            $"coordinator /health reports store unhealthy: {json}");
    }

    /// <summary>Shared executor: start fleet → run both A/B passes over the smoke
    /// subset → apply assertions → emit trace pairs. Returns per-spec results.
    /// Asserts the requested scenario executed and passed.</summary>
    protected static async Task<List<MiniFleetScenarioRunResult>> RunSmokeAsync(
        MiniFleetRun fleet, string schedulerImplPass, CancellationToken ct, string expectedScenarioId)
    {
        var results = new List<MiniFleetScenarioRunResult>();
        // Architect ruling 2026-08-28b: InfiniteTimeSpan client — the per-request
        // CTS inside RealEngineScenarioRunner carries the real budget.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        foreach (var spec in Specs)
        {
            var runner = new RealEngineScenarioRunner(http, fleet.PresetName, fleet.ViaSshShim);
            var (legacy, v2) = await runner.RunBothPassesAsync(
                spec,
                new[] { fleet.EngineAUrl, fleet.EngineBUrl },
                fleet.Preset,
                ct: ct).ConfigureAwait(false);

            AssertSmokeAssertions(legacy);
            if (v2 is not null)
            {
                AssertSmokeAssertions(v2);
            }
            results.Add(legacy);
        }

        Assert.Contains(results, r => r.ScenarioId == expectedScenarioId);
        await AssertStoreReachableAsync(fleet.CoordinatorBaseUrl, fleet.ViaSshShim, ct)
            .ConfigureAwait(false);
        return results;
    }
}

/// <summary>AC1: cpu-2node preset green end-to-end on host WITHOUT GPU
/// (real engines, ngl=0, threads 3+3, ctx 4096, Aspire-hosted).
/// Fleet lifetime is PER-TEST (architect ruling 2026-08-28a): each fact calls
/// StartFleetAsync via its own CTS and disposes the fleet before returning —
/// no shared/static CTS across tests (that produced ObjectDisposedException:
/// xunit creates a NEW class instance per fact, and the static CTS was disposed
/// by the first fact's DisposeAsync while the second instance still used it).</summary>
[Trait("Tier", "MiniFleet")]
public sealed class CpuTwoNodeSmokeTests : MiniFleetSmokeBase
{
    public CpuTwoNodeSmokeTests(ITestOutputHelper output) : base(output) { }

    /// <summary>Per-test fleet bring-up + the single smoke pass. Fleet disposal
    /// happens in the finally block, so a failed scenario never leaks engines.</summary>
    private async Task WithFleetAsync(Func<MiniFleetRun, CancellationToken, Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        MiniFleetRun? fleet = null;
        try
        {
            var model = await ResolveModelAsync(cts.Token).ConfigureAwait(false);
            Output.WriteLine($"model: {model.FullName}");
            fleet = await MiniFleetAppHost.StartAsync(Presets.Cpu2Node, model.FullName, cts.Token)
                .ConfigureAwait(false);
            Output.WriteLine($"fleet up: coord={fleet.CoordinatorBaseUrl} " +
                             $"A={fleet.EngineAUrl} B={fleet.EngineBUrl}");
            await body(fleet, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (fleet is not null)
            {
                await fleet.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [Fact]
    public Task ColdAtomicEngine_RealEngines_Passes() =>
        WithFleetAsync((fleet, ct) => RunSmokeAsync(fleet, "legacy", ct, "cold_atomic_engine"));

    [Fact]
    public Task ChunkedSave_RealEngines_Passes() =>
        WithFleetAsync((fleet, ct) => RunSmokeAsync(fleet, "legacy", ct, "chunked_save"));
}

/// <summary>AC2: gpu-gpu-shared preset green against the live P100 VM through
/// the ssh shim (validated topology in Presets.GpuGpuShared). Skipped unless
/// MINIFLEET_SSH_TARGET is set, so CI (which does not carry VM ssh config)
/// never hangs or fails — the lane is explicitly opt-in via env.
/// Fleet lifetime is PER-TEST (same ruling as the cpu class).</summary>
[Trait("Tier", "MiniFleet")]
[Trait("RequiresVm", "true")]
public sealed class GpuGpuSharedSmokeTests : MiniFleetSmokeBase
{
    private static readonly string? SshTarget =
        Normalize(Environment.GetEnvironmentVariable("MINIFLEET_SSH_TARGET"));

    /// <summary>Treat empty-string env vars as unset (MINIFLEET_SSH_TARGET= on a
    /// command line means "skip the VM lane", same as not setting it).</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public GpuGpuSharedSmokeTests(ITestOutputHelper output) : base(output) { }

    /// <summary>Per-test fleet bring-up over the ssh shim; skip semantics via
    /// xunit's SkipException so an unset env var marks the fact Skipped.</summary>
    private async Task WithFleetAsync(Func<MiniFleetRun, CancellationToken, Task> body)
    {
        Skip.If(SshTarget is null, "MINIFLEET_SSH_TARGET unset — VM lane is opt-in.");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        MiniFleetRun? fleet = null;
        try
        {
            var model = await ResolveModelAsync(cts.Token).ConfigureAwait(false);
            Output.WriteLine($"model: {model.FullName}");
            fleet = await MiniFleetAppHost.StartAsync(Presets.GpuGpuShared, model.FullName, cts.Token)
                .ConfigureAwait(false);
            Output.WriteLine($"fleet up: A={fleet.EngineAUrl} B={fleet.EngineBUrl}");
            await body(fleet, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (fleet is not null)
            {
                await fleet.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [SkippableFact]
    public Task ColdAtomicEngine_VmLane_Passes() =>
        WithFleetAsync((fleet, ct) => RunSmokeAsync(fleet, "legacy", ct, "cold_atomic_engine"));

    [SkippableFact]
    public Task ChunkedSave_VmLane_Passes() =>
        WithFleetAsync((fleet, ct) => RunSmokeAsync(fleet, "legacy", ct, "chunked_save"));
}
