using System.Text.Json;
using Hydra.Store;

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
    public void StoreChunk_NewChunk_ReturnsTrue()
    {
        var data = "hello chunk"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        var isNew = _store.StoreChunk(hash, data);

        Assert.True(isNew);
        Assert.True(_store.HasChunk(hash));
    }

    [Fact]
    public void StoreChunk_Duplicate_ReturnsFalse()
    {
        var data = "dedup me"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        _store.StoreChunk(hash, data);
        var isNew = _store.StoreChunk(hash, data);

        Assert.False(isNew);
    }

    [Fact]
    public void HasChunk_Existing_ReturnsTrue()
    {
        var data = "check me"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        _store.StoreChunk(hash, data);
        Assert.True(_store.HasChunk(hash));
    }

    [Fact]
    public void HasChunk_NonExistent_ReturnsFalse()
    {
        Assert.False(_store.HasChunk("nonexistenthash"));
    }

    [Fact]
    public void GetChunkPath_Existing_ReturnsPath()
    {
        var data = "path test"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        _store.StoreChunk(hash, data);

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
    public async Task SaveAndLoadManifest_RoundTrip()
    {
        var chunks = new List<ChunkRef>
        {
            new(0, "hash_a", 1024),
            new(1, "hash_b", 1024),
        };

        var manifest = new Manifest("sess_test", 1, 100, 2048, chunks, DateTime.UtcNow);

        await _store.SaveManifestAsync("sess_test", manifest, CancellationToken.None);

        var loaded = await _store.LoadManifestAsync("sess_test", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("sess_test", loaded.SessionId);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(100, loaded.NPast);
        Assert.Equal(2048, loaded.TotalSize);
        Assert.Equal(2, loaded.Chunks.Count);
    }

    [Fact]
    public async Task LoadManifest_NonExistent_ReturnsNull()
    {
        var loaded = await _store.LoadManifestAsync("ghost_session", CancellationToken.None);
        Assert.Null(loaded);
    }

    [Fact]
    public void DiffPlan_CorrectMissing()
    {
        var chunks = new List<ChunkRef>
        {
            new(0, "hash_a", 1024),
            new(1, "hash_b", 1024),
            new(2, "hash_c", 1024),
        };

        var manifest = new Manifest("sess_test", 1, 0, 3072, chunks, DateTime.UtcNow);
        var clientHashes = new List<string> { "hash_a", "hash_c" };

        var missing = _store.DiffPlan(manifest, clientHashes);

        Assert.Single(missing);
        Assert.Contains("hash_b", missing);
    }

    [Fact]
    public async Task GC_RemovesOrphanChunks()
    {
        // Store a chunk, store a manifest that references it
        var data1 = "chunk one"u8.ToArray();
        var hash1 = ChunkEngine.ComputeHash(data1);
        _store.StoreChunk(hash1, data1);

        var data2 = "orphan chunk"u8.ToArray();
        var hash2 = ChunkEngine.ComputeHash(data2);
        _store.StoreChunk(hash2, data2);

        var manifest = new Manifest("sess_active", 1, 0, data1.Length,
            [new ChunkRef(0, hash1, data1.Length)], DateTime.UtcNow);
        await _store.SaveManifestAsync("sess_active", manifest, CancellationToken.None);

        // GC with only the active session - should remove hash2
        var removed = _store.GC(["sess_active"]);

        Assert.True(removed >= 1);
        Assert.False(_store.HasChunk(hash2));
        Assert.True(_store.HasChunk(hash1));
    }

    [Fact]
    public async Task GC_RemovesUnreferencedManifests()
    {
        var data = "garbage"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);
        _store.StoreChunk(hash, data);

        var manifest = new Manifest("sess_gone", 1, 0, data.Length,
            [new ChunkRef(0, hash, data.Length)], DateTime.UtcNow);
        await _store.SaveManifestAsync("sess_gone", manifest, CancellationToken.None);

        // GC without keeping sess_gone
        var removed = _store.GC([]);

        Assert.False(_store.HasChunk(hash));
    }

    [Fact]
    public async Task StartupRebuild_PopulatesIndex()
    {
        var data = "rebuild test"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        // Manually create chunk file
        var chunksDir = _store.ChunksDirectory;
        await File.WriteAllBytesAsync(Path.Combine(chunksDir.FullName, hash), data);

        // Create new ChunkStore instance to trigger rebuild
        var freshStore = new ChunkStore(_storeDir);

        Assert.True(freshStore.HasChunk(hash));
        Assert.Equal(1, freshStore.KnownChunkCount);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectValues()
    {
        var data = "stats test"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);
        _store.StoreChunk(hash, data);

        var stats = await _store.GetStatsAsync(CancellationToken.None);

        Assert.Equal(1, stats.TotalChunks);
        Assert.Equal(0, stats.ManifestCount);
        Assert.True(stats.TotalBytes > 0);
    }

    [Fact]
    public void StoreChunk_WritesToCorrectDirectory()
    {
        var data = "path check"u8.ToArray();
        var hash = ChunkEngine.ComputeHash(data);

        _store.StoreChunk(hash, data);

        var expectedPath = Path.Combine(_store.ChunksDirectory.FullName, hash);
        Assert.True(File.Exists(expectedPath));
    }
}
