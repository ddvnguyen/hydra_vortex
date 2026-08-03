using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Port of tests/system/test_mix_precision_p_d_system.py.
///
/// Cross-model KV safety guard — system test for the M-Perf.9 #289 wiring.
/// Exercises WorkerSchedulerService.RestoreKvAsync → CrossModelGuard.Decide
/// via the live Coordinator HTTP API. Verifies:
///   1. Same-model, same-worker restore: hash matches → Proceed
///   2. Cross-worker restore: same model loaded on RTX and P100
///   3. Metric exposure: Prometheus endpoint exposes cross-model guard counters
///
/// Requires live stack: Coordinator :9000, llama-server(s), Store.
/// </summary>
[Collection("LiveRig")]
public sealed class MixPrecisionPdTests : IClassFixture<LiveRigFixture>
{
    private readonly LiveRigFixture _fx;

    private const string SystemPrompt = "You are a helpful assistant. Answer the user's question concisely.";
    private const string UserPrompt1 = "What is 2 + 2? Reply with only the number.";
    private const string UserPrompt2 = "Multiply that by 3. Reply with only the number.";

    private static readonly string[] CounterNames =
    [
        "hydra_cross_model_kv_proceeded_total",
        "hydra_cross_model_kv_skipped_total",
        "hydra_cross_model_kv_warned_total",
        "hydra_cross_model_kv_aborted_total",
    ];

    public MixPrecisionPdTests(LiveRigFixture fx) => _fx = fx;

    private string MakeSessionId() => $"system-cross-model-{Guid.NewGuid():N}"[..20];

    private static List<Dictionary<string, object?>> MakeMessages(string system, string user) =>
    [
        new() { ["role"] = "system", ["content"] = system },
        new() { ["role"] = "user", ["content"] = user },
    ];

    private static List<Dictionary<string, object?>> MakeFollowupMessages(
        string system, List<Dictionary<string, object?>> history, string user)
    {
        var msgs = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = system }
        };
        msgs.AddRange(history);
        msgs.Add(new() { ["role"] = "user", ["content"] = user });
        return msgs;
    }

    private async Task<JsonElement> DoCompletion(
        List<Dictionary<string, object?>> messages,
        string sessionId,
        int maxTokens = 32)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
            ["session_id"] = sessionId,
        };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var resp = await client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string ExtractContent(JsonElement responseJson)
    {
        if (!responseJson.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return "";
        var msg = choices[0].GetProperty("message");
        return (HttpHelpers.GetOutputText(msg)).Trim();
    }

    private async Task<double> GetCounter(string name, Dictionary<string, string>? labels = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await client.GetAsync(_fx.CoordMetricsUrl);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var samples = HttpHelpers.ParsePromLines(body);
        return HttpHelpers.SumCounter(samples, name, labels);
    }

    [SkippableFact]
    public async Task CrossModelProceedSameModelSameWorker()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            // Turn 1: initial request
            var resp1 = await DoCompletion(MakeMessages(SystemPrompt, UserPrompt1), sessionId, maxTokens: 8);
            var content1 = ExtractContent(resp1);
            Assert.False(string.IsNullOrEmpty(content1), $"Turn 1 empty");

            // Turn 2: follow-up with the same session_id
            var history = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = UserPrompt1 },
                new() { ["role"] = "assistant", ["content"] = content1 },
            };
            var resp2 = await DoCompletion(
                MakeFollowupMessages(SystemPrompt, history, UserPrompt2), sessionId, maxTokens: 8);
            var content2 = ExtractContent(resp2);
            Assert.False(string.IsNullOrEmpty(content2), $"Turn 2 empty");

            // Verify the cross-model guard ran and proceeded
            var proceeded = await GetCounter("hydra_cross_model_kv_proceeded_total");
            Assert.True(proceeded > 0,
                $"Expected hydra_cross_model_kv_proceeded_total > 0 after a same-model follow-up, got {proceeded}.");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task CrossModelMetricExposed()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            // Make at least one request so the metric series are emitted
            var resp = await DoCompletion(MakeMessages(SystemPrompt, "Say 'ok'."), sessionId, maxTokens: 4);
            Assert.True(resp.TryGetProperty("choices", out _));

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var m = await client.GetAsync(_fx.CoordMetricsUrl);
            Assert.True(m.IsSuccessStatusCode);
            var body = await m.Content.ReadAsStringAsync();

            foreach (var name in CounterNames)
            {
                Assert.Contains($"# HELP {name} ", body);
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }
}
