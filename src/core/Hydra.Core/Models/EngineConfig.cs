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
);
