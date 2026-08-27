using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.MiniFleet;

/// <summary>Handles to a started mini-fleet: the Aspire app (cpu lane) plus resolved
/// base URLs for the sandbox coordinator and the two real engine nodes. The ssh-shim
/// lane has no Aspire app — <see cref="MiniFleetRun.App"/> is null and lifecycle is
/// owned by <see cref="SshShimFleet"/>.</summary>
public sealed record MiniFleetRun(
    DistributedApplication? App,
    string CoordinatorBaseUrl,
    string EngineAUrl,
    string EngineBUrl,
    MiniFleetPreset Preset,
    string PresetName,
    IAsyncDisposable? Lifecycle = null)
{
    public bool ViaSshShim => Preset.ViaSshShim;

    public async ValueTask DisposeAsync() =>
        await (Lifecycle?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
}

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

    /// <summary>Entry point. cpu-2node runs fully local (no GPU, Aspire-hosted
    /// via the REAL Tests.MiniFleet.AppHost project — dcp/dashboard metadata comes
    /// from its AssemblyInfo, which an in-test-assembly builder cannot provide);
    /// gpu-gpu-shared drives the P100 VM through scripts/minifleet/vm-run.sh.</summary>
    public static async Task<MiniFleetRun> StartAsync(
        MiniFleetPreset preset, string modelPath, CancellationToken ct = default)
    {
        if (preset.ViaSshShim)
        {
            return await SshShimFleet.StartAsync(preset, modelPath, ct).ConfigureAwait(false);
        }
        return await StartCpuTwoNodeAsync(preset, modelPath, ct).ConfigureAwait(false);
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

        // Configuration travels via env vars (AppHost Program.cs-independent
        // testing path): the topology builder reads these inside the AppHost.
        Environment.SetEnvironmentVariable("MINIFLEET_ENGINE_BIN", engineBinary);
        Environment.SetEnvironmentVariable("MINIFLEET_MODEL_PATH", modelPath);
        Environment.SetEnvironmentVariable("MINIFLEET_PRESET_PORTS",
            $"{preset.EnginePortA}:{preset.RpcPortA}:{preset.NglA}:" +
            $"{preset.EnginePortB}:{preset.RpcPortB}:{preset.NglB}:" +
            $"{preset.ThreadsPerEngine}:{preset.ContextSize}");

        // Real AppHost project bootstrap (consultant diagnosis: in-test-assembly
        // DistributedApplication.CreateBuilder() lacks dcpclipath metadata →
        // Options validation failure at StartAsync). configureBuilder must be a
        // no-op lambda, NOT null — the 4-arg overload throws ArgumentNullException.
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Tests_MiniFleet_AppHost>(
                args: [], configureBuilder: static (_, _) => { }, cancellationToken: ct)
            .ConfigureAwait(false);

        var app = builder.Build();
        await app.StartAsync(ct).ConfigureAwait(false);

        // AC1-r6 instrumentation (consultant diagnosis: /health HANGS — connect
        // OK, no bytes, 120s cancel; topology matches E2E, so instrument first):
        // mirror resource logs to tests/logs/<runid>/{resource}.log so engine and
        // coordinator boot output is captured for the verdict.
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var logsDir = Path.Combine(RepoRoot(), "tests", "logs", runId);
        Directory.CreateDirectory(logsDir);
        var loggers = app.Services.GetRequiredService<ResourceLoggerService>();
        foreach (var resourceName in new[] { "hydra-core", "engine-a", "engine-b", "postgres" })
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in loggers.WatchAsync(resourceName)
                        .WithCancellation(ct).ConfigureAwait(false))
                    {
                        foreach (var logLine in batch)
                        {
                            var level = logLine.IsErrorMessage ? "ERR" : "INF";
                            await File.AppendAllTextAsync(
                                Path.Combine(logsDir, $"{resourceName}.log"),
                                $"[{level}] {logLine.Content}{Environment.NewLine}",
                                ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { /* fleet shutdown */ }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[minifleet] log watch {resourceName}: {ex.Message}");
                }
            }, CancellationToken.None);
        }

        // Readiness gates, ordered (fail fast with resource states on timeout):
        // postgres must be Running before hydra-core's StoreMetadata can connect;
        // hydra-core Running before the engines' traffic matters.
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await WaitForRunningAsync(notifications, "postgres", logsDir, ct).ConfigureAwait(false);
        await WaitForRunningAsync(notifications, "hydra-core", logsDir, ct).ConfigureAwait(false);
        await WaitForRunningAsync(notifications, "engine-a", logsDir, ct).ConfigureAwait(false);
        await WaitForRunningAsync(notifications, "engine-b", logsDir, ct).ConfigureAwait(false);
        await WaitForHealthyAsync(notifications, "engine-a", logsDir, ct).ConfigureAwait(false);
        await WaitForHealthyAsync(notifications, "engine-b", logsDir, ct).ConfigureAwait(false);

        // 6x retry health probe (5s gap) — AC1-r6: each attempt's URL + status is
        // logged to tests/logs/<runid>/probe.log so the hang becomes diagnosable.
        var coordinatorUrl = app.GetEndpoint("hydra-core", "http").ToString().TrimEnd('/');
        await ProbeHealthWithRetriesAsync(coordinatorUrl, logsDir, viaSshShim: false, ct)
            .ConfigureAwait(false);
        foreach (var (name, port) in new[] { ("engine-a", preset.EnginePortA), ("engine-b", preset.EnginePortB) })
        {
            await ProbeHealthWithRetriesAsync(
                app.GetEndpoint(name, "http").ToString().TrimEnd('/'), logsDir,
                viaSshShim: false, ct, label: name).ConfigureAwait(false);
            _ = port; // ports documented in preset; endpoints resolved via Aspire
        }

        return new MiniFleetRun(
            App: app,
            CoordinatorBaseUrl: coordinatorUrl,
            EngineAUrl: app.GetEndpoint("engine-a", "http").ToString().TrimEnd('/'),
            EngineBUrl: app.GetEndpoint("engine-b", "http").ToString().TrimEnd('/'),
            Preset: preset,
            PresetName: preset.Name);
    }

    /// <summary>Waits for a resource to reach Running; on 90s timeout throws with
    /// a snapshot of ALL resource states (the fail-fast diagnosis payload).</summary>
    private static async Task WaitForRunningAsync(
        ResourceNotificationService notifications, string resourceName,
        string logsDir, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await notifications
                .WaitForResourceAsync(resourceName, KnownResourceStates.Running, timeoutCts.Token)
                .ConfigureAwait(false);
            await File.AppendAllTextAsync(
                Path.Combine(logsDir, "probe.log"),
                $"[{DateTime.UtcNow:HH:mm:ss}] {resourceName}: Running{Environment.NewLine}",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var states = await SnapshotResourceStatesAsync(notifications).ConfigureAwait(false);
            await File.AppendAllTextAsync(
                Path.Combine(logsDir, "probe.log"),
                $"[{DateTime.UtcNow:HH:mm:ss}] {resourceName}: TIMEOUT. states: {states}{Environment.NewLine}",
                CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException(
                $"Resource '{resourceName}' did not reach Running within 90s. States: {states}");
        }
    }

    private static async Task WaitForHealthyAsync(
        ResourceNotificationService notifications, string resourceName,
        string logsDir, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await notifications.WaitForResourceHealthyAsync(resourceName, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var states = await SnapshotResourceStatesAsync(notifications).ConfigureAwait(false);
            throw new TimeoutException(
                $"Resource '{resourceName}' did not become Healthy within 90s. States: {states}");
        }
    }

    /// <summary>Health probe with 6 retries (5s gap) — every attempt's URL and
    /// outcome appended to tests/logs/&lt;runid&gt;/probe.log (AC1-r6 instrumentation).</summary>
    private static async Task ProbeHealthWithRetriesAsync(
        string baseUrl, string logsDir, bool viaSshShim, CancellationToken ct, string label = "coordinator")
    {
        var probeLog = Path.Combine(logsDir, "probe.log");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestCts.CancelAfter(viaSshShim
                ? TimeSpan.FromSeconds(180)
                : TimeSpan.FromSeconds(120));
            string outcome;
            try
            {
                using var response = await http.GetAsync($"{baseUrl}/health", requestCts.Token)
                    .ConfigureAwait(false);
                outcome = $"HTTP {(int)response.StatusCode}";
                if (response.IsSuccessStatusCode)
                {
                    await File.AppendAllTextAsync(probeLog,
                        $"[{DateTime.UtcNow:HH:mm:ss}] {label} {baseUrl}/health attempt {attempt}: {outcome} OK{Environment.NewLine}",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                outcome = ex is TaskCanceledException ? "TIMEOUT" : ex.Message;
            }
            await File.AppendAllTextAsync(probeLog,
                $"[{DateTime.UtcNow:HH:mm:ss}] {label} {baseUrl}/health attempt {attempt}: {outcome}{Environment.NewLine}",
                CancellationToken.None).ConfigureAwait(false);
            if (attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        throw new TimeoutException(
            $"{label} /health at {baseUrl} never succeeded after 6 attempts — see {probeLog}");
    }

    /// <summary>Compact name→state dump of the resources we gate on (fail-fast
    /// exception payload; also mirrored to probe.log by callers).</summary>
    private static Task<string> SnapshotResourceStatesAsync(
        ResourceNotificationService notifications)
    {
        var names = new[] { "postgres", "hydra-core", "engine-a", "engine-b" };
        var lines = names.Select(name =>
        {
            try
            {
                if (notifications.TryGetCurrentState(name, out var evt))
                {
                    return $"{name}={evt.Snapshot.State?.Text ?? "unknown"}";
                }
                return $"{name}=no-event";
            }
            catch (Exception ex)
            {
                return $"{name}=(snapshot failed: {ex.Message})";
            }
        });
        return Task.FromResult(string.Join(", ", lines));
    }

    /// <summary>Walks up from the test assembly to the repo root (tests/logs/
    /// &lt;runid&gt; lives there); honored override: MINIFLEET_REPO_ROOT.</summary>
    internal static string RepoRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("MINIFLEET_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot) &&
            Directory.Exists(Path.Combine(overrideRoot, "tests")))
        {
            return overrideRoot;
        }
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "tests")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                return dir;
            }
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new InvalidOperationException(
            $"Could not locate repo root from {AppContext.BaseDirectory}. " +
            "Set MINIFLEET_REPO_ROOT to the repo checkout root.");
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
                    $"or set {Artifacts.EngineBinEnvVar}. For the cpu CI lane, build the " +
                    "PR-pinned fork with GGML_CUDA=OFF (see scripts/minifleet/) and point " +
                    "MINIFLEET_ENGINE_BIN at build_cpu/bin/llama-engine.");
            }
        }
        return Path.GetFullPath(ExpandHome(bin));
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

