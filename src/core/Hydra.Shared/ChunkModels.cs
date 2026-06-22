namespace Hydra.Shared;

/// <summary>
/// Reference to a chunk in the manifest, including its index, hash, and size.
/// Index is the position of this chunk in the original KV state stream (0-based).
/// </summary>
public sealed record ChunkRef(
    int Index,
    string Hash,
    int Size
);

/// <summary>
/// Constants and utilities for chunking KV cache state.
/// </summary>
public static class ChunkConstants
{
    public static int ChunkSize = 8 * 1024 * 1024; // 8 MB per chunk
}
