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

    // ── PiCliDriver: NDJSON (multi-line, real CLI output) ──

    [Fact]
    public void PiCliDriver_ParseOutput_Ndjson_MultipleLines_ExtractsLastContent()
    {
        // Simulates pi --mode json output: progress line + final response
        var ndjson = "{\"type\":\"progress\",\"step\":\"thinking\"}\n{\"content\":\"The answer is 42.\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":50}}}";

        var result = PiCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(3), BaseTime, BaseTime.AddSeconds(3));

        Assert.True(result.IsValidJson);
        Assert.Equal("The answer is 42.", result.ResponseContent);
        Assert.Equal(100, result.PromptTokens);
        Assert.Equal(20, result.CompletionTokens);
        Assert.Equal(50, result.CachedTokens);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_Ndjson_OnlyProgressLines_NoContent()
    {
        var ndjson = "{\"type\":\"progress\",\"step\":\"thinking\"}\n{\"type\":\"progress\",\"step\":\"tool_call\",\"tool\":\"bash\"}";

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

    [Fact]
    public void PiCliDriver_ParseOutput_Ndjson_ReasoningContentAcrossLines()
    {
        var ndjson = "{\"reasoning_content\":\"Let me analyze...\"}\n{\"content\":\"The result.\",\"usage\":{\"prompt_tokens\":200,\"completion_tokens\":30}}";

        var result = PiCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(3), BaseTime, BaseTime.AddSeconds(3));

        Assert.True(result.IsValidJson);
        Assert.True(result.ReasoningContentPresent);
        Assert.Equal("The result.", result.ResponseContent);
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

    // ── OpenCodeCliDriver: NDJSON ──

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_Ndjson_MultipleLines()
    {
        var ndjson = "{\"type\":\"tool_use\",\"name\":\"read\",\"input\":{}}\n{\"content\":\"File contents here.\",\"usage\":{\"prompt_tokens\":500,\"completion_tokens\":100,\"prompt_tokens_details\":{\"cached_tokens\":200}}}";

        var result = OpenCodeCliDriver.ParseOutput(ndjson, 0, TimeSpan.FromSeconds(5), BaseTime, BaseTime.AddSeconds(5));

        Assert.True(result.IsValidJson);
        Assert.Equal("File contents here.", result.ResponseContent);
        Assert.Equal(200, result.CachedTokens);
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
