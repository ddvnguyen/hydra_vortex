using System.Text.Json;
using Hydra.Core;

namespace Tests.Core;

public sealed class SlotMetaTests
{
    [Fact]
    public void Deserialize_With_All_Fields()
    {
        var json = """
        {
            "slot_id": 0,
            "n_past": 1234,
            "state_size": 819200000,
            "is_processing": true,
            "model_alias": "balanced",
            "model_hash": "abc123",
            "model_path": "/models/test.gguf",
            "operation": "prefill",
            "progress": 0.65,
            "tokens_processed": 8192,
            "tokens_total": 12600,
            "elapsed_ms": 3400
        }
        """;

        var meta = JsonSerializer.Deserialize<SlotMeta>(json);

        Assert.NotNull(meta);
        Assert.Equal(0, meta.SlotId);
        Assert.Equal(1234, meta.NPast);
        Assert.Equal(819200000, meta.StateSize);
        Assert.True(meta.IsProcessing);
        Assert.Equal("balanced", meta.ModelAlias);
        Assert.Equal("abc123", meta.ModelHash);
        Assert.Equal("/models/test.gguf", meta.ModelPath);
        Assert.Equal("prefill", meta.Operation);
        Assert.Equal(0.65f, meta.Progress, 2);
        Assert.Equal(8192, meta.TokensProcessed);
        Assert.Equal(12600, meta.TokensTotal);
        Assert.Equal(3400, meta.ElapsedMs);
    }

    [Fact]
    public void Deserialize_Without_New_Fields_Defaults_To_Empty()
    {
        var json = """
        {
            "slot_id": 1,
            "n_past": 500,
            "state_size": 1000000,
            "is_processing": false,
            "model_alias": "",
            "model_hash": "",
            "model_path": ""
        }
        """;

        var meta = JsonSerializer.Deserialize<SlotMeta>(json);

        Assert.NotNull(meta);
        Assert.Equal(1, meta.SlotId);
        Assert.Equal(500, meta.NPast);
        Assert.Equal(1000000, meta.StateSize);
        Assert.False(meta.IsProcessing);
        Assert.Equal("", meta.Operation);
        Assert.Equal(0f, meta.Progress);
        Assert.Equal(0, meta.TokensProcessed);
        Assert.Equal(0, meta.TokensTotal);
        Assert.Equal(0, meta.ElapsedMs);
    }

    [Fact]
    public void Deserialize_Partial_Fields()
    {
        var json = """
        {
            "slot_id": 2,
            "n_past": 100,
            "state_size": 500000,
            "is_processing": true,
            "operation": "decode",
            "progress": 0.25
        }
        """;

        var meta = JsonSerializer.Deserialize<SlotMeta>(json);

        Assert.NotNull(meta);
        Assert.Equal(2, meta.SlotId);
        Assert.Equal(100, meta.NPast);
        Assert.Equal(500000, meta.StateSize);
        Assert.True(meta.IsProcessing);
        Assert.Equal("decode", meta.Operation);
        Assert.Equal(0.25f, meta.Progress, 2);
        Assert.Equal(0, meta.TokensProcessed);
        Assert.Equal(0, meta.TokensTotal);
        Assert.Equal(0, meta.ElapsedMs);
    }

    [Fact]
    public void Deserialize_Empty_Json_Returns_Defaults()
    {
        var json = "{}";

        var meta = JsonSerializer.Deserialize<SlotMeta>(json);

        Assert.NotNull(meta);
        Assert.Equal(0, meta.SlotId);
        Assert.Equal(0, meta.NPast);
        Assert.Equal(0, meta.StateSize);
        Assert.False(meta.IsProcessing);
        Assert.Equal("", meta.ModelAlias);
        Assert.Equal("", meta.ModelHash);
        Assert.Equal("", meta.ModelPath);
        Assert.Equal("", meta.Operation);
        Assert.Equal(0f, meta.Progress);
        Assert.Equal(0, meta.TokensProcessed);
        Assert.Equal(0, meta.TokensTotal);
        Assert.Equal(0, meta.ElapsedMs);
    }

