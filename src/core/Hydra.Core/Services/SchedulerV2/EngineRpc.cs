using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Hydra.Shared;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// The engine RPC channel abstraction (DIP). <see cref="RpcClient"/> already has
/// these exact signatures; a small adapter exposes them so tests can substitute a
/// recording/fault-injecting fake without sockets.
/// </summary>
public interface IEngineRpcClient
{
    Task<RpcResponse> RequestAsync(OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct);

    /// <summary>Engine PREFILL (0x42) with optional hydra_config injection (epic
    /// #591 COMBINED). <paramref name="payloadJson"/> already carries the top-level
    /// <c>hydra_config</c> key when non-null (the gateway's <c>BuildPrefillBody</c>
    /// injects it) — this overload transports the bytes verbatim to the shared
    /// <see cref="RpcClient.EnginePrefillAsync"/> (the harness ScenarioRpcClient
    /// records the exact payload length via its RequestAsync override) and parses
    /// the response into <see cref="EnginePrefillResult"/>.</summary>
    Task<EnginePrefillResult> EnginePrefillAsync(
        string slotKey, string payloadJson, string traceId, CancellationToken ct,
        Dictionary<string, object>? hydraConfig);

    /// <summary>Framed DECODE (0x43) — the merged decode path (#470): the control
    /// header carries kv_metadata + model_metadata + generation config, followed
    /// by prompt + KV segments. Delegates to <see cref="RpcClient.EngineMergedDecodeAsync"/>.</summary>
    Task<MergedDecodeResponse> EngineMergedDecodeAsync(
        string slotKey, int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias, string? messagesJson, int nPredict, string? samplingJson, bool stream,
        ReadOnlyMemory<byte> kvBlob, string traceId, CancellationToken ct);
}

/// <summary>Adapts a shared <see cref="RpcClient"/> to <see cref="IEngineRpcClient"/>.</summary>
public sealed class EngineRpcClientAdapter : IEngineRpcClient
{
    private readonly RpcClient _inner;
    public EngineRpcClientAdapter(RpcClient inner) => _inner = inner;
    public Task<RpcResponse> RequestAsync(OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct)
        => _inner.RequestAsync(op, key, payload, traceId, ct);

    public async Task<EnginePrefillResult> EnginePrefillAsync(
        string slotKey, string payloadJson, string traceId, CancellationToken ct,
        Dictionary<string, object>? hydraConfig)
    {
        var resp = await _inner.EnginePrefillAsync(slotKey, payloadJson, traceId, ct);
        return EnginePrefillResponseParser.Parse(resp);
    }

    public Task<MergedDecodeResponse> EngineMergedDecodeAsync(
        string slotKey, int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias, string? messagesJson, int nPredict, string? samplingJson, bool stream,
        ReadOnlyMemory<byte> kvBlob, string traceId, CancellationToken ct)
        => _inner.EngineMergedDecodeAsync(
            slotKey, nPast,
            kvTokenizer, kvModelName, kvModelQuant, kvModelCapabilities,
            modelTokenizer, modelName, modelQuant, modelCapabilities,
            modelAlias, messagesJson, nPredict, samplingJson, stream,
            kvBlob, traceId, ct);
}

/// <summary>Result of an engine prefill: produced KV bytes + the model's n_past.
/// <see cref="NotImplemented"/> marks the #279 case — the engine's binary predates
/// the PREFILL opcode 0x42 and the caller must fall back to the HTTP prefill.
/// The identity fields are parsed from the response meta (M-Perf.9 #289) so Gate A
/// (DECODE 0x43) can compare kv_metadata against the decode node's identity.</summary>
public sealed record EnginePrefillResult(
    int NPast,
    long StateBytes,
    bool ModelFallback,
    byte[]? KVPayload,
    bool NotImplemented = false,
    string Tokenizer = "",
    string ModelName = "",
    string ModelQuant = "",
    uint ModelCapabilities = 0);

/// <summary>Parsed STATE_PUT (0x31) response: transport Ok flag plus the model
/// identity of the slot the KV was restored into. <c>ModelMatch=false</c> is the
/// engine-side cross-model rejection (#470); the identity fields feed
/// <see cref="CrossModelGuard"/> for the coordinator-side comparison.</summary>
public sealed record StatePutResult(
    bool Ok,
    bool ModelMatch,
    int NPast,
    string Tokenizer,
    string ModelName,
    string ModelQuant,
    uint ModelCapabilities);

