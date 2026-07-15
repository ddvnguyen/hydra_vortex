using System.Text.Json;
using Hydra.Core.Models;
using Xunit;

namespace Tests.Core;

/// <summary>
/// Unit tests for <see cref="EngineRequestOverrides"/>: extraction from
/// the request body and wire-JSON serialization. Phase 2b of
/// ddvnguyen/llama.cpp#36.
/// </summary>
public class EngineRequestOverridesTests
{
    [Fact]
    public void FromRequest_EmptyBody_ReturnsEmpty()
    {
        var o = EngineRequestOverrides.FromRequest(new Dictionary<string, object>());
        Assert.True(o.IsEmpty);
    }

    [Fact]
    public void FromRequest_NullBody_ReturnsEmpty()
    {
        var o = EngineRequestOverrides.FromRequest(null);
        Assert.True(o.IsEmpty);
    }

    [Fact]
    public void FromRequest_TemperatureOnly_NonEmpty()
    {
        var o = EngineRequestOverrides.FromRequest(new Dictionary<string, object>
        {
            ["temperature"] = 0.5
        });
        Assert.False(o.IsEmpty);
        Assert.Equal(0.5f, o.Temperature);
        Assert.Null(o.TopP);
    }

    [Fact]
    public void FromRequest_AllSamplingKeys_Parsed()
    {
        var body = new Dictionary<string, object>
        {
            ["temperature"] = 0.7,
            ["top_p"] = 0.9,
            ["top_k"] = 40,
            ["min_p"] = 0.05,
            ["repeat_penalty"] = 1.1,
            ["seed"] = 42u,
            ["stop"] = new[] { "\nUser:", "\nAssistant:" },
            ["max_tokens"] = 256
        };
        var o = EngineRequestOverrides.FromRequest(body);
        Assert.False(o.IsEmpty);
        Assert.Equal(0.7f, o.Temperature);
        Assert.Equal(0.9f, o.TopP);
        Assert.Equal(40, o.TopK);
        Assert.Equal(0.05f, o.MinP);
        Assert.Equal(1.1f, o.RepeatPenalty);
        Assert.Equal(42u, o.Seed);
        Assert.NotNull(o.Stop);
        Assert.Equal(2, o.Stop!.Count);
        Assert.Equal("\nUser:", o.Stop[0]);
        Assert.Equal(256, o.NPredict);
    }

    [Fact]
    public void FromRequest_JsonElement_Values_Parsed()
    {
        // The C# deserializes the request body as Dictionary<string, object>
        // where primitive values come through as JsonElement. The FromRequest
        // parser must handle that.
        var json = "{\"temperature\":0.3,\"top_p\":0.7,\"seed\":17}";
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object>();
        foreach (var p in doc.RootElement.EnumerateObject())
            dict[p.Name] = p.Value;
        var o = EngineRequestOverrides.FromRequest(dict);
        Assert.Equal(0.3f, o.Temperature);
        Assert.Equal(0.7f, o.TopP);
        Assert.Equal(17u, o.Seed);
    }

    [Fact]
    public void FromRequest_NonSamplingKeys_Ignored()
    {
        // max_tokens is mapped to NPredict; other unknown keys are ignored
        // without affecting IsEmpty.
        var o = EngineRequestOverrides.FromRequest(new Dictionary<string, object>
        {
            ["model"] = "moe-35b-solo",
            ["messages"] = new[] { new Dictionary<string, object> { ["role"] = "user" } }
        });
        Assert.True(o.IsEmpty);
    }

    [Fact]
    public void ToWireJson_EmptyObject()
    {
        var o = new EngineRequestOverrides();
        Assert.Equal("{}", o.ToWireJson());
    }

    [Fact]
    public void ToWireJson_Sampling_EmitsSamplingBlock()
    {
        var o = new EngineRequestOverrides(Temperature: 0.5f, TopP: 0.9f, Seed: 42u);
        var json = o.ToWireJson();
        Assert.Contains("\"sampling\":{", json);
        Assert.Contains("\"temp\":0.5", json);
        Assert.Contains("\"top_p\":0.9", json);
        Assert.Contains("\"seed\":42", json);
    }

    [Fact]
    public void ToWireJson_Stop_EmitsAntipromptArray()
    {
        var o = new EngineRequestOverrides(Stop: new[] { "\nUser:" });
        var json = o.ToWireJson();
        Assert.Contains("\"antiprompt\":[", json);
        Assert.Contains("\"\\nUser:\"", json);
    }

    [Fact]
    public void ToWireJson_NPredict_EmitsTopLevel()
    {
        var o = new EngineRequestOverrides(NPredict: 500);
        Assert.Equal("{\"n_predict\":500}", o.ToWireJson());
    }

    [Fact]
    public void ToWireJson_FloatCultureInvariant()
    {
        // The wire JSON must use "." as the decimal separator regardless of
        // the runtime locale. Validated by checking the raw bytes.
        var o = new EngineRequestOverrides(Temperature: 0.5f);
        var json = o.ToWireJson();
        Assert.DoesNotContain(",", json);  // no locale commas
    }
}
