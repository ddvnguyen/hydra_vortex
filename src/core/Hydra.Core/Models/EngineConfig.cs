namespace Hydra.Core.Models;

/// <summary>
/// Stock-params-shaped engine configuration. Represents the subset of
/// <c>common_params</c> fields that the live MoE dual-load path and the
/// planned DENSE layer-split path actually consume. The C# side derives
/// this from a <see cref="Services.ModelRegistry"/> lookup keyed by
/// <see cref="WorkerConfig.ModelAlias"/>; the engine side already has
/// the matching flags loaded at startup (the Phase 1 fork binary still
/// uses <c>--combined-ot-pattern</c> / <c>--rpc-engine</c> / etc.; the
/// Phase 2b fork will accept a full <c>common_params</c> payload via
/// the new <c>0x40 EngineConfigure</c> opcode).
///
/// Wire payloads in Phase 2a: this shape is INTERNAL. The C# side
/// translates <see cref="EngineConfig"/> to the existing <c>0x44</c>
/// (<c>SET_EXPERT_MODE</c>) and <c>0x46</c> (<c>EnginePipelineAttach</c>)
/// wire opcodes. The on-the-wire JSON does not change in Phase 2a.
/// </summary>
public sealed record EngineConfig(
    /// <summary>Model alias (e.g. "moe-35b-mini", "dense-27b-q5"). Matches WorkerConfig.ModelAlias.</summary>
    string ModelAlias,
    /// <summary>Absolute path to the GGUF file on the engine host.</summary>
    string ModelPath,
    /// <summary>Layers to offload to GPU. <c>null</c> = use engine default (typically "all").</summary>
    int? NGpuLayers = null,
    /// <summary>CPU-side MoE expert count (Qwen35MoE / DeepSeek-style MoE).</summary>
    int? NCpuMoe = null,
    int? NCtx = null,
    string? CacheTypeK = null,
    string? CacheTypeV = null,
    string? RopeScaling = null,
    float? RopeScale = null,
    int? YarnOrigCtx = null,
    string? SpecType = null,
    int? SpecDraftNMax = null,
    float? SpecDraftPMin = null,
    int? SpecDraftNgl = null,
    bool? ContBatching = null,
    bool? Fit = null,
    int? UbatchSize = null,
    string? SplitMode = null,
    double[]? TensorSplit = null,
    string[]? OverrideTensors = null,
    string[]? RpcServers = null
)
{
    public Dictionary<string, object> ToHydraConfigDict()
    {
        var config = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(ModelPath))
            config["model_path"] = ModelPath;
        if (SplitMode is string sm)
            config["split_mode"] = sm;
        if (TensorSplit is double[] ts)
            config["tensor_split"] = ts;
        if (NGpuLayers is int ngl)
            config["n_gpu_layers"] = ngl;
        if (NCpuMoe is int ncm)
            config["n_cpu_moe"] = ncm;
        if (NCtx is int nctx)
            config["n_ctx"] = nctx;
        if (CacheTypeK is string ctk)
            config["cache_type_k"] = ctk;
        if (CacheTypeV is string ctv)
            config["cache_type_v"] = ctv;
        if (RopeScaling is string rs)
            config["rope_scaling"] = rs;
        if (RopeScale is float rsc)
            config["rope_scale"] = rsc;
        if (YarnOrigCtx is int yoc)
            config["yarn_orig_ctx"] = yoc;
        if (SpecType is string st)
            config["spec_type"] = st;
        if (SpecDraftNMax is int sdm)
            config["spec_draft_n_max"] = sdm;
        if (SpecDraftPMin is float sdp)
            config["spec_draft_p_min"] = sdp;
        if (SpecDraftNgl is int sngl)
            config["spec_draft_ngl"] = sngl;
        if (ContBatching is bool cb)
            config["cont_batching"] = cb;
        if (Fit is bool f)
            config["fit"] = f;
        if (UbatchSize is int ubs)
            config["ubatch_size"] = ubs;
        if (OverrideTensors is string[] ots && ots.Length > 0)
            config["override_tensor"] = string.Join("\n", ots);
        if (RpcServers is string[] rpcs && rpcs.Length > 0)
            config["rpc_servers"] = rpcs;
        return config;
    }
}