/// <summary>
/// Engine-facing operations for the v2 phase handlers. Single responsibility:
/// encode wire payloads, call the per-worker engine channel, decode the response.
/// </summary>
public interface IEngineRpcGateway
{
    /// <param name="slotKey">The engine keys prefill by SLOT id ("0"), not the session.</param>
    /// <param name="hydraConfig">COMBINED (epic #591): when non-null, injected as the
    /// top-level <c>hydra_config</c> key of the PREFILL body (byte parity with legacy
    /// <c>HydraEngineClient.EnginePrefillAsync</c>). Null for solo/atomic/P-D.</param>
    Task<EnginePrefillResult> PrefillAsync(
        string worker, string slotKey, ChatRequest chat, CancellationToken ct,
        Dictionary<string, object>? hydraConfig = null,
        bool prefixCacheHit = false, int prefixNPast = 0);

    /// <summary>Push the KV blob onto the decode worker's slot (STATE_PUT 0x31).
    /// Returns the parsed response — transport status AND the slot's model
    /// identity (model_match + tokenizer/name/quant/capabilities).</summary>
    Task<StatePutResult> RestoreAsync(string worker, string slotKey, ReadOnlyMemory<byte> kv, int nPast, CancellationToken ct);

    /// <summary>Capture the slot's CURRENT KV (StateGet) — used by BgSave to keep
    /// the Store in sync with the post-decode slot. Returns null when the engine
    /// cannot produce it.</summary>
    Task<byte[]?> CaptureAsync(string worker, string slotKey, CancellationToken ct);

    /// <summary>Framed DECODE 0x43 (merged decode, #470): the engine validates the
    /// KV against the slot's resident model BEFORE generating (Gate A) and returns
    /// the decode_request_id for polling GET /v1/decode/{id}.</summary>
    Task<MergedDecodeResponse> MergedDecodeAsync(
        string worker, string slotKey, int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias, string? messagesJson, int nPredict, string? samplingJson, bool stream,
        ReadOnlyMemory<byte> kvBlob, string traceId, CancellationToken ct);

    /// <summary>Lazily emit the engine-wide state_chunk_size CONFIGURE (0x40, 28-byte
    /// payload) once per worker, before its first engine RPC (wire parity).</summary>
    Task EnsureChunkConfiguredAsync(string worker, CancellationToken ct);

    /// <summary>Emit a decode-time CONFIGURE (0x40) with per-request overrides,
    /// e.g. <c>{"n_predict":N}</c> (17-byte payload) — wire parity.</summary>
    Task ConfigureAsync(string worker, string slotKey, string json, CancellationToken ct);
}

public sealed class EngineRpcGateway : IEngineRpcGateway
{
    private readonly IReadOnlyDictionary<string, IEngineRpcClient> _channels;
    private readonly string _fallbackModel;
    private readonly string _stateChunkConfigJson;
    private readonly ConcurrentDictionary<string, bool> _chunkConfigured = new(StringComparer.Ordinal);

    public EngineRpcGateway(IReadOnlyDictionary<string, IEngineRpcClient> channels, int stateChunkSizeBytes = 1_048_576, string fallbackModel = "nano")
    {
        _channels = channels;
        _fallbackModel = fallbackModel;
        _stateChunkConfigJson = $"{{\"state_chunk_size\":{stateChunkSizeBytes}}}";
    }

