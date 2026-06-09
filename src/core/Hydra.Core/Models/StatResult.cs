namespace Hydra.Core;

public sealed record StatResult(
    string Name,
    long Size,
    DateTime LastModified
);
