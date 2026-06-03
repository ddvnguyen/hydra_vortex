namespace Hydra.Shared;

public sealed record RpcResponse(
    byte    Status,
    string? Meta,
    byte[]  Payload
);
