using System.Collections.Concurrent;

namespace Hydra.Store;

public sealed class ChunkStore
{
    private readonly DirectoryInfo _chunksDir;
    private readonly ConcurrentDictionary<string, byte> _knownHashes = new();

    public ChunkStore(DirectoryInfo storeDir)
    {
        _chunksDir = new DirectoryInfo(Path.Combine(storeDir.FullName, "chunks"));
        if (!_chunksDir.Exists)
            _chunksDir.Create();
        RebuildIndex();
    }

    public void RefreshIndex() => RebuildIndex();

    private void RebuildIndex()
    {
        foreach (var file in _chunksDir.EnumerateFiles())
        {
            if (file.Name.EndsWith(".tmp")) continue;
            _knownHashes[file.Name] = 0;
        }
    }

    public int KnownChunkCount => _knownHashes.Count;
    public DirectoryInfo ChunksDirectory => _chunksDir;

    public async Task<bool> StoreChunkAsync(string hash, byte[] data, CancellationToken ct = default)
    {
        if (!_knownHashes.TryAdd(hash, 0))
            return false;

        var path = Path.Combine(_chunksDir.FullName, hash);
        var tmpPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmpPath, data, ct);
            File.Move(tmpPath, path, overwrite: true);
            return true;
        }
        catch
        {
            _knownHashes.TryRemove(hash, out _);
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    public bool HasChunk(string hash)
    {
        return _knownHashes.ContainsKey(hash);
    }

    public string? GetChunkPath(string hash)
    {
        var path = Path.Combine(_chunksDir.FullName, hash);
        return File.Exists(path) ? path : null;
    }

    public async Task<ChunkStoreStats> GetStatsAsync(CancellationToken ct)
    {
        var totalChunks = _knownHashes.Count;
        long totalBytes = 0;
        foreach (var file in _chunksDir.EnumerateFiles())
        {
            if (file.Name.EndsWith(".tmp")) continue;
            totalBytes += file.Length;
        }
        return new ChunkStoreStats(totalChunks, 0, totalBytes);
    }
}

public sealed record ChunkStoreStats(
    int TotalChunks,
    int ManifestCount,
    long TotalBytes
);
