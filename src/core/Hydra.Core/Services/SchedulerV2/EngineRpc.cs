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
    Task<EnginePrefillResult> PrefillAsync(string worker, ChatRequest chat, CancellationToken ct);
    Task<bool> RestoreAsync(string worker, string sessionId, ReadOnlyMemory<byte> kv, int nPast, CancellationToken ct);
}

public sealed class EngineRpcGateway : IEngineRpcGateway
{
    private readonly IReadOnlyDictionary<string, IEngineRpcClient> _channels;
    private readonly string _fallbackModel;

    public EngineRpcGateway(IReadOnlyDictionary<string, IEngineRpcClient> channels, string fallbackModel = "nano")
    {
        _channels = channels;
        _fallbackModel = fallbackModel;
    }

    public async Task<EnginePrefillResult> PrefillAsync(string worker, ChatRequest chat, CancellationToken ct)
    {
        var body = BuildPrefillBody(chat);
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        var resp = await Channel(worker).RequestAsync(OpCode.EnginePrefill, chat.SessionId, payload, chat.TraceId, ct);

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