/// <summary>
/// gpu-gpu-shared lane: drives the P100 VM through scripts/minifleet/vm-run.sh
/// (start/status/stop) instead of Aspire. Engines run on the VM at
/// 127.0.0.1:{8088,8089} — the test host reaches them via ssh port-forward
/// (LocalForward through the same ssh connection used for control).
///
/// Safety: start/stop ONLY act on pids whose cmdline matches the exact run
/// signature enforced inside vm-run.sh (llama-engine + Qwen3.5-9B-Q4_K_M +
/// qwen-2node alias). Residents (:8086 prod, :8090 upstream) are untouchable.
/// </summary>
public sealed class SshShimFleet : IAsyncDisposable
{
    private const string ScriptPath = "scripts/minifleet/vm-run.sh";
    private const string SshTargetEnvVar = "MINIFLEET_SSH_TARGET";
    private const string SshTargetDefault = "hydra-p100";
    private const int EngineAPort = 8088;
    private const int EngineBPort = 8089;

    private readonly string _sshTarget;
    private System.Diagnostics.Process? _tunnelA;
    private System.Diagnostics.Process? _tunnelB;

    private SshShimFleet(string sshTarget) => _sshTarget = sshTarget;

    public static async Task<MiniFleetRun> StartAsync(
        MiniFleetPreset preset, string modelPath, CancellationToken ct)
    {
        if (preset.Name != Presets.GpuGpuShared.Name)
        {
            throw new ArgumentException(
                $"ssh-shim lane only supports the '{Presets.GpuGpuShared.Name}' preset " +
                $"(got '{preset.Name}').", nameof(preset));
        }

        var sshTarget = Environment.GetEnvironmentVariable(SshTargetEnvVar);
        if (string.IsNullOrWhiteSpace(sshTarget))
        {
            // Defense-in-depth: SmokeTests facts already Skip.If-gate before
            // calling StartAsync, so this only fires on direct misuse.
            // (Xunit.Sdk.SkipException is NOT usable here — xunit.assert ships a
            // shadowing type with a private (string) ctor.)
            throw new InvalidOperationException(
                "MINIFLEET_SSH_TARGET unset — VM lane is opt-in; CI must not hang here.");
        }
        var fleet = new SshShimFleet(sshTarget);

        // 1. Launch both engines on the VM (idempotent; health-gated inside).
        await fleet.RunScriptAsync("start", ct).ConfigureAwait(false);

        // 2. Forward engine ports to localhost so the runner hits them like
        //    any other lane. Two dedicated tunnels per fleet instance.
        try
        {
            fleet._tunnelA = fleet.OpenTunnel(EngineAPort);
            fleet._tunnelB = fleet.OpenTunnel(EngineBPort);
        }
        catch
        {
            await fleet.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // 3. Health-gate through the tunnels (quirk #3: {"status":"ok"}).
        var urlA = $"http://127.0.0.1:{EngineAPort}";
        var urlB = $"http://127.0.0.1:{EngineBPort}";
        await WaitHealthyAsync(urlA, ct).ConfigureAwait(false);
        await WaitHealthyAsync(urlB, ct).ConfigureAwait(false);

        // 4. No sandbox coordinator on this lane yet: scenarios talk to the
        //    engines directly. CoordinatorBaseUrl points at node A as a
        //    placeholder until the coordinator-on-VM step lands.
        return new MiniFleetRun(
            App: null,
            CoordinatorBaseUrl: urlA,
            EngineAUrl: urlA,
            EngineBUrl: urlB,
            Preset: preset,
            PresetName: preset.Name,
            Lifecycle: fleet);
    }

    /// <summary>Local ssh tunnel: localhost:port → VM 127.0.0.1:port. -o ExitOnForwardFailure
    /// + BatchMode so failures surface immediately instead of hanging the gate.</summary>
    private System.Diagnostics.Process OpenTunnel(int port)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh",
            ArgumentList =
            {
                "-N",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                "-L", $"{port}:127.0.0.1:{port}",
                _sshTarget,
            },
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch ssh tunnel process.");
        // Give ssh a moment to establish the forward; ExitOnForwardFailure makes
        // a bind failure exit nonzero quickly, which we surface as an exception.
        Thread.Sleep(750);
        if (proc.HasExited)
        {
            var err = proc.StandardError.ReadToEnd();
            proc.Dispose();
            throw new InvalidOperationException(
                $"ssh tunnel for port {port} exited immediately: {err}");
        }
        return proc;
    }

