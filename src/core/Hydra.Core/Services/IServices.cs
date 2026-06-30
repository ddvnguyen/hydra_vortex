using Hydra.Core.Models;

namespace Hydra.Core.Services;

public interface ICompletionProxyService
{
    Task<Dictionary<string, object>> ProxyCompletionAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct);
    IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct);
    Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct);
}

public interface IWorkerScheduler
{
    string? LastDispatchedNode { get; }
    /// <summary>Alias of the model that served the most recent request (M-Perf.9 #289).</summary>
    string? LastDispatchedModel { get; }
    /// <summary>SHA-256 hex of the model that served the most recent request (M-Perf.9 #289).</summary>
    string? LastDispatchedModelHash { get; }
    Task<object> SubmitAsync(Dictionary<string, object> request, List<Dictionary<string, object>> messages, string sessionId, int estimatedTokens, int maxTokens, string? prefixHash, CancellationToken ct);
    Task<object> MigrateSessionAsync(string sessionId, string targetNodeName, CancellationToken ct);
    Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct);
   Task RunAsync(CancellationToken ct);
    Task NotifyStreamComplete(string sessionId);
    Task<object> ResetSystemAsync(CancellationToken ct);
}

public interface IHealthMonitorService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    bool IsHealthy(string nodeName);
    bool IsStoreHealthy { get; }
    int? GetIdleSlot(string nodeName);
    NodeInfo? GetNodeInfo(string nodeName);
    Dictionary<string, object> GetHealthSummary();
}
