using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tests.LiveRig;

/// <summary>
/// Port of tests/system/test_agent_workflow_system.py.
///
/// Covers gaps exposed by running a real coding agent (opencode) against Hydra:
///   1. Tool-call flows (tools silently dropped before fix in router.py)
///   2. Multi-turn conversation (existing tests max at 3 turns)
///   3. Context accumulation across turns (not just single large prompts)
///
/// Requires all 6 services: coordinator :9000, llama RTX :8080, llama P100 :8086,
/// store :9500, agent-rtx :9601, agent-p100 :9602.
/// </summary>
[Collection("LiveRig")]
public sealed class AgentWorkflowTests : IClassFixture<LiveRigFixture>
{
    private readonly LiveRigFixture _fx;

    public AgentWorkflowTests(LiveRigFixture fx) => _fx = fx;

    // ── Tool definitions ──────────────────────────────────────────────────

    private static readonly JsonElement CalculatorTool = JsonSerializer.SerializeToElement(new
    {
        type = "function",
        function = new
        {
            name = "calculator",
            description = "Evaluate a simple arithmetic expression and return the numeric result.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    expression = new { type = "string", description = "A math expression to evaluate, e.g. '1234 * 5678'" }
                },
                required = new[] { "expression" }
            }
        }
    });

    private static readonly JsonElement WordCountTool = JsonSerializer.SerializeToElement(new
    {
        type = "function",
        function = new
        {
            name = "word_count",
            description = "Count the number of words in a text string.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The text to count words in" }
                },
                required = new[] { "text" }
            }
        }
    });

    private static string ExecuteTool(JsonElement toolCall)
    {
        var name = toolCall.GetProperty("function").GetProperty("name").GetString()!;
        var argsStr = toolCall.GetProperty("function").GetProperty("arguments").GetString()!;
        var args = JsonSerializer.Deserialize<JsonElement>(argsStr);

        return name switch
        {
            "calculator" => ExecuteCalculator(args),
            "word_count" => args.TryGetProperty("text", out var t) ? t.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString() : "0",
            _ => $"unknown tool: {name}"
        };
    }

    private static string ExecuteCalculator(JsonElement args)
    {
        var expr = args.TryGetProperty("expression", out var e) ? e.GetString() ?? "0" : "0";
        var allowed = "0123456789+-*/(). ";
        if (expr.All(c => allowed.Contains(c)))
        {
            try
            {
                var dt = new System.Data.DataTable();
                var result = dt.Compute(expr, null);
                return result?.ToString() ?? "error";
            }
            catch { return "error"; }
        }
        return "error: unsafe expression";
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────

    private async Task<JsonElement> SendCompletion(
        string sessionId,
        List<Dictionary<string, object?>> messages,
        List<JsonElement>? tools = null,
        int maxTokens = 200)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
            ["session_id"] = sessionId,
        };
        if (tools != null) body["tools"] = tools;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetStatus()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var resp = await HttpHelpers.Client.GetAsync($"{_fx.CoordUrl}/status", cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── Tests: tool calls ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task ToolCallBasic()
    {
        _fx.SkipIfUnreachable();
        var sessionId = $"agent-tool-basic-{Guid.NewGuid():N}"[..20];
        try
        {
            // Turn 1 — model should call the calculator tool
            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = "What is 1234 multiplied by 5678? Use the calculator tool to get the exact answer." }
            };
            var resp = await SendCompletion(sessionId, messages, [CalculatorTool], maxTokens: 600);
            var choice = resp.GetProperty("choices")[0];
            Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
            var toolCalls = choice.GetProperty("message").GetProperty("tool_calls");
            Assert.True(toolCalls.GetArrayLength() >= 1);
            Assert.Equal("calculator", toolCalls[0].GetProperty("function").GetProperty("name").GetString());

            // Turn 2 — inject tool result, expect final natural-language answer
            var result = ExecuteTool(toolCalls[0]);
            Assert.Equal("7006652", result);

            messages.Add(new() { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = toolCalls });
            messages.Add(new() { ["role"] = "tool", ["tool_call_id"] = toolCalls[0].GetProperty("id").GetString(), ["content"] = result });
            var resp2 = await SendCompletion(sessionId, messages, [CalculatorTool], maxTokens: 400);
            var choice2 = resp2.GetProperty("choices")[0];
            Assert.Equal("stop", choice2.GetProperty("finish_reason").GetString());
            var answer = HttpHelpers.GetOutputText(choice2.GetProperty("message"));
            Assert.Contains("7006652", answer.Replace(",", ""));
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ToolCallMultiStep()
    {
        _fx.SkipIfUnreachable();
        var sessionId = $"agent-tool-multi-{Guid.NewGuid():N}"[..20];
        var padding = HttpHelpers.GenerateText(1000);
        try
        {
            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "user", ["content"] = $"{padding}\n\nGiven the above context: first calculate 999 * 111, then count the words in the phrase 'hello world foo bar'. Use both tools and report the results." }
            };
            var resp = await SendCompletion(sessionId, messages, [CalculatorTool, WordCountTool], maxTokens: 600);
            var choice = resp.GetProperty("choices")[0];
            Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
            var toolCalls1 = choice.GetProperty("message").GetProperty("tool_calls");
            Assert.True(toolCalls1.GetArrayLength() >= 1);

            // Inject all tool results from turn 1
            messages.Add(new() { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = toolCalls1 });
            foreach (var tc in toolCalls1.EnumerateArray())
            {
                messages.Add(new() { ["role"] = "tool", ["tool_call_id"] = tc.GetProperty("id").GetString(), ["content"] = ExecuteTool(tc) });
            }

            // Turn 2 — model may call more tools or produce final answer
            var resp2 = await SendCompletion(sessionId, messages, [CalculatorTool, WordCountTool], maxTokens: 600);
            var choice2 = resp2.GetProperty("choices")[0];
            var finalChoice = choice2;

            if (choice2.GetProperty("finish_reason").GetString() == "tool_calls")
            {
                var toolCalls2 = choice2.GetProperty("message").GetProperty("tool_calls");
                messages.Add(new() { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = toolCalls2 });
                foreach (var tc in toolCalls2.EnumerateArray())
                {
                    messages.Add(new() { ["role"] = "tool", ["tool_call_id"] = tc.GetProperty("id").GetString(), ["content"] = ExecuteTool(tc) });
                }
                var resp3 = await SendCompletion(sessionId, messages, [CalculatorTool, WordCountTool], maxTokens: 600);
                finalChoice = resp3.GetProperty("choices")[0];
            }

            Assert.Equal("stop", finalChoice.GetProperty("finish_reason").GetString());
            var final = HttpHelpers.GetOutputText(finalChoice.GetProperty("message"));
            Assert.True(final.Contains("110889") || final.Contains("4"),
                $"Expected tool results (110889 and/or 4) in final response. Got: {final[..Math.Min(300, final.Length)]}");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    // ── Tests: multi-turn context accumulation ────────────────────────────

    [SkippableTheory]
    [InlineData(8_000, 6, 150, 180)]
    [InlineData(16_000, 10, 150, 360)]
    public async Task MultiturnContext(int targetTokens, int turns, int maxTokensPerTurn, int timeoutSec)
    {
        _fx.SkipIfUnreachable();
        var sessionId = $"agent-ctx-{targetTokens}-{Guid.NewGuid():N}"[..20];
        var tokensPerTurn = targetTokens / turns;
        var history = new List<Dictionary<string, object?>>();
        var prevNPast = 0;
        int? slotIdSeen = null;

        try
        {
            for (var turn = 0; turn < turns; turn++)
            {
                var padding = HttpHelpers.GenerateText(tokensPerTurn - 30);
                var userMsg = $"[Turn {turn + 1}/{turns}] {padding} In one sentence, summarize the main theme above.";
                var messages = new List<Dictionary<string, object?>>(history)
                {
                    new() { ["role"] = "user", ["content"] = userMsg }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                var body = new Dictionary<string, object?>
                {
                    ["messages"] = messages,
                    ["max_tokens"] = maxTokensPerTurn,
                    ["temperature"] = 0,
                    ["stream"] = false,
                    ["session_id"] = sessionId,
                };
                var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
                resp.EnsureSuccessStatusCode();
                var respJson = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var choice = respJson.GetProperty("choices")[0];
                var finishReason = choice.GetProperty("finish_reason").GetString()!;
                Assert.True(finishReason is "stop" or "length",
                    $"Turn {turn + 1}: unexpected finish_reason={finishReason}");
                var reply = HttpHelpers.GetOutputText(choice.GetProperty("message"));
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn + 1}: empty reply");
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];

                // Verify n_past grows
                var status = await _fx.GetStatusAsync();
                var session = status.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session?.NPast is int np && np > 0)
                {
                    Assert.True(np > prevNPast,
                        $"Turn {turn + 1}: n_past did not grow ({prevNPast} → {np}). KV cache may have been evicted or reset.");
                    prevNPast = np;
                }

                // Verify slot_id remains stable
                if (session?.SlotId is int sid)
                {
                    if (slotIdSeen is null)
                        slotIdSeen = sid;
                    else
                        Assert.Equal(slotIdSeen.Value, sid);
                }
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Multiturn40kContext()
    {
        _fx.SkipIfUnreachable();
        var targetTokens = 40_000;
        var turns = 15;
        var tokensPerTurn = targetTokens / turns;
        var sessionId = $"agent-ctx-40k-{Guid.NewGuid():N}"[..20];
        var history = new List<Dictionary<string, object?>>();
        var prevNPast = 0;

        try
        {
            for (var turn = 0; turn < turns; turn++)
            {
                var padding = HttpHelpers.GenerateText(tokensPerTurn - 30);
                var userMsg = $"[Turn {turn + 1}/{turns}] {padding} Summarize in one sentence.";
                var messages = new List<Dictionary<string, object?>>(history)
                {
                    new() { ["role"] = "user", ["content"] = userMsg }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));
                var body = new Dictionary<string, object?>
                {
                    ["messages"] = messages,
                    ["max_tokens"] = 150,
                    ["temperature"] = 0,
                    ["stream"] = false,
                    ["session_id"] = sessionId,
                };
                var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
                resp.EnsureSuccessStatusCode();
                var respJson = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var reply = HttpHelpers.GetOutputText(respJson.GetProperty("choices")[0].GetProperty("message"));
                Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn + 1}: empty reply");
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];

                var status = await _fx.GetStatusAsync();
                var session = status.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session?.NPast is int np && np > 0)
                {
                    Assert.True(np > prevNPast, $"Turn {turn + 1}: n_past stalled at {prevNPast}");
                    prevNPast = np;
                }
            }
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    // ── Test: tool calls during a growing-context session ────────────────

    [SkippableFact]
    public async Task ToolCallWithGrowingContext()
    {
        _fx.SkipIfUnreachable();
        var sessionId = $"agent-tool-ctx-{Guid.NewGuid():N}"[..20];
        var tokensPerTurn = 2_000;
        var history = new List<Dictionary<string, object?>>();
        var toolCallTurns = new HashSet<int> { 3, 5, 7 };
        var toolCallCount = 0;

        try
        {
            for (var turn = 1; turn <= 8; turn++)
            {
                var padding = HttpHelpers.GenerateText(tokensPerTurn - 50);

                if (toolCallTurns.Contains(turn))
                {
                    var expression = $"{turn * 111} * {turn * 222}";
                    var expected = (turn * 111 * turn * 222).ToString();
                    var userMsg = $"[Turn {turn}] {padding}\n\nPlease calculate {expression} using the calculator tool.";
                    var messages = new List<Dictionary<string, object?>>(history)
                    {
                        new() { ["role"] = "user", ["content"] = userMsg }
                    };
                    var resp = await SendCompletion(sessionId, messages, [CalculatorTool], maxTokens: 600);
                    var choice = resp.GetProperty("choices")[0];

                    if (choice.GetProperty("finish_reason").GetString() == "tool_calls")
                    {
                        var toolCalls = choice.GetProperty("message").GetProperty("tool_calls");
                        var result = ExecuteTool(toolCalls[0]);
                        toolCallCount++;

                        messages.Add(new() { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = toolCalls });
                        messages.Add(new() { ["role"] = "tool", ["tool_call_id"] = toolCalls[0].GetProperty("id").GetString(), ["content"] = result });
                        var resp2 = await SendCompletion(sessionId, messages, [CalculatorTool], maxTokens: 400);
                        var reply = HttpHelpers.GetOutputText(resp2.GetProperty("choices")[0].GetProperty("message"));
                        Assert.Contains(expected, reply);
                        history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];
                    }
                    else
                    {
                        var reply = HttpHelpers.GetOutputText(choice.GetProperty("message"));
                        history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];
                    }
                }
                else
                {
                    var userMsg = $"[Turn {turn}] {padding} Respond with one sentence acknowledging this turn.";
                    var messages = new List<Dictionary<string, object?>>(history)
                    {
                        new() { ["role"] = "user", ["content"] = userMsg }
                    };
                    var resp = await SendCompletion(sessionId, messages, maxTokens: 100);
                    var reply = HttpHelpers.GetOutputText(resp.GetProperty("choices")[0].GetProperty("message"));
                    Assert.False(string.IsNullOrEmpty(reply), $"Turn {turn}: empty reply");
                    history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];
                }
            }

            Assert.True(toolCallCount >= 1,
                "Expected at least 1 tool call across turns 3, 5, 7. Model may not be calling tools when context is large.");
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }

    // ── Test: migration mid-workflow ──────────────────────────────────────

    [SkippableFact]
    public async Task SessionMigrationMidWorkflow()
    {
        _fx.SkipIfUnreachable();
        var sessionId = $"agent-migrate-{Guid.NewGuid():N}"[..20];
        var tokensPerTurn = 1_600;
        var history = new List<Dictionary<string, object?>>();

        try
        {
            // Phase 1: 5 turns on RTX building context
            for (var turn = 1; turn <= 5; turn++)
            {
                var padding = HttpHelpers.GenerateText(tokensPerTurn - 30);
                var userMsg = $"[Turn {turn}/5] {padding} Acknowledge in one sentence.";
                var messages = new List<Dictionary<string, object?>>(history)
                {
                    new() { ["role"] = "user", ["content"] = userMsg }
                };
                var resp = await SendCompletion(sessionId, messages, maxTokens: 100);
                var reply = HttpHelpers.GetOutputText(resp.GetProperty("choices")[0].GetProperty("message"));
                Assert.False(string.IsNullOrEmpty(reply), $"Phase 1 turn {turn}: empty reply");
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];
            }

            // Verify session is on RTX
            var status = await _fx.GetStatusAsync();
            var sessionInfo = status.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            Assert.NotNull(sessionInfo);
            Assert.Equal("rtx", sessionInfo.Node);
            Assert.True(sessionInfo.NPast > 0, "n_past should be > 0 after 5 turns");

            // Migrate to P100
            using var migrateCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var migrateResp = await HttpHelpers.Client.PostAsJsonAsync(
                $"{_fx.CoordUrl}/sessions/{sessionId}/migrate",
                new { target = "p100" }, migrateCts.Token);
            Assert.True(migrateResp.IsSuccessStatusCode, $"Migration failed: {await migrateResp.Content.ReadAsStringAsync()}");
            var migrateBody = await migrateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(migrateBody.GetProperty("migrated").GetBoolean());

            // Phase 2: 2 more turns on P100
            for (var turn = 6; turn <= 7; turn++)
            {
                var userMsg = $"[Turn {turn}/7] Continue the conversation. Summarize what we discussed so far.";
                var messages = new List<Dictionary<string, object?>>(history)
                {
                    new() { ["role"] = "user", ["content"] = userMsg }
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var body = new Dictionary<string, object?>
                {
                    ["messages"] = messages,
                    ["max_tokens"] = 200,
                    ["temperature"] = 0,
                    ["stream"] = false,
                    ["session_id"] = sessionId,
                };
                var resp = await HttpHelpers.Client.PostAsJsonAsync($"{_fx.CoordUrl}/v1/chat/completions", body, cts.Token);
                resp.EnsureSuccessStatusCode();
                var respJson = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var reply = HttpHelpers.GetOutputText(respJson.GetProperty("choices")[0].GetProperty("message"));
                Assert.False(string.IsNullOrEmpty(reply), $"Phase 2 turn {turn}: empty reply after migration");
                history = [.. messages, new() { ["role"] = "assistant", ["content"] = reply }];
            }

            // Verify session moved to P100
            var status2 = await _fx.GetStatusAsync();
            var sessionAfter = status2.Sessions?.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (sessionAfter is not null)
                Assert.Equal("p100", sessionAfter.Node);
        }
        finally
        {
            await _fx.DeleteSessionAsync(sessionId);
        }
    }
}
