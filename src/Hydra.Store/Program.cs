using Hydra.Store;

var cfg = new StoreConfig();
var engine = new StorageEngine(cfg.StoreDirectory);
var chunkStore = new ChunkStore(cfg.StoreDirectory);
var metadata = new StoreMetadata(cfg.PgConn);

// Bootstrap schema with retry (Postgres may not be ready yet).
await metadata.EnsureSchemaAsync(CancellationToken.None);

// Boot reconciliation: drop PG rows for unbacked chunks missing from tmpfs (fresh boot).
await metadata.ReconcileBootAsync(chunkStore.ChunksDirectory, CancellationToken.None);

// Startup recovery: if tmpfs is empty but NVMe has backups, restore hot sessions.
var backupDir = new DirectoryInfo(cfg.BackupDir);
var backupChunksDir = new DirectoryInfo(Path.Combine(cfg.BackupDir, "chunks"));
var chunksDir = chunkStore.ChunksDirectory;
var hasLocalChunks = chunksDir.Exists && chunksDir.EnumerateFiles().Any();

if (!hasLocalChunks && backupChunksDir.Exists)
{
    var recentSessions = await metadata.GetRecentSessionIdsAsync(cfg.RestoreTopN, CancellationToken.None);
    var totalRestored = 0L;
    var totalSessions = 0;

    foreach (var sid in recentSessions)
    {
        var manifest = await metadata.GetManifestAsync(sid, CancellationToken.None);
        if (manifest is null) continue;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var restoredBytes = 0L;
        foreach (var chunk in manifest.Chunks)
        {
            var nvmePath = Path.Combine(backupChunksDir.FullName, chunk.Hash);
            if (!File.Exists(nvmePath)) continue;

            var destPath = Path.Combine(chunksDir.FullName, chunk.Hash);
            if (File.Exists(destPath)) continue;

            File.Copy(nvmePath, destPath);
            restoredBytes += chunk.Size;
        }
        sw.Stop();
        totalRestored += restoredBytes;
        totalSessions++;
        Serilog.Log.Information(
            "Restored session {SessionId} ({Bytes} MB) in {Elapsed}ms",
            sid, restoredBytes / 1_048_576.0, sw.ElapsedMilliseconds);
    }

    if (totalSessions > 0)
    {
        chunkStore.RefreshIndex();
        Serilog.Log.Information(
            "Startup recovery complete: {Sessions} sessions, {Bytes} MB restored",
            totalSessions, totalRestored / 1_048_576.0);
    }
}
else if (!hasLocalChunks && !backupChunksDir.Exists)
{
    Serilog.Log.Information("No backup directory found, skipping startup recovery");
}
else
{
    Serilog.Log.Information("tmpfs already populated, skipping startup recovery");
}

var server = new StoreServer(cfg, engine, chunkStore, metadata);

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var serverTask = server.RunAsync(cts.Token);
var debugTask = server.StartDebugEndpointAsync(cts.Token);

// GC orphan chunks every 30 minutes via referential GC.
var writeBehind = new WriteBehindService(cfg, metadata, chunkStore);
var writeBehindTask = Task.Run(() => writeBehind.RunAsync(cts.Token), cts.Token);

var gcTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(30), cts.Token);
            var removed = await metadata.GcOrphanChunksAsync(chunkStore.ChunksDirectory, cts.Token);
            if (removed > 0)
                Serilog.Log.Information("GC removed {Removed} orphaned chunks", removed);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "GC cycle failed");
        }
    }
}, cts.Token);

try
{
    await Task.WhenAll(serverTask, debugTask, writeBehindTask, gcTask);
}
catch (OperationCanceledException)
{
}
