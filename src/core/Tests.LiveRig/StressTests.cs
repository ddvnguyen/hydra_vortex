using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Port of tests/system/test_stress_system.py.
///
/// Stress system tests for Coordinator HTTP API.
/// Tests concurrent request handling and cross-session consistency.
///
/// Requires all 6 services running.
/// </summary>
[Collection("LiveRig")]
public sealed class StressTests : IClassFixture<LiveRigFixture>
{
    private readonly LiveRigFixture _fx;

    public StressTests(LiveRigFixture fx) => _fx = fx;

    private async Task<JsonElement> DoCompletion(
        List<Dictionary<string, object?>> messages,
        string? sessionId = null,
        int maxTokens = 100,
        int timeoutSec = 120)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
        };
        if (sessionId is not null) body["session_id"] = sessionId;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        var resp = await client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetStatus()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await client.GetAsync($"{_fx.CoordUrl}/status");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [SkippableFact]
    public async Task FourConcurrentCompletions()
    {
        _fx.SkipIfUnreachable();
        var promptText = HttpHelpers.GenerateText(2_000);
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "user", ["content"] = promptText }
        };

        // ── Measure serial time (single request) ──────────────────────────
        var refSid = $"system-stress-ref-{Guid.NewGuid():N}"[..20];
        var t0 = Stopwatch.StartNew();
        var refResp = await DoCompletion(messages, sessionId: refSid);
        t0.Stop();
        var serialTime = t0.Elapsed.TotalSeconds;
        Assert.True(refResp.TryGetProperty("choices", out _));

        // ── Measure concurrent time (4 requests simultaneously) ───────────
        var sessionIds = Enumerable.Range(0, 4)
            .Select(_ => $"system-stress-{Guid.NewGuid():N}"[..20])
            .ToList();

        var t1 = Stopwatch.StartNew();
        var tasks = sessionIds.Select(sid => DoCompletion(messages, sessionId: sid)).ToList();
        var results = await Task.WhenAll(tasks);
        t1.Stop();
        var concurrentTime = t1.Elapsed.TotalSeconds;

        // ── Assertions ─────────────────────────────────────────────────────
        for (var i = 0; i < results.Length; i++)
        {
            var body = results[i];
            Assert.True(body.TryGetProperty("choices", out var choices),
                $"#{i} ({sessionIds[i]}) no choices");
            Assert.False(string.IsNullOrEmpty(HttpHelpers.GetOutputText(choices[0].GetProperty("message"))),
                $"#{i} ({sessionIds[i]}) empty output");
        }

        // ── Timing assertion ──────────────────────────────────────────────
        var ratio = concurrentTime / serialTime;
        Assert.True(ratio < 6.0,
            $"Concurrent time ({concurrentTime:F2}s) exceeded 6.0x serial time ({serialTime:F2}s) — ratio={ratio:F2}");

        // ── Verify all sessions registered ─────────────────────────────────
        var status = await GetStatus();
        var registeredIds = status.GetProperty("sessions").GetProperty("sessions")
            .EnumerateArray()
            .Select(s => s.GetProperty("session_id").GetString()!)
            .ToHashSet();
        foreach (var sid in sessionIds)
            Assert.Contains(sid, registeredIds);

        // ── Cleanup ────────────────────────────────────────────────────────
        foreach (var sid in sessionIds.Append(refSid))
            await _fx.DeleteSessionAsync(sid);
    }

    [SkippableFact]
    public async Task CrossSessionConsistency()
    {
        _fx.SkipIfUnreachable();
        var promptText = HttpHelpers.GenerateText(2_000);
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "user", ["content"] = promptText }
        };

        // ── Direct to RTX llama-server ─────────────────────────────────────
        using (var directClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
        {
            var directResp = await directClient.PostAsJsonAsync(
                $"{_fx.LlamaRtxUrl}/v1/chat/completions",
                new { messages, max_tokens = 100, temperature = 0 });
            Assert.True(directResp.IsSuccessStatusCode, $"Direct llama completion failed: {await directResp.Content.ReadAsStringAsync()}");
            var directBody = await directResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(directBody.TryGetProperty("choices", out var directChoices));
            Assert.False(string.IsNullOrEmpty(HttpHelpers.GetOutputText(directChoices[0].GetProperty("message"))),
                "Direct completion returned empty output");
        }

        // ── Through Coordinator ────────────────────────────────────────────
        var sessionId = $"system-cross-{Guid.NewGuid():N}"[..20];
        try
        {
            var coordBody = await DoCompletion(messages, sessionId: sessionId);
            Assert.True(coordBody.TryGetProperty("choices", out var coordChoices));
            Assert.False(string.IsNullOrEmpty(HttpHelpers.GetOutputText(coordChoices[0].GetProperty("message"))),
                "Coordinator completion returned empty output");

            // ── Verify hydra metadata only on coordinator response ─────────
            Assert.True(coordBody.TryGetProperty("hydra", out _), "Coordinator response missing hydra metadata");

            // ── Verify routing stats updated ───────────────────────────────
            var status = await GetStatus();
            Assert.True(status.TryGetProperty("routing_stats", out var rt));
            Assert.True(rt.GetProperty("total").GetInt32() > 0);
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }
}