    [Fact]
    public void Deserialize_Invalid_Json_Throws()
    {
        var json = "invalid json";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SlotMeta>(json));
    }

    [Fact]
    public void Deserialize_With_Extra_Fields_Ignores_Them()
    {
        var json = """
        {
            "slot_id": 3,
            "n_past": 200,
            "state_size": 200000,
            "is_processing": false,
            "model_alias": "mini",
            "model_hash": "def456",
            "model_path": "/models/mini.gguf",
            "operation": "idle",
            "progress": 1.0,
            "tokens_processed": 0,
            "tokens_total": 0,
            "elapsed_ms": 0,
            "unknown_field": "should_be_ignored"
        }
        """;

        var meta = JsonSerializer.Deserialize<SlotMeta>(json);

        Assert.NotNull(meta);
        Assert.Equal(3, meta.SlotId);
        Assert.Equal(200, meta.NPast);
        Assert.Equal(200000, meta.StateSize);
        Assert.False(meta.IsProcessing);
        Assert.Equal("mini", meta.ModelAlias);
        Assert.Equal("def456", meta.ModelHash);
        Assert.Equal("/models/mini.gguf", meta.ModelPath);
        Assert.Equal("idle", meta.Operation);
        Assert.Equal(1.0f, meta.Progress, 2);
        Assert.Equal(0, meta.TokensProcessed);
        Assert.Equal(0, meta.TokensTotal);
        Assert.Equal(0, meta.ElapsedMs);
    }

    [Fact]
    public void Deserialize_With_Various_Operations()
    {
        var operations = new[] { "prefill", "decode", "save", "restore", "idle", "unknown" };

        foreach (var operation in operations)
        {
            var json = $$"""
            {
                "slot_id": 0,
                "n_past": 0,
                "state_size": 0,
                "is_processing": false,
                "operation": "{{operation}}",
                "progress": 0.5
            }
            """;

            var meta = JsonSerializer.Deserialize<SlotMeta>(json);

            Assert.NotNull(meta);
            Assert.Equal(operation, meta.Operation);
        }
    }

    [Fact]
    public void Deserialize_With_Progress_Boundary_Values()
    {
        // Test 0.0 progress
        var json0 = """
        {
            "slot_id": 0,
            "n_past": 0,
            "state_size": 0,
            "is_processing": false,
            "progress": 0.0
        }
        """;

        var meta0 = JsonSerializer.Deserialize<SlotMeta>(json0);
        Assert.NotNull(meta0);
        Assert.Equal(0f, meta0.Progress);

        // Test 1.0 progress
        var json1 = """
        {
            "slot_id": 0,
            "n_past": 0,
            "state_size": 0,
            "is_processing": false,
            "progress": 1.0
        }
        """;

        var meta1 = JsonSerializer.Deserialize<SlotMeta>(json1);
        Assert.NotNull(meta1);
        Assert.Equal(1.0f, meta1.Progress);

        // Test progress > 1.0 (should still deserialize)
        var jsonOver = """
        {
            "slot_id": 0,
            "n_past": 0,
            "state_size": 0,
            "is_processing": false,
            "progress": 1.5
        }
        """;

        var metaOver = JsonSerializer.Deserialize<SlotMeta>(jsonOver);
        Assert.NotNull(metaOver);
        Assert.Equal(1.5f, metaOver.Progress);
    }

    [Fact]
    public void Deserialize_With_Negative_Values()
    {
        var json = """
        {
            "slot_id": -1,
            "n_past": -100,
            "state_size": -1,
            "is_processing": false,
            "progress": -0.5,
            "tokens_processed": -10,
            "tokens_total": -20,
            "elapsed_ms": -1000
        }
        """;

        var meta = JsonSerializer.Deserialize<SlotMeta>(json);

        Assert.NotNull(meta);
        Assert.Equal(-1, meta.SlotId);
        Assert.Equal(-100, meta.NPast);
        Assert.Equal(-1, meta.StateSize);
        Assert.False(meta.IsProcessing);
        Assert.Equal(-0.5f, meta.Progress, 2);
        Assert.Equal(-10, meta.TokensProcessed);
        Assert.Equal(-20, meta.TokensTotal);
        Assert.Equal(-1000, meta.ElapsedMs);
    }

    [Fact]
    public void Deserialize_With_Large_Values()
    {
        var json = """
        {
            "slot_id": 1000,
            "n_past": 1000000,
            "state_size": 8589934592,
            "is_processing": true,
            "progress": 0.9999,
            "tokens_processed": 100000,
            "tokens_total": 100001,
            "elapsed_ms": 1000000
        }
        """;

        var meta = JsonSerializer.Deserialize<SlotMeta>(json);

        Assert.NotNull(meta);
        Assert.Equal(1000, meta.SlotId);
        Assert.Equal(1000000, meta.NPast);
        Assert.Equal(8589934592, meta.StateSize);
        Assert.True(meta.IsProcessing);
        Assert.Equal(0.9999f, meta.Progress, 4);
        Assert.Equal(100000, meta.TokensProcessed);
        Assert.Equal(100001, meta.TokensTotal);
        Assert.Equal(1000000, meta.ElapsedMs);
    }
}