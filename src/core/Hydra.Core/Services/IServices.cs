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
    public string? LastDispatchedModel { get; }
    /// <summary>SHA-256 hex of the model that served the most recent request (M-Perf.9 #289).</summary>
    public string? LastDispatchedModelHash { get; }
    Task<object> SubmitAsync(Dictionary<string, object> request, List<Dictionary<string, object>> messages, string sessionId, int estimatedTokens, int maxTokens, string? prefixHash, CancellationToken ct);
    Task<object> MigrateSessionAsync(string sessionId, string targetNodeName, CancellationToken ct);
    Task EvictWarmSessionAsync(string sessionId, string nodeName, CancellationToken ct);
    Task RunAsync(CancellationToken ct);
    Task NotifyStreamComplete(string sessionId);
    int WarmLeaseCount { get; }

    /// <summary>
    /// P3.0+ / #368: trigger SWAP_QUANT (0x45) on the named worker. The worker
    /// is held in the SWAPPING state for the duration (mutually exclusive with
    /// SOLO_BUSY and COMBINED_SERVING). On completion the worker's
    /// SwapGeneration is bumped so any head holding a stale binding to this
    /// peer can detect the change. The actual model free/reload on the C++
    /// side lands with #263 — this Core path is the admission + lifecycle +
    /// epoch bookkeeping that makes the swap safe to wire in.
    /// Returns true on success, false if the worker was busy/swapping.
    /// </summary>
    Task<bool> TrySwapQuantAsync(string workerName, string quantKey, string tensorPattern, string traceId, CancellationToken ct);
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
