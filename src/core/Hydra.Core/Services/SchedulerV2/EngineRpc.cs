using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Hydra.Shared;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// The engine RPC channel abstraction (DIP). <see cref="RpcClient"/> already has
/// this exact signature; a small adapter exposes it so tests can substitute a
/// recording/fault-injecting fake without sockets.
/// </summary>
public interface IEngineRpcClient
{
    Task<RpcResponse> RequestAsync(OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct);
}

/// <summary>Adapts a shared <see cref="RpcClient"/> to <see cref="IEngineRpcClient"/>.</summary>
public sealed class EngineRpcClientAdapter : IEngineRpcClient
{
    private readonly RpcClient _inner;
    public EngineRpcClientAdapter(RpcClient inner) => _inner = inner;
    public Task<RpcResponse> RequestAsync(OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct)
        => _inner.RequestAsync(op, key, payload, traceId, ct);
}

/// <summary>Result of an engine prefill: produced KV bytes + the model's n_past.</summary>
public sealed record EnginePrefillResult(int NPast, long StateBytes, bool ModelFallback, byte[]? KVPayload);

/// <summary>
/// Engine-facing operations for the v2 phase handlers. Single responsibility:
/// encode wire payloads, call the per-worker engine channel, decode the response.
/// </summary>
public interface IEngineRpcGateway
{
    /// <param name="slotKey">The engine keys prefill by SLOT id ("0"), not the session.</param>
    Task<EnginePrefillResult> PrefillAsync(string worker, string slotKey, ChatRequest chat, CancellationToken ct);
    Task<bool> RestoreAsync(string worker, string sessionId, ReadOnlyMemory<byte> kv, int nPast, CancellationToken ct);

    /// <summary>Capture the slot's CURRENT KV (StateGet) — used by BgSave to keep
    /// the Store in sync with the post-decode slot. Returns null when the engine
    /// cannot produce it.</summary>
    Task<byte[]?> CaptureAsync(string worker, string slotKey, CancellationToken ct);

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

    public async Task<EnginePrefillResult> PrefillAsync(string worker, string slotKey, ChatRequest chat, CancellationToken ct)
    {
        var body = BuildPrefillBody(chat);
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        var resp = await Channel(worker).RequestAsync(OpCode.EnginePrefill, slotKey, payload, chat.TraceId, ct);

        if (resp.Status != (byte)StatusCode.Ok)
            throw new InvalidOperationException($"engine prefill failed: status={resp.Status}");

        // Meta: JSON { n_past, state_size, model_fallback, ... }; Payload: the KV blob.
        var meta = ParseMeta(resp.Meta);
        return new EnginePrefillResult(
            meta.NPast,
            resp.Payload?.LongLength ?? 0,
            meta.ModelFallback,
            resp.Payload);
    }

    public async Task<bool> RestoreAsync(string worker, string sessionId, ReadOnlyMemory<byte> kv, int nPast, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { n_past = nPast });
        var resp = await Channel(worker).RequestAsync(OpCode.StatePut, sessionId, kv, $"v2-restore-{sessionId}", ct);
        return resp.Status == (byte)StatusCode.Ok;
    }

    public async Task<byte[]?> CaptureAsync(string worker, string slotKey, CancellationToken ct)
    {
        var resp = await Channel(worker).RequestAsync(OpCode.StateGet, slotKey, ReadOnlyMemory<byte>.Empty, $"v2-stateget-{slotKey}", ct);
        return resp.Status == (byte)StatusCode.Ok ? resp.Payload : null;
    }

    private IEngineRpcClient Channel(string worker)
        => _channels.TryGetValue(worker, out var c)
            ? c
            : throw new InvalidOperationException($"no engine channel configured for worker '{worker}'");

    private static Dictionary<string, object> BuildPrefillBody(ChatRequest chat) => new()
    {
        ["model"] = chat.Model ?? "nano",
        ["messages"] = chat.Messages,
        ["stream"] = false,
        ["n_predict"] = chat.MaxTokens,
    };

    private static EnginePrefillMeta ParseMeta(string? meta)
    {
        if (string.IsNullOrEmpty(meta)) return default;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            return new EnginePrefillMeta(
                doc.RootElement.TryGetProperty("n_past", out var n) ? n.GetInt32() : 0,
                doc.RootElement.TryGetProperty("model_fallback", out var f) && f.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return default; // malformed engine meta — callers guard on status
        }
    }

    private readonly record struct EnginePrefillMeta(int NPast, bool ModelFallback);
}
