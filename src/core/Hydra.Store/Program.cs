using Hydra.Store;

var cfg = new StoreConfig();
cfg.Validate();

using var mainCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    mainCts.Cancel();
};
var ct = mainCts.Token;

await using var metadata = new StoreMetadata(cfg.PgConn);
var engine = new StorageEngine(cfg.StoreDirectory);
var chunkStore = new ChunkStore(cfg.StoreDirectory);

// Clean up orphaned .tmp files from previous crashes.
foreach (var tmp in chunkStore.ChunksDirectory.EnumerateFiles("*.tmp"))
    try { tmp.Delete(); } catch { }
foreach (var tmp in engine.StoreDirectory.EnumerateFiles("*.tmp", SearchOption.AllDirectories))
    try { tmp.Delete(); } catch { }

await metadata.EnsureSchemaAsync(ct);

await metadata.ReconcileBootAsync(chunkStore.ChunksDirectory, ct);

// Startup recovery: if tmpfs is empty but NVMe has backups, restore hot sessions.
var backupDir = new DirectoryInfo(cfg.BackupDir);
var backupChunksDir = new DirectoryInfo(Path.Combine(cfg.BackupDir, "chunks"));
var chunksDir = chunkStore.ChunksDirectory;
var hasLocalChunks = chunksDir.Exists && chunksDir.EnumerateFiles().Any(f => !f.Name.EndsWith(".tmp"));

if (!hasLocalChunks && backupChunksDir.Exists)
{
    var recentSessions = await metadata.GetRecentSessionIdsAsync(cfg.RestoreTopN, ct);
    var totalRestored = 0L;
    var totalSessions = 0;

    foreach (var sid in recentSessions)
    {
        var manifest = await metadata.GetManifestAsync(sid, ct);
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

await using var server = new StoreServer(cfg, engine, chunkStore, metadata);

var serverTask = server.RunAsync(ct);
var debugTask = server.StartDebugEndpointAsync(ct);

// GC orphan chunks every 30 minutes via referential GC.
var writeBehind = new WriteBehindService(cfg, metadata, chunkStore);
var writeBehindTask = Task.Run(() => writeBehind.RunAsync(ct), ct);

var gcTask = Task.Run(async () =>
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(30), ct);
            var removed = await metadata.GcOrphanChunksAsync(chunkStore.ChunksDirectory, ct);
            if (removed > 0)
            {
                StoreMetrics.ChunksRemoved.Inc(removed);
                chunkStore.RefreshIndex();
                Serilog.Log.Information("GC removed {Removed} orphaned chunks", removed);
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "GC cycle failed");
        }
    }
}, ct);

try
{
    await Task.WhenAll(serverTask, debugTask, writeBehindTask, gcTask);
}
catch (OperationCanceledException)
{
}
