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
    private readonly Timer _sweepTimer;

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
        // session index and the byte accounting. Orphan chunk files (chunks
        // written without a corresponding index, e.g. by ChunkHashTeeStream
        // on a session that was never SaveHashesAsync'd) are added to the
        // _caches dict so the LRU can find them.
        RebuildFromDisk();

        ChunkCacheMetrics.L1Bytes.Set(Interlocked.Read(ref _usedBytes));
        ChunkCacheMetrics.L1Chunks.Set(_cacheDir.EnumerateFiles().Count());

        // Periodic background sweep every 60 s. Independent of the L2's
        // soft sweep; this is L1-only and keeps the L1 under cap even if
        // the only write activity is reads (which still bump LRU timestamps
        // but don't trigger the at-write eviction path).
        _sweepTimer = new Timer(
            _ => SafeSweep(),
            state: null,
            dueTime: TimeSpan.FromSeconds(60),
            period: TimeSpan.FromSeconds(60));
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
            _caches[sessionId].SavedAt = DateTime.UtcNow;
        }

        // Subtract the old file size if this is a rewrite, so the byte
        // counter reflects the delta not the gross new size. Content-addressed
        // re-saves (same hash) should not inflate the budget.
        long oldSize = 0;
        if (File.Exists(chunkPath))
            oldSize = new FileInfo(chunkPath).Length;

        // At-write check: evict to 80% of cap when the new write would
        // push the L1 over cap. Using the 80% target (not maxBytes - chunkSize)
        // avoids thrashing near the cap.
        var projected = Interlocked.Read(ref _usedBytes) + chunkData.Length - oldSize;
        if (projected > _maxBytes)
        {
            var lowWater = (long)(_maxBytes * 0.8);
            EvictUntilUnderBytes(lowWater);
        }

        await File.WriteAllBytesAsync(chunkPath, chunkData, ct);
        Interlocked.Add(ref _usedBytes, chunkData.Length - oldSize);
        ChunkCacheMetrics.L1Bytes.Set(Interlocked.Read(ref _usedBytes));
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

        long oldSize = 0;
        if (File.Exists(chunkPath))
            oldSize = new FileInfo(chunkPath).Length;

        var projected = Interlocked.Read(ref _usedBytes) + chunkData.Length - oldSize;
        if (projected > _maxBytes)
        {
            var lowWater = (long)(_maxBytes * 0.8);
            EvictUntilUnderBytes(lowWater);
        }

        File.WriteAllBytes(chunkPath, chunkData);
        Interlocked.Add(ref _usedBytes, chunkData.Length - oldSize);
        ChunkCacheMetrics.L1Bytes.Set(Interlocked.Read(ref _usedBytes));
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
        ChunkCacheMetrics.L1Bytes.Set(Interlocked.Read(ref _usedBytes));
        return Task.CompletedTask;
    }

    public Task<int> EvictLRUAsync()
    {
        if (_disposed) return Task.FromResult(0);
        var lowWater = (long)(_maxBytes * 0.8);
        var evicted = EvictUntilUnderBytes(lowWater);
        return Task.FromResult(evicted);
    }

    /// <summary>
    /// Forcibly evict LRU sessions until the L1 is under <paramref name="targetBytes"/>.
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
        {
            ChunkCacheMetrics.L1EvictedSessions.Inc(evicted);
            ChunkCacheMetrics.L1EvictedBytes.Inc(Interlocked.Read(ref _usedBytes));
            ChunkCacheMetrics.L1Bytes.Set(Interlocked.Read(ref _usedBytes));
        }
        return evicted;
    }

    private void SafeSweep()
    {
        try
        {
            if (_disposed) return;
            var lowWater = (long)(_maxBytes * 0.8);
            EvictUntilUnderBytes(lowWater);
        }
        catch
        {
            // Best-effort; the next tick retries.
        }
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
        // First pass: read the index files (`.chunks.json`) and populate
        // _caches. Their SavedAt comes from the file's LastWriteTimeUtc
        // so a fresh restart preserves LRU ordering.
        foreach (var file in _cacheDir.EnumerateFiles())
        {
            if (file.Name.EndsWith(".chunks.json"))
            {
                try
                {
                    var json = File.ReadAllText(file.FullName);
                    var cache = JsonSerializer.Deserialize<SessionChunkCache>(json);
                    if (cache != null)
                    {
                        cache.SavedAt = file.LastWriteTimeUtc;
                        _caches[cache.SessionId] = cache;
                    }
                }
                catch { /* corrupt index; ignore */ }
            }
        }
        // Second pass: add the chunk data files to the byte counter, and
        // (for chunks with no index entry) register a synthetic session so
        // the LRU sweep can find them. Format: {safeSessionId}.{hash},
        // where hash is 64 hex chars; we split on the last `.` and check
        // the suffix is a 64-hex string.
        foreach (var file in _cacheDir.EnumerateFiles())
        {
            if (file.Name.EndsWith(".chunks.json")) continue;
            total += file.Length;

            var dot = file.Name.LastIndexOf('.');
            if (dot <= 0 || dot == file.Name.Length - 1) continue;
            var safeSession = file.Name.Substring(0, dot);
            var hashSuffix = file.Name.Substring(dot + 1);
            if (hashSuffix.Length != 64) continue;
            if (!IsAllHex(hashSuffix)) continue;
            // Round-trip: SafeName replaces / and \ with _. We can't fully
            // recover the original sessionId from the safe form (the
            // round-trip is lossy), so we key the synthetic entry on the
            // safe form. The save path will overwrite the index when a
            // real SaveHashesAsync lands.
            if (!_caches.ContainsKey(safeSession))
            {
                _caches[safeSession] = new SessionChunkCache
                {
                    SessionId = safeSession,
                    ChunkHashes = [],
                    SavedAt = file.LastWriteTimeUtc
                };
            }
        }
        Interlocked.Exchange(ref _usedBytes, total);
    }

    private static bool IsAllHex(string s)
    {
        foreach (var c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _sweepTimer.Dispose();
        _caches.Clear();
    }

    internal sealed class SessionChunkCache
    {
        public string SessionId { get; set; } = "";
        public List<string> ChunkHashes { get; set; } = [];
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    }
}
