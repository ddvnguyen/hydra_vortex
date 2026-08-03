using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Session-scoped fixture for live-rig tests. Warms once per test collection;
/// tests wait on 60-120s model loads and real decode, so re-warming per class
/// would make the suite take 10x longer for no benefit.
///
/// Health-checks the Coordinator at COORD_URL before running tests. If
/// unreachable, tests using this fixture skip (not fail).
/// </summary>
public sealed class LiveRigFixture : IAsyncLifetime
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

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

    public async Task InitializeAsync()
    {
        try
        {
            // First check the health endpoint
            var resp = await SharedClient.GetFromJsonAsync<HealthResponse>($"{CoordUrl}/health");
            if (resp?.Status is not ("healthy" or "degraded"))
            {
                IsHealthy = false;
                HealthStatus = resp?.Status ?? "unknown";
                return;
            }

            // Health endpoint is OK — now verify the stack can actually process a request.
            // A coordinator may report "healthy" while backends are down (503 on completions).
            try
            {
                var probeBody = new Dictionary<string, object?>
                {
                    ["messages"] = new[] { new { role = "user", content = "Say ok." } },
                    ["max_tokens"] = 4,
                    ["temperature"] = 0,
                    ["stream"] = false,
                    ["session_id"] = $"live-rig-probe-{Guid.NewGuid():N}"[..20],
                };
                var probeResp = await SharedClient.PostAsJsonAsync($"{CoordUrl}/v1/chat/completions", probeBody);
                if (probeResp.IsSuccessStatusCode)
                {
                    IsHealthy = true;
                    HealthStatus = resp?.Status ?? "healthy";
                }
                else
                {
                    // Coordinator is up but backends can't serve — treat as unhealthy
                    IsHealthy = false;
                    HealthStatus = $"coordinator-up but completions-return-{(int)probeResp.StatusCode}";
                }
            }
            catch
            {
                IsHealthy = false;
                HealthStatus = "completions-unreachable";
            }
        }
        catch
        {
            IsHealthy = false;
            HealthStatus = "unreachable";
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Call at the top of every SkippableFact that needs the live rig.
    /// Skips the test (instead of failing) when the stack is unreachable.
    /// </summary>
    public void SkipIfUnreachable()
    {
        Skip.IfNot(IsHealthy,
            $"Live rig unreachable at {CoordUrl} (status={HealthStatus}). " +
            "Set COORD_URL and ensure the full stack is running.");
    }

    /// <summary>GET {CoordUrl}/status and return the parsed JSON.</summary>
    public async Task<StatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await client.GetFromJsonAsync<StatusResponse>(
            $"{CoordUrl}/status", ct) ?? new StatusResponse();
    }

    /// <summary>DELETE {CoordUrl}/sessions/{sessionId} — best-effort cleanup.</summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await client.DeleteAsync($"{CoordUrl}/sessions/{sessionId}");
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
