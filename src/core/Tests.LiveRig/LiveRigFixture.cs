using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Session-scoped fixture for live-rig tests. Warms once per test collection;
/// tests wait on 60-120s model loads and real decode, so re-warming per class
/// would make the suite take 10x longer for no benefit.
///
/// Health-checks the Coordinator at COORD_URL before running tests. If
/// unreachable, tests using this fixture skip (not fail).
///
/// Uses static shared state so the health probe runs exactly once across all
/// test classes in the "LiveRig" collection — xUnit's IClassFixture creates a
/// new instance per class, but the static state ensures the expensive probe
/// only executes once.
/// </summary>
public sealed class LiveRigFixture : IAsyncLifetime
{
    // ── Static shared state: probe runs once for the entire collection ──
    // xUnit creates a new LiveRigFixture instance per test class even within
    // the same collection.  A static Lazy<T> ensures the health probe + model
    // readiness check executes exactly once across the whole test run.
    private static readonly object _probeLock = new();
    private static bool _probeCompleted;
    private static bool _probeResult;
    private static string _probeHealthStatus = "not-probed";
    private static List<string> _probeLog = [];

    public string CoordUrl { get; } =
        Environment.GetEnvironmentVariable("COORD_URL") ?? "http://localhost:9000";

    public string LlamaRtxUrl { get; } =
        Environment.GetEnvironmentVariable("LLAMA_RTX_URL") ?? "http://localhost:8080";

    public string LlamaP100Url { get; } =
        Environment.GetEnvironmentVariable("LLAMA_P100_URL") ?? "http://192.168.122.21:8086";

    public string CoordMetricsUrl { get; } =
        Environment.GetEnvironmentVariable("COORD_METRICS_URL") ?? "http://localhost:9501/metrics";

    public string StoreHost { get; } =
        Environment.GetEnvironmentVariable("STORE_HOST") ?? "127.0.0.1";

    public int StorePort { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("STORE_PORT"), out var p) ? p : 9500;

    /// <summary>Whether the live stack is reachable (set during InitializeAsync).</summary>
    public bool IsHealthy { get; private set; }

    /// <summary>Health status string from the Coordinator.</summary>
    public string HealthStatus { get; private set; } = "unknown";

    // Retry budget: poll every 5s for up to 120s to handle model load times.
    private const int RetryIntervalMs = 5_000;
    private const int MaxRetryMs = 120_000;

    public Task InitializeAsync()
    {
        lock (_probeLock)
        {
            if (_probeCompleted)
            {
                IsHealthy = _probeResult;
                HealthStatus = _probeHealthStatus;
                return Task.CompletedTask;
            }
        }

        // Probe runs outside the lock so concurrent InitializeAsync calls
        // don't deadlock; the lock is re-acquired only when writing the result.
        return RunProbeAsync();
    }

