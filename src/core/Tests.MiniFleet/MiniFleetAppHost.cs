using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.MiniFleet;

/// <summary>Handles to a started mini-fleet: the Aspire app plus resolved base URLs
/// for the sandbox coordinator and the two real engine nodes.</summary>
public sealed record MiniFleetRun(
    DistributedApplication App,
    string CoordinatorBaseUrl,
    string EngineAUrl,
    string EngineBUrl);

/// <summary>
/// Mini-fleet smoke tier — real-engine multi-node scenario runner
/// (spec of record: orchestration/state/tasks/2026-08-27-minifleet.md §Components 1).
///
/// Aspire DistributedApplication host: boots a sandbox Hydra.Core + REAL
/// llama-engine processes as ExecutableResources. No FakeLlamaEngine in this
/// tier — the point is validating implementation changes against real wire
/// behavior before the expensive rigs are touched.
///
/// Engine quirks honored here (owner-verified, brief "Engine quirks you MUST honor"):
/// #1 explicit distinct --rpc-port per node (else engine auto-uses port+1 and collides);
/// #2 LD_LIBRARY_PATH set only when a lib dir resolves (engine build prefix, $ORIGIN absent);
/// #3 /health returns {"status":"ok"} — wired as the Aspire HTTP health check.
/// </summary>
public static class MiniFleetAppHost
{
    /// <summary>A/B hook (brief §Components 1): when set, plumbed into hydra-core
    /// so legacy-vs-v2 scheduler passes run against identical topology.</summary>
    public const string SchedulerImplEnvVar = "HYDRA_SCHEDULER_IMPL";

    private const string LdLibraryPathEnvVar = "MINIFLEET_LD_LIBRARY_PATH";
    private const string DefaultEngineBin = "~/hydra-min-test/llama-engine";
    private const string DefaultLdLibraryPath = "~/hydra-min-test";
    private const int CoordinatorPort = 19000;

    /// <summary>Entry point. cpu-2node runs fully local (no GPU); the
    /// gpu-gpu-shared ssh-shim lane is the next implementation step.</summary>
    public static Task<MiniFleetRun> StartAsync(
        MiniFleetPreset preset, string modelPath, CancellationToken ct = default)
    {
        if (preset.ViaSshShim)
        {
            throw new NotImplementedException(
                "gpu-gpu-shared (P100 VM ssh shim) lane is not implemented yet — " +
                "follow-up step per orchestration/state/tasks/2026-08-27-minifleet.md.");
        }
        return StartCpuTwoNodeAsync(preset, modelPath, ct);
    }

    public static Task<MiniFleetRun> StartCpuTwoNodeAsync(
        string modelPath, CancellationToken ct = default) =>
        StartAsync(Presets.Cpu2Node, modelPath, ct);

