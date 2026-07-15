using System.Text.Json.Serialization;

namespace Hydra.Core.Models;

/// <summary>
/// Static capability bits for GPU hardware matching.
/// Used by AutoRouter to verify worker.gpu.capabilities and model.requirements.required_capabilities.
/// </summary>
public static class GpuCapabilities
{
    public const int FlashAttn = 1;
    public const int Rpc = 2;
    public const int Combined = 4;
}

/// <summary>
/// Hardware properties of a GPU worker. Profile-independent physical facts loaded
/// from gpu-specs.json. Joined to WorkerConfig via gpu_ref or name.
/// </summary>
public sealed record GpuSpec
{
    [JsonPropertyName("vram_mb")]
    public int VramMb { get; init; }

    [JsonPropertyName("compute_tflops")]
    public double ComputeTflops { get; init; }

    [JsonPropertyName("bandwidth_gbps")]
    public double BandwidthGbps { get; init; }

    [JsonPropertyName("cuda_arch")]
    public string CudaArch { get; init; } = "";

    [JsonPropertyName("capabilities")]
    public int Capabilities { get; init; }

    /// <summary>Check if this GPU has all required capabilities (bitwise AND).</summary>
    public bool HasCapability(int required) => (Capabilities & required) == required;
}
