using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace Tests.E2E;

/// <summary>
/// End-to-end tests for the OpenCode-like conversation flow through the Hydra
/// Coordinator HTTP API. Ported from tests/system/test_opencode_flow_system.py.
/// Uses Aspire.Hosting.Testing to spin up the full stack (Hydra.Core coordinator
/// + FakeLlamaEngine instances) in-process.
/// </summary>
[Collection("E2E")]
public sealed class OpencodeFlowTests : IAsyncLifetime
{
    private DistributedApplication? _app;
    private HttpClient? _httpClient;
    private string? _baseUrl;

    // Simulates opencode's coding-agent instructions (truncated for test brevity).
    private const string SystemPrompt =
        "You are an expert software engineer. Follow these rules:\n"
        + "1. Write clean, idiomatic code.\n"
        + "2. Always check for existing patterns before implementing.\n"
        + "3. Use proper error handling.\n"
        + "4. Write tests for all new code.\n"
        + "5. Prefer simple solutions over complex ones.\n"
        + "6. Never introduce breaking changes without a migration plan.\n"
        + "7. Document public APIs.\n"
        + "8. Follow the principle of least surprise.\n"
        + "9. Consider edge cases and failure modes.\n"
        + "10. Optimize for readability first, performance second.";

    private const string UserPrompt1 = "Write a function that reverses a linked list in Python.";
    private const string UserPrompt2 = "Now add type hints and a docstring.";
    private const string UserPrompt3 = "Also add a main() entry point with example usage.";

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Hydra_AppHost>();

        _app = await builder.BuildAsync();

        // Start the Aspire distributed application (spawns coordinator + engines).
        await _app.StartAsync();

