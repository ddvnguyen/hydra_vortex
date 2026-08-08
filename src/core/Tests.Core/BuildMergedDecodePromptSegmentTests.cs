using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Services;

namespace Tests.Core;

/// <summary>
/// Unit tests for
/// <see cref="WorkerSchedulerService.BuildMergedDecodePromptSegment"/> —
/// the #576 fix that carries tools/tool_choice/response_format/sampling/stop
/// on the merged-decode (0x43) prompt segment instead of dropping them.
/// </summary>
public class BuildMergedDecodePromptSegmentTests
{
    private static WorkItem MakeItem(
        Dictionary<string, object> request,
        EngineRequestOverrides? overrides = null)
    {
        var item = new WorkItem(
            request,
            new List<Dictionary<string, object>>(),
            "sess-576", "trace-576", null, 0, 0)
        {
            RequestOverrides = overrides
        };
        return item;
    }

    private static Dictionary<string, object> RequestWithMessages(string messagesJson, params (string Key, string Json)[] extra)
    {
        var dict = new Dictionary<string, object>();
        using var messagesDoc = JsonDocument.Parse(messagesJson);
        dict["messages"] = messagesDoc.RootElement.Clone();
        foreach (var (key, json) in extra)
        {
            using var doc = JsonDocument.Parse(json);
            dict[key] = doc.RootElement.Clone();
        }
        return dict;
    }

    [Fact]
    public void MessagesOnly_ProducesSingleKeyObject()
    {
        var item = MakeItem(RequestWithMessages("""[{"role":"user","content":"hi"}]"""));

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.Single(root.EnumerateObject());
        var messages = root.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public void ToolsToolChoiceResponseFormat_RoundTripUnchanged()
    {
        const string tools = """[{"type":"function","function":{"name":"get_weather","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}]""";
        const string toolChoice = """{"type":"function","function":{"name":"get_weather"}}""";
        const string responseFormat = """{"type":"json_schema","json_schema":{"name":"out","schema":{"type":"object"}}}""";
        const string messages = """[{"role":"user","content":"weather?"}]""";

        var item = MakeItem(RequestWithMessages(messages,
            ("tools", tools), ("tool_choice", toolChoice), ("response_format", responseFormat)));

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.Equal(4, root.EnumerateObject().Count());
        Assert.Equal(messages, root.GetProperty("messages").GetRawText());
        Assert.Equal(tools, root.GetProperty("tools").GetRawText());
        Assert.Equal(toolChoice, root.GetProperty("tool_choice").GetRawText());
        Assert.Equal(responseFormat, root.GetProperty("response_format").GetRawText());
        Assert.False(root.TryGetProperty("sampling", out _));
        Assert.False(root.TryGetProperty("stop", out _));
    }

    [Fact]
    public void Overrides_TemperatureTopPSeed_EmitDecodeApplySamplingKeys()
    {
        var item = MakeItem(
            RequestWithMessages("""[{"role":"user","content":"hi"}]"""),
            overrides: new EngineRequestOverrides(Temperature: 0.5f, TopP: 0.9f, Seed: 42u));

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        var sampling = root.GetProperty("sampling");
        Assert.Equal(3, sampling.EnumerateObject().Count());
        Assert.Equal(0.5, sampling.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, sampling.GetProperty("top_p").GetDouble());
        Assert.Equal(42u, sampling.GetProperty("seed").GetUInt32());
        // DECODE_APPLY wire shape — NOT the 0x40 CONFIGURE shape.
        Assert.False(sampling.TryGetProperty("temp", out _));
        Assert.False(root.TryGetProperty("stop", out _));
    }

    [Fact]
    public void Overrides_Stop_EmitTopLevelStopArray()
    {
        var item = MakeItem(
            RequestWithMessages("""[{"role":"user","content":"hi"}]"""),
            overrides: new EngineRequestOverrides(Stop: new[] { "\nUser:", "\nAssistant:" }));

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        var stop = root.GetProperty("stop");
        Assert.Equal(JsonValueKind.Array, stop.ValueKind);
        Assert.Equal(2, stop.GetArrayLength());
        Assert.Equal("\nUser:", stop[0].GetString());
        Assert.Equal("\nAssistant:", stop[1].GetString());
        // 0x40 shape ("antiprompt") must not leak into the prompt segment.
        Assert.False(root.TryGetProperty("antiprompt", out _));
        Assert.False(root.TryGetProperty("sampling", out _));
    }

    [Fact]
    public void Overrides_OnlyStop_NoSamplingObject()
    {
        var item = MakeItem(
            RequestWithMessages("""[{"role":"user","content":"hi"}]"""),
            overrides: new EngineRequestOverrides(Stop: new[] { "END" }));

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("sampling", out _));
        Assert.Equal("END", root.GetProperty("stop")[0].GetString());
    }

    [Fact]
    public void NoMessagesKey_ReturnsNull()
    {
        var item = MakeItem(new Dictionary<string, object>
        {
            ["model"] = "moe-35b-solo"
        });

        Assert.Null(WorkerSchedulerService.BuildMergedDecodePromptSegment(item));
    }

    [Fact]
    public void MaxTokens_EmitsNPredictInSegment()
    {
        // #581: DECODE_APPLY reads n_predict off the prompt segment directly;
        // the merged path must honor max_tokens even when the engine does not
        // merge the control header's "generation" object into the prompt.
        var item = MakeItem(RequestWithMessages("""[{"role":"user","content":"hi"}]""",
            ("max_tokens", "150")));

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.Equal(150, root.GetProperty("n_predict").GetInt32());
        Assert.Equal("hi", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void NPredictKey_TakesPrecedenceOverMaxTokens()
    {
        // Precedence mirrors llama-server's own request parsing
        // (params_from_json_cmpl: n_predict → max_completion_tokens → max_tokens).
        var item = MakeItem(RequestWithMessages("""[{"role":"user","content":"hi"}]""",
            ("n_predict", "50"), ("max_tokens", "150")));

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(50, doc.RootElement.GetProperty("n_predict").GetInt32());
    }

    [Fact]
    public void PrimitiveMaxTokens_EmitsNPredictInSegment()
    {
        // Requests built programmatically (tests/CLI) carry a plain int instead
        // of a JsonElement — the budget must still reach the engine.
        var item = MakeItem(new Dictionary<string, object>
        {
            ["messages"] = new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } },
            ["max_tokens"] = 150
        });

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(150, doc.RootElement.GetProperty("n_predict").GetInt32());
    }

    [Fact]
    public void MessagesOnly_WithEmptyOverrides_NoExtraKeys()
    {
        // An overrides record with all nulls must not add sampling/stop keys.
        var item = MakeItem(
            RequestWithMessages("""[{"role":"user","content":"hi"}]"""),
            overrides: new EngineRequestOverrides());

        var json = WorkerSchedulerService.BuildMergedDecodePromptSegment(item);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.Single(root.EnumerateObject());
        Assert.False(root.TryGetProperty("sampling", out _));
        Assert.False(root.TryGetProperty("stop", out _));
    }
}
