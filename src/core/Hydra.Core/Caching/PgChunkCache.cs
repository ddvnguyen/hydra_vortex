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
/// Two GC triggers:
///   * Soft: a Timer fires every <see cref="ChunkCacheConfig.L2GcIntervalSeconds"/>
///     (default 300 s) and runs EnforceCapacityAsync if the table is over the
///     configured low-water (cap × 0.9 by default).
///   * Hard: every successful PutAsync checks the post-write size; if it
///     crosses the cap, the GC runs synchronously (bounded) so a burst
///     write cannot push the L2 past the cap between soft ticks.
///
/// VACUUM cadence: after a meaningful eviction (>10 % of the low-water
/// freed), and at most once per <see cref="ChunkCacheConfig.L2VacuumIntervalSeconds"/>
/// (default 1 h). VACUUM reclaims TOAST space and updates planner stats.
/// </summary>
public sealed class PgChunkCache : IContentChunkStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ChunkCacheConfig _cfg;
    private readonly Timer _gcTimer;
    private readonly SemaphoreSlim _gcLock = new(1, 1);
    private DateTime _lastVacuumUtc = DateTime.MinValue;
    private long _evictedSinceLastVacuum;
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
        // The first tick fires after one full interval to let the rest of the
        // system come up.
        _gcTimer = new Timer(
            _ => _ = SafeSweepAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(cfg.L2GcIntervalSeconds),
            period: TimeSpan.FromSeconds(cfg.L2GcIntervalSeconds));
    }

    public async Task PutAsync(string hash, byte[] data, CancellationToken ct)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(hash)) throw new ArgumentException("hash is required", nameof(hash));
        if (data is null || data.Length == 0) return;

        const string sql = """
            INSERT INTO chunk_data_l2 (hash, bytes, size)
            VALUES (@hash, @bytes, @size)
            ON CONFLICT (hash) DO UPDATE
                SET bytes      = EXCLUDED.bytes,
                    size       = EXCLUDED.size,
                    last_used  = now(),
                    use_count  = chunk_data_l2.use_count + 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("hash", hash);
        cmd.Parameters.Add(new NpgsqlParameter("bytes", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = data });
        cmd.Parameters.AddWithValue("size", data.Length);
        await cmd.ExecuteNonQueryAsync(ct);
        ChunkCacheMetrics.L2Puts.Inc();

        // Hard trigger: if the post-write size exceeds the cap, evict synchronously.
        // The check is cheap (a single pg_total_relation_size query) and bounds
        // the burst case between soft GC ticks.
        await HardTriggerIfOverCapAsync(ct);
    }

    public async Task<byte[]?> GetAsync(string hash, CancellationToken ct)
    {
        if (_disposed) return null;
        if (string.IsNullOrEmpty(hash)) return null;

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

    public async Task<bool> ExistsAsync(string hash, CancellationToken ct)
    {
        if (_disposed) return false;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM chunk_data_l2 WHERE hash = @hash", conn);
        cmd.Parameters.AddWithValue("hash", hash);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<long> GetUsedBytesAsync(CancellationToken ct)
    {
        if (_disposed) return 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_total_relation_size('chunk_data_l2')", conn);
        var v = await cmd.ExecuteScalarAsync(ct);
        var bytes = v is long l ? l : (long)(v ?? 0L);
        ChunkCacheMetrics.L2Bytes.Set(bytes);
        return bytes;
    }

    public async Task<int> EnforceCapacityAsync(long targetBytes, int batchSize, CancellationToken ct)
    {
        if (_disposed) return 0;
        // Serialize GC across soft and hard triggers so a burst of writes
        // doesn't fan out into a hundred concurrent DELETEs.
        if (!await _gcLock.WaitAsync(0, ct)) return 0;
        var sw = Stopwatch.StartNew();
        try
        {
            var startBytes = await GetUsedBytesAsync(ct);
            if (startBytes <= targetBytes) return 0;

            var bytesToFree = startBytes - targetBytes;
            long freed = 0;
            int evicted = 0;
            int rounds = 0;
            const int maxRounds = 100;

            while (freed < bytesToFree && rounds < maxRounds)
            {
                // One round: pick the top `batchSize` chunks by eviction score
                // (age × idle / use_count, with +1 smoothing for very young
                // chunks to avoid divide-by-zero and 0-score for new chunks).
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

                freed += roundFreed;
                evicted += victims.Count;
                rounds++;
            }

            if (evicted > 0)
            {
                ChunkCacheMetrics.L2EvictedChunks.Inc(evicted);
                ChunkCacheMetrics.L2EvictedBytes.Inc(freed);
                _evictedSinceLastVacuum += freed;

                // VACUUM if meaningful eviction happened AND cooldown elapsed.
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
                        _evictedSinceLastVacuum = 0;
                    }
                    catch
                    {
                        // VACUUM failures are non-fatal; the next eviction will retry.
                    }
                }
            }

            // Refresh the gauge so the dashboard reflects post-GC size.
            await GetUsedBytesAsync(ct);
            ChunkCacheMetrics.L2GcRuns.Inc();
            ChunkCacheMetrics.L2GcDuration.Observe(sw.Elapsed.TotalSeconds);
            return evicted;
        }
        finally
        {
            _gcLock.Release();
        }
    }

    private async Task HardTriggerIfOverCapAsync(CancellationToken ct)
    {
        var used = await GetUsedBytesAsync(ct);
        if (used <= _cfg.L2MaxBytes) return;
        // Drop to 80 % of cap so the next write doesn't immediately retrigger.
        var target = (long)(_cfg.L2MaxBytes * _cfg.L2LowWater);
        await EnforceCapacityAsync(target, _cfg.L2BatchSize, ct);
    }

    private async Task SafeSweepAsync()
    {
        try
        {
            if (_disposed) return;
            var target = (long)(_cfg.L2MaxBytes * _cfg.L2LowWater);
            var used = await GetUsedBytesAsync(CancellationToken.None);
            if (used > target)
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
