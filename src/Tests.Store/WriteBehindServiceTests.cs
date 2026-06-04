using Hydra.Store;

namespace Tests.Store;

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
            ?? "Host=localhost;Database=hydra_test;Username=hydra;Password=hydra";

        _meta = new StoreMetadata(connStr);
        await _meta.EnsureSchemaAsync(CancellationToken.None);

        await using var cleanConn = await _meta.DataSource.OpenConnectionAsync();
        await using var cleanCmd = cleanConn.CreateCommand();
        cleanCmd.CommandText = "DELETE FROM session_chunks; DELETE FROM sessions; DELETE FROM chunks";
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
}
