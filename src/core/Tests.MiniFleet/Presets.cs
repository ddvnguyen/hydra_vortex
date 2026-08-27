namespace Tests.MiniFleet;

/// <summary>
/// The two smoke presets from the task brief §Components 1. Constants are
/// owner-verified (brief "Engine quirks you MUST honor" + validated topology);
/// do not drift them without re-reading the brief.
/// </summary>
public sealed record MiniFleetPreset
{
    public required string Name { get; init; }

    /// <summary>Node A: engine HTTP port.</summary>
    public required int EnginePortA { get; init; }
    /// <summary>Node B: engine HTTP port.</summary>
    public required int EnginePortB { get; init; }

    /// <summary>RPC ports MUST be explicit and distinct per node — engine otherwise
    /// auto-uses port+1 and collides (quirk #1).</summary>
    public required int RpcPortA { get; init; }
    public required int RpcPortB { get; init; }

    /// <summary>Offloaded layers per node (--n-gpu-layers).</summary>
    public required int NglA { get; init; }
    public required int NglB { get; init; }

    /// <summary>Worker threads (-t) — brief: 3+3 for both presets.</summary>
    public required int ThreadsPerEngine { get; init; }

    /// <summary>Context size (-c) — brief: 4096.</summary>
    public required int ContextSize { get; init; }

    /// <summary>cpu-2node runs engines in-process on the CI host (no GPU, ngl=0).
    /// gpu-gpu-shared launches through the ssh shim onto the P100 VM.</summary>
    public required bool ViaSshShim { get; init; }

    /// <summary>Smoke prompt token cap (architect ruling 2026-08-28a): CPU prefill
    /// of full-size scenario prompts can exceed any sane timeout — smoke must NOT
    /// use parity-size prompts. ScenarioRunner materializes requests under this
    /// cap; full-size parity stays in the rig tier (Tests.EngineParity etc.).</summary>
    public required int SmokePromptTokenCap { get; init; }

    /// <summary>Smoke completion token cap — smoke asserts non-empty output and
    /// finish_reason, not generation length. 48 is plenty for "ok"-shaped replies
    /// while keeping CPU decode time bounded (quirk #4 reasoning-model note:
    /// content may still come back empty — that is a PASS for smoke).</summary>
    public required int SmokeCompletionTokenCap { get; init; }
}

public static class Presets
{
    // TODO(minifleet): complete PresetSpec -> llama-engine argv mapping (incl. model path,
    // LD_LIBRARY_PATH=$HOME/hydra-min-test on the VM lane, quirk #2) in the implementation step.

    /// <summary>CI preset: real engines, CPU only. ngl=0, threads 3+3, ctx 4096.</summary>
    public static readonly MiniFleetPreset Cpu2Node = new()
    {
        Name = "cpu-2node",
        EnginePortA = 8088,
        EnginePortB = 8089,
        RpcPortA = 9513,
        RpcPortB = 9514,
        NglA = 0,
        NglB = 0,
        ThreadsPerEngine = 3,
        ContextSize = 4096,
        ViaSshShim = false,
        SmokePromptTokenCap = 256,
        SmokeCompletionTokenCap = 48,
    };

    /// <summary>P100 VM lane: SAME validated topology as the owner proof run
    /// (brief §gpu-gpu-shared): node-A :8088 ngl=16 rpc 9513; node-B :8089 ngl=8 rpc 9514;
    /// both -t 3 -c 4096; binary ~/hydra-min-test/llama-engine via ssh shim.
    /// mmap page-cache sharing ⇒ one GGUF read by two nodes costs ~zero extra RAM;
    /// VRAM only pays offloaded layers (+~150MB cuda ctx per proc) (quirk #5).</summary>
    public static readonly MiniFleetPreset GpuGpuShared = new()
    {
        Name = "gpu-gpu-shared",
        EnginePortA = 8088,
        EnginePortB = 8089,
        RpcPortA = 9513,
        RpcPortB = 9514,
        NglA = 16,
        NglB = 8,
        ThreadsPerEngine = 3,
        ContextSize = 4096,
        ViaSshShim = true,
        SmokePromptTokenCap = 256,
        SmokeCompletionTokenCap = 48,
    };
}
