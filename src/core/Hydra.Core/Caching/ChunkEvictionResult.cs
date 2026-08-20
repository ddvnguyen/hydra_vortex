namespace Hydra.Core.Caching;

/// <summary>
/// Result of an L1 chunk-cache LRU eviction cycle: how many sessions were
/// evicted and how many bytes were freed from the tmpfs. The bytes figure
/// feeds the periodic sweep's chunk_cache_lru_sweep log (#615).
/// </summary>
public readonly record struct ChunkEvictionResult(int Evicted, long BytesFreed);
