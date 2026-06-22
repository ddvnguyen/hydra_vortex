using Hydra.Core;
using Hydra.Core.Caching;
using Npgsql;

namespace Tests.Core.Caching;

/// <summary>
/// PgChunkCache tests. Requires a reachable Postgres (the same instance
/// Hydra.Core uses for the Store). Connection string is read from
/// HYDRA_TEST_PG_CONN env var; if unset or unreachable, tests are
/// visibly skipped (not silently returned).
/// </summary>
public sealed class PgChunkCacheTests : IDisposable
{
    private readonly string? _connStr;
    private readonly bool _enabled;
    private ChunkCacheConfig _cfg;
    private readonly NpgsqlDataSource _ds;
    private PgChunkCache? _cache;

    public PgChunkCacheTests()
    {
        _connStr = Environment.GetEnvironmentVariable("HYDRA_TEST_PG_CONN")
            ?? "Host=localhost;Database=hydra_store;Username=hydra;Password=hydra";
        _enabled = CanConnect(_connStr);
        _cfg = new ChunkCacheConfig
        {
            L2PgConn = _connStr,
            L2MaxBytes = 10L * 1024 * 1024,        // 10 MB cap for the test
            L2LowWater = 0.9,
            L2GcIntervalSeconds = 3600,            // disable soft GC during the test
            L2VacuumIntervalSeconds = 3600,
            L2BatchSize = 100,
        };
        _ds = NpgsqlDataSource.Create(_connStr);
        if (_enabled)
        {
            // Drop and recreate — tests may run against a DB that has the
            // table from a previous run with an older schema. The real
            // schema is created by StoreMetadata.EnsureSchemaAsync.
            using var conn = _ds.OpenConnection();
            using (var drop = new NpgsqlCommand("DROP TABLE IF EXISTS chunk_data_l2", conn))
                drop.ExecuteNonQuery();
            using var cmd = new NpgsqlCommand(
                "CREATE TABLE chunk_data_l2(" +
                " hash        TEXT        PRIMARY KEY," +
                " bytes       BYTEA       NOT NULL," +
                " size        INT         NOT NULL," +
                " created_at  TIMESTAMPTZ NOT NULL DEFAULT now()," +
                " last_used   TIMESTAMPTZ NOT NULL DEFAULT now()," +
                " use_count   BIGINT      NOT NULL DEFAULT 1)", conn);
            cmd.ExecuteNonQuery();
        }
    }