    public async Task EnsureChunkConfiguredAsync(string worker, CancellationToken ct)
    {
        if (_chunkConfigured.ContainsKey(worker))
            return;
        // First engine RPC on this worker: emit the lazy state_chunk_size CONFIGURE
        // (key "0" — the config is engine-wide across slots, wire parity).
        _chunkConfigured[worker] = true; // set BEFORE the RPC to avoid duplicate in-flight emits
        try
        {
            await ConfigureAsync(worker, "0", _stateChunkConfigJson, ct);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "v2_engine_configure_chunk_failed Worker={Worker}", worker);
        }
    }

    public async Task ConfigureAsync(string worker, string slotKey, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        await Channel(worker).RequestAsync(OpCode.EngineConfigure, slotKey, payload, $"v2-config-{slotKey}", ct);
    }

    public Task<EnginePrefillResult> PrefillAsync(
        string worker, string slotKey, ChatRequest chat, CancellationToken ct,
        Dictionary<string, object>? hydraConfig = null,
        bool prefixCacheHit = false, int prefixNPast = 0)
    {
        var body = BuildPrefillBody(chat, hydraConfig, prefixCacheHit, prefixNPast);
        var payloadJson = JsonSerializer.Serialize(body);
        return Channel(worker).EnginePrefillAsync(slotKey, payloadJson, chat.TraceId, ct, hydraConfig);
    }

    public async Task<StatePutResult> RestoreAsync(string worker, string slotKey, ReadOnlyMemory<byte> kv, int nPast, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { n_past = nPast });
        var resp = await Channel(worker).RequestAsync(OpCode.StatePut, slotKey, kv, $"v2-restore-{slotKey}", ct);
        if (resp.Status != (byte)StatusCode.Ok)
            return new StatePutResult(Ok: false, ModelMatch: false, NPast: nPast, Tokenizer: "", ModelName: "", ModelQuant: "", ModelCapabilities: 0);
        return ParseStatePutMeta(resp.Meta, nPast);
    }

    public Task<MergedDecodeResponse> MergedDecodeAsync(
        string worker, string slotKey, int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias, string? messagesJson, int nPredict, string? samplingJson, bool stream,
        ReadOnlyMemory<byte> kvBlob, string traceId, CancellationToken ct)
        => Channel(worker).EngineMergedDecodeAsync(
            slotKey, nPast,
            kvTokenizer, kvModelName, kvModelQuant, kvModelCapabilities,
            modelTokenizer, modelName, modelQuant, modelCapabilities,
            modelAlias, messagesJson, nPredict, samplingJson, stream,
            kvBlob, traceId, ct);

    public async Task<byte[]?> CaptureAsync(string worker, string slotKey, CancellationToken ct)
    {
        var resp = await Channel(worker).RequestAsync(OpCode.StateGet, slotKey, ReadOnlyMemory<byte>.Empty, $"v2-stateget-{slotKey}", ct);
        return resp.Status == (byte)StatusCode.Ok ? resp.Payload : null;
    }

    private IEngineRpcClient Channel(string worker)
        => _channels.TryGetValue(worker, out var c)
            ? c
            : throw new InvalidOperationException($"no engine channel configured for worker '{worker}'");

    private static Dictionary<string, object> BuildPrefillBody(
        ChatRequest chat, Dictionary<string, object>? hydraConfig,
        bool prefixCacheHit = false, int prefixNPast = 0)
    {
        // #715 R3: delta prefill — when session-KV was restored (PrefixCacheHit)
        // the engine already holds KV for the first prefixNPast tokens. Truncate
        // messages so the engine only tokenizes + evals the delta. Falls back
        // to full message list when delta <= 0 (invariant: n_tokens > n_past).
        var messages = (IReadOnlyList<Dictionary<string, object>>)chat.Messages;
        if (prefixCacheHit && prefixNPast > 0 && chat.EstimatedTokens > prefixNPast)
        {
            messages = TruncateMessagesForDelta(chat.Messages, prefixNPast);
            Serilog.Log.Information(
                "v2_prefill_delta Est={Est} NPast={NP} OrigMsgs={Om} DeltaMsgs={Dm}",
                chat.EstimatedTokens, prefixNPast, chat.Messages.Count, messages.Count);
        }

        var body = new Dictionary<string, object>(chat.Body)
        {
            // Wire parity with the legacy EnginePrefill body: the raw request (stream,
            // max_tokens, model) plus n_predict=0 (prefill generates no tokens) and
            // the messages array (the goldens pin the exact payload length).
            ["stream"] = false,
            ["n_predict"] = 0,
            ["messages"] = messages,
        };
        // COMBINED (epic #591): inject hydra_config as the LAST key. The value is a
        // JsonNode so System.Text.Json emits its raw JSON verbatim — byte parity with
        // the legacy HydraEngineClient.EnginePrefillAsync
        // (node["hydra_config"] = JsonSerializer.SerializeToNode(hydraConfig); ToJsonString()).
        if (hydraConfig is { Count: > 0 })
            body["hydra_config"] = JsonSerializer.SerializeToNode(hydraConfig);
        return body;
    }

    // #715 R3: delta-prefill message truncation (V2 path).
    // When session-KV is restored (prefixNPast tokens already in the slot),
    // only send messages whose tokens fall beyond prefixNPast.
    private static IReadOnlyList<Dictionary<string, object>> TruncateMessagesForDelta(
        IReadOnlyList<Dictionary<string, object>> messages, int prefixNPast)
    {
        int cumulative = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            var content = messages[i].GetValueOrDefault("content")?.ToString() ?? "";
            var tokens = content.Length / 4; // ~4 chars/token heuristic (matches Router estimator)
            if (cumulative + tokens > prefixNPast)
                return messages.Skip(i).ToList(); // includes the straddling message
            cumulative += tokens;
        }
        return messages; // fallback: all messages within prefix, send full
    }

    /// <summary>Parse the STATE_PUT response meta: model_match (absent → true,
    /// back-compat with old servers) + the slot's model identity fields (#470).</summary>
    private static StatePutResult ParseStatePutMeta(string? meta, int fallbackNPast)
    {
        if (string.IsNullOrEmpty(meta))
            return new StatePutResult(Ok: true, ModelMatch: true, NPast: fallbackNPast, Tokenizer: "", ModelName: "", ModelQuant: "", ModelCapabilities: 0);
        try
        {
            using var doc = JsonDocument.Parse(meta);
            var root = doc.RootElement;
            var modelMatch = !root.TryGetProperty("model_match", out var mm) || mm.ValueKind != JsonValueKind.False;
            var nPast = root.TryGetProperty("n_past", out var np) && np.ValueKind == JsonValueKind.Number ? np.GetInt32() : fallbackNPast;
            return new StatePutResult(
                Ok: true,
                ModelMatch: modelMatch,
                NPast: nPast,
                Tokenizer: root.TryGetProperty("tokenizer", out var t) ? t.GetString() ?? "" : "",
                ModelName: root.TryGetProperty("model_name", out var mn) ? mn.GetString() ?? "" : "",
                ModelQuant: root.TryGetProperty("model_quant", out var mq) ? mq.GetString() ?? "" : "",
                ModelCapabilities: root.TryGetProperty("model_capabilities", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetUInt32() : 0);
        }
        catch (JsonException)
        {
            return new StatePutResult(Ok: true, ModelMatch: true, NPast: fallbackNPast, Tokenizer: "", ModelName: "", ModelQuant: "", ModelCapabilities: 0);
        }
    }
}

