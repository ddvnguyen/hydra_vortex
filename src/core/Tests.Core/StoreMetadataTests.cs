using Hydra.Core;

namespace Tests.Core;

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
        cleanup.CommandText =
            "DELETE FROM session_chunks; DELETE FROM kv_manifests; DELETE FROM sessions; DELETE FROM chunks";
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
    public async Task UpsertManifest_WithUnregisteredChunk_SelfRegisters_NoFkViolation()
    {
        // Regression for #138: a chunk referenced by a manifest but NOT pre-registered in the
        // `chunks` table (e.g. resident on disk but absent from PG) must not violate
        // session_chunks_hash_fkey — UpsertManifestAsync upserts chunks in-tx before session_chunks.
        var chunks = new List<ChunkRef>
        {
            new(0, "unreg_chunk_a", 1024),
            new(1, "unreg_chunk_b", 2048),
        };

        Assert.False(await _meta!.HasChunkAsync("unreg_chunk_a"));
        Assert.False(await _meta.HasChunkAsync("unreg_chunk_b"));

        // Must NOT throw (previously threw 23503 FK violation).
        await _meta.UpsertManifestAsync("sess_unreg", 7, 3072, chunks);

        // Chunks were self-registered, and the manifest round-trips.
        Assert.True(await _meta.HasChunkAsync("unreg_chunk_a"));
        Assert.True(await _meta.HasChunkAsync("unreg_chunk_b"));
        var loaded = await _meta.GetManifestAsync("sess_unreg");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Chunks.Count);
        Assert.Equal("unreg_chunk_a", loaded.Chunks[0].Hash);
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
    public async Task GcStaleSessions_RemovesStaleSessionsAndTheirChunks()
    {
        // Retention GC must evict stale saved-KV sessions (and the chunks they
        // reference) from tmpfs + SSD backup + PG, while fresh sessions survive.
        var chunksDir = new DirectoryInfo(Path.Combine(_storeDir.FullName, "retention-chunks"));
        var backupDir = new DirectoryInfo(Path.Combine(_storeDir.FullName, "retention-backup"));
        chunksDir.Create();
        backupDir.Create();

        // Fresh session (recently updated): must survive the GC.
        await _meta!.RegisterChunkAsync("fresh_chunk", 128);
        await _meta.UpsertManifestAsync("sess_fresh", 0, 128,
            [new ChunkRef(0, "fresh_chunk", 128)]);

        // Stale session (updated 2h ago) referencing chunks that are also
        // backed up on the SSD dir: must be evicted, including its files.
        await _meta.RegisterChunkAsync("stale_chunk_a", 64);
        await _meta.RegisterChunkAsync("stale_chunk_b", 128);
        await _meta.UpsertManifestAsync("sess_stale", 10, 192,
            [new ChunkRef(0, "stale_chunk_a", 64), new ChunkRef(1, "stale_chunk_b", 128)]);
        await _meta.MarkBackedUpAsync("stale_chunk_a", "/backup/stale_chunk_a");
        await File.WriteAllBytesAsync(Path.Combine(chunksDir.FullName, "stale_chunk_a"), new byte[64]);
        await File.WriteAllBytesAsync(Path.Combine(chunksDir.FullName, "stale_chunk_b"), new byte[128]);
        await File.WriteAllBytesAsync(Path.Combine(backupDir.FullName, "stale_chunk_a"), new byte[64]);

        // Age the sessions: stale = 2h old, fresh = 1min old.
        await using (var conn = await _meta.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE sessions SET updated_at = now() - interval '2 hours'
                WHERE session_id = 'sess_stale';
                UPDATE sessions SET updated_at = now() - interval '1 minute'
                WHERE session_id = 'sess_fresh';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var removed = await _meta.GcStaleSessionsAsync(TimeSpan.FromHours(1), chunksDir, backupDir);

        // Stale session gone: manifest, chunk rows, tmpfs files, backup file.
        Assert.True(removed >= 2);
        Assert.Null(await _meta.GetManifestAsync("sess_stale"));
        Assert.False(await _meta.HasChunkAsync("stale_chunk_a"));
        Assert.False(await _meta.HasChunkAsync("stale_chunk_b"));
        Assert.False(File.Exists(Path.Combine(chunksDir.FullName, "stale_chunk_a")));
        Assert.False(File.Exists(Path.Combine(chunksDir.FullName, "stale_chunk_b")));
        Assert.False(File.Exists(Path.Combine(backupDir.FullName, "stale_chunk_a")));

        // Fresh session and its chunk survive.
        Assert.NotNull(await _meta.GetManifestAsync("sess_fresh"));
        Assert.True(await _meta.HasChunkAsync("fresh_chunk"));
    }

    [Fact]
    public async Task KvMarkBackedUp_And_GetUnbackedKv_RoundTrip()
    {
        // A .kv blob written via OpCode.Put never registers a PG row; the
        // write-behind discovers it by enumerating the StoreDir root. Before
        // marking, it must appear unbacked; after KvMarkBackedUpAsync it must not.
        const string sid = "sess_meta_kv";
        var kvPath = Path.Combine(_storeDir.FullName, $"{sid}.kv");
        await File.WriteAllBytesAsync(kvPath, new byte[2048]);

        var unbacked = await _meta!.GetUnbackedKvAsync(_storeDir, 100);
        Assert.Contains(unbacked, u => u.SessionId == sid && u.Size == 2048);

        await _meta.KvMarkBackedUpAsync(sid, Path.Combine("/backup", $"{sid}.kv"), 2048);

        var after = await _meta.GetUnbackedKvAsync(_storeDir, 100);
        Assert.DoesNotContain(after, u => u.SessionId == sid);

        // Idempotent re-mark must not throw.
        await _meta.KvMarkBackedUpAsync(sid, Path.Combine("/backup", $"{sid}.kv"), 2048);
    }

    [Fact]
    public async Task GetUnbackedKv_IgnoresBackedUpAndMissingFiles()
    {
        // Backed-up file is excluded from the drain set.
        const string backed = "sess_meta_backed";
        var backedPath = Path.Combine(_storeDir.FullName, $"{backed}.kv");
        await File.WriteAllBytesAsync(backedPath, new byte[512]);
        await _meta!.KvMarkBackedUpAsync(backed, Path.Combine("/backup", $"{backed}.kv"), 512);

        // Non-sess_ prefix + subdir entries are not per-session manifests.
        var prefixDir = new DirectoryInfo(Path.Combine(_storeDir.FullName, "prefix"));
        prefixDir.Create();
        await File.WriteAllBytesAsync(Path.Combine(prefixDir.FullName, "abc.kv"), new byte[256]);

        var unbacked = await _meta.GetUnbackedKvAsync(_storeDir, 100);
        Assert.DoesNotContain(unbacked, u => u.SessionId == backed);
        Assert.DoesNotContain(unbacked, u => u.SessionId == "abc");
    }

    [Fact]
    public async Task GetFreeableBackedUpChunkHashes_RespectsRecency()
    {
        // Backed-up chunk referenced ONLY by a stale session → freeable from RAM.
        const string staleChunk = "freeable_stale_meta";
        await _meta!.RegisterChunkAsync(staleChunk, 128);
        await _meta.UpsertManifestAsync("sess_meta_stale", 0, 128,
            [new ChunkRef(0, staleChunk, 128)]);
        await _meta.MarkBackedUpAsync(staleChunk, "/backup/freeable_stale_meta");

        // Backed-up chunk referenced by a recent session → must stay on RAM.
        const string recentChunk = "freeable_recent_meta";
        await _meta.RegisterChunkAsync(recentChunk, 128);
        await _meta.UpsertManifestAsync("sess_meta_recent", 0, 128,
            [new ChunkRef(0, recentChunk, 128)]);
        await _meta.MarkBackedUpAsync(recentChunk, "/backup/freeable_recent_meta");

        await using (var conn = await _meta.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE sessions SET updated_at = now() - interval '2 hours'
                WHERE session_id = 'sess_meta_stale';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var freeable = await _meta.GetFreeableBackedUpChunkHashesAsync(TimeSpan.FromHours(1), 100);
        Assert.Contains(staleChunk, freeable);
        Assert.DoesNotContain(recentChunk, freeable);
    }

    [Fact]
    public async Task GetFreeableBackedUpKv_RespectsRecency()
    {
        // .kv backed up for a session that went stale → freeable from RAM.
        const string staleSid = "sess_meta_kv_stale";
        await _meta!.KvMarkBackedUpAsync(staleSid, "/backup/sess_meta_kv_stale.kv", 512);
        await _meta.UpsertManifestAsync(staleSid, 5, 512, [new ChunkRef(0, "kv_stale_chunk", 512)]);
        await _meta.RegisterChunkAsync("kv_stale_chunk", 512);

        // .kv backed up for a recently-active session → kept on RAM.
        const string recentSid = "sess_meta_kv_recent";
        await _meta.KvMarkBackedUpAsync(recentSid, "/backup/sess_meta_kv_recent.kv", 512);
        await _meta.UpsertManifestAsync(recentSid, 5, 512, [new ChunkRef(0, "kv_recent_chunk", 512)]);
        await _meta.RegisterChunkAsync("kv_recent_chunk", 512);

        await using (var conn = await _meta.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE sessions SET updated_at = now() - interval '2 hours'
                WHERE session_id = 'sess_meta_kv_stale';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var freeable = await _meta.GetFreeableBackedUpKvAsync(TimeSpan.FromHours(1), 100);
        Assert.Contains(staleSid, freeable);
        Assert.DoesNotContain(recentSid, freeable);
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
