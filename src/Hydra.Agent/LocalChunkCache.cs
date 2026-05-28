using System.Collections.Concurrent;
using System.Text.Json;

namespace Hydra.Agent;

public sealed class LocalChunkCache
{
    private readonly DirectoryInfo _cacheDir;
    private readonly int _maxSessions;
    private readonly ConcurrentDictionary<string, SessionChunkCache> _caches = new();

    public LocalChunkCache(string cacheDir, int maxSessions = 100)
    {
        _cacheDir = new DirectoryInfo(cacheDir);
        _maxSessions = maxSessions;

        if (!_cacheDir.Exists)
            _cacheDir.Create();
    }

    public Task SaveHashesAsync(string sessionId, List<string> hashes, CancellationToken ct)
    {
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

    public async Task<List<string>> LoadHashesAsync(string sessionId, CancellationToken ct)
    {
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

    public Task ClearAsync(string sessionId)
    {
        _caches.TryRemove(sessionId, out _);
        var path = CachePath(sessionId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public int EvictLRU()
    {
        if (_caches.Count <= _maxSessions)
            return 0;

        var oldest = _caches.Values
            .OrderBy(c => c.SavedAt)
            .Take(_caches.Count - _maxSessions)
            .ToList();

        foreach (var entry in oldest)
        {
            _caches.TryRemove(entry.SessionId, out _);
            var path = CachePath(entry.SessionId);
            if (File.Exists(path))
                File.Delete(path);
        }

        return oldest.Count;
    }

    public int CachedSessionCount => _caches.Count;

    public bool HasCachedHashes(string sessionId) =>
        _caches.ContainsKey(sessionId) || File.Exists(CachePath(sessionId));

    private string CachePath(string sessionId)
    {
        var safeName = sessionId.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_cacheDir.FullName, $"{safeName}.chunks.json");
    }
}

internal sealed class SessionChunkCache
{
    public string SessionId { get; set; } = "";
    public List<string> ChunkHashes { get; set; } = [];
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}
