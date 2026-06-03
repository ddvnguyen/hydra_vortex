using Npgsql;

namespace Hydra.Store;

public sealed class StoreMetadata : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Serilog.ILogger _log = Serilog.Log.ForContext<StoreMetadata>();

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sessions(
            session_id TEXT PRIMARY KEY,
            n_past     INT    NOT NULL DEFAULT 0,
            total_size BIGINT NOT NULL DEFAULT 0,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

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
                if (retryDelay >= maxDelay)
                    throw;

                _log.Warning(ex, "Failed to bootstrap PG schema (attempt {Attempt}), retrying in {Delay}ms",
                    attempt, retryDelay.TotalMilliseconds);
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
        IReadOnlyList<ChunkRef> chunks, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var upsertSession = conn.CreateCommand();
        upsertSession.CommandText = """
            INSERT INTO sessions (session_id, n_past, total_size)
            VALUES (@sid, @np, @ts)
            ON CONFLICT (session_id) DO UPDATE SET
                n_past = EXCLUDED.n_past,
                total_size = EXCLUDED.total_size,
                updated_at = now()
            """;
        upsertSession.Parameters.AddWithValue("sid", sessionId);
        upsertSession.Parameters.AddWithValue("np", nPast);
        upsertSession.Parameters.AddWithValue("ts", totalSize);
        upsertSession.Transaction = tx;
        await upsertSession.ExecuteNonQueryAsync(ct);

        await using var deleteOld = conn.CreateCommand();
        deleteOld.CommandText = "DELETE FROM session_chunks WHERE session_id = @sid";
        deleteOld.Parameters.AddWithValue("sid", sessionId);
        deleteOld.Transaction = tx;
        await deleteOld.ExecuteNonQueryAsync(ct);

        if (chunks.Count > 0)
        {
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
        sessionCmd.CommandText = "SELECT n_past, total_size FROM sessions WHERE session_id = @sid";
        sessionCmd.Parameters.AddWithValue("sid", sessionId);
        await using var reader = await sessionCmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        var nPast = reader.GetInt32(0);
        var totalSize = reader.GetInt64(1);
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

        return new Manifest(sessionId, 1, nPast, totalSize, chunks, DateTime.UtcNow);
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

        foreach (var hash in hashes)
        {
            var path = Path.Combine(chunksDir.FullName, hash);
            if (File.Exists(path))
                File.Delete(path);
        }

        if (hashes.Count > 0)
        {
            await using var delSc = conn.CreateCommand();
            delSc.CommandText = """
                DELETE FROM session_chunks
                WHERE session_id IN (
                    SELECT s.session_id FROM sessions s
                    LEFT JOIN session_chunks sc ON sc.session_id = s.session_id
                    GROUP BY s.session_id
                    HAVING COUNT(sc.idx) = 0)
                """;
            await delSc.ExecuteNonQueryAsync(ct);

            await using var delS = conn.CreateCommand();
            delS.CommandText = """
                DELETE FROM sessions
                WHERE session_id NOT IN (SELECT DISTINCT session_id FROM session_chunks)
                """;
            await delS.ExecuteNonQueryAsync(ct);
        }

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

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
