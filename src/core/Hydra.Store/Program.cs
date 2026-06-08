using Hydra.Store;
using Hydra.Store.Extensions;
using Hydra.Store.Models;
using Hydra.Shared;
using Serilog;
using Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Log.Logger = HydraLogging.CreateLogger("store");
Log.Information("Starting Hydra.Store at {BootTime}", DateTime.UtcNow);

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
Console.Error.WriteLine($"[BOOT] StoreServer created, starting RPC on {cfg.Port}");
Console.Error.Flush();
var serverTask = server.RunAsync(ct);
var debugTask = server.StartDebugEndpointAsync(ct);

// ── Coordinator DI host ────────────────────────────────────────────────
Task? coordinatorTask = null;
var coordEnabled = Environment.GetEnvironmentVariable("HYDRA_COORD_ENABLED") != "false";
var workersJson = Environment.GetEnvironmentVariable("HYDRA_COORD_WORKERS") ?? "";
Console.Error.WriteLine($"[BOOT] coordEnabled={coordEnabled} workersJsonLen={workersJson.Length} workersFirst80={workersJson[..Math.Min(80, workersJson.Length)]}");
Console.Error.Flush();
if (coordEnabled)
{
    var coordCfg = new CoordinatorConfig();
    coordCfg.Workers.AddRange(CoordinatorConfig.LoadWorkers());
    if (coordCfg.Workers.Count > 0)
    {
        coordCfg.Validate();
        Log.Information("coordinator_init Workers={Count} Mix={Mix}", coordCfg.Workers.Count, coordCfg.MixPrecisionEnabled);

        coordinatorTask = Task.Run(async () =>
        {
            var builder = WebApplication.CreateSlimBuilder(args);
            builder.WebHost.UseUrls($"http://0.0.0.0:{coordCfg.Port}");

            // DI: register all coordinator services
            builder.Services.AddCoordinator(coordCfg);

            // Also register store components for shared debug endpoints
            builder.Services.AddSingleton(server);
            builder.Services.AddSingleton(cfg);

            var app = builder.Build();
            app.MapControllers();
            app.MapGet("/version", () => new { service = "hydra-coordinator", version = HydraLogging.ServiceVersion });
            app.UseMetricServer();

            await app.RunAsync();
        }, ct);
    }
}

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
    var allTasks = new List<Task> { serverTask, debugTask, writeBehindTask, gcTask };
    if (coordinatorTask != null) allTasks.Add(coordinatorTask);
    await Task.WhenAll(allTasks);
}
catch (OperationCanceledException)
{
}
