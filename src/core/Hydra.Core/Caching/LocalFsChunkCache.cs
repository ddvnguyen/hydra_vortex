using System.Collections.Concurrent;
using System.Text.Json;

namespace Hydra.Core.Caching;

/// <summary>
/// L1 chunk cache: per-session, in tmpfs (typically /mnt/llm-ram/chunk-cache-l1).
/// Bounded by a byte budget (default 20 GB); LRU eviction is enforced both
/// at-write (synchronous) and by a periodic background sweep. Closes
/// M3-P1 #332's no-eviction bug — the L1 can no longer grow without bound.
///
/// This class is L1-only; it does NOT know about the L2 durable store.
/// The facade <see cref="LocalChunkCache"/> is responsible for write-through
/// to the L2 (via IContentChunkStore) on every SaveChunkDataAsync.
///
/// Implements <see cref="IChunkCache"/> (the session+hash API used by
/// WorkerSchedulerService and StateHandler).
/// </summary>
public sealed class LocalFsChunkCache : IChunkCache
{
    private readonly DirectoryInfo _cacheDir;
    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, SessionChunkCache> _caches = new();
    private long _usedBytes;
    private bool _disposed;

    public LocalFsChunkCache(string cacheDir, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(cacheDir))
            throw new ArgumentException("cacheDir is required", nameof(cacheDir));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be > 0");

        _cacheDir = new DirectoryInfo(cacheDir);
        _maxBytes = maxBytes;

        if (!_cacheDir.Exists)
            _cacheDir.Create();

