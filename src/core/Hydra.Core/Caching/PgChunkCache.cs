using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Prometheus;

namespace Hydra.Core.Caching;

/// <summary>
/// L2 chunk cache: content-addressed, PG-backed, byte-budgeted.
///
/// Eviction score per chunk = (now - created_at) × (now - last_used) / use_count.
/// High score = evict first. The / use_count term makes heavily-read chunks
/// sticky even if old, and never-read chunks highly evictable.
///
/// Byte accounting: the cap is enforced against the **logical** size
/// (sum of \`size\` column), not \`pg_total_relation_size\`. Plain
/// \`VACUUM (ANALYZE)\` does not shrink the heap — only \`VACUUM FULL\` or
/// trailing-page truncation does. Driving the GC off physical size
/// would make the loop chase an unreachable target and empty the cache.
/// The \`l2_bytes\` gauge reports physical size for the dashboard, but
/// the GC uses logical size, kept in memory and updated on every Put/Delete.
///
/// Two GC triggers:
///   * Soft: a Timer fires every <see cref="ChunkCacheConfig.L2GcIntervalSeconds"/>
///     (default 300 s) and runs EnforceCapacityAsync if the table is over the
///     configured low-water (cap × 0.9 by default).
///   * Hard: every successful PutAsync checks the in-memory logical size;
///     if it crosses the cap, the GC runs synchronously (bounded) so a burst
///     write cannot push the L2 past the cap between soft ticks.
///
/// VACUUM cadence: after a meaningful eviction (>10 % of the low-water
/// freed), and at most once per <see cref="ChunkCacheConfig.L2VacuumIntervalSeconds"/>
/// (default 1 h). VACUUM reclaims dead tuples for planner stats; it does
/// not shrink the heap, so we don't depend on it for byte accounting.
/// </summary>
public sealed class PgChunkCache : IContentChunkStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ChunkCacheConfig _cfg;
    private readonly Timer _gcTimer;
    private readonly SemaphoreSlim _gcLock = new(1, 1);
    private long _logicalSizeBytes;     // sum of `size` column, updated in-memory on Put/Delete
    private long _lastPhysicalSizeBytes; // refreshed by the soft sweep + boot probe
    private DateTime _lastVacuumUtc = DateTime.MinValue;
    private bool _disposed;

    public PgChunkCache(ChunkCacheConfig cfg, ILogger? log = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        var builder = new NpgsqlDataSourceBuilder(cfg.L2PgConn);
        _dataSource = builder.Build();

        // Publish the configured cap and high-water to the dashboard.
        ChunkCacheMetrics.L2SizeCap.Set(cfg.L2MaxBytes);
        ChunkCacheMetrics.L2SizeHighWater.Set((long)(cfg.L2MaxBytes * cfg.L2LowWater));

        // Soft GC: scan and evict if over low-water, every cfg.L2GcIntervalSeconds.
        _gcTimer = new Timer(
            _ => _ = SafeSweepAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(cfg.L2GcIntervalSeconds),
            period: TimeSpan.FromSeconds(cfg.L2GcIntervalSeconds));
    }

    /// <summary>Synchronous boot probe — returns false if PG is unreachable.</summary>
    public bool TryProbe()
    {
        if (_disposed) return false;
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task PutAsync(string hash, byte[] data, CancellationToken ct)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(hash)) throw new ArgumentException("hash is required", nameof(hash));
        if (data is null || data.Length == 0) return;

        // If a previous row exists, capture its size so the logical-size
        // counter reflects the delta, not the new gross size. The first
        // writer wins the age; subsequent writers only update last_used
        // and use_count (the chunk is the same content-addressed blob).
        const string sql = """
            INSERT INTO chunk_data_l2 (hash, bytes, size)
            VALUES (@hash, @bytes, @size)
            ON CONFLICT (hash) DO UPDATE
                SET bytes      = EXCLUDED.bytes,
                    size       = EXCLUDED.size,
                    last_used  = now(),
                    use_count  = chunk_data_l2.use_count + 1
            RETURNING (xmax = 0) AS inserted
            """;
        bool inserted;
        await using (var conn = await _dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("hash", hash);
            cmd.Parameters.Add(new NpgsqlParameter("bytes", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = data });
            cmd.Parameters.AddWithValue("size", data.Length);
            var result = await cmd.ExecuteScalarAsync(ct);
            inserted = result is bool b && b;
        }
        ChunkCacheMetrics.L2Puts.Inc();
        if (inserted)
        {
            Interlocked.Add(ref _logicalSizeBytes, data.Length);
        }
        ChunkCacheMetrics.L2Bytes.Set(Interlocked.Read(ref _logicalSizeBytes));

        // Hard trigger: if the post-write size exceeds the cap, evict synchronously.
        if (Interlocked.Read(ref _logicalSizeBytes) > _cfg.L2MaxBytes)
        {
            var target = (long)(_cfg.L2MaxBytes * _cfg.L2LowWater);
            await EnforceCapacityAsync(target, _cfg.L2BatchSize, ct);
        }
    }

    public async Task<byte[]?> GetAsync(string hash, CancellationToken ct)
    {
        if (_disposed) return null;
        if (string.IsNullOrEmpty(hash)) return null;

        try
        {
            // Single round-trip: SELECT then UPDATE in one CTE, FOR UPDATE so a
            // concurrent GC cannot delete the row between the read and the
            // last_used bump.
            const string sql = """
                WITH got AS (
                    SELECT bytes FROM chunk_data_l2 WHERE hash = @hash FOR UPDATE
                ),
                upd AS (
                    UPDATE chunk_data_l2
                    SET    last_used = now(), use_count = use_count + 1
                    WHERE  hash = @hash
                    RETURNING 1
                )
                SELECT bytes FROM got;
                """;
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("hash", hash);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null || result is DBNull)
            {
                ChunkCacheMetrics.L2Misses.Inc();
                return null;
            }
            ChunkCacheMetrics.L2Hits.Inc();
            return (byte[])result;
        }
        catch
        {
            // Best-effort: a PG outage must not break the read path. The
            // facade will fall through to the Store.
            ChunkCacheMetrics.L2Misses.Inc();
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string hash, CancellationToken ct)
    {
        if (_disposed) return false;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1 FROM chunk_data_l2 WHERE hash = @hash", conn);
            cmd.Parameters.AddWithValue("hash", hash);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Logical size (sum of `size` column) — used for budget enforcement.</summary>
    public Task<long> GetLogicalUsedBytesAsync(CancellationToken ct)
        => Task.FromResult(Interlocked.Read(ref _logicalSizeBytes));

    /// <summary>Physical size (pg_total_relation_size, includes heap + TOAST + indexes).
    /// Used for the dashboard gauge only; do not use this for budget decisions.</summary>
    public async Task<long> GetUsedBytesAsync(CancellationToken ct)
    {
        if (_disposed) return 0;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT pg_total_relation_size('chunk_data_l2')", conn);
            var v = await cmd.ExecuteScalarAsync(ct);
            var bytes = v is long l ? l : (long)(v ?? 0L);
            _lastPhysicalSizeBytes = bytes;
            ChunkCacheMetrics.L2BytesPhysical.Set(bytes);
            return bytes;
        }
        catch
        {
            return _lastPhysicalSizeBytes;
        }
    }

    public async Task<int> EnforceCapacityAsync(long targetBytes, int batchSize, CancellationToken ct)
    {
        if (_disposed) return 0;
        if (!await _gcLock.WaitAsync(0, ct)) return 0;
        var sw = Stopwatch.StartNew();
        try
        {
            // Drive off the in-memory logical size (the budget we care about),
            // not the physical size. The first iteration may need to sync
            // the in-memory counter from the DB on a cold start.
            if (_logicalSizeBytes == 0)
            {
                await SyncLogicalSizeAsync(ct);
            }
            var startBytes = Interlocked.Read(ref _logicalSizeBytes);
            if (startBytes <= targetBytes) return 0;

            var bytesToFree = startBytes - targetBytes;
            long freed = 0;
            int evicted = 0;
            int rounds = 0;
            const int maxRounds = 100;

            while (freed < bytesToFree && rounds < maxRounds)
            {
                const string pickSql = """
                    WITH ranked AS (
                        SELECT hash,
                               EXTRACT(EPOCH FROM (now() - created_at))::bigint AS age_s,
                               EXTRACT(EPOCH FROM (now() - last_used))::bigint AS idle_s,
                               use_count
                        FROM chunk_data_l2
                    ),
                    scored AS (
                        SELECT hash,
                               ((age_s + 1) * (idle_s + 1))
                                 / GREATEST(use_count, 1)::bigint AS score
                        FROM ranked
                    )
                    SELECT hash FROM scored
                    ORDER BY score DESC
                    LIMIT @batch
                    """;
                var victims = new List<string>(batchSize);
                await using (var conn = await _dataSource.OpenConnectionAsync(ct))
                await using (var pickCmd = new NpgsqlCommand(pickSql, conn))
                {
                    pickCmd.Parameters.AddWithValue("batch", batchSize);
                    await using var rdr = await pickCmd.ExecuteReaderAsync(ct);
                    while (await rdr.ReadAsync(ct))
                        victims.Add(rdr.GetString(0));
                }
                if (victims.Count == 0) break;

                const string delSql = """
                    DELETE FROM chunk_data_l2
                    WHERE hash = ANY(@hashes)
                    RETURNING size
                    """;
                long roundFreed = 0;
                await using (var conn = await _dataSource.OpenConnectionAsync(ct))
                await using (var delCmd = new NpgsqlCommand(delSql, conn))
                {
                    delCmd.Parameters.AddWithValue("hashes", victims.ToArray());
                    await using var rdr = await delCmd.ExecuteReaderAsync(ct);
                    while (await rdr.ReadAsync(ct))
                        roundFreed += rdr.GetInt32(0);
                }

                // Decrement the in-memory counter by the actual deleted bytes.
                Interlocked.Add(ref _logicalSizeBytes, -roundFreed);
                ChunkCacheMetrics.L2Bytes.Set(Interlocked.Read(ref _logicalSizeBytes));

                freed += roundFreed;
                evicted += victims.Count;
                rounds++;
            }

            if (evicted > 0)
            {
                ChunkCacheMetrics.L2EvictedChunks.Inc(evicted);
                ChunkCacheMetrics.L2EvictedBytes.Inc(freed);

                // VACUUM if meaningful eviction happened AND cooldown elapsed.
                // VACUUM reclaims dead tuples for planner stats; it does not
                // shrink the heap (so byte accounting does not depend on it).
                var now = DateTime.UtcNow;
                var freedRatio = (double)freed / Math.Max(1, startBytes);
                if (freedRatio > 0.10 && (now - _lastVacuumUtc).TotalSeconds > _cfg.L2VacuumIntervalSeconds)
                {
                    try
                    {
                        await using var conn = await _dataSource.OpenConnectionAsync(ct);
                        await using var vac = new NpgsqlCommand("VACUUM (ANALYZE) chunk_data_l2", conn);
                        await vac.ExecuteNonQueryAsync(ct);
                        _lastVacuumUtc = now;
                    }
                    catch
                    {
                        // VACUUM failures are non-fatal; the next eviction will retry.
                    }
                }
            }

            // Refresh the dashboard gauges.
            ChunkCacheMetrics.L2Bytes.Set(Interlocked.Read(ref _logicalSizeBytes));
            ChunkCacheMetrics.L2GcRuns.Inc();
            ChunkCacheMetrics.L2GcDuration.Observe(sw.Elapsed.TotalSeconds);
            await RefreshOldestAgeAsync(ct);
            return evicted;
        }
        finally
        {
            _gcLock.Release();
        }
    }

    private async Task SyncLogicalSizeAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(size), 0) FROM chunk_data_l2", conn);
        var v = await cmd.ExecuteScalarAsync(ct);
        var sum = v is long l ? l : (long)(v ?? 0L);
        Interlocked.Exchange(ref _logicalSizeBytes, sum);
        ChunkCacheMetrics.L2Bytes.Set(sum);
    }

    private async Task RefreshOldestAgeAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT EXTRACT(EPOCH FROM (now() - MIN(created_at))) FROM chunk_data_l2", conn);
            var v = await cmd.ExecuteScalarAsync(ct);
            if (v is double d)
                ChunkCacheMetrics.L2OldestAge.Set(d);
            else if (v is decimal dec)
                ChunkCacheMetrics.L2OldestAge.Set((double)dec);
        }
        catch
        {
            // Dashboard-only metric; non-fatal on failure.
        }
    }

    private async Task SafeSweepAsync()
    {
        try
        {
            if (_disposed) return;
            // Soft sweep: refresh physical-size gauge (every tick), and run
            // GC if the logical size is over low-water.
            await GetUsedBytesAsync(CancellationToken.None);
            await RefreshOldestAgeAsync(CancellationToken.None);
            if (_logicalSizeBytes == 0)
                await SyncLogicalSizeAsync(CancellationToken.None);
            var target = (long)(_cfg.L2MaxBytes * _cfg.L2LowWater);
            if (Interlocked.Read(ref _logicalSizeBytes) > target)
                await EnforceCapacityAsync(target, _cfg.L2BatchSize, CancellationToken.None);
        }
        catch
        {
            // Soft GC failures are silent; the next tick retries.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gcTimer.Dispose();
        _gcLock.Dispose();
        _dataSource.Dispose();
    }
}
