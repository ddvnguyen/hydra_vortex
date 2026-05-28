using System.Collections.Concurrent;
using System.Text.Json;

namespace Hydra.Store;

public sealed class ChunkStore
{
    private readonly DirectoryInfo _chunksDir;
    private readonly DirectoryInfo _manifestsDir;
    private readonly ConcurrentDictionary<string, byte> _knownHashes = new();

    public ChunkStore(DirectoryInfo storeDir)
    {
        _chunksDir = new DirectoryInfo(Path.Combine(storeDir.FullName, "chunks"));
        _manifestsDir = new DirectoryInfo(Path.Combine(storeDir.FullName, "manifests"));

        if (!_chunksDir.Exists)
            _chunksDir.Create();
        if (!_manifestsDir.Exists)
            _manifestsDir.Create();

        RebuildIndex();
    }

    private void RebuildIndex()
    {
        foreach (var file in _chunksDir.EnumerateFiles())
        {
            _knownHashes[file.Name] = 0;
        }
    }

    public int KnownChunkCount => _knownHashes.Count;

    public DirectoryInfo ChunksDirectory => _chunksDir;
    public DirectoryInfo ManifestsDirectory => _manifestsDir;

    public async Task<bool> StoreChunkAsync(string hash, byte[] data, CancellationToken ct = default)
    {
        if (_knownHashes.ContainsKey(hash))
            return false;

        var path = Path.Combine(_chunksDir.FullName, hash);
        await File.WriteAllBytesAsync(path, data, ct);
        _knownHashes[hash] = 0;
        return true;
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

    public async Task SaveManifestAsync(string sessionId, Manifest manifest, CancellationToken ct)
    {
        var path = ManifestPath(sessionId);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    public async Task<Manifest?> LoadManifestAsync(string sessionId, CancellationToken ct)
    {
        var path = ManifestPath(sessionId);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<Manifest>(json);
    }

    public List<string> DiffPlan(Manifest manifest, List<string> clientHashes)
    {
        var clientSet = new HashSet<string>(clientHashes);
        return manifest.Chunks
            .Where(c => !clientSet.Contains(c.Hash))
            .Select(c => c.Hash)
            .ToList();
    }

    public int GC(HashSet<string> keepSessions)
    {
        var referenced = new HashSet<string>();

        foreach (var mf in _manifestsDir.EnumerateFiles("*.json"))
        {
            var sessionId = Path.GetFileNameWithoutExtension(mf.Name);
            if (!keepSessions.Contains(sessionId))
            {
                mf.Delete();
                continue;
            }

            var json = File.ReadAllText(mf.FullName);
            var manifest = JsonSerializer.Deserialize<Manifest>(json);
            if (manifest is not null)
            {
                foreach (var chunk in manifest.Chunks)
                    referenced.Add(chunk.Hash);
            }
        }

        int removed = 0;
        foreach (var file in _chunksDir.EnumerateFiles())
        {
            if (!referenced.Contains(file.Name))
            {
                file.Delete();
                _knownHashes.TryRemove(file.Name, out _);
                removed++;
            }
        }

        return removed;
    }

    public async Task<ChunkStoreStats> GetStatsAsync(CancellationToken ct)
    {
        var totalChunks = _knownHashes.Count;
        long totalBytes = 0;

        foreach (var file in _chunksDir.EnumerateFiles())
            totalBytes += file.Length;

        var manifestCount = _manifestsDir.EnumerateFiles("*.json").Count();

        return new ChunkStoreStats(totalChunks, manifestCount, totalBytes);
    }

    private string ManifestPath(string sessionId)
    {
        var safeName = sessionId.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_manifestsDir.FullName, $"{safeName}.json");
    }
}

public sealed record ChunkStoreStats(
    int TotalChunks,
    int ManifestCount,
    long TotalBytes
);
