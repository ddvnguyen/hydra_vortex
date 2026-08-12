using Hydra.Shared;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// KV store persistence for the v2 scheduler. Single responsibility: put/get the
/// per-session KV blob. (Chunked/delta save + content-addressed manifests are WP3
/// parity scope; the interface hides the transport so they can be added behind it.)
/// </summary>
public interface IStoreGateway
{
    Task<bool> PutAsync(string sessionId, ReadOnlyMemory<byte> kv, CancellationToken ct);
    Task<byte[]?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>Raw store Get by exact key (prefix checkpoints use <c>prefix/{hash}.kv</c>).</summary>
    Task<byte[]?> GetRawAsync(string key, CancellationToken ct);

    /// <summary>Raw store GET_MANIFEST by exact key — the JSON payload carries
    /// <c>{"n_past":N, "chunks":[...]}</c> for the prefix n_past guard.</summary>
    Task<byte[]?> GetManifestAsync(string key, CancellationToken ct);
}

public static class StoreKeys
{
    /// <summary>The store key for a session's KV blob (<c>{sessionId}.kv</c> — the
    /// wire key the goldens pin).</summary>
    public static string KvKey(string sessionId) => $"{sessionId}.kv";
}

public sealed class StoreGateway : IStoreGateway
{
    private readonly RpcClient _store;

    public StoreGateway(RpcClient store) => _store = store;

    public async Task<bool> PutAsync(string sessionId, ReadOnlyMemory<byte> kv, CancellationToken ct)
    {
        var resp = await _store.RequestAsync(OpCode.Put, sessionId, kv, "v2-store", ct);
        return resp.Status == (byte)StatusCode.Ok;
    }

    public async Task<byte[]?> GetAsync(string sessionId, CancellationToken ct)
    {
        var resp = await _store.RequestAsync(OpCode.Get, sessionId, ReadOnlyMemory<byte>.Empty, "v2-store", ct);
        return resp.Status == (byte)StatusCode.Ok ? resp.Payload : null;
    }

    public Task<byte[]?> GetRawAsync(string key, CancellationToken ct)
        => GetRawAsyncCore(key, OpCode.Get, ct);

    public Task<byte[]?> GetManifestAsync(string key, CancellationToken ct)
        => GetRawAsyncCore(key, OpCode.GetManifest, ct);

    private async Task<byte[]?> GetRawAsyncCore(string key, OpCode op, CancellationToken ct)
    {
        var resp = await _store.RequestAsync(op, key, ReadOnlyMemory<byte>.Empty, "v2-store", ct);
        return resp.Status == (byte)StatusCode.Ok ? resp.Payload : null;
    }
}