    private static async Task<MiniFleetRun> StartCpuTwoNodeAsync(
        MiniFleetPreset preset, string modelPath, CancellationToken ct)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Model artifact not found: {modelPath}. " +
                $"Resolve it via Artifacts.EnsureModelAsync() or set {Artifacts.ModelPathEnvVar}.",
                modelPath);
        }

        var engineBinary = ResolveEngineBinary();
        var builder = DistributedApplication.CreateBuilder();

        // ── PostgreSQL (Hydra.Core StoreMetadata requires it) — hermetic, no volume,
        //    mirroring Hydra.AppHost's E2E path (#531). ──────────────────────────
        var pgPassword = builder.AddParameter("pg-password", "hydra-test-pw");
        var postgres = builder.AddPostgres("postgres")
            .WithImageTag("16")
            .WithPassword(pgPassword);
        var hydraDb = postgres.AddDatabase("hydra-store");

        // ── REAL llama-engine nodes (quirks #1/#2/#3) ────────────────────────
        var engineA = RegisterEngine(builder, "engine-a", engineBinary,
            preset.EnginePortA, preset.RpcPortA, preset.NglA,
            preset.ThreadsPerEngine, preset.ContextSize, modelPath);
        var engineB = RegisterEngine(builder, "engine-b", engineBinary,
            preset.EnginePortB, preset.RpcPortB, preset.NglB,
            preset.ThreadsPerEngine, preset.ContextSize, modelPath);

        // ── Sandbox Hydra.Core coordinator (mirrors Hydra.AppHost wiring) ────
        var workersJson = $$"""
            [{"name":"engine-a","host":"localhost","rpc_port":{{preset.RpcPortA}},"llama_rpc_port":{{preset.RpcPortA}},"llama_url":"http://localhost:{{preset.EnginePortA}}","worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2},{"name":"engine-b","host":"localhost","rpc_port":{{preset.RpcPortB}},"llama_rpc_port":{{preset.RpcPortB}},"llama_url":"http://localhost:{{preset.EnginePortB}}","worker_type":2,"slots":1,"prefill_priority":100,"decode_priority":1}]
            """;

        var hydraCore = builder.AddProject<Projects.Hydra_Core>("hydra-core")
            .WithHttpEndpoint(targetPort: CoordinatorPort, name: "http")
            .WithReference(hydraDb)
            .WithEnvironment("HYDRA_COORD_ENABLED", "true")
            .WithEnvironment("HYDRA_COORD_PORT", CoordinatorPort.ToString())
            .WithEnvironment("HYDRA_COORD_WORKERS", workersJson)
            .WithEnvironment("HYDRA_STORE_PORT", "19500")
            .WithEnvironment("HYDRA_STORE_DEBUG_PORT", "19501")
            .WithEnvironment("HYDRA_STORE_HOST", "0.0.0.0")
            .WithEnvironment("HYDRA_STORE_DIR", "/tmp/hydra-store-minifleet")
            .WithEnvironment("HYDRA_COORD_STORE_PORT", "19500")
            .WithEnvironment("HYDRA_COORD_NO_STORE_KV_RESTORE", "true");

        var schedulerImpl = Environment.GetEnvironmentVariable(SchedulerImplEnvVar);
        if (!string.IsNullOrWhiteSpace(schedulerImpl))
        {
            hydraCore = hydraCore.WithEnvironment(SchedulerImplEnvVar, schedulerImpl);
        }

        var app = builder.Build();
        await app.StartAsync(ct).ConfigureAwait(false);

        // Readiness gate (quirk #3): engines healthy via GET /health.
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceHealthyAsync("engine-a", ct).ConfigureAwait(false);
        await notifications.WaitForResourceHealthyAsync("engine-b", ct).ConfigureAwait(false);

        return new MiniFleetRun(
            App: app,
            CoordinatorBaseUrl: app.GetEndpoint("hydra-core", "http").ToString().TrimEnd('/'),
            EngineAUrl: app.GetEndpoint("engine-a", "http").ToString().TrimEnd('/'),
            EngineBUrl: app.GetEndpoint("engine-b", "http").ToString().TrimEnd('/'));
    }

    /// <summary>One REAL llama-engine ExecutableResource with brief-exact argv:
    /// --host/--port/--rpc-port/--n-gpu-layers/-t/-c/--model. Health = GET /health.</summary>
    private static IResourceBuilder<ExecutableResource> RegisterEngine(
        IDistributedApplicationBuilder builder,
        string name,
        string binary,
        int httpPort,
        int rpcPort,
        int ngl,
        int threads,
        int contextSize,
        string modelPath)
    {
        var workingDirectory = Path.GetDirectoryName(binary) ?? Directory.GetCurrentDirectory();
        var engine = builder
            .AddExecutable(
                name, binary, workingDirectory,
                "--host", "127.0.0.1",
                "--port", httpPort.ToString(),
                "--rpc-port", rpcPort.ToString(),
                "--n-gpu-layers", ngl.ToString(),
                "-t", threads.ToString(),
                "-c", contextSize.ToString(),
                "--model", modelPath)
            .WithHttpEndpoint(targetPort: httpPort, name: "http", isProxied: false)
            .WithHttpHealthCheck("/health");

        // Quirk #2: only set LD_LIBRARY_PATH when a dir actually resolves.
        var ldLibraryPath = ResolveLdLibraryPath();
        if (ldLibraryPath is not null)
        {
            engine = engine.WithEnvironment("LD_LIBRARY_PATH", ldLibraryPath);
        }
        return engine;
    }

    /// <summary>MINIFLEET_ENGINE_BIN override, else the staged ~/hydra-min-test
    /// binary; throws with guidance when neither exists (never a silent default).</summary>
    private static string ResolveEngineBinary()
    {
        var bin = Artifacts.ResolveEngineBinary();
        if (bin is null)
        {
            var staged = ExpandHome(DefaultEngineBin);
            if (File.Exists(staged))
            {
                bin = staged;
            }
            else
            {
                throw new InvalidOperationException(
                    $"No llama-engine binary found. Stage it at {DefaultEngineBin} " +
                    $"or set {Artifacts.EngineBinEnvVar}.");
            }
        }
        return Path.GetFullPath(ExpandHome(bin));
    }

    /// <summary>MINIFLEET_LD_LIBRARY_PATH override, else the engine build prefix
    /// dir when it exists (quirk #2), else null (env var not emitted).</summary>
    private static string? ResolveLdLibraryPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(LdLibraryPathEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }
        var staged = ExpandHome(DefaultLdLibraryPath);
        return Directory.Exists(staged) ? staged : null;
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, path[1..].TrimStart('/')));
        }
        return Environment.ExpandEnvironmentVariables(path);
    }
}
