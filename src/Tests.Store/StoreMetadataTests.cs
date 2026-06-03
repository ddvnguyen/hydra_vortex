using Hydra.Store;

namespace Tests.Store;

/// <summary>
/// Tests for StoreMetadata (PostgreSQL-backed metadata layer).
/// Requires Postgres accessible via HYDRA_STORE_PG_CONN or the default connection string.
/// Starts with: docker compose up -d postgres  (from infra/)
/// </summary>
[Collection("SerializedPG")]
public sealed class StoreMetadataTests : IAsyncLifetime
{
    private readonly DirectoryInfo _storeDir;
    private StoreMetadata? _meta;

    public StoreMetadataTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-meta-test-{Guid.NewGuid():N}"));
        if (!_storeDir.Exists)
            _storeDir.Create();
    }

    public async Task InitializeAsync()
    {
        var connStr = Environment.GetEnvironmentVariable("HYDRA_STORE_PG_CONN")
            ?? "Host=localhost;Database=hydra_store;Username=hydra;Password=hydra";

        _meta = new StoreMetadata(connStr);
        await _meta.EnsureSchemaAsync(CancellationToken.None);
        // Clean any leftover test data from previous runs
        await using var conn = await _meta.DataSource.OpenConnectionAsync();
        await using var cleanup = conn.CreateCommand();
        cleanup.CommandText = "DELETE FROM session_chunks; DELETE FROM sessions; DELETE FROM chunks";
        await cleanup.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_meta is not null)
            await _meta.DisposeAsync();
        if (_storeDir.Exists)
            _storeDir.Delete(recursive: true);
    }

    [Fact]
    public async Task EnsureSchema_CreatesTables()
    {
        await using var conn = await _meta!.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN ('sessions','chunks','session_chunks')
            ORDER BY table_name
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        Assert.Contains("chunks", tables);
        Assert.Contains("session_chunks", tables);
        Assert.Contains("sessions", tables);
    }

    [Fact]
    public async Task RegisterAndHasChunk_RoundTrip()
    {
        await _meta!.RegisterChunkAsync("abc123", 1024);
        Assert.True(await _meta.HasChunkAsync("abc123"));
    }

    [Fact]
    public async Task HasChunk_NonExistent_ReturnsFalse()
    {
        Assert.False(await _meta!.HasChunkAsync("nonexistent_hash"));
    }

    [Fact]
    public async Task RegisterChunk_Duplicate_DoesNotThrow()
    {
        await _meta!.RegisterChunkAsync("dup_hash", 512);
        await _meta.RegisterChunkAsync("dup_hash", 512);
        Assert.True(await _meta.HasChunkAsync("dup_hash"));
    }

    [Fact]
    public async Task UpsertAndGetManifest_RoundTrip()
    {
        var chunks = new List<ChunkRef>
        {
            new(0, "chunk_a", 1024),
            new(1, "chunk_b", 2048),
        };

        await _meta!.RegisterChunkAsync("chunk_a", 1024);
        await _meta.RegisterChunkAsync("chunk_b", 2048);
        await _meta.UpsertManifestAsync("sess_test", 42, 3072, chunks);

        var loaded = await _meta.GetManifestAsync("sess_test");
        Assert.NotNull(loaded);
        Assert.Equal("sess_test", loaded.SessionId);
        Assert.Equal(42, loaded.NPast);
        Assert.Equal(3072, loaded.TotalSize);
        Assert.Equal(2, loaded.Chunks.Count);
        Assert.Equal("chunk_a", loaded.Chunks[0].Hash);
        Assert.Equal("chunk_b", loaded.Chunks[1].Hash);
    }

    [Fact]
    public async Task GetManifest_NonExistent_ReturnsNull()
    {
        var loaded = await _meta!.GetManifestAsync("nonexistent_session");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SetAndGetNPast_RoundTrip()
    {
        await _meta!.SetNPastAsync("sess_npast", 100);
        var np = await _meta.GetNPastAsync("sess_npast");
        Assert.Equal(100, np);
    }

    [Fact]
    public async Task GetNPast_NonExistent_ReturnsNull()
    {
        var np = await _meta!.GetNPastAsync("nonexistent");
        Assert.Null(np);
    }

    [Fact]
    public async Task MarkAndGetUnbackedChunks()
    {
        await _meta!.RegisterChunkAsync("backup_test_a", 512);
        await _meta.RegisterChunkAsync("backup_test_b", 1024);

        var unbacked = await _meta.GetUnbackedChunksAsync(100);
        var hashSet = unbacked.Select(x => x.Hash).ToHashSet();
        Assert.Contains("backup_test_a", hashSet);
        Assert.Contains("backup_test_b", hashSet);
    }

    [Fact]
    public async Task MarkBackedUp_RemovesFromUnbacked()
    {
        await _meta!.RegisterChunkAsync("mark_backed", 256);
        await _meta.MarkBackedUpAsync("mark_backed", "/nvme/chunks/mark_backed");

        var unbacked = await _meta.GetUnbackedChunksAsync(100);
        Assert.DoesNotContain("mark_backed", unbacked.Select(x => x.Hash));
    }

    [Fact]
    public async Task GetRecentSessions_ReturnsOrdered()
    {
        await _meta!.RegisterChunkAsync("recent_a_chunk", 100);
        await _meta.RegisterChunkAsync("recent_b_chunk", 200);
        await _meta.UpsertManifestAsync("sess_recent_a", 10, 100,
            [new ChunkRef(0, "recent_a_chunk", 100)]);
        await Task.Delay(100);
        await _meta.UpsertManifestAsync("sess_recent_b", 20, 200,
            [new ChunkRef(0, "recent_b_chunk", 200)]);

        var recent = await _meta.GetRecentSessionIdsAsync(5);
        Assert.Contains("sess_recent_a", recent);
        Assert.Contains("sess_recent_b", recent);
    }

    [Fact]
    public async Task GcOrphanChunks_RemovesUnreferenced()
    {
        // Create a chunk referenced by a manifest
        await _meta!.RegisterChunkAsync("gc_ref_chunk", 128);
        await _meta.UpsertManifestAsync("sess_gc", 0, 128,
            [new ChunkRef(0, "gc_ref_chunk", 128)]);

        // Create an unreferenced chunk (orphan) with a past timestamp
        await _meta.RegisterChunkAsync("gc_orphan", 64);
        await using var ageConn = await _meta.DataSource.OpenConnectionAsync();
        await using var ageCmd = ageConn.CreateCommand();
        ageCmd.CommandText =
            "UPDATE chunks SET created_at = now() - interval '5 minutes' WHERE hash = 'gc_orphan'";
        await ageCmd.ExecuteNonQueryAsync();
        var orphanPath = Path.Combine(_storeDir.FullName, "gc_orphan");
        await File.WriteAllBytesAsync(orphanPath, new byte[64]);

        // GC should remove the orphan
        var removed = await _meta.GcOrphanChunksAsync(_storeDir);
        Assert.True(removed >= 1);
        Assert.False(File.Exists(orphanPath));

        // Referenced chunk should remain in PG
        Assert.True(await _meta.HasChunkAsync("gc_ref_chunk"));
    }

    [Fact]
    public async Task ReconcileBoot_RemovesUnbackedRowsMissingFromDisk()
    {
        // Register a chunk in PG that is unbacked and has no file
        await _meta!.RegisterChunkAsync("reconcile_test", 64);

        // Register another chunk with a real file
        await _meta.RegisterChunkAsync("reconcile_keep", 64);
        var keepPath = Path.Combine(_storeDir.FullName, "reconcile_keep");
        await File.WriteAllBytesAsync(keepPath, new byte[64]);

        await _meta.ReconcileBootAsync(_storeDir);

        Assert.False(await _meta.HasChunkAsync("reconcile_test"));
        Assert.True(await _meta.HasChunkAsync("reconcile_keep"));
    }
}
