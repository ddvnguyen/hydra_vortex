using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Tests.MiniFleet.AppHost;

/// <summary>
/// Real AppHost project for the MiniFleet smoke tier.
///
/// WHY A SEPARATE PROJECT (consultant diagnosis 2026-08-28): building a
/// DistributedApplication inside the TEST assembly lacks the dcpclipath /
/// aspiredashboardpath build metadata that the Aspire.AppHost.Sdk injects into
/// a real AppHost's AssemblyInfo — Options validation then fails with
/// "Property CliPath: The path to the DCP executable ... is required".
/// Tests bootstrap via DistributedApplicationTestingBuilder
/// .CreateAsync&lt;Projects.Tests_MiniFleet_AppHost&gt;(), exactly like Tests.E2E
/// does with Hydra_AppHost.
///
/// Topology (was Tests.MiniFleet.MiniFleetAppHost.StartCpuTwoNodeAsync):
/// postgres + sandbox Hydra.Core coordinator + 2 REAL llama-engine
/// ExecutableResources with brief-exact argv. Engine quirks honored:
/// #1 explicit distinct --rpc-port per node; #2 LD_LIBRARY_PATH only when a
/// lib dir resolves; #3 /health = {"status":"ok"} as the HTTP health check.
/// </summary>
public static class Topology
{
    public const string SchedulerImplEnvVar = "HYDRA_SCHEDULER_IMPL";
    private const string LdLibraryPathEnvVar = "MINIFLEET_LD_LIBRARY_PATH";
    private const int CoordinatorPort = 19000;

    /// <summary>Registers the full cpu-2node topology onto the builder.
    /// Resource names are contract: engine-a / engine-b / hydra-core.</summary>
    public static IResourceBuilder<ExecutableResource>[] Build(
        IDistributedApplicationBuilder builder,
        string engineBinary,
        string modelPath,
        int enginePortA,
        int rpcPortA,
        int nglA,
        int enginePortB,
        int rpcPortB,
        int nglB,
        int threadsPerEngine,
        int contextSize)
    {
        // ── PostgreSQL (Hydra.Core StoreMetadata requires it) — hermetic, no
        //    volume, mirroring Hydra.AppHost's E2E path (#531). ──────────────
        var pgPassword = builder.AddParameter("pg-password", "hydra-test-pw");
        var postgres = builder.AddPostgres("postgres")
            .WithImageTag("16")
            .WithPassword(pgPassword);
        var hydraDb = postgres.AddDatabase("hydra-store");

        // ── REAL llama-engine nodes (quirks #1/#2/#3) ────────────────────────
        var engineA = RegisterEngine(builder, "engine-a", engineBinary,
            enginePortA, rpcPortA, nglA, threadsPerEngine, contextSize, modelPath);
        var engineB = RegisterEngine(builder, "engine-b", engineBinary,
            enginePortB, rpcPortB, nglB, threadsPerEngine, contextSize, modelPath);

        // ── Sandbox Hydra.Core coordinator (mirrors Hydra.AppHost wiring) ────
        var workersJson = $$"""
            [{"name":"engine-a","host":"localhost","rpc_port":{{rpcPortA}},"llama_rpc_port":{{rpcPortA}},"llama_url":"http://localhost:{{enginePortA}}","worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2},{"name":"engine-b","host":"localhost","rpc_port":{{rpcPortB}},"llama_rpc_port":{{rpcPortB}},"llama_url":"http://localhost:{{enginePortB}}","worker_type":2,"slots":1,"prefill_priority":100,"decode_priority":1}]
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

        return [engineA, engineB];
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

    /// <summary>MINIFLEET_LD_LIBRARY_PATH override, else the engine build prefix
    /// dir when it exists (quirk #2), else null (env var not emitted).</summary>
    private static string? ResolveLdLibraryPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(LdLibraryPathEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }
        // Engine binary's own directory works for the cpu build (libs are
        // linked next to / inside the bin dir).
        var bin = Environment.GetEnvironmentVariable("MINIFLEET_ENGINE_BIN");
        if (!string.IsNullOrWhiteSpace(bin) && Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(bin))))
        {
            return Path.GetDirectoryName(Path.GetFullPath(bin));
        }
        return null;
    }
}
