namespace Tests.AgentWorkload;

/// <summary>
/// Pure-logic unit tests for CLI driver output parsing.
/// These do NOT require the actual CLI binaries.
/// </summary>
public class CliDriverParsingTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PiCliDriver_ParseOutput_ValidJson_ExtractsAllFields()
    {
        var json = """
        {
          "content": "The file handles GPU initialization.",
          "reasoning_content": "Let me think about this...",
          "usage": {
            "prompt_tokens": 1500,
            "completion_tokens": 200,
            "prompt_tokens_details": {
              "cached_tokens": 800
            }
          }
        }
        """;

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
        var json = """
        {
          "content": "Hello",
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 50
          }
        }
        """;

        var result = PiCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.True(result.IsValidJson);
        Assert.Equal(0, result.CachedTokens);
        Assert.False(result.ReasoningContentPresent);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_InvalidJson_ReturnsRawAsContent()
    {
        const string raw = "Error: connection refused to localhost:9000";

        var result = PiCliDriver.ParseOutput(raw, 1, TimeSpan.FromSeconds(2), BaseTime, BaseTime.AddSeconds(2));

        Assert.False(result.IsValidJson);
        Assert.Equal(raw, result.ResponseContent);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void PiCliDriver_ParseOutput_ContentAsObject_ParsesRaw()
    {
        var json = """
        {
          "content": {"type": "text", "text": "response"},
          "usage": {"prompt_tokens": 50, "completion_tokens": 10}
        }
        """;

        var result = PiCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(1), BaseTime, BaseTime.AddSeconds(1));

        Assert.True(result.IsValidJson);
        Assert.NotNull(result.ResponseContent);
        Assert.Contains("response", result.ResponseContent);
    }

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_ValidJson_ExtractsFields()
    {
        var json = """
        {
          "content": "Analysis complete.",
          "usage": {
            "prompt_tokens": 2000,
            "completion_tokens": 300,
            "prompt_tokens_details": {
              "cached_tokens": 1200
            }
          }
        }
        """;

        var result = OpenCodeCliDriver.ParseOutput(json, 0, TimeSpan.FromSeconds(10), BaseTime, BaseTime.AddSeconds(10));

        Assert.True(result.IsValidJson);
        Assert.Equal("Analysis complete.", result.ResponseContent);
        Assert.Equal(2000, result.PromptTokens);
        Assert.Equal(1200, result.CachedTokens);
    }

    [Fact]
    public void OpenCodeCliDriver_ParseOutput_InvalidJson_ReturnsRawAsContent()
    {
        const string raw = "bash: opencode: command not found";

        var result = OpenCodeCliDriver.ParseOutput(raw, 127, TimeSpan.FromSeconds(0.1), BaseTime, BaseTime.AddMilliseconds(100));

        Assert.False(result.IsValidJson);
        Assert.Equal(raw, result.ResponseContent);
        Assert.Equal(127, result.ExitCode);
    }
}