    private async Task RunProbeAsync()
    {
        var log = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Created once for the whole completion-probe phase so retries reuse the
        // same session instead of leaking a new one per attempt.
        string? probeSessionId = null;

        try
        {
            log.Add($"[t={sw.ElapsedMilliseconds}ms] Starting health probe for {CoordUrl}");

            // ── Phase 1: retry-loop health endpoint ──
            // A freshly-deployed rig's model load takes 60-120s (per CLAUDE.md).
            // Poll /health every 5s up to 120s to distinguish "still warming up"
            // from "actually down".
            bool healthOk = false;
            string lastHealthStatus = "unknown";
            while (sw.ElapsedMilliseconds < MaxRetryMs)
            {
                try
                {
                    using var healthCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var healthResp = await HttpHelpers.Client.GetAsync($"{CoordUrl}/health", healthCts.Token);
                    log.Add($"[t={sw.ElapsedMilliseconds}ms] GET /health → {(int)healthResp.StatusCode}");

                    if (healthResp.IsSuccessStatusCode)
                    {
                        var healthBody = await healthResp.Content.ReadFromJsonAsync<HealthResponse>();
                        lastHealthStatus = healthBody?.Status ?? "unknown";
                        log.Add($"[t={sw.ElapsedMilliseconds}ms] Health status: {lastHealthStatus}");

                        if (lastHealthStatus is "healthy" or "degraded")
                        {
                            healthOk = true;
                            break;
                        }
                    }
                    else
                    {
                        lastHealthStatus = $"http-{(int)healthResp.StatusCode}";
                    }
                }
                catch (HttpRequestException ex)
                {
                    lastHealthStatus = $"connection-refused: {ex.Message}";
                    log.Add($"[t={sw.ElapsedMilliseconds}ms] Health check failed: {ex.Message}");
                }
                catch (TaskCanceledException)
                {
                    lastHealthStatus = "timeout";
                    log.Add($"[t={sw.ElapsedMilliseconds}ms] Health check timed out");
                }

                log.Add($"[t={sw.ElapsedMilliseconds}ms] Retrying health check in {RetryIntervalMs}ms...");
                await Task.Delay(RetryIntervalMs);
            }

            if (!healthOk)
            {
                var diag = $"Coordinator unreachable at {CoordUrl} after {sw.ElapsedMilliseconds}ms. " +
                    $"Last status: {lastHealthStatus}. " +
                    $"Log: [{string.Join("; ", log)}]. " +
                    "Set COORD_URL and ensure the full stack is running.";
                lock (_probeLock)
                {
                    _probeCompleted = true;
                    _probeResult = false;
                    _probeHealthStatus = diag;
                    _probeLog = log;
                    IsHealthy = false;
                    HealthStatus = diag;
                }
                return;
            }

            // ── Phase 2: verify the stack can process a completion request ──
            // The coordinator may report "healthy" while backends are still
            // warming up (503 on completions). Poll until the first successful
            // completion or the budget is exhausted.
            log.Add($"[t={sw.ElapsedMilliseconds}ms] Health OK, probing completions endpoint...");
            probeSessionId = $"live-rig-probe-{Guid.NewGuid():N}"[..20];
            while (sw.ElapsedMilliseconds < MaxRetryMs)
            {
                try
                {
                    using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var probeBody = new Dictionary<string, object?>
                    {
                        ["messages"] = new[] { new { role = "user", content = "Say ok." } },
                        ["max_tokens"] = 4,
                        ["temperature"] = 0,
                        ["stream"] = false,
                        ["session_id"] = probeSessionId,
                    };
                    var probeResp = await HttpHelpers.Client.PostAsJsonAsync(
                        $"{CoordUrl}/v1/chat/completions", probeBody, probeCts.Token);
                    log.Add($"[t={sw.ElapsedMilliseconds}ms] POST /v1/chat/completions → {(int)probeResp.StatusCode}");

                    if (probeResp.IsSuccessStatusCode)
                    {
                        var probeJson = await probeResp.Content.ReadFromJsonAsync<JsonElement>();
                        var hasChoices = probeJson.TryGetProperty("choices", out _);
                        log.Add($"[t={sw.ElapsedMilliseconds}ms] Probe response has choices: {hasChoices}");

                        // Best-effort cleanup of the probe session on the success
                        // path so live-rig runs do not leak a session per probe.
                        await DeleteSessionAsync(probeSessionId);

                        lock (_probeLock)
                        {
                            _probeCompleted = true;
                            _probeResult = true;
                            _probeHealthStatus = lastHealthStatus;
                            _probeLog = log;
                            IsHealthy = true;
                            HealthStatus = lastHealthStatus;
                        }
                        return;
                    }
                    else
                    {
                        var bodyText = await probeResp.Content.ReadAsStringAsync();
                        log.Add($"[t={sw.ElapsedMilliseconds}ms] Probe failed: {bodyText[..Math.Min(200, bodyText.Length)]}");
                    }
                }
                catch (Exception ex)
                {
                    log.Add($"[t={sw.ElapsedMilliseconds}ms] Probe exception: {ex.Message}");
                }

                log.Add($"[t={sw.ElapsedMilliseconds}ms] Retrying completion probe in {RetryIntervalMs}ms...");
                await Task.Delay(RetryIntervalMs);
            }

            // Exhausted budget: backends never became ready
            var diagFail = $"Coordinator at {CoordUrl} responds to /health ({lastHealthStatus}) " +
                $"but completions never succeeded after {sw.ElapsedMilliseconds}ms. " +
                $"Log: [{string.Join("; ", log)}]. " +
                "Backends may still be loading models (expect 60-120s).";
            if (probeSessionId is not null)
                await DeleteSessionAsync(probeSessionId);
            lock (_probeLock)
            {
                _probeCompleted = true;
                _probeResult = false;
                _probeHealthStatus = diagFail;
                _probeLog = log;
                IsHealthy = false;
                HealthStatus = diagFail;
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup on the failure path too — a session may have
            // been created before the exception was thrown.
            if (probeSessionId is not null)
                await DeleteSessionAsync(probeSessionId);
            var diag = $"Unexpected probe failure at {CoordUrl}: {ex}. " +
                $"Log: [{string.Join("; ", log)}].";
            lock (_probeLock)
            {
                _probeCompleted = true;
                _probeResult = false;
                _probeHealthStatus = diag;
                _probeLog = log;
                IsHealthy = false;
                HealthStatus = diag;
            }
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Call at the top of every SkippableFact that needs the live rig.
    /// Skips the test (instead of failing) when the stack is unreachable.
    /// </summary>
    public void SkipIfUnreachable()
    {
        Skip.IfNot(IsHealthy, HealthStatus);
    }

    /// <summary>GET {CoordUrl}/status and return the parsed JSON.</summary>
    public async Task<StatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return await HttpHelpers.Client.GetFromJsonAsync<StatusResponse>(
            $"{CoordUrl}/status", cts.Token) ?? new StatusResponse();
    }

    /// <summary>DELETE {CoordUrl}/sessions/{sessionId} — best-effort cleanup.</summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await HttpHelpers.Client.DeleteAsync($"{CoordUrl}/sessions/{sessionId}", cts.Token);
        }
        catch
        {
            // Best-effort cleanup; ignore failures.
        }
    }

    // ── DTOs ──────────────────────────────────────────────────────────────

    public sealed class HealthResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    public sealed class StatusResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("sessions")]
        public SessionsWrapper? Sessions { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("routing_stats")]
        public RoutingStats? RoutingStats { get; set; }
    }

    public sealed class SessionsWrapper
    {
        [System.Text.Json.Serialization.JsonPropertyName("active")]
        public int Active { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("sessions")]
        public List<SessionInfo> Sessions { get; set; } = [];
    }

    public sealed class SessionInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("node")]
        public string? Node { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("slot_id")]
        public int? SlotId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("n_past")]
        public int? NPast { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("slot_freed")]
        public bool? SlotFreed { get; set; }
    }

    public sealed class RoutingStats
    {
        [System.Text.Json.Serialization.JsonPropertyName("total")]
        public int Total { get; set; }
    }
}
