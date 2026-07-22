using System.Text.Json.Serialization;

namespace Hydra.Core.Models;

/// <summary>
/// GGUF-derived model identity carried through the save/restore pipeline.
/// Replaces the former single SHA-256 model_hash with semantic fields so
/// CrossModelGuard can make per-capability decisions (e.g. abort on MTP
/// mismatch, allow quant mismatch for P/D mix-quant).
/// </summary>
public sealed record ModelIdentity
{
    public const uint CapMTP       = 0x01;
    public const uint CapVision    = 0x02;
    public const uint CapReasoning = 0x04;
    public const uint CapToolUse   = 0x08;
    public const uint CapCode      = 0x10;
    /// <summary>Bits 5-31 reserved. Mask of all defined capability bits.</summary>
    public const uint CapAllDefined = CapMTP | CapVision | CapReasoning | CapToolUse | CapCode;

    [JsonPropertyName("tokenizer")]
    public string Tokenizer { get; init; } = "";

    [JsonPropertyName("model_name")]
    public string ModelName { get; init; } = "";

    [JsonPropertyName("model_quant")]
    public string ModelQuant { get; init; } = "";

    [JsonPropertyName("model_capabilities")]
    public uint ModelCapabilities { get; init; }

    /// <summary>True when all identity fields are empty/zero (pre-migration or not-yet-populated).</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Tokenizer)
        && string.IsNullOrEmpty(ModelName)
        && string.IsNullOrEmpty(ModelQuant)
        && ModelCapabilities == 0;

    /// <summary>Create an identity from a pre-#289 manifest (all defaults).</summary>
    public static readonly ModelIdentity Empty = new();
}
