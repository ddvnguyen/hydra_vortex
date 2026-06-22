using Hydra.Shared;
using Npgsql;

namespace Hydra.Core;

public sealed class StoreMetadata : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Serilog.ILogger _log = Serilog.Log.ForContext<StoreMetadata>();

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sessions(
            session_id  TEXT PRIMARY KEY,
            n_past      INT    NOT NULL DEFAULT 0,
            total_size  BIGINT NOT NULL DEFAULT 0,
            -- M-Perf.9 #289: model identity of the slot that built this KV cache.
            -- Nullable + back-compat default '' so the schema is additive: pre-#289
            -- sessions get empty model_* and the cross-model guard treats that
            -- as "skip" (no-op on restore).
            model_alias TEXT NOT NULL DEFAULT '',
            model_hash  TEXT NOT NULL DEFAULT '',
            model_path  TEXT NOT NULL DEFAULT '',
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at  TIMESTAMPTZ NOT NULL DEFAULT now());

        CREATE TABLE IF NOT EXISTS chunks(
            hash         TEXT PRIMARY KEY,
            size         INT  NOT NULL,
            backed_up    BOOLEAN NOT NULL DEFAULT false,
            nvme_path    TEXT,
            created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
            backed_up_at TIMESTAMPTZ);

        CREATE TABLE IF NOT EXISTS session_chunks(
            session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
            idx        INT  NOT NULL,
            hash       TEXT NOT NULL REFERENCES chunks(hash),
            PRIMARY KEY (session_id, idx));

        CREATE INDEX IF NOT EXISTS ix_sessions_updated ON sessions(updated_at DESC);
        CREATE INDEX IF NOT EXISTS ix_chunks_unbacked  ON chunks(backed_up) WHERE backed_up = false;
        """;

    public StoreMetadata(string connectionString, Serilog.ILogger? log = null)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        if (log is not null)
            _log = log;
    }

    public NpgsqlDataSource DataSource => _dataSource;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(30);
        var maxAttempts = 10;
        var attempt = 0;

        while (true)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = Schema;
                await cmd.ExecuteNonQueryAsync(ct);
                _log.Information("PostgreSQL schema bootstrapped");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempt++;
                if (attempt >= maxAttempts)
                    throw;

                _log.Warning(ex, "Failed to bootstrap PG schema (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                    attempt, maxAttempts, retryDelay.TotalMilliseconds);
                await Task.Delay(retryDelay, ct);
                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 1.5, maxDelay.TotalMilliseconds));
            }
        }
    }

    public async Task<bool> HasChunkAsync(string hash, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM chunks WHERE hash = @hash";
        cmd.Parameters.AddWithValue("hash", hash);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task RegisterChunkAsync(string hash, int size, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chunks (hash, size)
            VALUES (@hash, @size)
            ON CONFLICT (hash) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("hash", hash);
        cmd.Parameters.AddWithValue("size", size);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertManifestAsync(
        string sessionId, int nPast, long totalSize,
        IReadOnlyList<ChunkRef> chunks, CancellationToken ct = default,
        // M-Perf.9 #289: model identity passed by the caller (WorkerSchedulerService)
        // so a Coordinator restart can still gate RestoreKvAsync on model_hash.
        string modelAlias = "", string modelHash = "", string modelPath = "")
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var upsertSession = conn.CreateCommand();
        upsertSession.CommandText = """
            INSERT INTO sessions (session_id, n_past, total_size,
                                  model_alias, model_hash, model_path)
            VALUES (@sid, @np, @ts, @ma, @mh, @mp)
            ON CONFLICT (session_id) DO UPDATE SET
                n_past = EXCLUDED.n_past,
                total_size = EXCLUDED.total_size,
                model_alias = EXCLUDED.model_alias,
                model_hash = EXCLUDED.model_hash,
                model_path = EXCLUDED.model_path,
                updated_at = now()
            """;
        upsertSession.Parameters.AddWithValue("sid", sessionId);
        upsertSession.Parameters.AddWithValue("np", nPast);
        upsertSession.Parameters.AddWithValue("ts", totalSize);
        upsertSession.Parameters.AddWithValue("ma", modelAlias);
        upsertSession.Parameters.AddWithValue("mh", modelHash);
        upsertSession.Parameters.AddWithValue("mp", modelPath);
        upsertSession.Transaction = tx;
        await upsertSession.ExecuteNonQueryAsync(ct);

        await using var deleteOld = conn.CreateCommand();
        deleteOld.CommandText = "DELETE FROM session_chunks WHERE session_id = @sid";
        deleteOld.Parameters.AddWithValue("sid", sessionId);
        deleteOld.Transaction = tx;
        await deleteOld.ExecuteNonQueryAsync(ct);

        if (chunks.Count > 0)
        {
            // Ensure every referenced chunk has a parent row in `chunks` before inserting
            // session_chunks — otherwise the FK session_chunks_hash_fkey fails when a chunk is
            // resident on disk but absent from PG (e.g. pushed body that already existed, or a
            // GC race). Residency was already verified by PUT_MANIFEST, so this is truthful.
            // Idempotent (ON CONFLICT DO NOTHING) and atomic (same transaction).
            await using var registerChunks = conn.CreateCommand();
            var rsb = new System.Text.StringBuilder();
            rsb.Append("INSERT INTO chunks (hash, size) VALUES ");
            var ridx = 0;
            foreach (var c in chunks)
            {
                if (ridx > 0) rsb.Append(',');
                rsb.Append($"(@rh{ridx},@rs{ridx})");
                registerChunks.Parameters.AddWithValue($"rh{ridx}", c.Hash);
                registerChunks.Parameters.AddWithValue($"rs{ridx}", c.Size);
                ridx++;
            }
            rsb.Append(" ON CONFLICT (hash) DO NOTHING");
            registerChunks.CommandText = rsb.ToString();
            registerChunks.Transaction = tx;
            await registerChunks.ExecuteNonQueryAsync(ct);

            await using var insert = conn.CreateCommand();
            var sb = new System.Text.StringBuilder();
            sb.Append("INSERT INTO session_chunks (session_id, idx, hash) VALUES ");
            var idx = 0;
            foreach (var c in chunks)
            {
                if (idx > 0) sb.Append(',');
                sb.Append($"(@sid,@i{idx},@h{idx})");
                insert.Parameters.AddWithValue($"i{idx}", c.Index);
                insert.Parameters.AddWithValue($"h{idx}", c.Hash);
                idx++;
            }
            insert.CommandText = sb.ToString();
            insert.Parameters.AddWithValue("sid", sessionId);
            insert.Transaction = tx;
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task SetNPastAsync(string sessionId, int nPast, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, n_past, total_size)
            VALUES (@sid, @np, 0)
            ON CONFLICT (session_id) DO UPDATE SET
                n_past = EXCLUDED.n_past,
                updated_at = now()
            """;
        cmd.Parameters.AddWithValue("sid", sessionId);
        cmd.Parameters.AddWithValue("np", nPast);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteChunkAsync(string hash, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var delSc = conn.CreateCommand();
        delSc.CommandText = "DELETE FROM session_chunks WHERE hash = @hash";
        delSc.Parameters.AddWithValue("hash", hash);
        await delSc.ExecuteNonQueryAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE hash = @hash";
        cmd.Parameters.AddWithValue("hash", hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int?> GetNPastAsync(string sessionId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT n_past FROM sessions WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("sid", sessionId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int val ? val : null;
    }

    public async Task<Manifest?> GetManifestAsync(string sessionId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var sessionCmd = conn.CreateCommand();
        // M-Perf.9 #289: read the 3 model-identity columns too so the cross-model
        // guard in WorkerSchedulerService.RestoreKvAsync survives a Coordinator
        // restart. Pre-#289 sessions get '' for the 3 fields via the schema
        // default; the guard treats "both empty" as "skip".
        sessionCmd.CommandText = """
            SELECT n_past, total_size, model_alias, model_hash, model_path
            FROM sessions WHERE session_id = @sid
            """;
        sessionCmd.Parameters.AddWithValue("sid", sessionId);
        await using var reader = await sessionCmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        var nPast      = reader.GetInt32(0);
        var totalSize  = reader.GetInt64(1);
        var modelAlias = reader.GetString(2);
        var modelHash  = reader.GetString(3);
        var modelPath  = reader.GetString(4);
        await reader.CloseAsync();

        await using var chunksCmd = conn.CreateCommand();
        chunksCmd.CommandText = """
            SELECT sc.idx, sc.hash, c.size
            FROM session_chunks sc
            JOIN chunks c ON c.hash = sc.hash
            WHERE sc.session_id = @sid
            ORDER BY sc.idx
            """;
        chunksCmd.Parameters.AddWithValue("sid", sessionId);

        var chunks = new List<ChunkRef>();
        await using var chunkReader = await chunksCmd.ExecuteReaderAsync(ct);
        while (await chunkReader.ReadAsync(ct))
        {
            var idx = chunkReader.GetInt32(0);
            var hash = chunkReader.GetString(1);
            var size = chunkReader.GetInt32(2);
            chunks.Add(new ChunkRef(idx, hash, size));
        }

        return new Manifest(
            sessionId, 1, nPast, totalSize, chunks, DateTime.UtcNow,
            modelAlias, modelHash, modelPath);
    }

    public async Task MarkBackedUpAsync(string hash, string nvmePath, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE chunks SET
                backed_up = true,
                nvme_path = @path,
                backed_up_at = now()
            WHERE hash = @hash
            """;
        cmd.Parameters.AddWithValue("hash", hash);
        cmd.Parameters.AddWithValue("path", nvmePath);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(string Hash, int Size)>> GetUnbackedChunksAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash, size FROM chunks WHERE backed_up = false LIMIT @lim";
        cmd.Parameters.AddWithValue("lim", limit);

        var results = new List<(string, int)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    public async Task<List<string>> GetRecentSessionIdsAsync(int n, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id FROM sessions ORDER BY updated_at DESC LIMIT @lim";
        cmd.Parameters.AddWithValue("lim", n);

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<int> GcOrphanChunksAsync(DirectoryInfo chunksDir, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM chunks
            WHERE hash NOT IN (SELECT DISTINCT hash FROM session_chunks)
              AND created_at < now() - interval '60 seconds'
            RETURNING hash
            """;

        var hashes = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            hashes.Add(reader.GetString(0));
        await reader.CloseAsync();

        var sessionCount = 0;
        foreach (var hash in hashes)
        {
            var path = Path.Combine(chunksDir.FullName, hash);
            if (File.Exists(path))
                File.Delete(path);
        }

        await using var delS = conn.CreateCommand();
        delS.CommandText = """
            DELETE FROM sessions
            WHERE session_id NOT IN (SELECT DISTINCT session_id FROM session_chunks)
            """;
        sessionCount = await delS.ExecuteNonQueryAsync(ct);
        if (sessionCount > 0)
            _log.Information("GC: removed {Count} zombie sessions with no remaining chunks", sessionCount);

        return hashes.Count;
    }

    public async Task ReconcileBootAsync(DirectoryInfo chunksDir, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM chunks WHERE backed_up = false";

        var toRemove = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var hash = reader.GetString(0);
            var path = Path.Combine(chunksDir.FullName, hash);
            if (!File.Exists(path))
                toRemove.Add(hash);
        }
        await reader.CloseAsync();

        if (toRemove.Count == 0)
            return;

        _log.Information("Boot reconciliation: removing {Count} PG rows for chunks missing from tmpfs", toRemove.Count);
        foreach (var hash in toRemove)
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM session_chunks WHERE hash = @hash";
            del.Parameters.AddWithValue("hash", hash);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var hash in toRemove)
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM chunks WHERE hash = @hash";
            del.Parameters.AddWithValue("hash", hash);
            await del.ExecuteNonQueryAsync(ct);
        }

        await using var delZombie = conn.CreateCommand();
        delZombie.CommandText = """
            DELETE FROM sessions
            WHERE session_id NOT IN (SELECT DISTINCT session_id FROM session_chunks)
            """;
        var zombieCount = await delZombie.ExecuteNonQueryAsync(ct);
        if (zombieCount > 0)
            _log.Information("Boot reconciliation: removed {Count} zombie sessions with no chunks", zombieCount);
    }

    public async Task<(int ManifestCount, int ChunkRows)> GetStatsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions";
        var count = (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        cmd.CommandText = "SELECT COUNT(*) FROM session_chunks";
        var chunkRows = (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        _log.Debug("PG stats: {Count} manifests, {ChunkRows} chunk rows", count, chunkRows);
        return (count, chunkRows);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
