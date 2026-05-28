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

try
{
    await Task.WhenAll(serverTask, debugTask);
}
catch (OperationCanceledException)
{
}
