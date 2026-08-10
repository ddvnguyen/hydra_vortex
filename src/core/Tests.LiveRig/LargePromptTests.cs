using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Port of tests/system/test_large_prompt_system.py.
///
/// System tests for large prompt handling through Coordinator HTTP API.
/// Simulates coding agent behavior: large context window initial prompt,
/// then shorter follow-up continuation. Verifies metrics on both llama-servers.
///
/// Requires all 6 services running.
/// </summary>
[Collection("LiveRig")]
public sealed class LargePromptTests : IClassFixture<LiveRigFixture>
{
    private readonly LiveRigFixture _fx;

    public LargePromptTests(LiveRigFixture fx) => _fx = fx;

    private string MakeSessionId() => $"system-lg-{Guid.NewGuid():N}"[..20];

    private async Task<JsonElement> DoCompletion(
        List<Dictionary<string, object?>> messages,
        string? sessionId = null,
        bool stream = false,
        int maxTokens = 100,
        int timeoutSec = 300)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = stream,
        };
        if (sessionId is not null) body["session_id"] = sessionId;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<(Dictionary<string, JsonElement> Slots, Dictionary<string, double> Metrics)> ScrapeLlama(string baseUrl)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var slotsResp = await HttpHelpers.Client.GetAsync($"{baseUrl}/slots", cts.Token);
        slotsResp.EnsureSuccessStatusCode();
        var slots = await slotsResp.Content.ReadFromJsonAsync<JsonElement>();

        var metricsResp = await HttpHelpers.Client.GetAsync($"{baseUrl}/metrics", cts.Token);
        metricsResp.EnsureSuccessStatusCode();
        var metricsText = await metricsResp.Content.ReadAsStringAsync();
        var metrics = HttpHelpers.ParseLlamaMetrics(metricsText);

        var slotsDict = new Dictionary<string, JsonElement>();
        if (slots.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in slots.EnumerateArray())
                if (s.TryGetProperty("id", out var id))
                    slotsDict[id.GetInt32().ToString()] = s;
        }

        return (slotsDict, metrics);
    }

    [SkippableTheory]
    [InlineData(8_000, 2_000, 300)]
    [InlineData(8_000, 4_000, 300)]
    [InlineData(16_000, 2_000, 300)]
    [InlineData(16_000, 4_000, 300)]
    [InlineData(48_000, 2_000, 420)]
    [InlineData(48_000, 4_000, 420)]
    public async Task LargePromptWithMetricsAndContinuation(int promptTokens, int continueTokens, int timeoutSec)
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        var initPrompt = HttpHelpers.GenerateText(promptTokens);
        var continuePrompt = HttpHelpers.GenerateText(continueTokens);

        try
        {
            // ── Scrape before metrics ────────────────────────────────────
            var (rtxBeforeSlots, rtxBefore) = await ScrapeLlama(_fx.LlamaRtxUrl);
            var (p100BeforeSlots, p100Before) = await ScrapeLlama(_fx.LlamaP100Url);
            var rtxPttBefore = rtxBefore.GetValueOrDefault("llamacpp:prompt_tokens_total");
            var p100PttBefore = p100Before.GetValueOrDefault("llamacpp:prompt_tokens_total");
            var rtxTptBefore = rtxBefore.GetValueOrDefault("llamacpp:tokens_predicted_total");
            var p100TptBefore = p100Before.GetValueOrDefault("llamacpp:tokens_predicted_total");

            // ── Send initial prompt ──────────────────────────────────────
            var initResp = await DoCompletion(
                [new() { ["role"] = "user", ["content"] = initPrompt }],
                sessionId,
                maxTokens: 4096,
                timeoutSec: timeoutSec);
            Assert.True(initResp.TryGetProperty("choices", out var initChoices));
            Assert.True(initChoices.GetArrayLength() > 0);
            Assert.False(string.IsNullOrEmpty(HttpHelpers.GetOutputText(initChoices[0].GetProperty("message"))),
                "Empty output in init response");

            // ── Scrape after metrics + verify ────────────────────────────
            var (rtxAfterSlots, rtxAfter) = await ScrapeLlama(_fx.LlamaRtxUrl);
            var (p100AfterSlots, p100After) = await ScrapeLlama(_fx.LlamaP100Url);
            var rtxPttAfter = rtxAfter.GetValueOrDefault("llamacpp:prompt_tokens_total");
            var p100PttAfter = p100After.GetValueOrDefault("llamacpp:prompt_tokens_total");
            var rtxTptAfter = rtxAfter.GetValueOrDefault("llamacpp:tokens_predicted_total");
            var p100TptAfter = p100After.GetValueOrDefault("llamacpp:tokens_predicted_total");
            var pttDiff = (rtxPttAfter + p100PttAfter) - (rtxPttBefore + p100PttBefore);
            var tptDiff = (rtxTptAfter + p100TptAfter) - (rtxTptBefore + p100TptBefore);

            var actualPromptTokens = initResp.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            var cachedTokens = usage.TryGetProperty("prompt_tokens_details", out var ptd) &&
                ptd.TryGetProperty("cached_tokens", out var ct) ? ct.GetInt32() : 0;

            Assert.True(actualPromptTokens > 0, "No prompt_tokens in response usage");
            Assert.True(pttDiff > 0 || cachedTokens > 0,
                $"prompt_tokens_total (rtx+p100) increased by {pttDiff:F0} with {cachedTokens} cached tokens — no evidence of processing");
            Assert.True(tptDiff > 0,
                $"tokens_predicted_total (rtx+p100) did not increase (diff={tptDiff:F0})");

            // Verify no requests processing after completion
            var rtxReqProc = rtxAfter.GetValueOrDefault("llamacpp:requests_processing");
            var p100ReqProc = p100After.GetValueOrDefault("llamacpp:requests_processing");
            Assert.Equal(0, rtxReqProc);
            Assert.Equal(0, p100ReqProc);

            // ── Send continuation with same session_id ───────────────────
            var assistantReply = HttpHelpers.GetOutputText(initChoices[0].GetProperty("message"));
            var continueMessages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = initPrompt },
                new() { ["role"] = "assistant", ["content"] = assistantReply },
                new() { ["role"] = "user", ["content"] = continuePrompt },
            };
            var contResp = await DoCompletion(continueMessages, sessionId, maxTokens: 4096, timeoutSec: timeoutSec);
            Assert.True(contResp.TryGetProperty("choices", out var contChoices));
            Assert.False(string.IsNullOrEmpty(HttpHelpers.GetOutputText(contChoices[0].GetProperty("message"))),
                "Empty output in continuation");

            // ── Scrape metrics after continuation ────────────────────────
            var (rtxContSlots, rtxCont) = await ScrapeLlama(_fx.LlamaRtxUrl);
            var (p100ContSlots, p100Cont) = await ScrapeLlama(_fx.LlamaP100Url);
            var rtxTptCont = rtxCont.GetValueOrDefault("llamacpp:tokens_predicted_total");
            var p100TptCont = p100Cont.GetValueOrDefault("llamacpp:tokens_predicted_total");
            var contTptDiff = (rtxTptCont + p100TptCont) - (rtxTptAfter + p100TptAfter);
            Assert.True(contTptDiff > 0,
                $"tokens_predicted_total (rtx+p100) did not increase after continuation (diff={contTptDiff:F0})");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }
}
