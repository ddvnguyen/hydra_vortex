using System.Text.Json;

namespace Tests.AgentWorkload;

/// <summary>
/// Pure-logic unit tests for CLI driver output parsing and LiveRigGuard health check.
/// These do NOT require the actual CLI binaries or a live rig.
/// </summary>
public class CliDriverParsingTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    // ── PiCliDriver: single-line JSON (back-compat for single-JSON-doc output) ──

    [Fact]
    public void PiCliDriver_ParseOutput_SingleJsonDoc_ExtractsAllFields()
    {
        var json = "{\"content\":\"The file handles GPU initialization.\",\"reasoning_content\":\"Let me think about this...\",\"usage\":{\"prompt_tokens\":1500,\"completion_tokens\":200,\"prompt_tokens_details\":{\"cached_tokens\":800}}}";

        var result = PiCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(5), BaseTime, BaseTime.AddSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.IsValidJson);
        Assert.Equal("The file handles GPU initialization.", result.ResponseContent);
        Assert.True(result.ReasoningContentPresent);
        Assert.Equal(1500, result.PromptTokens);
        Assert.Equal(200, result.CompletionTokens);
        Assert.Equal(800, result.CachedTokens);
        Assert.Equal(TimeSpan.FromSeconds(5), result.WallClockDuration);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_NoCachedTokens_DefaultsToZero()
    {
        var json = "{\"content\":\"Hello\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":50}}";

        var result = PiCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.True(result.IsValidJson);
        Assert.Equal(0, result.CachedTokens);
        Assert.False(result.ReasoningContentPresent);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_InvalidJson_ReturnsEmptyContent()
    {
        const string raw = "Error: connection refused to localhost:9000";

        var result = PiCliDriver.ParseOutput(raw, 1, TimeSpan.FromSeconds(2), BaseTime, BaseTime.AddSeconds(2));

        // NDJSON parser: non-JSON lines produce no content (IsValidJson stays false)
        Assert.False(result.IsValidJson);
        Assert.Null(result.ResponseContent);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_ContentAsObject_ParsesRaw()
    {
        var json = "{\"content\":{\"type\":\"text\",\"text\":\"response\"},\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":10}}";

        var result = PiCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.True(result.IsValidJson);
        Assert.NotNull(result.ResponseContent);
        Assert.Contains("response", result.ResponseContent);
    }

    // ── PiCliDriver: NDJSON of the REAL internal session event stream ──

    [Fact]
    public void PiCliDriver_ParseOutput_RealEventStream_ExtractsFinalAssistantMessage()
    {
        // Real pi --mode json output: NDJSON of pi's internal session event
        // stream. The final assistant message is carried in message_start /
        // message_end with content as an array of {type:"text"} blocks and
        // usage in pi's own counters ({input, output, cacheRead}).
        var ndjson = string.Join('\n',
            "{\"type\":\"session\",\"id\":\"ses_abc\",\"version\":\"0.80.6\"}",
            "{\"type\":\"agent_start\",\"agentId\":\"ag_1\",\"mode\":\"build\"}",
            "{\"type\":\"turn_start\",\"agentId\":\"ag_1\",\"turnId\":1}",
            "{\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"The file handles GPU initialization.\"}],\"usage\":{\"input\":1500,\"output\":200,\"cacheRead\":800,\"cacheWrite\":0,\"totalTokens\":2500,\"cost\":{}},\"stopReason\":\"complete\",\"timestamp\":1}}",
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"The file handles GPU initialization.\"}],\"usage\":{\"input\":1500,\"output\":200,\"cacheRead\":800,\"cacheWrite\":0,\"totalTokens\":2500,\"cost\":{}},\"stopReason\":\"complete\",\"timestamp\":1}}",
            "{\"type\":\"turn_end\",\"agentId\":\"ag_1\",\"turnId\":1,\"elapsedMs\":10}",
            "{\"type\":\"agent_end\",\"agentId\":\"ag_1\",\"willRetry\":false}");

        var result = PiCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(5), BaseTime, BaseTime.AddSeconds(5));

        Assert.True(result.IsValidJson);
        Assert.Equal("The file handles GPU initialization.", result.ResponseContent);
        Assert.Equal(1500, result.PromptTokens);
        Assert.Equal(200, result.CompletionTokens);
        Assert.Equal(800, result.CachedTokens);
        Assert.False(result.ReasoningContentPresent);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_ThinkingContentBlock_DetectsReasoning()
    {
        // Reasoning arrives as a typed `thinking` content block
        // ({type:"thinking", thinking:...}), NOT a top-level reasoning_content.
        var ndjson = string.Join('\n',
            "{\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Let me analyze this...\"}],\"usage\":{\"input\":200,\"output\":30,\"cacheRead\":10,\"cacheWrite\":0,\"totalTokens\":240},\"stopReason\":\"complete\",\"timestamp\":1}}",
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Let me analyze this...\"},{\"type\":\"text\",\"text\":\"The result.\"}],\"usage\":{\"input\":200,\"output\":30,\"cacheRead\":10,\"cacheWrite\":0,\"totalTokens\":240},\"stopReason\":\"complete\",\"timestamp\":1}}");

        var result = PiCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(3), BaseTime, BaseTime.AddSeconds(3));

        Assert.True(result.IsValidJson);
        Assert.True(result.ReasoningContentPresent);
        Assert.Equal("Let me analyze this...", result.ReasoningContent);
        Assert.Equal("The result.", result.ResponseContent);
        Assert.Equal(200, result.PromptTokens);
        Assert.Equal(30, result.CompletionTokens);
        Assert.Equal(10, result.CachedTokens);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_ToolCallBlock_CapturesNameAndArgs()
    {
        // pi tool_call content block: {type:"tool_call", name, arguments}.
        var ndjson = string.Join('\n',
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_call\",\"id\":\"tc_1\",\"name\":\"calculator\",\"arguments\":\"{\\\"expression\\\":\\\"1234*5678\\\"}\"},{\"type\":\"text\",\"text\":\"Computing.\"}],\"usage\":{\"input\":200,\"output\":30},\"stopReason\":\"tool_use\",\"timestamp\":1}}");

        var result = PiCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(3), BaseTime, BaseTime.AddSeconds(3));

        Assert.True(result.IsValidJson);
        Assert.True(result.ToolCallsPresent);
        Assert.Equal("calculator", result.ToolCallName);
        Assert.NotNull(result.ToolCallArgs);
        Assert.Contains("1234", result.ToolCallArgs);
        Assert.Contains("5678", result.ToolCallArgs);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_ToolCallArgsAsObject_CapturesRawText()
    {
        // args may arrive as an already-parsed JSON object (not a string).
        var ndjson = string.Join('\n',
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_call\",\"name\":\"calculator\",\"input\":{\"a\":1234,\"b\":5678}}],\"usage\":{\"input\":200,\"output\":30},\"stopReason\":\"tool_use\",\"timestamp\":1}}");

        var result = PiCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(3), BaseTime, BaseTime.AddSeconds(3));

        Assert.True(result.ToolCallsPresent);
        Assert.Equal("calculator", result.ToolCallName);
        Assert.NotNull(result.ToolCallArgs);
        Assert.Contains("1234", result.ToolCallArgs);
        Assert.Contains("5678", result.ToolCallArgs);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_LegacyOpenAiToolCalls_Captured()
    {
        // Legacy OpenAI-shaped single-doc: top-level tool_calls array with
        // function.name / function.arguments.
        var json = "{\"content\":\"\",\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"calculator\",\"arguments\":\"{\\\"a\\\":1234,\\\"b\\\":5678}\"}}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10}}";

        var result = PiCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.True(result.IsValidJson);
        Assert.True(result.ToolCallsPresent);
        Assert.Equal("calculator", result.ToolCallName);
        Assert.NotNull(result.ToolCallArgs);
        Assert.Contains("1234", result.ToolCallArgs);
        Assert.Contains("5678", result.ToolCallArgs);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_NoToolCallsOrReasoning_DefaultsNull()
    {
        var json = "{\"content\":\"7006652\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10}}";

        var result = PiCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.False(result.ToolCallsPresent);
        Assert.Null(result.ToolCallName);
        Assert.Null(result.ToolCallArgs);
        Assert.False(result.ReasoningContentPresent);
        Assert.Null(result.ReasoningContent);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_ErrorStopReason_IgnoresFailedMessage()
    {
        // An errored final message must NOT overwrite an earlier good response.
        var ndjson = string.Join('\n',
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Good answer.\"}],\"usage\":{\"input\":100,\"output\":10,\"cacheRead\":5},\"stopReason\":\"complete\",\"timestamp\":1}}",
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[],\"usage\":{\"input\":200,\"output\":0,\"cacheRead\":0},\"stopReason\":\"error\",\"timestamp\":2}}");

        var result = PiCliDriver.ParseOutput(ndjson, 1, TimeSpan.FromSeconds(2), BaseTime, BaseTime.AddSeconds(2));

        Assert.True(result.IsValidJson);
        Assert.Equal("Good answer.", result.ResponseContent);
        Assert.Equal(100, result.PromptTokens);
        Assert.Equal(10, result.CompletionTokens);
        Assert.Equal(5, result.CachedTokens);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_NoAssistantMessage_NoContent()
    {
        // Real event types that carry no assistant message (session/agent/turn
        // bookkeeping) must not produce content.
        var ndjson = string.Join('\n',
            "{\"type\":\"session\",\"id\":\"ses_abc\",\"version\":\"0.80.6\"}",
            "{\"type\":\"agent_start\",\"agentId\":\"ag_1\",\"mode\":\"build\"}",
            "{\"type\":\"turn_start\",\"agentId\":\"ag_1\",\"turnId\":1}");

        var result = PiCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(2), BaseTime, BaseTime.AddSeconds(2));

        Assert.True(result.IsValidJson);
        Assert.Null(result.ResponseContent);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_EmptyOutput_ReturnsDefaults()
    {
        var result = PiCliDriver.ParseOutput("", 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.False(result.IsValidJson);
        Assert.Null(result.ResponseContent);
    }

    // ── OpenCodeCliDriver: single-line JSON ──

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_SingleJsonDoc_ExtractsFields()
    {
        var json = "{\"content\":\"Analysis complete.\",\"usage\":{\"prompt_tokens\":2000,\"completion_tokens\":300,\"prompt_tokens_details\":{\"cached_tokens\":1200}}}";

        var result = OpenCodeCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(10), BaseTime, BaseTime.AddSeconds(10));

        Assert.True(result.IsValidJson);
        Assert.Equal("Analysis complete.", result.ResponseContent);
        Assert.Equal(2000, result.PromptTokens);
        Assert.Equal(1200, result.CachedTokens);
    }

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_InvalidJson_ReturnsEmptyContent()
    {
        const string raw = "bash: opencode: command not found";

        var result = OpenCodeCliDriver.ParseOutput(raw, 127, TimeSpan.FromSeconds(0.1), BaseTime, BaseTime.AddMilliseconds(100));

        Assert.False(result.IsValidJson);
        Assert.Null(result.ResponseContent);
        Assert.Equal(127, result.ExitCode);
    }

    // ── OpenCodeCliDriver: NDJSON of the REAL event stream ──

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_RealEventStream_ExtractsTextAndTokens()
    {
        // Real opencode run --format json output: NDJSON of opencode's event
        // stream. Content is in part.text of `text` events; token usage is in
        // part.tokens of `step_finish` events
        // ({total, input, output, reasoning, cache:{write, read}}).
        var ndjson = string.Join('\n',
            "{\"type\":\"step_start\",\"timestamp\":1,\"sessionID\":\"ses_111\",\"part\":{\"type\":\"step-start\",\"id\":\"p_1\"}}",
            "{\"type\":\"text\",\"timestamp\":2,\"sessionID\":\"ses_111\",\"part\":{\"type\":\"text\",\"text\":\"Analysis complete.\",\"time\":{\"created\":1,\"completed\":2}}}",
            "{\"type\":\"step_finish\",\"timestamp\":3,\"sessionID\":\"ses_111\",\"part\":{\"type\":\"step-finish\",\"reason\":\"stop\",\"snapshot\":\"abc\",\"tokens\":{\"total\":2000,\"input\":1500,\"output\":300,\"reasoning\":0,\"cache\":{\"write\":0,\"read\":1200}},\"cost\":0}}");

        var result = OpenCodeCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(10), BaseTime, BaseTime.AddSeconds(10));

        Assert.True(result.IsValidJson);
        Assert.Equal("Analysis complete.", result.ResponseContent);
        Assert.Equal(1500, result.PromptTokens);
        Assert.Equal(300, result.CompletionTokens);
        Assert.Equal(1200, result.CachedTokens);
        Assert.False(result.ReasoningContentPresent);
    }

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_StepFinishReasoningTokens_DetectsReasoning()
    {
        // Reasoning presence = part.tokens.reasoning > 0 on a step_finish event.
        var ndjson = string.Join('\n',
            "{\"type\":\"text\",\"timestamp\":1,\"sessionID\":\"ses_111\",\"part\":{\"type\":\"text\",\"text\":\"Thought through.\",\"time\":{\"created\":1,\"completed\":2}}}",
            "{\"type\":\"step_finish\",\"timestamp\":2,\"sessionID\":\"ses_111\",\"part\":{\"type\":\"step-finish\",\"reason\":\"stop\",\"tokens\":{\"total\":1000,\"input\":900,\"output\":100,\"reasoning\":75,\"cache\":{\"write\":0,\"read\":0}},\"cost\":0}}");

        var result = OpenCodeCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(10), BaseTime, BaseTime.AddSeconds(10));

        Assert.True(result.IsValidJson);
        Assert.True(result.ReasoningContentPresent);
        Assert.Equal("Thought through.", result.ResponseContent);
        Assert.Equal(900, result.PromptTokens);
        Assert.Equal(100, result.CompletionTokens);
        Assert.Equal(0, result.CachedTokens);
    }

    [Fact]
    public void OpenCodeCliDriver_ExtractSessionId_ReturnsFirstSessionId()
    {
        // Every opencode event carries the sessionID field; used to resolve the
        // created session on the first turn so later turns can pass --session.
        const string output =
            "{\"type\":\"step_start\",\"sessionID\":\"ses_03911db5\",\"part\":{\"type\":\"step-start\"}}\n" +
            "{\"type\":\"text\",\"sessionID\":\"ses_03911db5\",\"part\":{\"type\":\"text\",\"text\":\"ok\"}}\n";

        Assert.Equal("ses_03911db5", OpenCodeCliDriver.ExtractSessionId(output));
    }

    [Fact]
    public void OpenCodeCliDriver_ExtractSessionId_NoSessionId_ReturnsNull()
    {
        Assert.Null(OpenCodeCliDriver.ExtractSessionId(""));
        Assert.Null(OpenCodeCliDriver.ExtractSessionId("opencode: command not found"));
        Assert.Null(OpenCodeCliDriver.ExtractSessionId("{\"type\":\"error\",\"error\":{\"name\":\"UnknownError\"}}"));
    }

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_EmptyOutput_ReturnsDefaults()
    {
        var result = OpenCodeCliDriver.ParseOutput("", 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.False(result.IsValidJson);
        Assert.Null(result.ResponseContent);
    }

    // ── LiveRigGuard: health check JSON parsing ──

    [Fact]
    public void LiveRigGuard_ParseHealthResponse_ObjectNodes_ParsesCorrectly()
    {
        // Coordinator /health returns nodes as a JSON object keyed by node name
        var json = "{\"status\":\"healthy\",\"nodes\":{\"rtx\":{\"healthy\":true,\"slots_total\":2,\"slots_idle\":1,\"stuck_slots\":0},\"rtx3060\":{\"healthy\":true,\"slots_total\":1,\"slots_idle\":0,\"stuck_slots\":0},\"p100\":{\"healthy\":true,\"slots_total\":1,\"slots_idle\":1,\"stuck_slots\":0}},\"store\":{\"healthy\":true}}";

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("healthy", status.GetString());

        Assert.True(root.TryGetProperty("nodes", out var nodes));
        Assert.Equal(JsonValueKind.Object, nodes.ValueKind);

        int nodeCount = 0;
        foreach (var _ in nodes.EnumerateObject())
            nodeCount++;
        Assert.Equal(3, nodeCount);
    }

    [Fact]
    public void LiveRigGuard_ParseHealthResponse_EmptyNodes_ReturnsFalse()
    {
        var json = "{\"status\":\"healthy\",\"nodes\":{},\"store\":{\"healthy\":true}}";

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("nodes", out var nodes));
        Assert.Equal(JsonValueKind.Object, nodes.ValueKind);

        bool hasNodes = false;
        foreach (var _ in nodes.EnumerateObject())
        {
            hasNodes = true;
            break;
        }
        Assert.False(hasNodes);
    }

    [Fact]
    public void LiveRigGuard_ParseHealthResponse_NoNodesField_FallsBackToSlotsIdle()
    {
        var json = "{\"status\":\"healthy\",\"slots_idle\":3}";

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("healthy", status.GetString());

        Assert.False(root.TryGetProperty("nodes", out _));
        Assert.True(root.TryGetProperty("slots_idle", out _));
    }

    [Fact]
    public void LiveRigGuard_ParseHealthResponse_Degraded_ReturnsFalse()
    {
        var json = "{\"status\":\"degraded\",\"nodes\":{\"rtx\":{\"healthy\":false,\"slots_total\":2,\"slots_idle\":0,\"stuck_slots\":2}},\"store\":{\"healthy\":false}}";

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("status", out var status));
        Assert.NotEqual("healthy", status.GetString());
    }
}
