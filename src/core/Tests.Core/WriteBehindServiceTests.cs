using Hydra.Core;

namespace Tests.Core;

[Collection("SerializedPG")]
public sealed class WriteBehindServiceTests : IAsyncLifetime
{
    private readonly DirectoryInfo _storeDir;
    private readonly DirectoryInfo _backupDir;
    private StoreMetadata? _meta;

    public WriteBehindServiceTests()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-wb-test-{Guid.NewGuid():N}"));
        _backupDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-wb-backup-{Guid.NewGuid():N}"));
        _storeDir.Create();
    }

    public async Task InitializeAsync()
    {
        var connStr = Environment.GetEnvironmentVariable("HYDRA_STORE_PG_CONN")
            ?? "Host=localhost;Database=hydra_store;Username=hydra;Password=hydra";

        _meta = new StoreMetadata(connStr);
        await _meta.EnsureSchemaAsync(CancellationToken.None);

        await using var cleanConn = await _meta.DataSource.OpenConnectionAsync();
        await using var cleanCmd = cleanConn.CreateCommand();
        cleanCmd.CommandText =
            "DELETE FROM session_chunks; DELETE FROM kv_manifests; DELETE FROM sessions; DELETE FROM chunks";
        await cleanCmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_meta is not null)
            await _meta.DisposeAsync();

        if (_storeDir.Exists)
            _storeDir.Delete(recursive: true);
        if (_backupDir.Exists)
            _backupDir.Delete(recursive: true);
    }

    [Fact]
    public async Task WriteBehind_CopiesUnbackedChunksToBackupDir()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);

        var data = "hello world"u8.ToArray();
        const string hash = "testhash1";
        await _meta!.RegisterChunkAsync(hash, data.Length);
        await chunkStore.StoreChunkAsync(hash, data);

        var unbacked = await _meta.GetUnbackedChunksAsync(100);
        Assert.Contains(unbacked, u => u.Hash == hash);

        var backupChunksDir = new DirectoryInfo(Path.Combine(_backupDir.FullName, "chunks"));
        backupChunksDir.Create();
        await wb.FlushUnbackedAsync(backupChunksDir, CancellationToken.None);

        var dstPath = Path.Combine(backupChunksDir.FullName, hash);
        Assert.True(File.Exists(dstPath), "Backup file should exist");

        var contents = await File.ReadAllBytesAsync(dstPath);
        Assert.Equal(data, contents);

        await using var checkConn = await _meta.DataSource.OpenConnectionAsync();
        await using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT backed_up, nvme_path FROM chunks WHERE hash = @hash";
        checkCmd.Parameters.AddWithValue("hash", hash);
        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(dstPath, reader.GetString(1));
    }

    [Fact]
    public async Task WriteBehind_SkipsMissingSourceChunks()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);

        const string hash = "missingchunk";
        await _meta!.RegisterChunkAsync(hash, 100);

        var unbacked = await _meta.GetUnbackedChunksAsync(100);
        Assert.Contains(unbacked, u => u.Hash == hash);

        var backupChunksDir = new DirectoryInfo(Path.Combine(_backupDir.FullName, "chunks"));
        backupChunksDir.Create();
        await wb.FlushUnbackedAsync(backupChunksDir, CancellationToken.None);

        var stillUnbacked = await _meta.GetUnbackedChunksAsync(100);
        Assert.Contains(stillUnbacked, u => u.Hash == hash);
    }

    [Fact]
    public async Task WriteBehind_HandlesAlreadyExistsRace()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);

        var data = "race data"u8.ToArray();
        const string hash = "racechunk";
        await _meta!.RegisterChunkAsync(hash, data.Length);
        await chunkStore.StoreChunkAsync(hash, data);

        var backupChunksDir = new DirectoryInfo(Path.Combine(_backupDir.FullName, "chunks"));
        backupChunksDir.Create();
        var dstPath = Path.Combine(backupChunksDir.FullName, hash);
        await File.WriteAllBytesAsync(dstPath, "stale"u8.ToArray());

        await wb.FlushUnbackedAsync(backupChunksDir, CancellationToken.None);

        await using var checkConn = await _meta.DataSource.OpenConnectionAsync();
        await using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT backed_up FROM chunks WHERE hash = @hash";
        checkCmd.Parameters.AddWithValue("hash", hash);
        var result = await checkCmd.ExecuteScalarAsync();
        Assert.Equal(true, result);
    }

    [Fact]
    public async Task WriteBehind_CopiesUnbackedKvToBackupRoot()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);

        var data = "kv manifest blob"u8.ToArray();
        const string sid = "sess_kvdrain";
        var srcPath = Path.Combine(_storeDir.FullName, $"{sid}.kv");
        await File.WriteAllBytesAsync(srcPath, data);

        var backupRootDir = new DirectoryInfo(_backupDir.FullName);
        backupRootDir.Create();
        await wb.FlushUnbackedKvAsync(backupRootDir, CancellationToken.None);

        var dstPath = Path.Combine(_backupDir.FullName, $"{sid}.kv");
        Assert.True(File.Exists(dstPath), "Backup .kv should exist");
        Assert.Equal(data, await File.ReadAllBytesAsync(dstPath));

        await using var checkConn = await _meta!.DataSource.OpenConnectionAsync();
        await using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT backed_up, nvme_path FROM kv_manifests WHERE session_id = @sid";
        checkCmd.Parameters.AddWithValue("sid", sid);
        await using var reader = await checkCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(dstPath, reader.GetString(1));
    }

    [Fact]
    public async Task WriteBehind_SkipsMissingSourceKv()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);

        const string sid = "sess_kvmissing";
        // .kv has no PG row until it's backed up — a missing source is simply
        // skipped by the disk-driven enumeration; nothing may crash or be marked.
        var backupRootDir = new DirectoryInfo(_backupDir.FullName);
        backupRootDir.Create();
        await wb.FlushUnbackedKvAsync(backupRootDir, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_backupDir.FullName, $"{sid}.kv")));
        await using var checkConn = await _meta!.DataSource.OpenConnectionAsync();
        await using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM kv_manifests WHERE session_id = @sid";
        checkCmd.Parameters.AddWithValue("sid", sid);
        var count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task WriteBehind_KvAlreadyExistsRace_MarksBackedUp()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);

        const string sid = "sess_kvrace";
        var srcPath = Path.Combine(_storeDir.FullName, $"{sid}.kv");
        await File.WriteAllBytesAsync(srcPath, "fresh"u8.ToArray());

        var backupRootDir = new DirectoryInfo(_backupDir.FullName);
        backupRootDir.Create();
        var dstPath = Path.Combine(_backupDir.FullName, $"{sid}.kv");
        await File.WriteAllBytesAsync(dstPath, "stale"u8.ToArray());

        await wb.FlushUnbackedKvAsync(backupRootDir, CancellationToken.None);

        await using var checkConn = await _meta!.DataSource.OpenConnectionAsync();
        await using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT backed_up FROM kv_manifests WHERE session_id = @sid";
        checkCmd.Parameters.AddWithValue("sid", sid);
        var result = await checkCmd.ExecuteScalarAsync();
        Assert.Equal(true, result);
    }

    [Fact]
    public async Task WriteBehind_FreeAfterBackup_EvictsStaleChunksKeepsRecent()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
            RamKeepRecentHours = 1,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);
        var chunksDir = chunkStore.ChunksDirectory;

        // Stale session (2h ago) referencing a backed-up chunk → RAM copy evictable.
        const string staleChunk = "freeable_stale";
        await _meta!.RegisterChunkAsync(staleChunk, 128);
        await _meta.UpsertManifestAsync("sess_stale_free", 0, 128,
            [new ChunkRef(0, staleChunk, 128)]);
        await _meta.MarkBackedUpAsync(staleChunk, Path.Combine(_backupDir.FullName, "chunks", staleChunk));
        await chunkStore.StoreChunkAsync(staleChunk, new byte[128]);

        // Recent session (updated now) referencing a backed-up chunk → RAM copy kept.
        const string recentChunk = "freeable_recent";
        await _meta.RegisterChunkAsync(recentChunk, 128);
        await _meta.UpsertManifestAsync("sess_recent_free", 0, 128,
            [new ChunkRef(0, recentChunk, 128)]);
        await _meta.MarkBackedUpAsync(recentChunk, Path.Combine(_backupDir.FullName, "chunks", recentChunk));
        await chunkStore.StoreChunkAsync(recentChunk, new byte[128]);

        // Age the stale session beyond the keep-recent window.
        await using (var conn = await _meta.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE sessions SET updated_at = now() - interval '2 hours'
                WHERE session_id = 'sess_stale_free';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var freed = await wb.FreeBackedUpFromRamAsync(CancellationToken.None);

        Assert.True(freed >= 1, $"expected ≥1 freed, got {freed}");
        Assert.False(File.Exists(Path.Combine(chunksDir.FullName, staleChunk)),
            "Stale chunk RAM copy should be evicted after backup");
        Assert.False(chunkStore.HasChunk(staleChunk),
            "Stale chunk should leave the ChunkStore index");
        Assert.True(File.Exists(Path.Combine(chunksDir.FullName, recentChunk)),
            "Recent session's chunk RAM copy must be kept");
    }

    [Fact]
    public async Task WriteBehind_FreeAfterBackup_KeepsFilesWhenDisabled()
    {
        var cfg = new StoreConfig
        {
            StoreDir = _storeDir.FullName,
            BackupDir = _backupDir.FullName,
            RamKeepRecentHours = 0,
        };
        var chunkStore = new ChunkStore(_storeDir);
        var wb = new WriteBehindService(cfg, _meta!, chunkStore);
        var chunksDir = chunkStore.ChunksDirectory;

        const string chunk = "free_disabled";
        await _meta!.RegisterChunkAsync(chunk, 128);
        await _meta.UpsertManifestAsync("sess_free_disabled", 0, 128,
            [new ChunkRef(0, chunk, 128)]);
        await _meta.MarkBackedUpAsync(chunk, Path.Combine(_backupDir.FullName, "chunks", chunk));
        await chunkStore.StoreChunkAsync(chunk, new byte[128]);

        await using (var conn = await _meta.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE sessions SET updated_at = now() - interval '2 hours'
                WHERE session_id = 'sess_free_disabled';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var freed = await wb.FreeBackedUpFromRamAsync(CancellationToken.None);

        Assert.Equal(0, freed);
        Assert.True(File.Exists(Path.Combine(chunksDir.FullName, chunk)),
            "0 keep-recent hours must disable free-after-backup (backward compatible)");
    }
}
