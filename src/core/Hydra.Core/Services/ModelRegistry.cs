using Hydra.Core.Models;

namespace Hydra.Core.Services;

/// <summary>
/// Hardcoded model registry. Maps a model alias (e.g. "moe-35b-mini") to
/// the corresponding <see cref="EngineConfig"/> (stock-params shape).
///
/// In Phase 2a the registry is in-process. Phase 3 (per the v4 design in
/// ddvnguyen/llama.cpp#36) will move it to a JSON config so operators can
/// add models without recompiling. For now: hardcoded for the production
/// set the live system actually serves.
///
/// The registry is the single source of truth for per-model engine config.
/// The old <c>WorkerConfig.CombinedOtSplit</c> / <c>PipelineOtSplit</c>
/// fields are gone — those values now live here, keyed by alias.
/// </summary>
public static class ModelRegistry
{
    private static readonly Dictionary<string, EngineConfig> _entries = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Production MoE 35B-A3B (Q3_K-mini) ───────────────────────────
        // Served on the 5060 Ti (SOLO) or on the 5060 Ti + 3060 pair (COMBINE).
        // Override-tensor pattern routes FFN expert tensors to the 3060's
        // ggml-RPC backend when COMBINE is active; engine-startup config
        // (--combined-ot-pattern in the Phase 1 fork).
        ["moe-35b-mini"] = new EngineConfig(
            ModelAlias: "moe-35b-mini",
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
        ),

        // ── Production DENSE 27B (Q5_K_M, MTP spec-decode) ──────────────
        // Served layer-split across 5060 Ti + 3060. The 25/40 split was
        // the best in the Phase 1 live baseline (2026-07-08): 543 tok/s
        // prefill, 23.36 tok/s decode (96K ctx, MTP, q8/q8 KV).
        ["dense-27b-q5"] = new EngineConfig(
            ModelAlias: "dense-27b-q5",
            ModelPath: "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            NGpuLayers: 65,
            NCtx: 60000,
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
        ),
    };

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
        // Keep the production entries; tests should register their own and
        // call ClearForTest in their fixture teardown.
        _entries.Clear();
        _entries["moe-35b-mini"] = new EngineConfig(
            ModelAlias: "moe-35b-mini",
            ModelPath: "/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            NGpuLayers: 99, NCpuMoe: 8, NCtx: 320000,
            OverrideTensors: new[] { "blk.*.ffn_*_exps.weight=CPU" },
            ContBatching: true, Fit: false, UbatchSize: 512,
            SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0);
        _entries["dense-27b-q5"] = new EngineConfig(
            ModelAlias: "dense-27b-q5",
            ModelPath: "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
            NGpuLayers: 65, NCtx: 65536, CacheTypeK: "q8_0", CacheTypeV: "q8_0",
            SplitMode: "layer", TensorSplit: new[] { 25.0, 40.0 },
            OverrideTensors: new[] { "token_embd.weight=CPU" },
            RopeScaling: "yarn", RopeScale: 4f, YarnOrigCtx: 32768,
            ContBatching: true, Fit: false, UbatchSize: 512,
            SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0);
    }

    /// <summary>List all registered aliases (for diagnostics and tests).</summary>
    public static IReadOnlyCollection<string> RegisteredAliases => _entries.Keys;
}
