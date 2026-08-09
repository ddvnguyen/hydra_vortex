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
}
