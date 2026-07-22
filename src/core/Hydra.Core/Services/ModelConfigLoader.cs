using System.Text.Json;
using Hydra.Core.Models;

namespace Hydra.Core.Services;

/// <summary>
/// Loads model definitions from JSON config files at startup. Replaces the
/// hardcoded entries in <see cref="ModelRegistry"/> with a data-driven config
/// loaded from <c>HYDRA_COORD_MODELS_FILE</c> (models.json).
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

    /// <summary>Directory prefix for resolving model file paths (from HYDRA_COORD_MODELS_DIR).</summary>
    public string ModelsDir { get; }

    private readonly Dictionary<string, ModelTemplate> _templates;
    private readonly Dictionary<string, string> _modelFileAliases;

    private ModelConfigLoader(ModelsConfig config, string modelsDir)
    {
        Config = config;
        ModelsDir = modelsDir;
        _templates = config.Models ?? new Dictionary<string, ModelTemplate>();
        // #481 Phase 2c: alias-name -> GGUF file name table. Strict — every
        // model.PrefillAlias / model.DecodeAlias MUST be a key here, resolved
        // at ResolveEngineConfig time so a typo fails the model load, not
        // silently.
        _modelFileAliases = config.ModelFileAliases is { } raw
            ? new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Singleton access. Throws if not initialized — call <see cref="TryLoad"/> first.</summary>
    public static ModelConfigLoader? InstanceOrNull => _instance;

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

            _instance = new ModelConfigLoader(config, modelsDir);
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

    /// <summary>Create a standalone loader for tests (bypasses file I/O and the singleton).</summary>
    internal static ModelConfigLoader Create(ModelsConfig config, string modelsDir = "/models")
        => new(config, modelsDir);

    /// <summary>
    /// Create a minimal in-memory ModelConfigLoader from hardcoded ModelRegistry
    /// entries. Used when HYDRA_COORD_MODELS_FILE is not set, so AutoRouter
    /// still has access to model templates for routing decisions.
    /// </summary>
    internal static void InitializeFallback()
    {
        if (_instance != null) return; // already loaded from file
        // Build model-specific templates for the fallback path.
        // moe-35b-pd needs decode_requirements to trigger P/D split routing.
        var models = new Dictionary<string, ModelTemplate>();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, engineCfg) in ModelRegistry.AllEntries)
        {
            var isPd = alias == "moe-35b-pd";
            var isCombined = alias == "dense-27b-combined";
            // #481 Phase 2c: every ModelTemplate now references an alias
            // from the model_file_aliases table. Fallback hardcodes the
            // three known aliases and one file per alias.
            var fileName = System.IO.Path.GetFileName(engineCfg.ModelPath);
            string ggufAlias = alias switch
            {
                "moe-35b-solo"      => "qwen3.6-35B-balanced",
                "moe-35b-pd"        => "qwen3.6-35B-mini",  // prefill is mini, see DecodeAlias below
                "moe-35b-pd-mix"    => "qwen3.6-35B-mini",
                "moe-35b-pd-mini"   => "qwen3.6-35B-mini",
                "balanced"          => "qwen3.6-35B-balanced",
                "dense-27b-combined"=> "qwen3.6-27B-coder",
                _                   => "qwen3.6-35B-mini",
            };
            if (!aliases.ContainsKey(ggufAlias))
                aliases[ggufAlias] = fileName;
            models[alias] = new ModelTemplate
            {
                Description = alias,
                PrefillAlias = ggufAlias,
                DecodeAlias  = ggufAlias,
                LoadTimeS = 40,
                QualityTier = isCombined ? 3 : isPd ? 2 : 1,
                Requirements = new ModelRequirements
                {
                    MinVramMb = 8000,
                    RequiredCapabilities = isCombined ? GpuCapabilities.Combined : GpuCapabilities.FlashAttn,
                    DecodeRequirements = isPd ? new ModelRequirements
                    {
                        MinVramMb = 12000,
                        RequiredCapabilities = GpuCapabilities.FlashAttn,
                    } : null,
                },
                Routing = new RoutingRule
                {
                    AutoEligible = true,
                    MinPromptTokens = 0,
                    MaxPromptTokens = 999999,
                    MaxContextTokens = 128000,
                    RequiresWorkers = isCombined ? ["rtx3060"] : isPd ? ["p100"] : [],
                },
                EngineConfig = null,
                WorkersFile = null,
            };
        }
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            EngineDefaults = null,
            AutoRouting = new AutoRoutingPolicy { Enabled = true, DefaultModel = "moe-35b-solo", SwapCostBudgetS = 30 },
            Models = models,
            ModelFileAliases = aliases,
        };
        _instance = new ModelConfigLoader(config, "/models");
    }

    // ── Public query methods ─────────────────────────────────────────────

    /// <summary>
    /// Resolve a model alias to its <see cref="EngineConfig"/> by merging
    /// <c>engine_defaults</c> with the per-model <c>engine_config</c>.
    /// Model file paths are resolved relative to <c>HYDRA_COORD_MODELS_DIR</c>.
    /// Throws if the alias is not registered.
    /// </summary>
    public EngineConfig ResolveEngineConfig(string alias, bool decodeRole = false)
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
        // #481 Phase 2c: resolve prefill_alias / decode_alias via the
        // model_file_aliases table. Decode role uses DecodeAlias when
        // present, else PrefillAlias. Strict: unknown aliases throw.
        var aliasName = decodeRole && !string.IsNullOrWhiteSpace(template.DecodeAlias)
            ? template.DecodeAlias
            : template.PrefillAlias;
        var modelFile = ResolveAliasFile(alias, aliasName);
        var modelPath = ResolveModelPath(alias, modelFile);

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

    /// <summary>
    /// #481 Phase 2c: resolve a GGUF-file alias to its file name via the
    /// <c>model_file_aliases</c> table. Strict: throws when the alias is
    /// missing or empty so a typo fails fast at startup, not silently at
    /// PREFILL time.
    /// </summary>
    public string ResolveAliasFile(string modelAlias, string? ggufAlias)
    {
        if (string.IsNullOrWhiteSpace(ggufAlias))
            throw new InvalidOperationException(
                $"Model alias '{modelAlias}' has no prefill_alias/decode_alias configured");
        if (!_modelFileAliases.TryGetValue(ggufAlias, out var file) || string.IsNullOrEmpty(file))
            throw new InvalidOperationException(
                $"Model alias '{modelAlias}' references GGUF alias '{ggufAlias}' which is not in model_file_aliases. Add it to the 'model_file_aliases' table in models.json.");
        return file;
    }

    /// <summary>List all registered model aliases.</summary>
    public IReadOnlyCollection<string> GetAllAliases() => _templates.Keys;

    /// <summary>All registered model templates keyed by alias.</summary>
    public IReadOnlyDictionary<string, ModelTemplate> GetAllModels() => _templates;

    /// <summary>The auto-routing policy from models.json, or null if not configured.</summary>
    public AutoRoutingPolicy? GetAutoRoutingPolicy() => Config?.AutoRouting;

    // ── Internal helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Resolve a model file name to an absolute path using <c>HYDRA_COORD_MODELS_DIR</c>.
    /// If the name is already an absolute path, return it as-is.
    /// </summary>
    internal string ResolveModelPath(string alias, string? modelFileName)
    {
        if (string.IsNullOrWhiteSpace(modelFileName))
            throw new InvalidOperationException(
                $"Model alias '{alias}' has no model file name resolved (prefill_alias/decode_alias missing or unknown)");

        if (Path.IsPathRooted(modelFileName))
            return modelFileName;

        return Path.Combine(ModelsDir, modelFileName);
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