    /// <summary>Polls GET /health until {"status":"ok"} or timeout (quirk #3).</summary>
    private static async Task WaitHealthyAsync(string baseUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync($"{baseUrl}/health", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Engine at {baseUrl}/health never became healthy within 3 minutes." +
            (last is null ? "" : $" Last error: {last.Message}"));
    }

    /// <summary>Runs `bash scripts/minifleet/vm-run.sh &lt;verb&gt;` from the repo root,
    /// streaming output; throws on nonzero exit.</summary>
    private async Task<string> RunScriptAsync(string verb, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = RepoRootForScript(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add(verb);

        var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch vm-run.sh {verb}.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"vm-run.sh {verb} failed (exit {proc.ExitCode}): {stderr}{stdout}");
        }
        return stdout;
    }

    /// <summary>Walks up from the test assembly to find scripts/minifleet/vm-run.sh
    /// (tests run from arbitrary working directories under bin/). Falls back to
    /// the source-adjacent path recorded at build time via MSBuild : since the
    /// bin tree sits under the repo, upward traversal suffices there; for
    /// out-of-repo execution a MINIFLEET_REPO_ROOT override is honored.</summary>
    private static string RepoRootForScript() => MiniFleetAppHost.RepoRoot();

    public async ValueTask DisposeAsync()
    {
        // Tunnels first (they hold the port forwards), then engines on the VM.
        foreach (var tunnel in new[] { _tunnelA, _tunnelB })
        {
            if (tunnel is null)
            {
                continue;
            }
            try
            {
                if (!tunnel.HasExited)
                {
                    tunnel.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[minifleet] tunnel cleanup: {ex.Message}");
            }
            tunnel.Dispose();
        }

        try
        {
            await RunScriptAsync("stop", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[minifleet] vm-run stop failed: {ex.Message}");
        }
    }
}
