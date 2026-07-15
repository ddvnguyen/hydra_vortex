using Hydra.Core.Models;

namespace Hydra.Core.Services;

/// <summary>
/// Model registry. Maps a model alias (e.g. "moe-35b-solo") to the corresponding
/// <see cref="EngineConfig"/> (stock-params shape).
///
/// When <c>HYDRA_COORD_MODELS_FILE</c> is set, the registry is initialized from the
/// external JSON config via <see cref="ModelConfigLoader"/>. Otherwise, a hardcoded
/// fallback set is used (renamed aliases for backward compatibility).
///
/// The registry is the single source of truth for per-model engine config.
/// </summary>
public static class ModelRegistry
{
    private static readonly Dictionary<string, EngineConfig> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialize the registry from a <see cref="ModelConfigLoader"/>.
    /// Called once at startup. When the loader is available, all aliases come from
    /// the external config; when it's not, the hardcoded fallback is used.
    /// </summary>
    public static void Initialize(ModelConfigLoader loader)
    {
        _entries.Clear();
        foreach (var alias in loader.GetAllAliases())
        {
            try
            {
                _entries[alias] = loader.ResolveEngineConfig(alias);
            }
            catch
            {
                // Skip aliases that fail to resolve (e.g. missing model file).
            }
        }
    }

    /// <summary>
    /// Initialize with hardcoded fallback entries (used when HYDRA_COORD_MODELS_FILE
    /// is not set). Aliases use the new names: "moe-35b-solo" and "dense-27b-combined".
    /// </summary>
    public static void InitializeFallback()
    {
        _entries.Clear();

        // Backward-compat: "balanced" maps to moe-35b-solo (used by hydra/balanced in opencode global config)
        _entries["balanced"] = new EngineConfig(
            ModelAlias: "balanced",
            ModelPath: "/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            NGpuLayers: 99,
            NCpuMoe: 8,
            NCtx: 320000,
            OverrideTensors: new[] { "blk.*.ffn_*_exps.weight=CPU" },
            ContBatching: true,
            Fit: false,
            UbatchSize: 512,
            SpecType: "draft-mtp",
            SpecDraftNMax: 3,
            SpecDraftPMin: 0.75f,
            SpecDraftNgl: 0
        );

        _entries["moe-35b-solo"] = new EngineConfig(
            ModelAlias: "moe-35b-solo",
            ModelPath: "/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            NGpuLayers: 99,
            NCpuMoe: 8,
            NCtx: 320000,
            OverrideTensors: new[] { "blk.*.ffn_*_exps.weight=CPU" },
            ContBatching: true,
            Fit: false,
            UbatchSize: 512,
            SpecType: "draft-mtp",
            SpecDraftNMax: 3,
            SpecDraftPMin: 0.75f,
            SpecDraftNgl: 0
        );

        // P/D split: RTX prefill (Q3_K-mini) + P100 decode (Q5_K-balanced).
        // ModelPath is the prefill model; decode model is loaded on the P100 worker.
        _entries["moe-35b-pd"] = new EngineConfig(
            ModelAlias: "moe-35b-pd",
            ModelPath: "/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            NGpuLayers: 99,
            NCpuMoe: 8,
            NCtx: 320000,
            OverrideTensors: new[] { "blk.*.ffn_*_exps.weight=CPU" },
            ContBatching: true,
            Fit: false,
            UbatchSize: 512,
            SpecType: "draft-mtp",
            SpecDraftNMax: 3,
            SpecDraftPMin: 0.75f,
            SpecDraftNgl: 0
        );

        _entries["dense-27b-combined"] = new EngineConfig(
            ModelAlias: "dense-27b-combined",
            ModelPath: "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            NGpuLayers: 65,
            NCtx: 96000,
            CacheTypeK: "q8_0",
            CacheTypeV: "q8_0",
            SplitMode: "layer",
            TensorSplit: new[] { 25.0, 40.0 },
            OverrideTensors: new[] { "token_embd.weight=CPU" },
            RopeScaling: "yarn",
            RopeScale: 4f,
            YarnOrigCtx: 32768,
            ContBatching: true,
            Fit: false,
            UbatchSize: 512,
            SpecType: "draft-mtp",
            SpecDraftNMax: 3,
            SpecDraftPMin: 0.75f,
            SpecDraftNgl: 0
        );
    }

    /// <summary>
    /// Resolve a model alias to its <see cref="EngineConfig"/>. Throws
    /// when the alias is not registered — better to fail at admission
    /// than to silently fall back to a default model.
    /// </summary>
    public static EngineConfig Resolve(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new InvalidOperationException("Model alias is required");
        if (_entries.TryGetValue(alias, out var cfg))
            return cfg;
        throw new InvalidOperationException(
            $"Unknown model alias '{alias}'. Registered: [{string.Join(", ", _entries.Keys)}]");
    }

    /// <summary>Test helper: register a new entry (for unit tests that want a custom alias).</summary>
    public static void RegisterForTest(EngineConfig cfg) => _entries[cfg.ModelAlias] = cfg;

    /// <summary>Test helper: clear the registry (for unit tests that want a clean slate).</summary>
    public static void ClearForTest()
    {
        _entries.Clear();
        _entries["moe-35b-solo"] = new EngineConfig(
            ModelAlias: "moe-35b-solo",
            ModelPath: "/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            NGpuLayers: 99, NCpuMoe: 8, NCtx: 320000,
            OverrideTensors: new[] { "blk.*.ffn_*_exps.weight=CPU" },
            ContBatching: true, Fit: false, UbatchSize: 512,
            SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0);
        _entries["dense-27b-combined"] = new EngineConfig(
            ModelAlias: "dense-27b-combined",
            ModelPath: "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            NGpuLayers: 65, NCtx: 96000, CacheTypeK: "q8_0", CacheTypeV: "q8_0",
            SplitMode: "layer", TensorSplit: new[] { 25.0, 40.0 },
            OverrideTensors: new[] { "token_embd.weight=CPU" },
            RopeScaling: "yarn", RopeScale: 4f, YarnOrigCtx: 32768,
            ContBatching: true, Fit: false, UbatchSize: 512,
            SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0);
    }

    /// <summary>List all registered aliases (for diagnostics and tests).</summary>
    public static IReadOnlyCollection<string> RegisteredAliases => _entries.Keys;

    /// <summary>All registered model entries (alias → EngineConfig). Used by ModelConfigLoader fallback init.</summary>
    internal static IReadOnlyDictionary<string, EngineConfig> AllEntries => _entries;
}
