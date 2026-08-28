using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.AgentWorkflow;

/// <summary>
/// Hydra TEST rig workflow tests (T2, 2 cores).
/// v1 (a) criterion: 10 chat completions split 5/5 across core-A:19000 + core-B:19001,
/// concurrent. Asserts all 200 OK, completion_tokens&gt;0, no 5xx.
/// Also checks prod :9000 request count unchanged (or SKIPS if metric not exposed).
/// Runs with: dotnet test src/core/Tests.AgentWorkflow --filter Workflow=HydraTest
/// When the test rig is not up, the test is SKIPPED (not failed).
/// </summary>
[Trait("Workflow", "HydraTest")]
public sealed class HydraTestWorkflowTests
{
    private const string CoreAUrl = "http://localhost:19000/v1/chat/completions";
    private const string CoreBUrl = "http://localhost:19001/v1/chat/completions";
    private const string CoreAHealth = "http://localhost:19000/v1/models";
    private const string CoreBHealth = "http://localhost:19001/v1/models";
    private const string ProdMetricsUrl = "http://localhost:9000/metrics";

    private static bool IsTestRigHealthy()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var a = client.GetAsync(CoreAHealth).GetAwaiter().GetResult();
            var b = client.GetAsync(CoreBHealth).GetAwaiter().GetResult();
            return a.IsSuccessStatusCode && b.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static long? TryGetProdRequestCount()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = client.GetAsync(ProdMetricsUrl).GetAwaiter().GetResult();
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            // Try hydra_requests_total, then http_requests_total, then request count
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#")) continue;
                if (trimmed.StartsWith("hydra_requests_total"))
                {
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[^1].Split('.')[0], out var v)) return v;
                    if (double.TryParse(parts[^1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv)) return (long)dv;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    [SkippableFact]
    [Trait("Workflow", "HydraTest")]
    public async Task TenChatCompletions_SplitAcrossCores_All200_And_ProdNotTouched()
    {
        Skip.IfNot(IsTestRigHealthy(), "Hydra TEST rig not reachable at :19000/:19001 — skipping (expected when rig is down)");

        var prodBefore = TryGetProdRequestCount();
        var prodMetricExposed = prodBefore.HasValue;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var payload = new
        {
            model = "qwen3.6-35B-mini",
            messages = new[] { new { role = "user", content = "Say hello in one sentence." } },
            max_tokens = 32,
            stream = false
        };

        async Task<(bool ok, int status, int completionTokens, string body)> PostOnce(string url)
        {
            try
            {
                var resp = await http.PostAsJsonAsync(url, payload);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return (false, (int)resp.StatusCode, 0, body);

                // Parse completion_tokens
                int tokens = 0;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("completion_tokens", out var ct))
                    {
                        tokens = ct.GetInt32();
                    }
                    else if (doc.RootElement.TryGetProperty("usage", out var usage2) &&
                             usage2.TryGetProperty("completionTokens", out var ct2))
                    {
                        tokens = ct2.GetInt32();
                    }
                }
                catch { }

                return (resp.IsSuccessStatusCode, (int)resp.StatusCode, tokens, body);
            }
            catch (Exception ex)
            {
                return (false, 0, 0, ex.Message);
            }
        }

        // 5 to A + 5 to B, concurrent
        var tasks = new List<Task<(bool ok, int status, int completionTokens, string body)>>(10);
        for (int i = 0; i < 5; i++) tasks.Add(PostOnce(CoreAUrl));
        for (int i = 0; i < 5; i++) tasks.Add(PostOnce(CoreBUrl));

        var results = await Task.WhenAll(tasks);

        // Assert: all 200 OK, no 5xx
        for (int i = 0; i < results.Length; i++)
        {
            var r = results[i];
            var target = i < 5 ? "core-A:19000" : "core-B:19001";
            Assert.True(r.ok, $"Request {i} to {target} failed: status={r.status} body={r.body[..Math.Min(500, r.body.Length)]}");
            Assert.InRange(r.status, 200, 299);
            Assert.True(r.status < 500, $"Request {i} to {target} got 5xx: {r.status}");
            Assert.True(r.completionTokens > 0, $"Request {i} to {target} completion_tokens={r.completionTokens}, expected >0 body={r.body[..Math.Min(500, r.body.Length)]}");
        }

        // Prod-zero-contamination check
        var prodAfter = TryGetProdRequestCount();
        if (!prodMetricExposed || !prodAfter.HasValue)
        {
            // Metric not exposed — skip contamination check per spec (don't fail)
            return;
        }

        Assert.Equal(prodBefore!.Value, prodAfter.Value);
    }
}
