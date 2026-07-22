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
      "prefill_alias": "qwen3.6-35B-mini",
      "decode_alias": "qwen3.6-35B-balanced",
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
      "engine_config": { "n_gpu_layers": 99, "n_ctx": 320000 }
    }
    """;

    [Fact]
    public void Deserialize_ModelTemplate()
    {
        var template = JsonSerializer.Deserialize<ModelTemplate>(SampleModelJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(template);
        Assert.Equal("qwen3.6-35B-mini", template!.PrefillAlias);
        Assert.Equal("qwen3.6-35B-balanced", template.DecodeAlias);
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

    // ── #481 Phase 2c: model_file_aliases is alias-name -> GGUF file name ──

    private static ModelConfigLoader LoaderWithAliases()
    {
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            AutoRouting = new AutoRoutingPolicy { Enabled = true, DefaultModel = "moe-35b-solo", SwapCostBudgetS = 30 },
            Models = new Dictionary<string, ModelTemplate>
            {
                ["moe-35b-solo"] = new ModelTemplate
                {
                    PrefillAlias = "qwen3.6-35B-balanced",
                    DecodeAlias  = "qwen3.6-35B-balanced",
                },
                ["moe-35b-pd"] = new ModelTemplate
                {
                    PrefillAlias = "qwen3.6-35B-mini",
                    DecodeAlias  = "qwen3.6-35B-balanced",
                },
                ["dense-27b-combined"] = new ModelTemplate
                {
                    PrefillAlias = "qwen3.6-27B-coder",
                    DecodeAlias  = "qwen3.6-27B-coder",
                },
            },
            ModelFileAliases = new Dictionary<string, string>
            {
                ["qwen3.6-35B-mini"]     = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                ["qwen3.6-35B-balanced"] = "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf",
                ["qwen3.6-27B-coder"]    = "Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            },
        };
        return ModelConfigLoader.Create(config);
    }

    [Fact]
    public void ResolveAliasFile_PrefillAlias_ReturnsFileName()
    {
        var loader = LoaderWithAliases();
        Assert.Equal("Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            loader.ResolveAliasFile("moe-35b-pd", "qwen3.6-35B-mini"));
    }

    [Fact]
    public void ResolveAliasFile_UnknownAlias_Throws()
    {
        var loader = LoaderWithAliases();
        var ex = Assert.Throws<InvalidOperationException>(
            () => loader.ResolveAliasFile("moe-35b-pd", "qwen3.6-99B-mythical"));
        Assert.Contains("qwen3.6-99B-mythical", ex.Message);
        Assert.Contains("model_file_aliases", ex.Message);
    }

    [Fact]
    public void ResolveAliasFile_NullAlias_Throws()
    {
        var loader = LoaderWithAliases();
        var ex = Assert.Throws<InvalidOperationException>(
            () => loader.ResolveAliasFile("moe-35b-pd", null));
        Assert.Contains("prefill_alias", ex.Message);
    }

    [Fact]
    public void ResolveAliasFile_AliasTableCaseInsensitive()
    {
        var loader = LoaderWithAliases();
        // Lookup by case-mismatched alias name should still work.
        Assert.Equal("Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            loader.ResolveAliasFile("moe-35b-pd", "QWEN3.6-35B-MINI"));
    }

    [Fact]
    public void Deserialize_ModelFileAliases_NewShape()
    {
        var json = """
        {
            "schema_version": 3,
            "model_file_aliases": {
                "qwen3.6-35B-mini":     "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                "qwen3.6-35B-balanced": "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf"
            }
        }
        """;
        var config = JsonSerializer.Deserialize<ModelsConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
        Assert.NotNull(config?.ModelFileAliases);
        Assert.Equal("Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf", config!.ModelFileAliases!["qwen3.6-35B-mini"]);
        Assert.Equal("Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf", config.ModelFileAliases["qwen3.6-35B-balanced"]);
    }

    [Fact]
    public void ResolveEngineConfig_AliasResolution_PrefillRole()
    {
        var loader = LoaderWithAliases();
        var cfg = loader.ResolveEngineConfig("moe-35b-pd", decodeRole: false);
        // Prefill role should resolve to Mini file name.
        Assert.Equal("/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf", cfg.ModelPath);
    }

    [Fact]
    public void ResolveEngineConfig_AliasResolution_DecodeRole()
    {
        var loader = LoaderWithAliases();
        var cfg = loader.ResolveEngineConfig("moe-35b-pd", decodeRole: true);
        // Decode role should resolve to Balanced file name.
        Assert.Equal("/models/Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf", cfg.ModelPath);
    }

    [Fact]
    public void ResolveEngineConfig_AliasResolution_UnknownAliasThrows()
    {
        var loader = LoaderWithAliases();
        // Build a model that references a non-existent alias. Should fail fast.
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            Models = new Dictionary<string, ModelTemplate>
            {
                ["bad-model"] = new ModelTemplate { PrefillAlias = "qwen3.6-99B-mythical", DecodeAlias = "qwen3.6-99B-mythical" },
            },
            ModelFileAliases = new Dictionary<string, string> { ["qwen3.6-35B-mini"] = "mini.gguf" },
        };
        var badLoader = ModelConfigLoader.Create(config);
        var ex = Assert.Throws<InvalidOperationException>(
            () => badLoader.ResolveEngineConfig("bad-model"));
        Assert.Contains("qwen3.6-99B-mythical", ex.Message);
    }
}
