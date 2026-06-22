using Hydra.Core;
using Hydra.Core.Caching;

namespace Tests.Core.Caching;

public sealed class LocalFsChunkCacheTests : IDisposable
{
    private readonly string _cacheDir;

    public LocalFsChunkCacheTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"hydra-l1-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsChunk()
    {
        var cache = new LocalFsChunkCache(_cacheDir, maxBytes: 1024 * 1024);
        var data = new byte[4096];
        Random.Shared.NextBytes(data);

        await cache.SaveChunkDataAsync("ses_1", "hashA", data, CancellationToken.None);
        var read = await cache.GetChunkDataAsync("ses_1", "hashA", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(data, read);
        Assert.Equal(data.Length, cache.L1UsedBytes);
    }

    [Fact]
    public async Task Get_MissingHash_ReturnsNull()
    {
        var cache = new LocalFsChunkCache(_cacheDir, maxBytes: 1024 * 1024);
        var read = await cache.GetChunkDataAsync("ses_1", "nope", CancellationToken.None);
        Assert.Null(read);
    }

    [Fact]
    public async Task SaveChunkData_AtWriteCheck_EvictsOldestWhenOverCap()
    {
        // Cap is 10 KB. Write 4 chunks of 4 KB each. The 3rd write should
        // trigger eviction of the oldest session.
        var cache = new LocalFsChunkCache(_cacheDir, maxBytes: 10 * 1024);
        var chunk = new byte[4 * 1024];
        Random.Shared.NextBytes(chunk);

        await cache.SaveChunkDataAsync("ses_old", "h1", chunk, CancellationToken.None);
        await cache.SaveChunkDataAsync("ses_old", "h2", chunk, CancellationToken.None);
        await cache.SaveChunkDataAsync("ses_new", "h3", chunk, CancellationToken.None);
        await cache.SaveChunkDataAsync("ses_new", "h4", chunk, CancellationToken.None);

        // ses_old's data should have been evicted.
        Assert.False(cache.HasChunkData("ses_old", "h1"));
        Assert.False(cache.HasChunkData("ses_old", "h2"));
        Assert.True(cache.HasChunkData("ses_new", "h3"));
        Assert.True(cache.HasChunkData("ses_new", "h4"));
        Assert.True(cache.L1UsedBytes <= 10 * 1024);
    }

    [Fact]
    public async Task EvictLRUAsync_DropsBelowLowWater()
    {
        var cache = new LocalFsChunkCache(_cacheDir, maxBytes: 10 * 1024);
        var chunk = new byte[3 * 1024];
        Random.Shared.NextBytes(chunk);

        for (int i = 0; i < 5; i++)
            await cache.SaveChunkDataAsync($"ses_{i}", $"h{i}", chunk, CancellationToken.None);

        var evicted = await cache.EvictLRUAsync();
        Assert.True(evicted >= 1);
        Assert.True(cache.L1UsedBytes <= (long)(10 * 1024 * 0.8) + 3 * 1024);
    }

    [Fact]
    public async Task ClearAsync_DropsSessionAndFreesBytes()
    {
        var cache = new LocalFsChunkCache(_cacheDir, maxBytes: 1024 * 1024);
        var chunk = new byte[2048];
        Random.Shared.NextBytes(chunk);

        await cache.SaveHashesAsync("ses_1", new List<string> { "h1" }, CancellationToken.None);
        await cache.SaveChunkDataAsync("ses_1", "h1", chunk, CancellationToken.None);
        var before = cache.L1UsedBytes;
        Assert.True(before >= 2048);

        await cache.ClearAsync("ses_1");
        Assert.False(cache.HasCachedHashes("ses_1"));
        Assert.False(cache.HasChunkData("ses_1", "h1"));
        Assert.True(cache.L1UsedBytes < before);
    }

    [Fact]
    public void Constructor_NegativeMaxBytes_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalFsChunkCache(_cacheDir, 0));
    }

    [Fact]
    public void LocalChunkCache_BackCompat_WrapsL1()
    {
        var facade = new LocalChunkCache(_cacheDir, maxSessions: 50);
        Assert.IsType<LocalFsChunkCache>(facade.GetType()
            .GetField("_l1", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(facade));
    }

    [Fact]
    public async Task SaveChunkData_RewriteSameHash_DoesNotInflateByteCounter()
    {
        var cache = new LocalFsChunkCache(_cacheDir, maxBytes: 1024 * 1024);
        var chunk = new byte[4096];
        Random.Shared.NextBytes(chunk);

        await cache.SaveChunkDataAsync("ses_1", "hA", chunk, CancellationToken.None);
        var after1 = cache.L1UsedBytes;

        // Rewrite the same (session, hash) — counter should not double.
        await cache.SaveChunkDataAsync("ses_1", "hA", chunk, CancellationToken.None);
        var after2 = cache.L1UsedBytes;

        Assert.Equal(after1, after2);
        Assert.Equal(4096L, after2);
    }

    [Fact]
    public async Task RebuildFromDisk_AddsOrphanChunkToLRU()
    {
        // Pre-create the cache dir (the L1 ctor would do this, but we
        // want to drop a file in BEFORE the ctor runs so the ctor's
        // RebuildFromDisk sees it).
        Directory.CreateDirectory(_cacheDir);
        var data = new byte[2048];
        Random.Shared.NextBytes(data);
        var hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        // Write a chunk file without an index — this is the "orphan".
        File.WriteAllBytes(Path.Combine(_cacheDir, $"orphan_session.{hash}"), data);

        // Cap at 1 KB so the orphan (2 KB) is already over the 80% low-water
        // mark, forcing eviction.
        var cache = new LocalFsChunkCache(_cacheDir, maxBytes: 1024);
        // The orphan should be counted toward _usedBytes.
        Assert.True(cache.L1UsedBytes >= 2048);

        // And evictable: the byte counter should drop when the cache evicts
        // (the orphan is registered in _caches during RebuildFromDisk).
        var evicted = await cache.EvictLRUAsync();
        Assert.True(evicted >= 1);
        Assert.False(cache.HasChunkData("orphan_session", hash));
    }
}
