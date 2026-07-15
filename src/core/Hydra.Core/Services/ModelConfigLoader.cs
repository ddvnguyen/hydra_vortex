using System.Text.Json;
using Hydra.Core.Models;

namespace Hydra.Core.Services;

/// <summary>
/// Loads model definitions and GPU hardware specs from JSON config files at startup.
/// Replaces the hardcoded entries in <see cref="ModelRegistry"/> with a data-driven
/// config loaded from <c>HYDRA_COORD_MODELS_FILE</c> (models.json) and the co-located
/// <c>gpu-specs.json</c>.
///
/// Singleton pattern: call <see cref="TryLoad"/> once at startup; callers use the
/// <see cref="Instance"/> property.
/// </summary>
public sealed class ModelConfigLoader
{
    private static ModelConfigLoader? _instance;

    /// <summary>
    /// The loaded config, or <c>null</c> if the loader was not initialized
    /// (HYDRA_COORD_MODELS_FILE not set or file not found).
    /// </summary>
    public ModelsConfig? Config { get; }

    /// <summary>GPU hardware specs keyed by worker name (e.g. "rtx", "p100").</summary>
    public IReadOnlyDictionary<string, GpuSpec> GpuSpecs { get; }

    /// <summary>Directory prefix for resolving model file paths (from HYDRA_COORD_MODELS_DIR).</summary>
    public string ModelsDir { get; }

    private readonly Dictionary<string, ModelTemplate> _templates;

    private ModelConfigLoader(ModelsConfig config, Dictionary<string, GpuSpec> gpuSpecs, string modelsDir)
    {
        Config = config;
        GpuSpecs = gpuSpecs;
        ModelsDir = modelsDir;
        _templates = config.Models ?? new Dictionary<string, ModelTemplate>();
    }

    /// <summary>Singleton access. Throws if not initialized — call <see cref="TryLoad"/> first.</summary>
    public static ModelConfigLoader Instance =>
        _instance ?? throw new InvalidOperationException(
            "ModelConfigLoader not initialized. Call ModelConfigLoader.TryLoad() at startup.");

