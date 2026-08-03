using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Port of tests/system/test_multiturn_warm_system.py.
///
/// Multi-turn warm affinity + slot-leak regression tests.
/// Verifies that the system:
///   1. Actually reuses KV cache on P100 across turns (n_prompt_tokens_cache > 10 post-turn-2)
///   2. Decode speed on turn 2+ is not catastrophically slow vs turn 1
///   3. No two concurrent sessions share the same (node, slot_id) pair
///
/// Requires live stack: Coordinator → Workers → llama-servers → Store.
/// </summary>
[Collection("LiveRig")]
public sealed class MultiturnWarmTests : IClassFixture<LiveRigFixture>
{
    private readonly LiveRigFixture _fx;

    private const string SystemPrompt =
        "You are an expert software engineer specialising in distributed systems and GPU inference. " +
        "When answering questions:\n" +
        "1. Provide concise, accurate answers.\n" +
        "2. Use code examples where relevant.\n" +
        "3. Explain trade-offs between approaches.\n" +
        "4. Consider memory, latency, and throughput implications.\n" +
        "5. Reference established patterns from systems like vLLM, TensorRT-LLM, and llama.cpp.\n" +
        "Your answers should be helpful for an engineer building production LLM serving infrastructure.";

    private static readonly string[] Turns =
    [
        "What is KV cache reuse and why does it matter for LLM inference performance?",
        "How does prefix caching work at the llama.cpp level?",
        "What are the challenges of migrating KV cache state between two different GPUs?",
        "How would you implement a P/D disaggregated serving system for heterogeneous GPUs?",
        "What metrics would you track to verify that KV cache reuse is actually working in production?",
    ];

    public MultiturnWarmTests(LiveRigFixture fx) => _fx = fx;

    private string MakeSessionId() => $"sys-warm-{Guid.NewGuid():N}"[..20];

    private static List<Dictionary<string, object?>> MakeHistory(string system, List<(string user, string assistant)> turnsDone)
    {
        var msgs = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = system }
        };
        foreach (var (userMsg, assistantReply) in turnsDone)
        {
            msgs.Add(new() { ["role"] = "user", ["content"] = userMsg });
            msgs.Add(new() { ["role"] = "assistant", ["content"] = assistantReply });
        }
        return msgs;
    }

    private async Task<(HttpResponseMessage Response, List<JsonElement> Events)> DoCompletionStream(
        string sessionId,
        List<Dictionary<string, object?>> messages,
        int maxTokens = 300,
        int timeoutSec = 600)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = true,
            ["session_id"] = sessionId,
        };
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        var resp = await client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();

        var events = new List<JsonElement>();
        var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var payload = line["data: ".Length..];
            if (payload == "[DONE]") break;
            try { events.Add(JsonSerializer.Deserialize<JsonElement>(payload)); }
            catch { /* skip malformed */ }
        }
        return (resp, events);
    }

    private static string ExtractContent(List<JsonElement> events)
    {
        var parts = new List<string>();
        foreach (var ev in events)
        {
            if (!ev.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var delta = choices[0].GetProperty("delta");
            var content = HttpHelpers.GetOutputText(delta);
            if (!string.IsNullOrEmpty(content)) parts.Add(content);
        }
        return string.Join("", parts);
    }

    private async Task<List<JsonElement>?> ScrapeSlots(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await client.GetAsync($"{url}/slots");
            resp.EnsureSuccessStatusCode();
            var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().ToList()
                : null;
        }
        catch { return null; }
    }

    [SkippableFact]
    public async Task FiveTurnWarmAffinity()
    {
        _fx.SkipIfUnreachable();
        var sessionId = MakeSessionId();
        var history = new List<(string user, string assistant)>();
        var turnTimes = new List<double>();

        try
        {
            for (var i = 0; i < Turns.Length; i++)
            {
                var messages = MakeHistory(SystemPrompt, history);
                messages.Add(new() { ["role"] = "user", ["content"] = Turns[i] });

                var t0 = Stopwatch.StartNew();
                var (_, events) = await DoCompletionStream(sessionId, messages);
                t0.Stop();
                turnTimes.Add(t0.Elapsed.TotalSeconds);

                var reply = ExtractContent(events);
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {i + 1} produced empty reply");
                history.Add((Turns[i], reply));

                if (i == 1)
                {
                    // After turn 2: P100 should have cache populated
                    await Task.Delay(500);
                    var slots = await ScrapeSlots(_fx.LlamaP100Url);
                    if (slots is not null)
                    {
                        var p100Cached = slots
                            .Select(s => s.TryGetProperty("n_prompt_tokens_cache", out var c) ? c.GetInt32() : 0)
                            .DefaultIfEmpty(0)
                            .Max();
                        Assert.True(p100Cached > 10,
                            $"P100 n_prompt_tokens_cache={p100Cached} after turn 2 — expected >10, which would mean cache was erased to 1 (UB bug)");
                    }
                }
            }

            // Turn 2–5 should not be catastrophically slower than turn 1
            var turn1s = turnTimes[0];
            for (var i = 1; i < turnTimes.Count; i++)
            {
                Assert.True(turnTimes[i] < turn1s * 3,
                    $"Turn {i + 2} took {turnTimes[i]:F1}s vs turn-1 {turn1s:F1}s (3× threshold) — likely a full re-prefill regression");
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task NoSlotLeak()
    {
        _fx.SkipIfUnreachable();
        var sidA = $"sys-leak-a-{Guid.NewGuid():N}"[..20];
        var sidB = $"sys-leak-b-{Guid.NewGuid():N}"[..20];

        async Task DoSession(string sid, int nTurns)
        {
            var history = new List<(string user, string assistant)>();
            for (var i = 0; i < nTurns; i++)
            {
                var messages = MakeHistory(SystemPrompt, history);
                messages.Add(new() { ["role"] = "user", ["content"] = Turns[i % Turns.Length] });
                var (_, events) = await DoCompletionStream(sid, messages, maxTokens: 150);
                var reply = ExtractContent(events);
                Assert.False(string.IsNullOrEmpty(reply), $"Session {sid} turn {i + 1} empty reply");
                history.Add((Turns[i % Turns.Length], reply));

                // After each turn, verify no duplicate (node, slot_id)
                var status = await _fx.GetStatusAsync();
                var sessionsList = status.Sessions?.Sessions ?? [];
                var active = sessionsList
                    .Where(s => !(s.SlotFreed ?? true) && s.SlotId is not null)
                    .ToList();
                var seen = new HashSet<(string? node, int? slotId)>();
                foreach (var s in active)
                {
                    var key = (s.Node, s.SlotId);
                    Assert.True(seen.Add(key),
                        $"Slot leak detected after session={sid} turn={i + 1}: two sessions share {key}");
                }
            }
        }

        await DoSession(sidA, 3);
        await DoSession(sidB, 3);
    }
}
