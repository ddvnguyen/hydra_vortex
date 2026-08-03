using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Port of tests/system/test_full_workflow_system.py.
///
/// Tests the complete stack end-to-end with real running services:
///   Coordinator :9000 → Agent RTX :9601 / Agent P100 :9602 → llama-server → Store :9500
///
/// Requires all 6 services running.
/// </summary>
[Collection("LiveRig")]
public sealed class FullWorkflowTests : IClassFixture<LiveRigFixture>
{
    private readonly LiveRigFixture _fx;

    private const string Prompt = "What is the capital of France? Give a detailed answer.";
    private const string Continuation = "Now tell me about the Eiffel Tower's history and construction details.";
    private const int MaxTokens = 100;

    public FullWorkflowTests(LiveRigFixture fx) => _fx = fx;

    private string MakeSessionId() => $"system-full-{Guid.NewGuid():N}"[..20];

    private async Task<JsonElement> DoCompletion(
        string sessionId,
        List<Dictionary<string, object?>> messages,
        bool stream = false,
        int maxTokens = MaxTokens,
        int timeoutSec = 300)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = stream,
            ["session_id"] = sessionId,
        };
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

    private async Task<JsonElement> GetSessions()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await client.GetAsync($"{_fx.CoordUrl}/sessions");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<Dictionary<string, object?>> MakeMessages(string prompt) =>
        [new() { ["role"] = "user", ["content"] = prompt }];

    // ── Tests ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task HealthEndpoint()
    {
        _fx.SkipIfUnreachable();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await client.GetAsync($"{_fx.CoordUrl}/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("status", out var statusProp));
        var status = statusProp.GetString()!;
        Assert.True(status is "healthy" or "degraded");
        Assert.True(body.TryGetProperty("nodes", out _));
        Assert.True(body.TryGetProperty("store", out _));
    }

    [SkippableFact]
    public async Task StatusEndpoint()
    {
        _fx.SkipIfUnreachable();
        var body = await GetStatus();
        Assert.True(body.TryGetProperty("uptime_s", out _));
        Assert.True(body.TryGetProperty("sessions", out _));
        Assert.True(body.TryGetProperty("routing_stats", out _));
        Assert.True(body.TryGetProperty("nodes", out _));
        Assert.True(body.TryGetProperty("routing_stats", out var rt));
        Assert.True(rt.GetProperty("total").GetInt32() >= 0);
    }

    [SkippableFact]
    public async Task CompletionNonStream()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            var resp = await DoCompletion(sessionId, MakeMessages(Prompt));
            Assert.True(resp.TryGetProperty("choices", out var choices));
            Assert.True(choices.GetArrayLength() > 0);
            var msg = choices[0].GetProperty("message");
            var hasOutput = !string.IsNullOrEmpty(HttpHelpers.GetOutputText(msg));
            Assert.True(hasOutput, "neither 'content' nor 'reasoning_content' present");
            Assert.True(resp.TryGetProperty("hydra", out var hydra));
            Assert.True(hydra.TryGetProperty("trace_id", out _));
            Assert.True(hydra.TryGetProperty("node", out _));
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task CompletionStream()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["messages"] = MakeMessages(Prompt),
                ["max_tokens"] = MaxTokens,
                ["temperature"] = 0,
                ["stream"] = true,
                ["session_id"] = sessionId,
            };
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
            var resp = await client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var allOutputs = new List<string>();
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data: ")) continue;
                var payload = line["data: ".Length..];
                if (payload == "[DONE]") break;
                try
                {
                    var ev = JsonSerializer.Deserialize<JsonElement>(payload);
                    if (ev.TryGetProperty("choices", out var evChoices) && evChoices.GetArrayLength() > 0)
                    {
                        var delta = evChoices[0].GetProperty("delta");
                        var content = HttpHelpers.GetOutputText(delta);
                        if (!string.IsNullOrEmpty(content)) allOutputs.Add(content);
                    }
                }
                catch { /* skip malformed events */ }
            }

            Assert.True(allOutputs.Count > 0, "no 'content' or 'reasoning_content' across all stream events");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task SessionLifecycle()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            var resp = await DoCompletion(sessionId, MakeMessages(Prompt));
            Assert.True(resp.TryGetProperty("choices", out _));

            var sessionsResp = await GetSessions();
            var sessionIds = sessionsResp.GetProperty("sessions")
                .EnumerateArray()
                .Select(s => s.GetProperty("session_id").GetString()!)
                .ToList();
            Assert.Contains(sessionId, sessionIds);

            using var delClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var delResp = await delClient.DeleteAsync($"{_fx.CoordUrl}/sessions/{sessionId}");
            delResp.EnsureSuccessStatusCode();
            var delBody = await delResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(delBody.GetProperty("evicted").GetBoolean());

            var sessionsAfter = await GetSessions();
            var afterIds = sessionsAfter.GetProperty("sessions")
                .EnumerateArray()
                .Select(s => s.GetProperty("session_id").GetString()!)
                .ToList();
            Assert.DoesNotContain(sessionId, afterIds);
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task PrefixCheckpoint()
    {
        _fx.SkipIfUnreachable();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var saveResp = await client.PostAsync(
            $"{_fx.CoordUrl}/prefix/system_prompt/save?node_name=rtx&slot_id=0", null);
        Assert.True(saveResp.IsSuccessStatusCode, $"Prefix save failed: {await saveResp.Content.ReadAsStringAsync()}");
        var saveBody = await saveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(saveBody.GetProperty("saved").GetBoolean());

        var restoreResp = await client.PostAsync(
            $"{_fx.CoordUrl}/prefix/system_prompt/restore?node_name=p100&slot_id=0", null);
        Assert.True(restoreResp.IsSuccessStatusCode, $"Prefix restore failed: {await restoreResp.Content.ReadAsStringAsync()}");
        var restoreBody = await restoreResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(restoreBody.GetProperty("restored").GetBoolean());
    }

    [SkippableFact]
    public async Task MigrateSession()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            var resp = await DoCompletion(sessionId, MakeMessages(Prompt));
            Assert.True(resp.TryGetProperty("hydra", out var hydra));
            var sourceNode = hydra.TryGetProperty("node", out var n) ? n.GetString()! : "";

            using var migrateClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var migrateResp = await migrateClient.PostAsJsonAsync(
                $"{_fx.CoordUrl}/sessions/{sessionId}/migrate",
                new { target_node = "p100" });
            Assert.True(migrateResp.IsSuccessStatusCode, $"Migration failed: {await migrateResp.Content.ReadAsStringAsync()}");
            var migrateBody = await migrateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(migrateBody.GetProperty("migrated").GetBoolean());
            Assert.Equal("p100", migrateBody.GetProperty("target").GetString());

            var status = await GetStatus();
            var session = status.GetProperty("sessions").GetProperty("sessions")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("session_id").GetString() == sessionId);
            Assert.True(session.ValueKind != JsonValueKind.Undefined, $"Session {sessionId} not found in status after migration");
            Assert.Equal("p100", session.GetProperty("node").GetString());
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task MigrationCacheHit()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            var first = await DoCompletion(sessionId, MakeMessages(Prompt), maxTokens: 50);
            var assistantReply = first.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;

            using var migrateClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var migrateResp = await migrateClient.PostAsJsonAsync(
                $"{_fx.CoordUrl}/sessions/{sessionId}/migrate",
                new { target_node = "p100" });
            Assert.True(migrateResp.IsSuccessStatusCode, $"Migration failed: {await migrateResp.Content.ReadAsStringAsync()}");

            var continuationMessages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = Prompt },
                new() { ["role"] = "assistant", ["content"] = assistantReply },
                new() { ["role"] = "user", ["content"] = Continuation },
            };
            var contResp = await DoCompletion(sessionId, continuationMessages);
            var timings = contResp.TryGetProperty("timings", out var t) ? t : default;
            var cacheN = timings.ValueKind != JsonValueKind.Undefined && timings.TryGetProperty("cache_n", out var cn) ? cn.GetInt32() : 0;
            var promptMs = timings.ValueKind != JsonValueKind.Undefined && timings.TryGetProperty("prompt_ms", out var pm) ? pm.GetDouble() : 0;

            Assert.True(cacheN > 0,
                $"cache_n={cacheN} — KV cache was not used after migration.");
            Assert.True(promptMs < 5000,
                $"prompt_ms={promptMs} — full re-prefill occurred instead of cached path.");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task EvictionWithSave()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            var resp = await DoCompletion(sessionId, MakeMessages(Prompt));
            Assert.True(resp.TryGetProperty("choices", out _));

            using var delClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var delResp = await delClient.DeleteAsync($"{_fx.CoordUrl}/sessions/{sessionId}");
            Assert.True(delResp.IsSuccessStatusCode);
            var delBody = await delResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(delBody.GetProperty("evicted").GetBoolean());

            var status = await GetStatus();
            var session = status.GetProperty("sessions").GetProperty("sessions")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("session_id").GetString() == sessionId);
            Assert.True(session.ValueKind == JsonValueKind.Undefined,
                $"Session {sessionId} still present after eviction");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task SlotIdResolvedAfterFirstCompletion()
    {
        _fx.SkipIfUnreachable();
        var sessId = $"system-slot-resolve-{Guid.NewGuid():N}"[..20];
        try
        {
            // Turn 1: new session — slot_id resolved after completion
            var resp1 = await DoCompletion(sessId, MakeMessages("What is 2+2? Answer briefly."), maxTokens: 30);
            Assert.True(resp1.TryGetProperty("choices", out _));

            var status = await GetStatus();
            var session = status.GetProperty("sessions").GetProperty("sessions")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("session_id").GetString() == sessId);
            Assert.True(session.ValueKind != JsonValueKind.Undefined);
            var slotId1 = session.GetProperty("slot_id").GetInt32();

            // Turn 2: slot_id should be stable
            var resp2 = await DoCompletion(sessId, MakeMessages("What is 3+3? Answer briefly."), maxTokens: 30);
            var status2 = await GetStatus();
            var session2 = status2.GetProperty("sessions").GetProperty("sessions")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("session_id").GetString() == sessId);
            var slotId2 = session2.GetProperty("slot_id").GetInt32();
            Assert.Equal(slotId1, slotId2);

            // Turn 3: streaming — also resolves slot_id correctly
            var streamBody = new Dictionary<string, object?>
            {
                ["messages"] = MakeMessages("What is 4+4? Answer briefly."),
                ["max_tokens"] = 30,
                ["temperature"] = 0,
                ["stream"] = true,
                ["session_id"] = sessId,
            };
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
            var streamResp = await client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", streamBody);
            streamResp.EnsureSuccessStatusCode();

            var status3 = await GetStatus();
            var session3 = status3.GetProperty("sessions").GetProperty("sessions")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("session_id").GetString() == sessId);
            var slotId3 = session3.GetProperty("slot_id").GetInt32();
            Assert.Equal(slotId1, slotId3);
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessId);
        }
    }

    [SkippableFact]
    public async Task SlotIdPersistsAcrossSessionLifecycle()
    {
        _fx.SkipIfUnreachable();
        var sessId = $"system-slot-lifecycle-{Guid.NewGuid():N}"[..20];
        try
        {
            // Step 1: first completion resolves slot_id
            var resp = await DoCompletion(sessId, MakeMessages("What is 2+2? Answer briefly."), maxTokens: 30);
            Assert.True(resp.TryGetProperty("choices", out _));

            var status = await GetStatus();
            var session = status.GetProperty("sessions").GetProperty("sessions")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("session_id").GetString() == sessId);
            var slotIdBefore = session.GetProperty("slot_id").GetInt32();

            // Step 2: evict
            using var delClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var delResp = await delClient.DeleteAsync($"{_fx.CoordUrl}/sessions/{sessId}");
            Assert.True(delResp.IsSuccessStatusCode);

            // Step 3: restore via new completion
            var resp2 = await DoCompletion(sessId, MakeMessages("What is 2+2? Answer briefly."), maxTokens: 30);
            Assert.True(resp2.TryGetProperty("choices", out _));

            var status2 = await GetStatus();
            var session2 = status2.GetProperty("sessions").GetProperty("sessions")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("session_id").GetString() == sessId);
            Assert.True(session2.ValueKind != JsonValueKind.Undefined, "Session should be restored");
            Assert.True(session2.GetProperty("slot_id").GetInt32() >= 0);
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessId);
        }
    }

    [SkippableFact]
    public async Task FullCycleCompletionMigrationContinuation()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        try
        {
            var body = await DoCompletion(sessionId, MakeMessages(Prompt), maxTokens: 50);
            var assistantReply = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;

            using var migrateClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var migrateResp = await migrateClient.PostAsJsonAsync(
                $"{_fx.CoordUrl}/sessions/{sessionId}/migrate",
                new { target_node = "p100" });
            Assert.True(migrateResp.IsSuccessStatusCode, $"Migration failed: {await migrateResp.Content.ReadAsStringAsync()}");
            var migrateBody = await migrateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(migrateBody.GetProperty("migrated").GetBoolean());

            var continuationMessages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = Prompt },
                new() { ["role"] = "assistant", ["content"] = assistantReply },
                new() { ["role"] = "user", ["content"] = Continuation },
            };
            var contBody = await DoCompletion(sessionId, continuationMessages);
            var timings = contBody.TryGetProperty("timings", out var t) ? t : default;
            var cacheN = timings.ValueKind != JsonValueKind.Undefined && timings.TryGetProperty("cache_n", out var cn) ? cn.GetInt32() : 0;
            var promptMs = timings.ValueKind != JsonValueKind.Undefined && timings.TryGetProperty("prompt_ms", out var pm) ? pm.GetDouble() : 0;

            Assert.True(cacheN > 0,
                $"cache_n={cacheN} — KV cache not used after migration cycle.");
            Assert.True(promptMs < 5000,
                $"prompt_ms={promptMs} — full re-prefill (expected cache path).");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }
}