    /// <summary>
    /// Attempt to load config from <c>HYDRA_COORD_MODELS_FILE</c>.
    /// Returns <c>true</c> if loaded successfully; <c>false</c> if the env var is
    /// unset or the file doesn't exist (callers fall back to hardcoded entries).
    /// </summary>
    public static bool TryLoad()
    {
        var modelsFile = Environment.GetEnvironmentVariable("HYDRA_COORD_MODELS_FILE");
        if (string.IsNullOrWhiteSpace(modelsFile) || !File.Exists(modelsFile))
            return false;

        var modelsDir = Environment.GetEnvironmentVariable("HYDRA_COORD_MODELS_DIR") ?? "/models";

        try
        {
            var json = File.ReadAllText(modelsFile);
            var config = JsonSerializer.Deserialize<ModelsConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (config is null)
                return false;

            // Load co-located gpu-specs.json (same directory as models.json).
            var gpuSpecs = LoadGpuSpecs(Path.GetDirectoryName(modelsFile)!);

            _instance = new ModelConfigLoader(config, gpuSpecs, modelsDir);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "model_config_load_failed File={File}", modelsFile);
            return false;
        }
    }

    /// <summary>
    /// Inject a pre-built loader for tests. Bypasses file I/O entirely.
    /// </summary>
    internal static void SetInstance(ModelConfigLoader loader) => _instance = loader;

    /// <summary>Reset the singleton (test cleanup).</summary>
    internal static void Reset() => _instance = null;

    // ── Public query methods ─────────────────────────────────────────────

    /// <summary>
    /// Resolve a model alias to its <see cref="EngineConfig"/> by merging
    /// <c>engine_defaults</c> with the per-model <c>engine_config</c>.
    /// Model file paths are resolved relative to <c>HYDRA_COORD_MODELS_DIR</c>.
    /// Throws if the alias is not registered.
    /// </summary>
    public EngineConfig ResolveEngineConfig(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new InvalidOperationException("Model alias is required");

        if (!_templates.TryGetValue(alias, out var template))
            throw new InvalidOperationException(
                $"Unknown model alias '{alias}'. Registered: [{string.Join(", ", _templates.Keys)}]");

        // Start with engine_defaults as the base, then overlay per-model overrides.
        var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (Config?.EngineDefaults is { } defaults)
        {
            AddIfNotNull(merged, "cache_type_k", defaults.CacheTypeK);
            AddIfNotNull(merged, "cache_type_v", defaults.CacheTypeV);
            AddIfNotNull(merged, "cont_batching", defaults.ContBatching);
            AddIfNotNull(merged, "ubatch_size", defaults.UbatchSize);
            AddIfNotNull(merged, "fit", defaults.Fit);
            AddIfNotNull(merged, "cache_prompt", defaults.CachePrompt);
            AddIfNotNull(merged, "cache_reuse", defaults.CacheReuse);
            AddIfNotNull(merged, "reasoning", defaults.Reasoning);
            AddIfNotNull(merged, "jinja", defaults.Jinja);
            AddIfNotNull(merged, "context_shift", defaults.ContextShift);
            AddIfNotNull(merged, "chat_template_kwargs", defaults.ChatTemplateKwargs);
        }

        // Per-model engine_config overrides the defaults.
        if (template.EngineConfig is { } perModel)
        {
            foreach (var kv in perModel)
                merged[kv.Key] = kv.Value;
        }

        // Resolve model file paths to absolute paths.
        var modelPath = ResolveModelPath(alias, template.PrefillModelFileName);

        return new EngineConfig(
            ModelAlias: alias,
            ModelPath: modelPath,
            NGpuLayers: GetInt(merged, "n_gpu_layers"),
            NCpuMoe: GetInt(merged, "n_cpu_moe"),
            NCtx: GetInt(merged, "n_ctx"),
            CacheTypeK: GetString(merged, "cache_type_k"),
            CacheTypeV: GetString(merged, "cache_type_v"),
            RopeScaling: GetString(merged, "rope_scaling"),
            RopeScale: GetFloat(merged, "rope_scale"),
            YarnOrigCtx: GetInt(merged, "yarn_orig_ctx"),
            SpecType: GetString(merged, "spec_type"),
            SpecDraftNMax: GetInt(merged, "spec_draft_n_max"),
            SpecDraftPMin: GetFloat(merged, "spec_draft_p_min"),
            SpecDraftNgl: GetInt(merged, "spec_draft_ngl"),
            ContBatching: GetBool(merged, "cont_batching"),
            Fit: GetBool(merged, "fit"),
            UbatchSize: GetInt(merged, "ubatch_size"),
            SplitMode: GetString(merged, "split_mode"),
            TensorSplit: GetDoubleArray(merged, "tensor_split"),
            OverrideTensors: GetStringArray(merged, "override_tensors"),
            RpcServers: GetStringArray(merged, "rpc_servers"),
            // engine_defaults fields
            FlashAttn: GetBool(merged, "flash_attn"),
            KvUnified: GetBool(merged, "kv_unified"),
            CachePrompt: GetBool(merged, "cache_prompt"),
            CacheReuse: GetInt(merged, "cache_reuse"),
            Reasoning: GetBool(merged, "reasoning"),
            Jinja: GetBool(merged, "jinja"),
            ContextShift: GetBool(merged, "context_shift"),
            ChatTemplateKwargs: GetString(merged, "chat_template_kwargs")
        );
    }

    /// <summary>Get the raw <see cref="ModelTemplate"/> for an alias, or null if not found.</summary>
    public ModelTemplate? GetModelTemplate(string alias) =>
        _templates.TryGetValue(alias, out var t) ? t : null;

    /// <summary>List all registered model aliases.</summary>
    public IReadOnlyCollection<string> GetAllAliases() => _templates.Keys;

    /// <summary>All registered model templates keyed by alias.</summary>
    public IReadOnlyDictionary<string, ModelTemplate> GetAllModels() => _templates;

    /// <summary>The auto-routing policy from models.json, or null if not configured.</summary>
    public AutoRoutingPolicy? GetAutoRoutingPolicy() => Config?.AutoRouting;

    /// <summary>Look up GPU hardware specs by worker name (e.g. "rtx", "p100").</summary>
    public GpuSpec? GetGpuSpec(string workerName) =>
        GpuSpecs.TryGetValue(workerName, out var spec) ? spec : null;

    // ── Internal helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Resolve a model file name to an absolute path using <c>HYDRA_COORD_MODELS_DIR</c>.
    /// If the name is already an absolute path, return it as-is.
    /// </summary>
    internal string ResolveModelPath(string alias, string? modelFileName)
    {
        if (string.IsNullOrWhiteSpace(modelFileName))
            throw new InvalidOperationException(
                $"Model alias '{alias}' has no prefill_model_file_name configured");

        if (Path.IsPathRooted(modelFileName))
            return modelFileName;

        return Path.Combine(ModelsDir, modelFileName);
    }

    private static Dictionary<string, GpuSpec> LoadGpuSpecs(string configDir)
    {
        var path = Path.Combine(configDir, "gpu-specs.json");
        if (!File.Exists(path))
            return new Dictionary<string, GpuSpec>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, GpuSpec>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return raw ?? new Dictionary<string, GpuSpec>(StringComparer.OrdinalIgnoreCase);
    }

    // ── Value extraction helpers ─────────────────────────────────────────

    private static void AddIfNotNull<T>(Dictionary<string, object> dict, string key, T? value)
    {
        if (value is not null)
            dict[key] = value!;
    }

    private static string? GetString(Dictionary<string, object> dict, string key) =>
        dict.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int? GetInt(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v)) return null;
        return v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            _ => int.TryParse(v?.ToString(), out var parsed) ? parsed : null,
        };
    }

    private static float? GetFloat(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v)) return null;
        return v switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            long l => l,
            JsonElement je when je.ValueKind == JsonValueKind.Number => (float)je.GetDouble(),
            _ => float.TryParse(v?.ToString(), out var parsed) ? parsed : null,
        };
    }

    private static bool? GetBool(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v)) return null;
        return v switch
        {
            bool b => b,
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            _ => bool.TryParse(v?.ToString(), out var parsed) ? parsed : null,
        };
    }

    private static double[]? GetDoubleArray(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v)) return null;
        return v switch
        {
            double[] arr => arr,
            JsonElement je when je.ValueKind == JsonValueKind.Array =>
                je.EnumerateArray().Select(e => e.GetDouble()).ToArray(),
            _ => null,
        };
    }

    private static string[]? GetStringArray(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v)) return null;
        return v switch
        {
            string[] arr => arr,
            JsonElement je when je.ValueKind == JsonValueKind.Array =>
                je.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(),
            _ => null,
        };
    }
}