        // Rebuild in-memory state from disk so a restart preserves both the
        // session index and the byte accounting.
        RebuildFromDisk();
    }

    public long L1MaxBytes => _maxBytes;
    public long L1UsedBytes => Interlocked.Read(ref _usedBytes);

    public Task SaveHashesAsync(string sessionId, List<string> hashes, CancellationToken ct)
    {
        if (_disposed) return Task.CompletedTask;

        var cache = new SessionChunkCache
        {
            SessionId = sessionId,
            ChunkHashes = hashes,
            SavedAt = DateTime.UtcNow
        };
        _caches[sessionId] = cache;

        var json = JsonSerializer.Serialize(cache);
        return File.WriteAllTextAsync(CachePath(sessionId), json, ct);
    }

    public async Task SaveChunkDataAsync(string sessionId, string hash, byte[] chunkData, CancellationToken ct)
    {
        if (_disposed || chunkData is null || chunkData.Length == 0) return;

        var chunkPath = ChunkPath(sessionId, hash);

        // Make sure the session is tracked for LRU even if SaveHashesAsync
        // wasn't called first (callers like ChunkHashTeeStream write chunks
        // without ever persisting the hash list).
        if (!_caches.ContainsKey(sessionId))
        {
            _caches[sessionId] = new SessionChunkCache
            {
                SessionId = sessionId,
                ChunkHashes = [],
                SavedAt = DateTime.UtcNow
            };
        }
        else
        {
            // Bump the LRU timestamp on every write — a session that's
            // actively being written to is also being read from.
            _caches[sessionId].SavedAt = DateTime.UtcNow;
        }

        // At-write check: if this write would push us over cap, evict first.
        // The chunk we're about to write is counted toward the budget, so
        // we trigger eviction when (used + newChunk) > max, leaving room.
        var projected = Interlocked.Read(ref _usedBytes) + chunkData.Length;
        if (projected > _maxBytes)
        {
            // Evict until under (cap - 2*newChunk) so we don't thrash on
            // every write. The 2x guard keeps headroom for the next write
            // of the same size.
            var lowWater = _maxBytes - (chunkData.Length * 2);
            if (lowWater < 0) lowWater = _maxBytes / 2;
            EvictUntilUnderBytes(lowWater);
        }

        await File.WriteAllBytesAsync(chunkPath, chunkData, ct);
        Interlocked.Add(ref _usedBytes, chunkData.Length);
    }

    public void SaveChunkData(string sessionId, string hash, byte[] chunkData)
    {
        if (_disposed || chunkData is null || chunkData.Length == 0) return;

        var chunkPath = ChunkPath(sessionId, hash);

        if (!_caches.ContainsKey(sessionId))
        {
            _caches[sessionId] = new SessionChunkCache
            {
                SessionId = sessionId,
                ChunkHashes = [],
                SavedAt = DateTime.UtcNow
            };
        }
        else
        {
            _caches[sessionId].SavedAt = DateTime.UtcNow;
        }

        var projected = Interlocked.Read(ref _usedBytes) + chunkData.Length;
        if (projected > _maxBytes)
        {
            var lowWater = _maxBytes - (chunkData.Length * 2);
            if (lowWater < 0) lowWater = _maxBytes / 2;
            EvictUntilUnderBytes(lowWater);
        }

        File.WriteAllBytes(chunkPath, chunkData);
        Interlocked.Add(ref _usedBytes, chunkData.Length);
    }

    public async Task<byte[]?> GetChunkDataAsync(string sessionId, string hash, CancellationToken ct)
    {
        if (_disposed) return null;
        var chunkPath = ChunkPath(sessionId, hash);
        if (!File.Exists(chunkPath)) return null;
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
        if (cache is null) return [];

        _caches[sessionId] = cache;
        return cache.ChunkHashes;
    }

    public bool HasCachedHashes(string sessionId)
    {
        if (_disposed) return false;
        if (_caches.ContainsKey(sessionId)) return true;
        return File.Exists(CachePath(sessionId));
    }

    public bool HasChunkData(string sessionId, string hash)
    {
        if (_disposed) return false;
        return File.Exists(ChunkPath(sessionId, hash));
    }

    public Task ClearAsync(string sessionId)
    {
        if (_disposed) return Task.CompletedTask;

        _caches.TryRemove(sessionId, out _);

        var path = CachePath(sessionId);
        if (File.Exists(path)) File.Delete(path);

        // Drop all per-session chunk files; subtract their sizes from the byte counter.
        var safeSessionId = SafeName(sessionId);
        foreach (var file in _cacheDir.EnumerateFiles($"{safeSessionId}.*"))
        {
            if (file.Name.EndsWith(".chunks.json")) continue;
            try
            {
                Interlocked.Add(ref _usedBytes, -file.Length);
                file.Delete();
            }
            catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    public Task<int> EvictLRUAsync()
    {
        if (_disposed) return Task.FromResult(0);
        var evicted = EvictUntilUnderBytes((long)(_maxBytes * 0.8));
        return Task.FromResult(evicted);
    }

    /// <summary>
    /// Forcibly evict LRU sessions until the L1 is under <paramref name="targetBytes"/>.
    /// Used by SaveChunkDataAsync (at-write check) and the periodic background sweep.
    /// Returns the number of sessions evicted.
    /// </summary>
    private int EvictUntilUnderBytes(long targetBytes)
    {
        if (_disposed) return 0;
        var evicted = 0;
        while (Interlocked.Read(ref _usedBytes) > targetBytes && _caches.Count > 0)
        {
            var oldest = _caches.Values
                .OrderBy(c => c.SavedAt)
                .FirstOrDefault();
            if (oldest is null) break;

            _caches.TryRemove(oldest.SessionId, out _);
            // Subtract the bytes that ClearAsync would drop, and delete the files
            // (avoiding the extra EnumerateFiles scan in ClearAsync).
            var safe = SafeName(oldest.SessionId);
            var path = CachePath(oldest.SessionId);
            if (File.Exists(path)) File.Delete(path);
            foreach (var file in _cacheDir.EnumerateFiles($"{safe}.*"))
            {
                if (file.Name.EndsWith(".chunks.json")) continue;
                try
                {
                    Interlocked.Add(ref _usedBytes, -file.Length);
                    file.Delete();
                }
                catch { /* ignore */ }
            }
            evicted++;
        }
        if (evicted > 0)
            ChunkCacheMetrics.L1EvictedSessions.Inc(evicted);
        return evicted;
    }

    private string CachePath(string sessionId)
        => Path.Combine(_cacheDir.FullName, $"{SafeName(sessionId)}.chunks.json");

    private string ChunkPath(string sessionId, string hash)
        => Path.Combine(_cacheDir.FullName, $"{SafeName(sessionId)}.{hash}");

    private static string SafeName(string sessionId)
        => sessionId.Replace('/', '_').Replace('\\', '_');

    private void RebuildFromDisk()
    {
        _caches.Clear();
        long total = 0;
        foreach (var file in _cacheDir.EnumerateFiles())
        {
            if (file.Name.EndsWith(".chunks.json"))
            {
                try
                {
                    var json = File.ReadAllText(file.FullName);
                    var cache = JsonSerializer.Deserialize<SessionChunkCache>(json);
                    if (cache != null) _caches[cache.SessionId] = cache;
                }
                catch { /* corrupt index; ignore */ }
            }
            else
            {
                total += file.Length;
            }
        }
        Interlocked.Exchange(ref _usedBytes, total);
    }

    public void Dispose()
    {
        _disposed = true;
        _caches.Clear();
    }

    internal sealed class SessionChunkCache
    {
        public string SessionId { get; set; } = "";
        public List<string> ChunkHashes { get; set; } = [];
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    }
}
