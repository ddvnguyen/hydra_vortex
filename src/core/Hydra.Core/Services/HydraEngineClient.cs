using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hydra.Shared;

namespace Hydra.Core.Services;

/// <summary>
/// Wraps the Hydra engine control-plane RPCs (opcodes 0x40-0x46) for clean
/// C# consumption. Backed by the same <see cref="RpcClient"/> the rest of
/// the coordinator uses (the engine reuses the existing --rpc-port).
///
/// Wire format reference: specs/rpc-protocol.md + issue #289 (M-Perf.9).
/// </summary>
public sealed class HydraEngineClient
{
    private readonly RpcClient _rpc;

    public HydraEngineClient(RpcClient rpc)
    {
        _rpc = rpc;
    }

    /// <summary>Engine INFO (0x41). Returns the engine's capability advertisement.</summary>
    public async Task<EngineInfo?> EngineInfoAsync(string traceId, CancellationToken ct)
    {
        var resp = await _rpc.EngineInfoAsync("", traceId, ct);
        if (resp.Status != (byte)StatusCode.Ok || string.IsNullOrEmpty(resp.Meta))
            return null;
        try
        {
            return JsonSerializer.Deserialize<EngineInfo>(resp.Meta);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Engine PREFILL (0x42). Sends the request JSON + optional model alias
    /// to the engine. Returns the parsed response meta (n_past, state_size,
    /// model_alias, model_hash, model_path, model_fallback) and the raw KV
    /// blob (the response payload, may be empty for non-engine builds).
    /// </summary>
    public async Task<EnginePrefillResult?> EnginePrefillAsync(
        int slotId, string? model, string requestJson, string traceId, CancellationToken ct)
    {
        var node = JsonNode.Parse(requestJson) as JsonObject
            ?? throw new ArgumentException("requestJson must be a JSON object", nameof(requestJson));

        // Inject the model key only when the caller actually requested one
        // AND the request body does not already carry a `model` key — the
        // caller's value wins (e.g. an explicit `--model` from a client).
        // An empty `model` key would be a contract violation; the engine
        // treats absent as "use current resident model".
        if (!string.IsNullOrEmpty(model) && !node.ContainsKey("model"))
            node["model"] = model;

        var payloadJson = node.ToJsonString();
        var resp = await _rpc.EnginePrefillAsync(
            slotId.ToString(), payloadJson, traceId, ct);

        if (resp.Status != (byte)StatusCode.Ok)
            return null;

        EnginePrefillResult? meta = null;
        if (!string.IsNullOrEmpty(resp.Meta))
        {
            try { meta = JsonSerializer.Deserialize<EnginePrefillResult>(resp.Meta); }
            catch { meta = null; }
        }
        return new EnginePrefillResult
        {
            NPast         = meta?.NPast         ?? 0,
            StateSize     = meta?.StateSize     ?? 0,
            ModelAlias    = meta?.ModelAlias    ?? "",
            ModelHash     = meta?.ModelHash     ?? "",
            ModelPath     = meta?.ModelPath     ?? "",
            ModelFallback = meta?.ModelFallback ?? false,
            KvBlob        = resp.Payload
        };
    }

    /// <summary>Engine CONFIGURE (0x40). Apply a JSON config blob at runtime.</summary>
    public Task<RpcResponse> EngineConfigureAsync(
        string slotKey, string configJson, string traceId, CancellationToken ct)
        => _rpc.EngineConfigureAsync(slotKey, configJson, traceId, ct);

    /// <summary>Engine DECODE (0x43) non-streaming.</summary>
    public Task<RpcResponse> EngineDecodeAsync(
        string slotKey, int nPredict, string? requestJson, string traceId, CancellationToken ct)
        => _rpc.EngineDecodeAsync(slotKey, nPredict, requestJson, traceId, ct);

    /// <summary>Engine DECODE (0x43) streaming.</summary>
    public IAsyncEnumerable<byte[]> EngineDecodeStreamAsync(
        string slotKey, int nPredict, string? requestJson, string traceId,
        CancellationToken ct)
        => _rpc.EngineDecodeStreamAsync(slotKey, nPredict, requestJson, traceId, ct);

    /// <summary>Engine SET_EXPERT_MODE (0x44). Stubbed on the C++ side; returns NOT_IMPLEMENTED today.</summary>
    public Task<RpcResponse> EngineSetExpertModeAsync(
        string slotKey, string mode, string traceId, CancellationToken ct)
        => _rpc.EngineSetExpertModeAsync(slotKey, mode, traceId, ct);

    /// <summary>Engine PIPELINE_ATTACH (0x46). Stubbed on the C++ side; returns NOT_IMPLEMENTED today.</summary>
    public Task<RpcResponse> EnginePipelineAttachAsync(
        string slotKey, string peer, string otSplit, string traceId, CancellationToken ct)
        => _rpc.EnginePipelineAttachAsync(slotKey, peer, otSplit, traceId, ct);

    /// <summary>Engine SWAP_QUANT (0x45). Stubbed on the C++ side; returns NOT_IMPLEMENTED today.</summary>
    public Task<RpcResponse> EngineSwapQuantAsync(
        string slotKey, string quantKey, string tensorPattern, string traceId, CancellationToken ct)
        => _rpc.EngineSwapQuantAsync(slotKey, quantKey, tensorPattern, traceId, ct);
}

public sealed class EngineInfo
{
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = "";
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";
    [JsonPropertyName("capabilities")]
    public HashSet<string> Capabilities { get; init; } = new();
    [JsonPropertyName("preset_aliases")]
    public HashSet<string> PresetAliases { get; init; } = new();

    public bool HasCapability(string name) => Capabilities.Contains(name);
}

public sealed class EnginePrefillResult
{
    [JsonPropertyName("n_past")]
    public int NPast { get; init; }
    [JsonPropertyName("state_size")]
    public long StateSize { get; init; }
    [JsonPropertyName("model_alias")]
    public string ModelAlias { get; init; } = "";
    [JsonPropertyName("model_hash")]
    public string ModelHash { get; init; } = "";
    [JsonPropertyName("model_path")]
    public string ModelPath { get; init; } = "";
    [JsonPropertyName("model_fallback")]
    public bool ModelFallback { get; init; }

    /// <summary>Raw KV state blob returned by the engine (caller takes ownership).</summary>
    [JsonIgnore]
    public byte[] KvBlob { get; init; } = Array.Empty<byte>();
}
