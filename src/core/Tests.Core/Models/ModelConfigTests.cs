using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Services;
using Xunit;

namespace Tests.Core.Models;

public class ModelConfigTests
{
    private const string SampleModelJson = """
    {
      "description": "Test model",
      "prefill_model_file_name": "model-q3.gguf",
      "decode_model_file_name": "model-q5.gguf",
      "load_time_s": 40,
      "quality_tier": 1,
      "requirements": {
        "min_vram_mb": 12000,
        "required_capabilities": 1,
        "decode_requirements": { "min_vram_mb": 16000, "required_capabilities": 1 }
      },
      "routing": {
        "auto_eligible": true,
        "min_prompt_tokens": 0,
        "max_prompt_tokens": 2048,
        "max_context_tokens": 320000,
        "requires_workers": ["p100"]
      },
      "engine_config": { "n_gpu_layers": 99, "n_ctx": 320000 },
      "node_config": { "rtx": "node-rtx.yaml" },
      "workers_file": "workers.json"
    }
    """;

    [Fact]
    public void Deserialize_ModelTemplate()
    {
        var template = JsonSerializer.Deserialize<ModelTemplate>(SampleModelJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(template);
        Assert.Equal("model-q3.gguf", template.PrefillModelFileName);
        Assert.Equal("model-q5.gguf", template.DecodeModelFileName);
        Assert.Equal(40, template.LoadTimeS);
        Assert.Equal(1, template.QualityTier);
        Assert.NotNull(template.Requirements);
        Assert.Equal(12000, template.Requirements!.MinVramMb);
        Assert.NotNull(template.Requirements.DecodeRequirements);
        Assert.Equal(16000, template.Requirements.DecodeRequirements!.MinVramMb);
    }

    [Fact]
    public void Deserialize_RoutingRule()
    {
        var template = JsonSerializer.Deserialize<ModelTemplate>(SampleModelJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(template?.Routing);
        Assert.True(template!.Routing!.AutoEligible);
        Assert.Equal(0, template.Routing.MinPromptTokens);
        Assert.Equal(2048, template.Routing.MaxPromptTokens);
        Assert.Contains("p100", template.Routing.RequiresWorkers!);
    }

    [Fact]
    public void Deserialize_EngineDefaults()
    {
        var json = """{"flash_attn": true, "cache_type_k": "q8_0", "ubatch_size": 512, "fit": false}""";
        var defaults = JsonSerializer.Deserialize<EngineDefaults>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(defaults);
        Assert.True(defaults!.FlashAttn);
        Assert.Equal("q8_0", defaults.CacheTypeK);
        Assert.Equal(512, defaults.UbatchSize);
        Assert.False(defaults.Fit!.Value);
    }

    [Fact]
    public void Deserialize_AutoRoutingPolicy()
    {
        var json = """{"enabled": true, "default_model": "moe-35b-solo", "swap_cost_budget_s": 30}""";
        var policy = JsonSerializer.Deserialize<AutoRoutingPolicy>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(policy);
        Assert.True(policy!.Enabled);
        Assert.Equal("moe-35b-solo", policy.DefaultModel);
        Assert.Equal(30, policy.SwapCostBudgetS);
    }

    // #479/S3: routing identity → GGUF-file alias translation.
    private static ModelConfigLoader LoaderWithAliases()
    {
        var config = new ModelsConfig
        {
            SchemaVersion = 2,
            AutoRouting = new AutoRoutingPolicy { Enabled = true, DefaultModel = "moe-35b-solo", SwapCostBudgetS = 30 },
            Models = new Dictionary<string, ModelTemplate>(),
            ModelFileAliases = new Dictionary<string, GgufAliasMapping>
            {
                ["moe-35b-pd"] = new GgufAliasMapping { Prefill = "qwen3.6-35B-mini", Decode = "qwen3.6-35B-balanced" },
                ["dense-27b-combined"] = new GgufAliasMapping { Prefill = "qwen3.6-27B-coder", Decode = "qwen3.6-27B-coder" },
            },
        };
        return ModelConfigLoader.Create(config);
    }

    [Fact]
    public void TranslateToGgufAlias_PrefillRole_ReturnsPrefillAlias()
    {
        var loader = LoaderWithAliases();
        Assert.Equal("qwen3.6-35B-mini", loader.TranslateToGgufAlias("moe-35b-pd"));
    }

    [Fact]
    public void TranslateToGgufAlias_DecodeRole_ReturnsDecodeAlias()
    {
        var loader = LoaderWithAliases();
        Assert.Equal("qwen3.6-35B-balanced", loader.TranslateToGgufAlias("moe-35b-pd", decodeRole: true));
    }

    [Fact]
    public void TranslateToGgufAlias_SameQuant_ReturnsSameAliasBothRoles()
    {
        var loader = LoaderWithAliases();
        Assert.Equal("qwen3.6-27B-coder", loader.TranslateToGgufAlias("dense-27b-combined"));
        Assert.Equal("qwen3.6-27B-coder", loader.TranslateToGgufAlias("dense-27b-combined", decodeRole: true));
    }

    [Fact]
    public void TranslateToGgufAlias_UnknownAlias_ReturnsNull()
    {
        var loader = LoaderWithAliases();
        Assert.Null(loader.TranslateToGgufAlias("does-not-exist"));
    }

    [Fact]
    public void TranslateToGgufAlias_NoMapConfigured_ReturnsNull()
    {
        var loader = ModelConfigLoader.Create(new ModelsConfig
        {
            SchemaVersion = 2,
            AutoRouting = new AutoRoutingPolicy { Enabled = true },
            Models = new Dictionary<string, ModelTemplate>(),
        });
        Assert.Null(loader.TranslateToGgufAlias("moe-35b-pd"));
    }

    [Fact]
    public void Deserialize_ModelFileAliases_MapsRoutingIdentityToGgufAlias()
    {
        var json = """
        {
            "schema_version": 2,
            "model_file_aliases": {
                "moe-35b-pd": { "prefill": "qwen3.6-35B-mini", "decode": "qwen3.6-35B-balanced" }
            }
        }
        """;
        var config = JsonSerializer.Deserialize<ModelsConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
        Assert.NotNull(config?.ModelFileAliases);
        Assert.Equal("qwen3.6-35B-mini", config!.ModelFileAliases!["moe-35b-pd"].Prefill);
        Assert.Equal("qwen3.6-35B-balanced", config.ModelFileAliases["moe-35b-pd"].Decode);
    }
}