        // Resolve the coordinator's HTTP endpoint from the Aspire resource model.
        // Resource name "hydra-core" matches the AppHost's AddProject<...>("hydra-core").
        var endpoint = _app.GetEndpoint("hydra-core", "http");
        _baseUrl = endpoint.ToString().TrimEnd('/');
        _httpClient = _app.CreateHttpClient("hydra-core", "http");
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
    }

    public async Task DisposeAsync()
    {
        if (_httpClient is not null)
        {
            _httpClient.Dispose();
        }
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Helper: build messages list ─────────────────────────────────────

    private static List<Dictionary<string, string>> MakeMessages(
        string? system, params string[] content)
    {
        var msgs = new List<Dictionary<string, string>>();
        if (system is not null)
        {
            msgs.Add(new Dictionary<string, string> { ["role"] = "system", ["content"] = system });
        }
        foreach (var c in content)
        {
            msgs.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = c });
        }
        return msgs;
    }

    private static List<Dictionary<string, string>> MakeFollowupMessages(
        string? system,
        List<Dictionary<string, string>> history,
        string newPrompt)
    {
        var msgs = new List<Dictionary<string, string>>();
        if (system is not null)
        {
            msgs.Add(new Dictionary<string, string> { ["role"] = "system", ["content"] = system });
        }
        msgs.AddRange(history);
        msgs.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = newPrompt });
        return msgs;
    }

    // ── Helper: send completion request ─────────────────────────────────

    private async Task<HttpResponseMessage> DoCompletionAsync(
        List<Dictionary<string, string>> messages,
        string? sessionId = null,
        bool stream = true,
        int maxTokens = 200)
    {
        var body = new Dictionary<string, object>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = stream,
        };
        if (sessionId is not null)
        {
            body["session_id"] = sessionId;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };

        var response = await _httpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}). Body: {errorBody}");
        }
        return response;
    }

    // ── Helper: parse SSE stream ────────────────────────────────────────

    private static async Task<List<JsonElement>> ParseSseAsync(HttpResponseMessage response)
    {
        var events = new List<JsonElement>();
        var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            line = line.Trim();
            if (!line.StartsWith("data: ")) continue;

            var payload = line["data: ".Length..];
            if (payload == "[DONE]") break;

            try
            {
                var doc = JsonDocument.Parse(payload);
                events.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // Skip malformed events (matches Python behaviour).
            }
        }
        return events;
    }

    // ── Helper: extract content from SSE events ─────────────────────────

    private static string ExtractContent(IReadOnlyList<JsonElement> events)
    {
        var parts = new StringBuilder();
        foreach (var ev in events)
        {
            if (!ev.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            var choice = choices[0];
            var delta = choice.TryGetProperty("delta", out var d) ? d : default;
            var content = delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String
                    ? rc.GetString() ?? ""
                    : "";
            if (content.Length > 0)
            {
                parts.Append(content);
            }
        }
        return parts.ToString();
    }

    // ── Helper: extract usage from SSE events ───────────────────────────

    private static Dictionary<string, object?> ExtractUsage(IReadOnlyList<JsonElement> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in usage.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.GetInt64(),
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                }
                return dict;
            }
        }
        return [];
    }

    // ── Helper: GET /status ─────────────────────────────────────────────

    private async Task<JsonElement> GetStatusAsync()
    {
        var response = await _httpClient!.GetAsync("/status");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement;
    }

    // ── Helper: DELETE /sessions/{id} ───────────────────────────────────

    private async Task DeleteSessionAsync(string sessionId)
    {
        try
        {
            await _httpClient!.DeleteAsync($"/sessions/{sessionId}");
        }
        catch
        {
            // Best-effort cleanup (matches Python behaviour).
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TEST 1: Initial request
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TestOpencodeInitialRequest()
    {
        var sessionId = $"system-opencode-{Guid.NewGuid():N}"[..16];
        var messages = MakeMessages(SystemPrompt, UserPrompt1);

        var resp = await DoCompletionAsync(messages, sessionId: sessionId, stream: true);
        Assert.True(resp.IsSuccessStatusCode, $"Initial request failed: {await resp.Content.ReadAsStringAsync()}");

        var events = await ParseSseAsync(resp);
        Assert.True(events.Count > 0, "No SSE events received");

        var content = ExtractContent(events);
        Assert.False(string.IsNullOrEmpty(content),
            $"No content in response: {(events.Count > 0 ? events[^1] : "no events")}");

        var usage = ExtractUsage(events);
        Assert.True(usage.Count > 0, "No usage data in final event");
        Assert.True(usage.TryGetValue("total_tokens", out var totalTokens) && totalTokens is not null
            && Convert.ToInt64(totalTokens) > 0,
            "total_tokens should be > 0");

        // Verify session appears in status
        var status = await GetStatusAsync();
        var sessions = status.GetProperty("sessions").GetProperty("sessions");
        var sessionIds = new List<string>();
        foreach (var s in sessions.EnumerateArray())
        {
            sessionIds.Add(s.GetProperty("session_id").GetString()!);
        }
        Assert.Contains(sessionId, sessionIds);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TEST 2: Follow-up reuses KV cache
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TestOpencodeFollowupReusesKvCache()
    {
        var sessionId = $"system-opencode-{Guid.NewGuid():N}"[..16];

        // ── Turn 1: initial request with system prompt ──
        var messages1 = MakeMessages(SystemPrompt, UserPrompt1);
        var resp1 = await DoCompletionAsync(messages1, sessionId: sessionId, stream: true);
        Assert.True(resp1.IsSuccessStatusCode, $"Turn 1 failed: {await resp1.Content.ReadAsStringAsync()}");

        var events1 = await ParseSseAsync(resp1);
        var reply1 = ExtractContent(events1);
        Assert.False(string.IsNullOrEmpty(reply1), "Turn 1 has no content in response");

        // ── Turn 2: follow-up with conversation history ──
        var history = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "user", ["content"] = UserPrompt1 },
            new() { ["role"] = "assistant", ["content"] = reply1 },
        };
        var messages2 = MakeFollowupMessages(SystemPrompt, history, UserPrompt2);
        var resp2 = await DoCompletionAsync(messages2, sessionId: sessionId, stream: true);
        Assert.True(resp2.IsSuccessStatusCode, $"Turn 2 failed: {await resp2.Content.ReadAsStringAsync()}");

        var events2 = await ParseSseAsync(resp2);
        var reply2 = ExtractContent(events2);
        Assert.False(string.IsNullOrEmpty(reply2), "Turn 2 has no content in response");

        // Verify session is restored (appears in status)
        var status = await GetStatusAsync();
        var sessions = status.GetProperty("sessions").GetProperty("sessions");
        JsonElement? foundSession = null;
        foreach (var s in sessions.EnumerateArray())
        {
            if (s.GetProperty("session_id").GetString() == sessionId)
            {
                foundSession = s;
                break;
            }
        }
        Assert.True(foundSession is not null, $"Session {sessionId} not found after turn 2");

        var slotId = foundSession.Value.TryGetProperty("slot_id", out var slotIdProp)
            && slotIdProp.ValueKind == JsonValueKind.Number
            ? (int?)slotIdProp.GetInt32()
            : null;
        Assert.True(slotId.HasValue, $"slot_id not a valid int after turn 2: {slotId}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TEST 3: Multi-turn session lifecycle
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TestOpencodeMultiTurnSessionLifecycle()
    {
        var sessionId = $"system-opencode-{Guid.NewGuid():N}"[..16];

        // ── Turn 1 ──
        var messages1 = MakeMessages(SystemPrompt, UserPrompt1);
        var resp1 = await DoCompletionAsync(messages1, sessionId: sessionId, stream: true);
        Assert.True(resp1.IsSuccessStatusCode, $"T1 failed: {await resp1.Content.ReadAsStringAsync()}");
        var events1 = await ParseSseAsync(resp1);
        var reply1 = ExtractContent(events1);
        Assert.False(string.IsNullOrEmpty(reply1), "T1 no content");

        var status1 = await GetStatusAsync();
        var session1 = FindSession(status1, sessionId);
        Assert.True(session1 is not null, "Session missing after T1");
        Assert.True(session1!.Value.TryGetProperty("slot_id", out var slotId1)
            && slotId1.ValueKind == JsonValueKind.Number,
            $"slot_id not resolved after T1");

        // ── Turn 2 ──
        var history2 = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "user", ["content"] = UserPrompt1 },
            new() { ["role"] = "assistant", ["content"] = reply1 },
        };
        var messages2 = MakeFollowupMessages(SystemPrompt, history2, UserPrompt2);
        var resp2 = await DoCompletionAsync(messages2, sessionId: sessionId, stream: true);
        Assert.True(resp2.IsSuccessStatusCode, $"T2 failed: {await resp2.Content.ReadAsStringAsync()}");
        var events2 = await ParseSseAsync(resp2);
        var reply2 = ExtractContent(events2);
        Assert.False(string.IsNullOrEmpty(reply2), "T2 no content");

        var status2 = await GetStatusAsync();
        var session2 = FindSession(status2, sessionId);
        Assert.True(session2 is not null, "Session missing after T2");
        Assert.True(session2!.Value.TryGetProperty("slot_id", out var slotId2)
            && slotId2.ValueKind == JsonValueKind.Number,
            $"slot_id not resolved after T2");

        // ── Turn 3 ──
        var history3 = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "user", ["content"] = UserPrompt1 },
            new() { ["role"] = "assistant", ["content"] = reply1 },
            new() { ["role"] = "user", ["content"] = UserPrompt2 },
            new() { ["role"] = "assistant", ["content"] = reply2 },
        };
        var messages3 = MakeFollowupMessages(SystemPrompt, history3, UserPrompt3);
        var resp3 = await DoCompletionAsync(messages3, sessionId: sessionId, stream: true);
        Assert.True(resp3.IsSuccessStatusCode, $"T3 failed: {await resp3.Content.ReadAsStringAsync()}");
        var events3 = await ParseSseAsync(resp3);
        var reply3 = ExtractContent(events3);
        Assert.False(string.IsNullOrEmpty(reply3), "T3 no content");

        // Cleanup
        await DeleteSessionAsync(sessionId);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TEST 4: Concurrent sessions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TestOpencodeConcurrentSessions()
    {
        var sidA = $"system-oc-concur-a-{Guid.NewGuid():N}"[..16];
        var sidB = $"system-oc-concur-b-{Guid.NewGuid():N}"[..16];

        async Task<string> SessionTurn(string sid, string prompt)
        {
            var msgs = MakeMessages(SystemPrompt, prompt);
            var resp = await DoCompletionAsync(msgs, sessionId: sid, stream: true);
            Assert.True(resp.IsSuccessStatusCode, $"Session {sid} failed: {await resp.Content.ReadAsStringAsync()}");
            var evts = await ParseSseAsync(resp);
            var content = ExtractContent(evts);
            Assert.False(string.IsNullOrEmpty(content), $"Session {sid} has no content");
            return content;
        }

        // Run both sessions concurrently
        var taskA = SessionTurn(sidA, "Write a function to find the max element in a list.");
        var taskB = SessionTurn(sidB, "Write a function to check if a string is a palindrome.");

        var resultA = await taskA;
        var resultB = await taskB;

        Assert.False(string.IsNullOrEmpty(resultA), "Session A has no content");
        Assert.False(string.IsNullOrEmpty(resultB), "Session B has no content");

        // Both sessions should appear in status
        var status = await GetStatusAsync();
        var sessionIds = new List<string>();
        foreach (var s in status.GetProperty("sessions").GetProperty("sessions").EnumerateArray())
        {
            sessionIds.Add(s.GetProperty("session_id").GetString()!);
        }
        Assert.Contains(sidA, sessionIds);
        Assert.Contains(sidB, sessionIds);

        // Cleanup
        await DeleteSessionAsync(sidA);
        await DeleteSessionAsync(sidB);
    }

    // ── Helper: find session by ID in status JSON ───────────────────────

    private static JsonElement? FindSession(JsonElement status, string sessionId)
    {
        if (!status.TryGetProperty("sessions", out var sessionsWrapper))
            return null;
        if (!sessionsWrapper.TryGetProperty("sessions", out var sessions))
            return null;

        foreach (var s in sessions.EnumerateArray())
        {
            if (s.TryGetProperty("session_id", out var idProp)
                && idProp.GetString() == sessionId)
            {
                return s;
            }
        }
        return null;
    }
}