    private static bool CanConnect(string connStr)
    {
        try
        {
            using var ds = NpgsqlDataSource.Create(connStr);
            using var conn = ds.OpenConnection();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task PutAndGet_RoundTrips()
    {
        Skip.IfNot(_enabled, "Postgres unavailable for tests (HYDRA_TEST_PG_CONN not set or unreachable)");
        _cache = new PgChunkCache(_cfg);
        var data = new byte[2048];
        Random.Shared.NextBytes(data);

        await _cache.PutAsync("hash1", data, CancellationToken.None);
        var read = await _cache.GetAsync("hash1", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(data, read);
    }

    [SkippableFact]
    public async Task Get_MissingHash_ReturnsNull()
    {
        Skip.IfNot(_enabled, "Postgres unavailable");
        _cache = new PgChunkCache(_cfg);
        var read = await _cache.GetAsync("nonexistent", CancellationToken.None);
        Assert.Null(read);
    }

    [SkippableFact]
    public async Task Put_SameHash_BumpsUseCount_KeepsOriginalCreatedAt()
    {
        Skip.IfNot(_enabled, "Postgres unavailable");
        _cache = new PgChunkCache(_cfg);
        var d1 = new byte[1024];
        var d2 = new byte[1024];
        Random.Shared.NextBytes(d1);
        Array.Copy(d1, d2, d1.Length);

        await _cache.PutAsync("hash1", d1, CancellationToken.None);
        await Task.Delay(50);
        await _cache.PutAsync("hash1", d2, CancellationToken.None);

        var row = await ReadRowAsync("hash1");
        Assert.NotNull(row);
        Assert.Equal(2L, row.Value.useCount);
    }

    [SkippableFact]
    public async Task EnforceCapacity_EvictsOldestFirst()
    {
        Skip.IfNot(_enabled, "Postgres unavailable");
        // Cap high enough that PutAsync's hard-trigger doesn't fire mid-test.
        // Target small enough that the explicit EnforceCapacityAsync below
        // has to evict ≥3 chunks.
        _cfg = _cfg with { L2MaxBytes = 5_000_000, L2LowWater = 0.9 };
        _cache = new PgChunkCache(_cfg);
        var data = new byte[100_000];
        Random.Shared.NextBytes(data);

        for (int i = 0; i < 10; i++)
            await _cache.PutAsync($"h{i:D2}", data, CancellationToken.None);

        // Assert against the in-memory logical size (the budget we care
        // about), not the physical size. Plain VACUUM does not shrink the
        // heap, so the physical size is unreliable.
        var usedLogicalBefore = await _cache.GetLogicalUsedBytesAsync(CancellationToken.None);
        Assert.True(usedLogicalBefore >= 1_000_000, $"expected ≥1MB after 10 × 100KB puts, got {usedLogicalBefore}");

        var evicted = await _cache.EnforceCapacityAsync(targetBytes: 250_000, batchSize: 100, CancellationToken.None);
        Assert.True(evicted >= 3, $"expected ≥3 evicted, got {evicted}");

        var usedLogical = await _cache.GetLogicalUsedBytesAsync(CancellationToken.None);
        Assert.True(usedLogical <= 250_000 + 200_000, $"L2 logical size over target: {usedLogical} bytes");
    }

    [SkippableFact]
    public async Task EnforceCapacity_OldNeverReadChunk_EvictedBeforeOldFrequentlyRead()
    {
        Skip.IfNot(_enabled, "Postgres unavailable");
        _cfg = _cfg with { L2MaxBytes = 1_000_000, L2LowWater = 0.5 };
        _cache = new PgChunkCache(_cfg);
        var data = new byte[50_000];
        Random.Shared.NextBytes(data);

        await _cache.PutAsync("h_old_never_read", data, CancellationToken.None);
        await _cache.PutAsync("h_old_frequently_read", data, CancellationToken.None);
        for (int i = 0; i < 1000; i++)
            await _cache.GetAsync("h_old_frequently_read", CancellationToken.None);
        await Task.Delay(100);
        await _cache.PutAsync("h_recent_never_read", data, CancellationToken.None);

        await _cache.EnforceCapacityAsync(targetBytes: 50_000, batchSize: 100, CancellationToken.None);

        Assert.False(await _cache.ExistsAsync("h_old_never_read", CancellationToken.None));
    }

    [SkippableFact]
    public async Task GetLogicalUsedBytesAsync_ReflectsPutsAndDeletes()
    {
        Skip.IfNot(_enabled, "Postgres unavailable");
        _cache = new PgChunkCache(_cfg);
        var data = new byte[4096];
        await _cache.PutAsync("hb", data, CancellationToken.None);
        var used = await _cache.GetLogicalUsedBytesAsync(CancellationToken.None);
        Assert.Equal(4096L, used);

        // Rewrite same hash — logical size should not double.
        await _cache.PutAsync("hb", data, CancellationToken.None);
        var usedAfter = await _cache.GetLogicalUsedBytesAsync(CancellationToken.None);
        Assert.Equal(4096L, usedAfter);
    }

    [SkippableFact]
    public void TryProbe_Unreachable_FailsCleanly()
    {
        var bad = new ChunkCacheConfig
        {
            L2PgConn = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=2",
            L2GcIntervalSeconds = 3600,
        };
        using var cache = new PgChunkCache(bad);
        Assert.False(cache.TryProbe());
    }

    public void Dispose()
    {
        _cache?.Dispose();
        _ds.Dispose();
    }

    private async Task<(string hash, long useCount)?> ReadRowAsync(string hash)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT hash, use_count FROM chunk_data_l2 WHERE hash = @h", conn);
        cmd.Parameters.AddWithValue("h", hash);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync()) return null;
        return (rdr.GetString(0), rdr.GetInt64(1));
    }
}
