using Hydra.Core;

namespace Tests.Store;

public sealed class ChunkStoreTests : IDisposable
{
    private readonly DirectoryInfo _storeDir;
    private readonly ChunkStore _store;

    public ChunkStoreTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-chunk-test-{Guid.NewGuid():N}"));
        _store = new ChunkStore(_storeDir);
    }

    public void Dispose()
    {
        if (_storeDir.Exists)
            _storeDir.Delete(recursive: true);
    }

    [Fact]
    public async Task StoreChunk_NewChunk_ReturnsTrue()
    {
        var data = "hello chunk"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        var isNew = await _store.StoreChunkAsync(hash, data);

        Assert.True(isNew);
        Assert.True(_store.HasChunk(hash));
    }

    [Fact]
    public async Task StoreChunk_Duplicate_ReturnsFalse()
    {
        var data = "dedup me"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        await _store.StoreChunkAsync(hash, data);
        var isNew = await _store.StoreChunkAsync(hash, data);

        Assert.False(isNew);
    }

    [Fact]
    public async Task HasChunk_Existing_ReturnsTrue()
    {
        var data = "check me"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        await _store.StoreChunkAsync(hash, data);
        Assert.True(_store.HasChunk(hash));
    }

    [Fact]
    public void HasChunk_NonExistent_ReturnsFalse()
    {
        Assert.False(_store.HasChunk("nonexistenthash"));
    }

    [Fact]
    public async Task GetChunkPath_Existing_ReturnsPath()
    {
        var data = "path test"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        await _store.StoreChunkAsync(hash, data);

        var path = _store.GetChunkPath(hash);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetChunkPath_NonExistent_ReturnsNull()
    {
        var path = _store.GetChunkPath("nonexistenthash");
        Assert.Null(path);
    }

    [Fact]
    public async Task StartupRebuild_PopulatesIndex()
    {
        var data = "rebuild test"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        var chunksDir = _store.ChunksDirectory;
        await File.WriteAllBytesAsync(Path.Combine(chunksDir.FullName, hash), data);

        var freshStore = new ChunkStore(_storeDir);

        Assert.True(freshStore.HasChunk(hash));
        Assert.Equal(1, freshStore.KnownChunkCount);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectValues()
    {
        var data = "stats test"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);
        await _store.StoreChunkAsync(hash, data);

        var stats = await _store.GetStatsAsync(CancellationToken.None);

        Assert.Equal(1, stats.TotalChunks);
        Assert.True(stats.TotalBytes > 0);
    }

    [Fact]
    public async Task StoreChunk_WritesToCorrectDirectory()
    {
        var data = "path check"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        await _store.StoreChunkAsync(hash, data);

        var expectedPath = Path.Combine(_store.ChunksDirectory.FullName, hash);
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task StoreChunk_ConcurrentSameHash_OnlyOneStored()
    {
        var data = "concurrent race"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        // Fire multiple concurrent stores of the same hash
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _store.StoreChunkAsync(hash, data.ToArray()))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // TryAdd guarantees exactly one call returns true
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(9, results.Count(r => !r));
        Assert.True(_store.HasChunk(hash));
    }

    [Fact]
    public async Task StoreChunk_AtomicWrite_NoTempFileLeftBehind()
    {
        var data = "atomic chunk"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        await _store.StoreChunkAsync(hash, data);

        var tmpPath = Path.Combine(_store.ChunksDirectory.FullName, hash + ".tmp");
        Assert.False(File.Exists(tmpPath), "temp file should be cleaned up");
        Assert.True(_store.HasChunk(hash));
    }
}
