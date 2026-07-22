using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hydra.Core.Models;
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
        if (resp.Status == (byte)StatusCode.NotImplemented)
        {
            // The engine binary doesn't support INFO — likely a pre-#289 build.
            // Return a sentinel so the caller can short-circuit (treat as no
            // engine support and fall back to the HTTP /v1/chat/completions path).
            return new EngineInfo
            {
                Engine         = "unknown",
                Version        = "",
                Capabilities   = new HashSet<string>(),
                PresetAliases  = new HashSet<string>(),
                NotImplemented = true
            };
        }
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
    /// model_alias, tokenizer, model_name, model_quant, model_capabilities,
    /// model_path, model_fallback) and the raw KV blob (the response payload,
    /// may be empty for non-engine builds).
    ///
    /// When the engine returns <see cref="StatusCode.NotImplemented"/> (the
    /// pre-#289 build path), the returned object's <see cref="EnginePrefillResult.NotImplemented"/>
    /// is <c>true</c> and the caller should fall back to the HTTP path.
    /// </summary>
    public async Task<EnginePrefillResult?> EnginePrefillAsync(
        int slotId, string? model, string requestJson, string traceId, CancellationToken ct)
        => await EnginePrefillAsync(slotId, model, requestJson, traceId, ct, hydraConfig: null);

    /// <summary>
    /// Engine PREFILL (0x42) with optional hydra_config injection. When
    /// <paramref name="hydraConfig"/> is non-null, it is serialised as a
    /// top-level <c>hydra_config</c> key in the wire JSON sent to the engine.
    /// The engine uses this to apply per-request split/tensor/rpc overrides
    /// (COMBINED mode Phase 2b payload, issue #481).
    /// </summary>
    public async Task<EnginePrefillResult?> EnginePrefillAsync(
        int slotId, string? model, string requestJson, string traceId,
        CancellationToken ct, Dictionary<string, object>? hydraConfig)
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

        // Phase 2b (#481): inject the hydra_config payload when provided.
        // The engine reads this top-level key to apply split_mode,
        // tensor_split, rpc_servers, etc. — per-request overrides that
        // replace the static --combined-ot-pattern startup config.
        if (hydraConfig is { Count: > 0 })
            node["hydra_config"] = JsonSerializer.SerializeToNode(hydraConfig);

        var payloadJson = node.ToJsonString();
        var resp = await _rpc.EnginePrefillAsync(
            slotId.ToString(), payloadJson, traceId, ct);

        if (resp.Status == (byte)StatusCode.NotImplemented)
        {
            return new EnginePrefillResult
            {
                NotImplemented = true,
                KvBlob         = Array.Empty<byte>()
            };
        }

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
            NPast             = meta?.NPast             ?? 0,
            StateSize         = meta?.StateSize         ?? 0,
            ModelAlias        = meta?.ModelAlias        ?? "",
            Tokenizer         = meta?.Tokenizer         ?? "",
            ModelName         = meta?.ModelName         ?? "",
            ModelQuant        = meta?.ModelQuant        ?? "",
            ModelCapabilities = meta?.ModelCapabilities ?? 0,
            ModelPath         = meta?.ModelPath         ?? "",
            ModelFallback     = meta?.ModelFallback     ?? false,
            PrefillMs         = meta?.PrefillMs         ?? 0,
            ModelLoadMs       = meta?.ModelLoadMs       ?? 0,
            PromptTokens      = meta?.PromptTokens      ?? 0,
            TokensPerSecond   = meta?.TokensPerSecond   ?? 0,
            CacheTokens       = meta?.CacheTokens       ?? 0,
            KvSize            = meta?.KvSize            ?? 0,
            LogitsSize        = meta?.LogitsSize        ?? 0,
            KvBlob            = resp.Payload
        };
    }

    /// <summary>Engine CONFIGURE (0x40). Apply a JSON config blob at runtime.
    /// Returns the typed <see cref="EngineConfigureResult"/> matching the
    /// Phase 2b wire schema (ddvnguyen/hydra_vortex#406).</summary>
    public async Task<EngineConfigureResult> EngineConfigureAsync(
        string slotKey, string configJson, string traceId, CancellationToken ct)
    {
        var resp = await _rpc.EngineConfigureAsync(slotKey, configJson, traceId, ct);
        return ParseConfigureResponse(resp);
    }

    /// <summary>Parse a 0x40 CONFIGURE wire response into the typed
    /// <see cref="EngineConfigureResult"/>. Public for callers that
    /// already have a <see cref="RpcResponse"/> (e.g. the
    /// legacy <see cref="Hydra.Shared.RpcClient.EngineConfigureAsync"/>
    /// path used by <c>WorkerSchedulerService.cs:2842</c> at startup).</summary>
    public static EngineConfigureResult ParseConfigureResponse(RpcResponse resp)
    {
        if (resp.Status != (byte)StatusCode.Ok || string.IsNullOrEmpty(resp.Meta))
        {
            return new EngineConfigureResult(
                Success: resp.Status == (byte)StatusCode.Ok,
                Tier: "",
                ParamsApplied: new Dictionary<string, JsonElement>(),
                DeferredKeys: Array.Empty<string>(),
                Error: resp.Status == (byte)StatusCode.Ok
                    ? null
                    : ExtractErrorMessage(resp.Meta),
                StateChunkSizeApplied: 0
            );
        }
        try
        {
            using var doc = JsonDocument.Parse(resp.Meta);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var sEl)
                && sEl.ValueKind == JsonValueKind.True;
            var tier = root.TryGetProperty("tier", out var tEl)
                && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() ?? "" : "";
            var dict = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("params_applied", out var paEl)
                && paEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in paEl.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
            }
            var deferred = new List<string>();
            if (root.TryGetProperty("deferred_keys", out var dkEl)
                && dkEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dkEl.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.String)
                        deferred.Add(d.GetString()!);
            }
            var error = root.TryGetProperty("error", out var eEl)
                && eEl.ValueKind == JsonValueKind.String
                ? eEl.GetString() : null;
            long chunkApplied = 0;
            // Legacy echo (hydra#334) — keep populated for backward compat
            if (root.TryGetProperty("state_chunk_size_applied", out var csEl)
                && csEl.ValueKind == JsonValueKind.Number)
                chunkApplied = csEl.GetInt64();
            return new EngineConfigureResult(
                Success: success,
                Tier: tier,
                ParamsApplied: dict,
                DeferredKeys: deferred,
                Error: error,
                StateChunkSizeApplied: chunkApplied
            );
        }
        catch
        {
            return new EngineConfigureResult(
                Success: false,
                Tier: "",
                ParamsApplied: new Dictionary<string, JsonElement>(),
                DeferredKeys: Array.Empty<string>(),
                Error: "malformed configure response"
            );
        }
    }

    private static string? ExtractErrorMessage(string? meta)
    {
        if (string.IsNullOrEmpty(meta)) return null;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            if (doc.RootElement.TryGetProperty("error", out var eEl)
                && eEl.ValueKind == JsonValueKind.String)
                return eEl.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Engine DECODE (0x43) non-streaming.</summary>
    public Task<RpcResponse> EngineDecodeAsync(
        string slotKey, int nPredict, string? requestJson, string traceId, CancellationToken ct)
        => _rpc.EngineDecodeAsync(slotKey, nPredict, requestJson, traceId, ct);

    /// <summary>Engine DECODE (0x43) streaming.</summary>
    public IAsyncEnumerable<byte[]> EngineDecodeStreamAsync(
        string slotKey, int nPredict, string? requestJson, string traceId,
        CancellationToken ct)
        => _rpc.EngineDecodeStreamAsync(slotKey, nPredict, requestJson, traceId, ct);

    /// <summary>Engine SET_EXPERT_MODE (0x44). Implemented (issue #287/#260 COMBINED half);
    /// switches the engine's expert-placement mode ("solo" | "combined") and reports the
    /// ACTUAL applied mode in the response (may be "solo" when "combined" was requested
    /// but the engine never dual-loaded expert tensors onto a peer). Coordinator's
    /// ReportsSolo() reads the response to detect the fallback.</summary>
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

    /// <summary>
    /// True when the engine returned <see cref="StatusCode.NotImplemented"/>
    /// for the INFO call (pre-#289 binary that doesn't speak the new
    /// opcodes). The caller should treat this as "engine support absent"
    /// and fall back to the HTTP /v1/chat/completions path.
    /// </summary>
    [JsonIgnore]
    public bool NotImplemented { get; init; }

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
    [JsonPropertyName("tokenizer")]
    public string Tokenizer { get; init; } = "";
    [JsonPropertyName("model_name")]
    public string ModelName { get; init; } = "";
    [JsonPropertyName("model_quant")]
    public string ModelQuant { get; init; } = "";
    [JsonPropertyName("model_capabilities")]
    public uint ModelCapabilities { get; init; }
    [JsonPropertyName("model_path")]
    public string ModelPath { get; init; } = "";
    [JsonPropertyName("model_fallback")]
    public bool ModelFallback { get; init; }

    // #451: PREFILL metrics for Hydra Core statistics
    [JsonPropertyName("prefill_ms")]
    public double PrefillMs { get; init; }
    [JsonPropertyName("model_load_ms")]
    public double ModelLoadMs { get; init; }
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }
    [JsonPropertyName("tokens_per_second")]
    public double TokensPerSecond { get; init; }
    [JsonPropertyName("cache_tokens")]
    public int CacheTokens { get; init; }
    [JsonPropertyName("kv_size")]
    public long KvSize { get; init; }
    [JsonPropertyName("logits_size")]
    public long LogitsSize { get; init; }

    /// <summary>Raw KV state blob returned by the engine (caller takes ownership).</summary>
    [JsonIgnore]
    public byte[] KvBlob { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// True when the engine returned <see cref="StatusCode.NotImplemented"/>
    /// for the PREFILL call. The caller should fall back to the HTTP path.
    /// </summary>
    [JsonIgnore]
    public bool NotImplemented { get; init; }
}