/// <summary>
/// Shared parse of an EnginePrefill (0x42) response into <see cref="EnginePrefillResult"/>
/// (epic #591) — used by both the channel adapter (<see cref="EngineRpcClientAdapter"/>)
/// and test fakes so the response handling is exactly one implementation. Mirrors the
/// legacy <c>HydraEngineClient.EnginePrefillAsync</c>: the #279 NotImplemented signal
/// (caller falls back to the HTTP prefill), a terminal throw on any other non-Ok
/// status, and the meta/payload mapping.
/// </summary>
internal static class EnginePrefillResponseParser
{
    public static EnginePrefillResult Parse(RpcResponse resp)
    {
        // #279: the engine's binary predates PREFILL (0x42) — report the fallback
        // signal instead of failing; the caller routes to the HTTP prefill.
        if (resp.Status == (byte)StatusCode.NotImplemented)
            return new EnginePrefillResult(NPast: 0, StateBytes: 0, ModelFallback: false, KVPayload: null, NotImplemented: true);

        if (resp.Status != (byte)StatusCode.Ok)
            throw new InvalidOperationException($"engine prefill failed: status={resp.Status}");

        // Meta: JSON { n_past, state_size, model_fallback, tokenizer, model_name, ... };
        // Payload: the KV blob.
        var meta = ParseMeta(resp.Meta);
        return new EnginePrefillResult(
            meta.NPast,
            resp.Payload?.LongLength ?? 0,
            meta.ModelFallback,
            resp.Payload,
            NotImplemented: false,
            Tokenizer: meta.Tokenizer,
            ModelName: meta.ModelName,
            ModelQuant: meta.ModelQuant,
            ModelCapabilities: meta.ModelCapabilities);
    }

    private static EnginePrefillMeta ParseMeta(string? meta)
    {
        if (string.IsNullOrEmpty(meta)) return default;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            var root = doc.RootElement;
            return new EnginePrefillMeta(
                root.TryGetProperty("n_past", out var n) ? n.GetInt32() : 0,
                root.TryGetProperty("model_fallback", out var f) && f.ValueKind == JsonValueKind.True,
                root.TryGetProperty("tokenizer", out var t) ? t.GetString() ?? "" : "",
                root.TryGetProperty("model_name", out var mn) ? mn.GetString() ?? "" : "",
                root.TryGetProperty("model_quant", out var mq) ? mq.GetString() ?? "" : "",
                root.TryGetProperty("model_capabilities", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetUInt32() : 0);
        }
        catch (JsonException)
        {
            return default; // malformed engine meta — callers guard on status
        }
    }

    private readonly record struct EnginePrefillMeta(
        int NPast, bool ModelFallback,
        string Tokenizer, string ModelName, string ModelQuant, uint ModelCapabilities);
}
