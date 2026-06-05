using Hydra.Agent;
using Hydra.Shared;

var cfg = new AgentConfig();
var log = HydraLogging.CreateLogger("agent");

var llamaClient = new LlamaClient(cfg.LlamaUrl);
var storeClient = new RpcClient(cfg.StoreHost, cfg.StorePort);
var chunkCache = new LocalChunkCache(cfg.ChunkCacheDir);
var handler = new StateHandler(llamaClient, storeClient, chunkCache, log);
var server = new AgentServer(cfg, handler, llamaClient, log);

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

log.Information("Agent starting on {Host}:{Port} (node={Node}, llama={Llama}, store={StoreHost}:{StorePort})",
    cfg.Host, cfg.Port, cfg.NodeName, cfg.LlamaUrl, cfg.StoreHost, cfg.StorePort);

var serverTask = server.RunAsync(cts.Token);
var debugTask = server.StartDebugEndpointAsync(cts.Token);

try
{
    await Task.WhenAll(serverTask, debugTask);
}
catch (OperationCanceledException)
{
}
finally
{
    await storeClient.DisposeAsync();
    llamaClient.Dispose();
    log.Information("Agent stopped");
}
