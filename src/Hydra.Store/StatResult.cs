namespace Hydra.Store;

public sealed record StatResult(
    string Name,
    long Size,
    DateTime LastModified
);
