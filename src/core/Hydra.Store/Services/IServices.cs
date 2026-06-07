using Hydra.Store.Models;

namespace Hydra.Store.Services;

public interface ICompletionProxyService
{
    Task<Dictionary<string, object>> ProxyCompletionAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct);
    IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct);
}

public interface IWorkerScheduler
{
    string? LastDispatchedNode { get; }
    Task<object> SubmitAsync(Dictionary<string, object> request, List<Dictionary<string, object>> messages, string sessionId, int estimatedTokens, int maxTokens, string? prefixHash, CancellationToken ct);
    Task<object> MigrateSessionAsync(string sessionId, string targetNodeName, CancellationToken ct);
    Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct);
    Task RunAsync(CancellationToken ct);
    int WarmLeaseCount { get; }
}

public interface IHealthMonitorService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    bool IsHealthy(string nodeName);
    bool IsStoreHealthy { get; }
    int? GetIdleSlot(string nodeName);
    Dictionary<string, object> GetHealthSummary();
}
