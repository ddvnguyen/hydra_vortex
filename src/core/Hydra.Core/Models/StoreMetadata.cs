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
            -- M-Perf.9 #289 / #470: model identity of the slot that built this KV cache.
            -- #470: replaced model_hash with GGUF-derived semantic identity fields.
            -- Nullable + back-compat defaults so pre-#470 sessions get empty/zero
            -- values and the cross-model guard treats that as "skip".
            model_alias      TEXT    NOT NULL DEFAULT '',
            tokenizer        TEXT    NOT NULL DEFAULT '',
            model_name       TEXT    NOT NULL DEFAULT '',
            model_quant      TEXT    NOT NULL DEFAULT '',
            model_capabilities INTEGER NOT NULL DEFAULT 0,
            model_path       TEXT    NOT NULL DEFAULT '',
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at  TIMESTAMPTZ NOT NULL DEFAULT now());

        -- #470 migration: drop model_hash (replaced by 4 identity columns above).
        -- Safe to run repeatedly: ALTER COLUMN IF EXISTS is idempotent.
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name = 'sessions' AND column_name = 'model_hash') THEN
                ALTER TABLE sessions DROP COLUMN model_hash;
            END IF;
        END $$;

        -- #470 migration: add identity columns if they don't exist yet.
        -- For fresh installs the CREATE TABLE above already has them; this
        -- handles upgrade from the pre-#470 schema.
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'sessions' AND column_name = 'tokenizer') THEN
                ALTER TABLE sessions ADD COLUMN tokenizer TEXT NOT NULL DEFAULT '';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'sessions' AND column_name = 'model_name') THEN
                ALTER TABLE sessions ADD COLUMN model_name TEXT NOT NULL DEFAULT '';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'sessions' AND column_name = 'model_quant') THEN
                ALTER TABLE sessions ADD COLUMN model_quant TEXT NOT NULL DEFAULT '';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'sessions' AND column_name = 'model_capabilities') THEN
                ALTER TABLE sessions ADD COLUMN model_capabilities INTEGER NOT NULL DEFAULT 0;
            END IF;
        END $$;

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

        -- M3-P1 #332: L2 chunk cache. Content-addressed, byte-budgeted (50 GB default).
        -- Eviction score = (now - created_at) * (now - last_used) / use_count.
        -- Hard-triggered GC on Put when size > L2MaxBytes; soft-triggered every
        -- L2GcIntervalSeconds. Survives Coordinator restart.
        CREATE TABLE IF NOT EXISTS chunk_data_l2(
            hash        TEXT        PRIMARY KEY,
            bytes       BYTEA       NOT NULL,
            size        INT         NOT NULL,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_used   TIMESTAMPTZ NOT NULL DEFAULT now(),
            use_count   BIGINT      NOT NULL DEFAULT 1);
        CREATE INDEX IF NOT EXISTS ix_chunk_data_l2_last_used  ON chunk_data_l2(last_used);
        CREATE INDEX IF NOT EXISTS ix_chunk_data_l2_created_at ON chunk_data_l2(created_at);
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
        // M-Perf.9 #289 / #470: model identity passed by the caller (WorkerSchedulerService)
        // so a Coordinator restart can still gate RestoreKvAsync on model identity.
        string modelAlias = "", string tokenizer = "", string modelName = "",
        string modelQuant = "", uint modelCapabilities = 0, string modelPath = "")
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var upsertSession = conn.CreateCommand();
        upsertSession.CommandText = """
            INSERT INTO sessions (session_id, n_past, total_size,
                                  model_alias, tokenizer, model_name,
                                  model_quant, model_capabilities, model_path)
            VALUES (@sid, @np, @ts, @ma, @tk, @mn, @mq, @mc, @mp)
            ON CONFLICT (session_id) DO UPDATE SET
                n_past = EXCLUDED.n_past,
                total_size = EXCLUDED.total_size,
                model_alias = EXCLUDED.model_alias,
                tokenizer = EXCLUDED.tokenizer,
                model_name = EXCLUDED.model_name,
                model_quant = EXCLUDED.model_quant,
                model_capabilities = EXCLUDED.model_capabilities,
                model_path = EXCLUDED.model_path,
                updated_at = now()
            """;
        upsertSession.Parameters.AddWithValue("sid", sessionId);
        upsertSession.Parameters.AddWithValue("np", nPast);
        upsertSession.Parameters.AddWithValue("ts", totalSize);
        upsertSession.Parameters.AddWithValue("ma", modelAlias);
        upsertSession.Parameters.AddWithValue("tk", tokenizer);
        upsertSession.Parameters.AddWithValue("mn", modelName);
        upsertSession.Parameters.AddWithValue("mq", modelQuant);
        upsertSession.Parameters.AddWithValue("mc", (int)modelCapabilities);
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
        // M-Perf.9 #289 / #470: read the model identity columns so the cross-model
        // guard in WorkerSchedulerService.RestoreKvAsync survives a Coordinator
        // restart. Pre-#470 sessions get '' for text fields and 0 for capabilities
        // via the schema default; the guard treats "both empty" as "skip".
        sessionCmd.CommandText = """
            SELECT n_past, total_size, model_alias, tokenizer, model_name,
                   model_quant, model_capabilities, model_path
            FROM sessions WHERE session_id = @sid
            """;
        sessionCmd.Parameters.AddWithValue("sid", sessionId);
        await using var reader = await sessionCmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        var nPast            = reader.GetInt32(0);
        var totalSize        = reader.GetInt64(1);
        var modelAlias       = reader.GetString(2);
        var tokenizer        = reader.GetString(3);
        var modelName        = reader.GetString(4);
        var modelQuant       = reader.GetString(5);
        var modelCapabilities = (uint)reader.GetInt32(6);
        var modelPath        = reader.GetString(7);
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
            modelAlias, tokenizer, modelName, modelQuant, modelCapabilities, modelPath);
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

    /// <summary>
    /// Retention GC (#470 post-fix queue #1): evict saved-KV sessions that
    /// have gone untouched for longer than <paramref name="ttl"/>, then free
    /// the chunk files those sessions referenced — from the tmpfs chunk dir
    /// AND the SSD backup dir — plus their PG rows, once no other session
    /// references them. This is the age-based TTL that the referential
    /// <see cref="GcOrphanChunksAsync"/> cannot provide: a chunk referenced by
    /// a stale-but-never-deleted session would otherwise stay pinned in tmpfs
    /// + SSD + PG forever (the save_failed_fallback / stale-session leak class).
    ///
    /// Freshness = <c>sessions.updated_at</c>, bumped on every save (save_kv
    /// and prefix saves), so an actively used session is never a victim. Both
    /// deletes are gated on the staleness predicate evaluated at execution
    /// time, so a session re-saved mid-GC is skipped. The Store is a cache:
    /// a restore that races a purge simply falls back to a cold start (the
    /// same recovery the orphan GC already relies on).
    /// </summary>
    /// <returns>Number of chunk files/rows freed.</returns>
    public async Task<int> GcStaleSessionsAsync(
        TimeSpan ttl, DirectoryInfo chunksDir, DirectoryInfo backupChunksDir,
        int limit = 500, CancellationToken ct = default)
    {
        if (ttl <= TimeSpan.Zero)
            return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. Drop the stale sessions' session_chunks, remembering the hashes
        //    this orphans. Gated on updated_at at execution time.
        var orphaned = new HashSet<string>();
        await using (var delSc = conn.CreateCommand())
        {
            delSc.Transaction = tx;
            delSc.CommandText = """
                DELETE FROM session_chunks
                WHERE session_id IN (
                    SELECT session_id FROM sessions
                    WHERE updated_at < now() - make_interval(secs => @ttlS)
                    LIMIT @lim)
                RETURNING hash
                """;
            delSc.Parameters.AddWithValue("ttlS", ttl.TotalSeconds);
            delSc.Parameters.AddWithValue("lim", limit);
            await using var rdr = await delSc.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                orphaned.Add(rdr.GetString(0));
        }

        // 2. Drop the stale sessions themselves.
        var removedSessions = 0;
        await using (var delS = conn.CreateCommand())
        {
            delS.Transaction = tx;
            delS.CommandText = """
                DELETE FROM sessions
                WHERE session_id IN (
                    SELECT session_id FROM sessions
                    WHERE updated_at < now() - make_interval(secs => @ttlS)
                    LIMIT @lim)
                RETURNING session_id
                """;
            delS.Parameters.AddWithValue("ttlS", ttl.TotalSeconds);
            delS.Parameters.AddWithValue("lim", limit);
            await using var rdr = await delS.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                removedSessions++;
        }

        // 3. Free chunk files (tmpfs + SSD backup) and rows for hashes no
        //    other session references. Mirrors GcOrphanChunksAsync's shape
        //    but also clears the backup copy, which orphan GC never touches.
        var toDelete = new List<string>();
        if (orphaned.Count > 0)
        {
            var stillReferenced = new HashSet<string>();
            await using (var refs = conn.CreateCommand())
            {
                refs.Transaction = tx;
                refs.CommandText = "SELECT DISTINCT hash FROM session_chunks";
                await using var rdr = await refs.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                    stillReferenced.Add(rdr.GetString(0));
            }

            toDelete = orphaned.Where(h => !stillReferenced.Contains(h)).ToList();
            foreach (var hash in toDelete)
            {
                var tmpfsPath = Path.Combine(chunksDir.FullName, hash);
                try { if (File.Exists(tmpfsPath)) File.Delete(tmpfsPath); } catch { /* ignore */ }
                var backupPath = Path.Combine(backupChunksDir.FullName, hash);
                try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { /* ignore */ }
            }

            if (toDelete.Count > 0)
            {
                await using var delC = conn.CreateCommand();
                delC.Transaction = tx;
                delC.CommandText = "DELETE FROM chunks WHERE hash = ANY(@hashes)";
                delC.Parameters.AddWithValue("hashes", toDelete.ToArray());
                await delC.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);

        if (removedSessions > 0 || toDelete.Count > 0)
        {
            _log.Information("Retention GC: removed {Sessions} stale sessions, {Chunks} orphaned chunks (TTL {Ttl})",
                removedSessions, toDelete.Count, ttl);
        }
        return toDelete.Count;
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
