using System.Text.Json.Serialization;

namespace Hydra.Core.Models;

/// <summary>
/// Root config loaded from models.json (HYDRA_COORD_MODELS_FILE).
/// Single source of truth for model definitions, engine defaults, and auto-routing policy.
/// </summary>
public sealed record ModelsConfig
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("engine_defaults")]
    public EngineDefaults? EngineDefaults { get; init; }

    [JsonPropertyName("auto_routing")]
    public AutoRoutingPolicy? AutoRouting { get; init; }

    [JsonPropertyName("models")]
    public Dictionary<string, ModelTemplate>? Models { get; init; }
}

public sealed record EngineDefaults
{
    [JsonPropertyName("flash_attn")]
    public bool? FlashAttn { get; init; }
    [JsonPropertyName("cache_type_k")]
    public string? CacheTypeK { get; init; }
    [JsonPropertyName("cache_type_v")]
    public string? CacheTypeV { get; init; }
    [JsonPropertyName("kv_unified")]
    public bool? KvUnified { get; init; }
    [JsonPropertyName("cont_batching")]
    public bool? ContBatching { get; init; }
    [JsonPropertyName("cache_prompt")]
    public bool? CachePrompt { get; init; }
    [JsonPropertyName("cache_reuse")]
    public int? CacheReuse { get; init; }
    [JsonPropertyName("reasoning")]
    public bool? Reasoning { get; init; }
    [JsonPropertyName("jinja")]
    public bool? Jinja { get; init; }
    [JsonPropertyName("context_shift")]
    public bool? ContextShift { get; init; }
    [JsonPropertyName("chat_template_kwargs")]
    public string? ChatTemplateKwargs { get; init; }
    [JsonPropertyName("ubatch_size")]
    public int? UbatchSize { get; init; }
    [JsonPropertyName("fit")]
    public bool? Fit { get; init; }
}

public sealed record AutoRoutingPolicy
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
    [JsonPropertyName("default_model")]
    public string? DefaultModel { get; init; }
    [JsonPropertyName("swap_cost_budget_s")]
    public int SwapCostBudgetS { get; init; }
}

public sealed record ModelTemplate
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    [JsonPropertyName("prefill_model_file_name")]
    public string? PrefillModelFileName { get; init; }
    [JsonPropertyName("decode_model_file_name")]
    public string? DecodeModelFileName { get; init; }
    [JsonPropertyName("load_time_s")]
    public int LoadTimeS { get; init; }
    [JsonPropertyName("quality_tier")]
    public int QualityTier { get; init; }
    [JsonPropertyName("requirements")]
    public ModelRequirements? Requirements { get; init; }
    [JsonPropertyName("routing")]
    public RoutingRule? Routing { get; init; }
    [JsonPropertyName("engine_config")]
    public Dictionary<string, object>? EngineConfig { get; init; }
    [JsonPropertyName("node_config")]
    public Dictionary<string, string>? NodeConfig { get; init; }
    [JsonPropertyName("workers_file")]
    public string? WorkersFile { get; init; }
}

public sealed record ModelRequirements
{
    [JsonPropertyName("min_vram_mb")]
    public int MinVramMb { get; init; }
    [JsonPropertyName("min_compute_tflops")]
    public double? MinComputeTflops { get; init; }
    [JsonPropertyName("min_bandwidth_gbps")]
    public double? MinBandwidthGbps { get; init; }
    [JsonPropertyName("required_capabilities")]
    public int RequiredCapabilities { get; init; }
    [JsonPropertyName("decode_requirements")]
    public ModelRequirements? DecodeRequirements { get; init; }
    [JsonPropertyName("peer_requirements")]
    public ModelRequirements? PeerRequirements { get; init; }
}

public sealed record RoutingRule
{
    [JsonPropertyName("auto_eligible")]
    public bool AutoEligible { get; init; }
    [JsonPropertyName("min_prompt_tokens")]
    public int MinPromptTokens { get; init; }
    [JsonPropertyName("max_prompt_tokens")]
    public int MaxPromptTokens { get; init; }
    [JsonPropertyName("max_context_tokens")]
    public int MaxContextTokens { get; init; }
    [JsonPropertyName("requires_workers")]
    public List<string>? RequiresWorkers { get; init; }
    [JsonPropertyName("default_eligible")]
    public bool DefaultEligible { get; init; } = true;
}
