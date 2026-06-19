using System.Text.Json.Serialization;
using Hydra.Shared;

namespace Hydra.Core;

public sealed record Manifest(
    string SessionId,
    int Version,
    int NPast,
    long TotalSize,
    List<ChunkRef> Chunks,
    DateTime CreatedAt,
    // M-Perf.9 #289: model identity of the slot that built this KV cache.
    // Empty for pre-#289 manifests (the cross-model guard treats "both empty"
    // as "skip"). Stored in PG alongside n_past/total_size so the model
    // identity survives a Coordinator restart.
    string ModelAlias = "",
    string ModelHash  = "",
    string ModelPath  = ""
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
