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
}
