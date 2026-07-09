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
    /// <summary>Context size in tokens.</summary>
    int? NCtx = null,
    /// <summary>KV-cache key quant (e.g. "q8_0", "q4_0").</summary>
    string? CacheTypeK = null,
    /// <summary>KV-cache value quant.</summary>
    string? CacheTypeV = null,
    /// <summary>
    /// Split mode for COMBINE: "none" (SOLO), "layer" (DENSE layer-split),
    /// or "row" (DENSE row-split). Engine-startup config in Phase 2a;
    /// runtime via <c>0x40</c> in Phase 2b.
    /// </summary>
    string? SplitMode = null,
    /// <summary>
    /// Per-device layer counts for layer-split. e.g. <c>{25, 40}</c>
    /// = 25 layers on CUDA0 (head), 40 layers on the peer.
    /// </summary>
    double[]? TensorSplit = null,
    /// <summary>
    /// --override-tensor patterns routed to the peer's RPC backend for
    /// MoE COMBINE. Engine-startup config (the <c>--combined-ot-pattern</c>
    /// CLI flag in Phase 1; will become stock <c>--override-tensor</c> in
    /// Phase 2b). Used by the C# translator to validate the plan, not
    /// sent on the wire for COMBINE mode (the engine already has it).
    /// </summary>
    string[]? OverrideTensors = null,
    /// <summary>
    /// Peer RPC endpoints (e.g. <c>["localhost:9506"]</c>). Engine-startup
    /// config for the head's <c>--rpc-engine</c> flag in Phase 1.
    /// </summary>
    string[]? RpcServers = null
)
{
    /// <summary>
    /// Serialize to the wire JSON shape the engine accepts on 0x40
    /// CONFIGURE. Skips null fields; includes only the keys the engine
    /// knows how to apply (T1 + T2 + T3, per ddvnguyen/hydra_vortex#406).
    /// Unknown keys are silently ignored by the engine (forward-compat).
    /// </summary>
    public string ToWireJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        var first = true;
        void Emit(string k, string v)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(k).Append("\":").Append(v);
        }
        if (NGpuLayers is int ngl) Emit("n_gpu_layers", ngl.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (NCpuMoe is int ncpu) Emit("n_cpu_moe", ncpu.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (NCtx is int nctx) Emit("n_ctx", nctx.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (CacheTypeK is string ctk) Emit("cache_type_k", JsonString(ctk));
        if (CacheTypeV is string ctv) Emit("cache_type_v", JsonString(ctv));
        if (SplitMode is string sm) Emit("split_mode", JsonString(sm));
        if (TensorSplit is double[] ts)
        {
            sb.Append(first ? "" : ",").Append("\"tensor_split\":[");
            for (int i = 0; i < ts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ts[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            first = false;
        }
        if (OverrideTensors is string[] ots && ots.Length > 0)
        {
            sb.Append(first ? "" : ",").Append("\"override_tensor\":");
            // engine accepts a single pattern string (the most common case);
            // we join multiple with a newline so the parser sees distinct lines.
            sb.Append(JsonString(string.Join('\n', ots)));
            first = false;
        }
        if (RpcServers is string[] rpcs && rpcs.Length > 0)
        {
            sb.Append(first ? "" : ",").Append("\"rpc_servers\":[");
            for (int i = 0; i < rpcs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(rpcs[i]));
            }
            sb.Append(']');
            first = false;
        }
        if (!string.IsNullOrEmpty(ModelPath))
        {
            sb.Append(first ? "" : ",").Append("\"model\":{\"path\":").Append(JsonString(ModelPath));
            sb.Append('}');
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string JsonString(string s) =>
        System.Text.Json.JsonSerializer.Serialize(s);
}
