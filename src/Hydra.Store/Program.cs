using Hydra.Store;

var cfg = new StoreConfig();
var engine = new StorageEngine(cfg.StoreDirectory);
var chunkStore = new ChunkStore(cfg.StoreDirectory);
var server = new StoreServer(cfg, engine, chunkStore);

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var serverTask = server.RunAsync(cts.Token);
var debugTask = server.StartDebugEndpointAsync(cts.Token);

// Collect orphaned chunks every 30 minutes. Keep all sessions referenced by
// an existing manifest — anything unreferenced is safe to delete.
var gcTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(30), cts.Token);
            var keepSessions = chunkStore.ManifestsDirectory
                .EnumerateFiles("*.json")
                .Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Name))
                .ToHashSet();
            var removed = chunkStore.GC(keepSessions);
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
    await Task.WhenAll(serverTask, debugTask, gcTask);
}
catch (OperationCanceledException)
{
}
