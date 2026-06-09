using System.Collections.Concurrent;
using System.Text.Json;

namespace Hydra.Core;

public sealed class LocalChunkCache : IDisposable
{
    private readonly DirectoryInfo _cacheDir;
    private readonly int _maxSessions;
    private readonly ConcurrentDictionary<string, SessionChunkCache> _caches = new();
    private bool _disposed;

    public LocalChunkCache(string cacheDir, int maxSessions = 100)
    {
        _cacheDir = new DirectoryInfo(cacheDir);
        _maxSessions = maxSessions;

        if (!_cacheDir.Exists)
            _cacheDir.Create();
    }

    public Task SaveHashesAsync(string sessionId, List<string> hashes, CancellationToken ct)
    {
        if (_disposed) return Task.CompletedTask;

        var path = CachePath(sessionId);

        var cache = new SessionChunkCache
        {
            SessionId = sessionId,
            ChunkHashes = hashes,
            SavedAt = DateTime.UtcNow
        };

        _caches[sessionId] = cache;

        var json = JsonSerializer.Serialize(cache);
        return File.WriteAllTextAsync(path, json, ct);
    }

    public async Task SaveChunkDataAsync(string sessionId, string hash, byte[] chunkData, CancellationToken ct)
    {
        if (_disposed || chunkData == null || chunkData.Length == 0) return;

        var safeSessionId = sessionId.Replace('/', '_').Replace('\\', '_');
        var chunkPath = Path.Combine(_cacheDir.FullName, $"{safeSessionId}.{hash}");
        var dir = new FileInfo(chunkPath).Directory;
        if (dir is not null && !dir.Exists)
            dir.Create();

        await File.WriteAllBytesAsync(chunkPath, chunkData, ct);
    }

    // Synchronous version for use in non-async contexts (e.g., ChunkHashTeeStream.Read).
    public void SaveChunkData(string sessionId, string hash, byte[] chunkData)
    {
        if (_disposed || chunkData == null || chunkData.Length == 0) return;

        var safeSessionId = sessionId.Replace('/', '_').Replace('\\', '_');
        var chunkPath = Path.Combine(_cacheDir.FullName, $"{safeSessionId}.{hash}");
        var dir = new FileInfo(chunkPath).Directory;
        if (dir is not null && !dir.Exists)
            dir.Create();

        File.WriteAllBytes(chunkPath, chunkData);
    }

    public async Task<byte[]?> GetChunkDataAsync(string sessionId, string hash, CancellationToken ct)
    {
        if (_disposed) return null;

        var safeSessionId = sessionId.Replace('/', '_').Replace('\\', '_');
        var chunkPath = Path.Combine(_cacheDir.FullName, $"{safeSessionId}.{hash}");
        if (!File.Exists(chunkPath))
            return null;

        return await File.ReadAllBytesAsync(chunkPath, ct);
    }

    public async Task<List<string>> LoadHashesAsync(string sessionId, CancellationToken ct)
    {
        if (_disposed) return [];

        if (_caches.TryGetValue(sessionId, out var cached))
            return cached.ChunkHashes;

        var path = CachePath(sessionId);
        if (!File.Exists(path))
            return [];

        var json = await File.ReadAllTextAsync(path, ct);
        var cache = JsonSerializer.Deserialize<SessionChunkCache>(json);
        if (cache is null)
            return [];

        _caches[sessionId] = cache;
        return cache.ChunkHashes;
    }

    public bool HasCachedHashes(string sessionId)
    {
        if (_disposed) return false;

        // Check in-memory cache first.
        if (_caches.ContainsKey(sessionId))
            return true;

        // Fall back to disk check.
        var path = CachePath(sessionId);
        return File.Exists(path);
    }

    public Task ClearAsync(string sessionId)
    {
        if (_disposed) return Task.CompletedTask;

        _caches.TryRemove(sessionId, out _);
        var path = CachePath(sessionId);
        if (File.Exists(path))
            File.Delete(path);

        var safeSessionId = sessionId.Replace('/', '_').Replace('\\', '_');
        foreach (var file in _cacheDir.EnumerateFiles($"{safeSessionId}.*"))
        {
            try
            {
                if (file.Name.EndsWith(".chunks.json")) continue; // keep index
                file.Delete();
            }
            catch { /* ignore */ }
        }

        return Task.CompletedTask;
    }

    public async Task<int> EvictLRUAsync()
    {
        if (_disposed) return 0;

        var evicted = 0;
        while (_caches.Count > _maxSessions)
        {
            var oldest = _caches.Values
                .OrderBy(c => c.SavedAt)
                .FirstOrDefault();
            if (oldest is null) break;

            _caches.TryRemove(oldest.SessionId, out _);
            var path = CachePath(oldest.SessionId);
            if (File.Exists(path))
                File.Delete(path);

            await ClearAsync(oldest.SessionId);

            evicted++;
        }

        return evicted;
    }

    public int CachedSessionCount => _caches.Count;

    public bool HasChunkData(string sessionId, string hash)
    {
        if (_disposed) return false;

        var safeSessionId = sessionId.Replace('/', '_').Replace('\\', '_');
        var chunkPath = Path.Combine(_cacheDir.FullName, $"{safeSessionId}.{hash}");
        return File.Exists(chunkPath);
    }

    private string CachePath(string sessionId)
    {
        var safeName = sessionId.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_cacheDir.FullName, $"{safeName}.chunks.json");
    }

    public void Dispose()
    {
        _disposed = true;
        _caches.Clear();
    }
}

internal sealed class SessionChunkCache
{
    public string SessionId { get; set; } = "";
    public List<string> ChunkHashes { get; set; } = [];
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}
