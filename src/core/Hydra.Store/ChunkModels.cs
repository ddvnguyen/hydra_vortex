using System.Text.Json.Serialization;
using Hydra.Shared;

namespace Hydra.Store;

public sealed record Manifest(
    string SessionId,
    int Version,
    int NPast,
    long TotalSize,
    List<ChunkRef> Chunks,
    DateTime CreatedAt
);

public sealed record ChunkedPutResult(
    int NewChunks,
    int DedupedChunks,
    int TotalChunks
);

public sealed record ChunkedGetMeta(
    int MissingCount,
    long TotalSize
);
