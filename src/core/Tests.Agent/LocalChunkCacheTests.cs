using Hydra.Core;

namespace Tests.Agent;

public sealed class LocalChunkCacheTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly LocalChunkCache _cache;

    public LocalChunkCacheTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"hydra-cache-test-{Guid.NewGuid():N}");
        _cache = new LocalChunkCache(_cacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    [Fact]
    public async Task SaveAndLoadHashes_RoundTrip()
    {
        var hashes = new List<string> { "hash_a", "hash_b", "hash_c" };

        await _cache.SaveHashesAsync("sess_test", hashes, CancellationToken.None);

        var loaded = await _cache.LoadHashesAsync("sess_test", CancellationToken.None);
        Assert.Equal(hashes, loaded);
    }

    [Fact]
    public async Task LoadHashes_NonExistent_ReturnsEmpty()
    {
        var loaded = await _cache.LoadHashesAsync("ghost_session", CancellationToken.None);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task HasCachedHashes_Existing_ReturnsTrue()
    {
        await _cache.SaveHashesAsync("sess_abc", ["hash1"], CancellationToken.None);
        Assert.True(_cache.HasCachedHashes("sess_abc"));
    }

    [Fact]
    public void HasCachedHashes_NonExistent_ReturnsFalse()
    {
        Assert.False(_cache.HasCachedHashes("ghost_session"));
    }

    [Fact]
    public async Task Clear_RemovesCache()
    {
        await _cache.SaveHashesAsync("sess_del", ["hash_x"], CancellationToken.None);
        Assert.True(_cache.HasCachedHashes("sess_del"));

        await _cache.ClearAsync("sess_del");

        Assert.False(_cache.HasCachedHashes("sess_del"));
    }

    [Fact]
    public async Task SaveHashes_OverwritesPrevious()
    {
        await _cache.SaveHashesAsync("sess_upd", ["old"], CancellationToken.None);
        await _cache.SaveHashesAsync("sess_upd", ["new1", "new2"], CancellationToken.None);

        var loaded = await _cache.LoadHashesAsync("sess_upd", CancellationToken.None);
        Assert.Equal(2, loaded.Count);
        Assert.Contains("new1", loaded);
        Assert.Contains("new2", loaded);
    }

    [Fact]
    public async Task LoadHashes_FromDisk_WorksAcrossInstances()
    {
        await _cache.SaveHashesAsync("sess_disk", ["disk_hash"], CancellationToken.None);

        var freshCache = new LocalChunkCache(_cacheDir);
        var loaded = await freshCache.LoadHashesAsync("sess_disk", CancellationToken.None);

        Assert.Single(loaded);
        Assert.Contains("disk_hash", loaded);
    }

    [Fact]
    public async Task EvictLRU_RemovesOldest()
    {
        var smallCache = new LocalChunkCache(_cacheDir, maxSessions: 2);

        await smallCache.SaveHashesAsync("sess_a", ["a"], CancellationToken.None);
        await smallCache.SaveHashesAsync("sess_b", ["b"], CancellationToken.None);
        await smallCache.SaveHashesAsync("sess_c", ["c"], CancellationToken.None);

        var evicted = await smallCache.EvictLRUAsync();

        Assert.True(evicted >= 1);
        Assert.False(smallCache.HasCachedHashes("sess_a"));
    }

    [Fact]
    public async Task CachedSessionCount_ReturnsCorrect()
    {
        Assert.Equal(0, _cache.CachedSessionCount);

        await _cache.SaveHashesAsync("sess_1", ["h1"], CancellationToken.None);
        Assert.Equal(1, _cache.CachedSessionCount);

        await _cache.SaveHashesAsync("sess_2", ["h2"], CancellationToken.None);
        Assert.Equal(2, _cache.CachedSessionCount);

        await _cache.ClearAsync("sess_1");
        Assert.Equal(1, _cache.CachedSessionCount);
    }

    [Fact]
    public async Task SaveHashes_PersistsToDisk()
    {
        var hashes = new List<string> { "persist_hash" };
        await _cache.SaveHashesAsync("sess_persist", hashes, CancellationToken.None);

        var expectedFile = Path.Combine(_cacheDir, "sess_persist.chunks.json");
        Assert.True(File.Exists(expectedFile));

        var content = await File.ReadAllTextAsync(expectedFile);
        Assert.Contains("persist_hash", content);
    }
}
