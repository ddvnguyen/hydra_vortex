using Hydra.Core.Models;

namespace Hydra.Core.Services;

public interface ICompletionProxyService
{
    Task<Dictionary<string, object>> ProxyCompletionAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct);
    IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct);
    Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct);

    // #470: poll GET /v1/decode/{decodeRequestId} for merged-decode results.
    // Returns SSE lines (streaming) or throws on timeout.
    // Branch: 404=retry (transient mid-generation, #587), 202=keep-polling+record-phases, 400=terminal, 200=stream.
    IAsyncEnumerable<byte[]> PollDecodeStreamAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct, WorkItem? item = null);
    // #470: poll GET /v1/decode/{decodeRequestId} for buffered result.
    Task<Dictionary<string, object>> PollDecodeResultAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct);
    // #470: DELETE /v1/decode/{decodeRequestId} to cancel orphaned generation.
    Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct);

    /// <summary>#470: erase a slot's KV state (POST /slots/{id}?action=erase).
    /// Best-effort — the engine tolerates missing slot-save support (404/501).</summary>
    Task EraseSlotAsync(string nodeUrl, int slotId, CancellationToken ct);
}

/// <summary>Kind of a completed submission: a buffered final response, or a live SSE stream.</summary>
public enum CompletionResultKind { Final, Stream }

/// <summary>
/// Typed result of <see cref="IWorkerScheduler.SubmitAsync"/> — replaces the
/// previous untyped <c>object</c> return. <see cref="CompletionResults.Unwrap"/>
/// recovers the legacy-shaped payload/stream for adapters that need it.
/// </summary>
public interface ICompletionResult
{
    CompletionResultKind Kind { get; }
}

/// <summary>Buffered, non-streaming completion (an OpenAI-shaped response payload).</summary>
public sealed class FinalCompletionResult : ICompletionResult
{
    public CompletionResultKind Kind => CompletionResultKind.Final;
    public object Payload { get; }
    public FinalCompletionResult(object payload) => Payload = payload;
}

/// <summary>Live SSE stream completion.</summary>
public sealed class StreamCompletionResult : ICompletionResult
{
    public CompletionResultKind Kind => CompletionResultKind.Stream;
    public IAsyncEnumerable<byte[]> Chunks { get; }
    public StreamCompletionResult(IAsyncEnumerable<byte[]> chunks) => Chunks = chunks;
}

/// <summary>Helpers for consumers that must bridge the typed result back to the
/// legacy object/stream shape (fixtures, adapters).</summary>
public static class CompletionResults
{
    /// <summary>Returns the final payload, or the chunk stream for stream results.</summary>
    public static object? Unwrap(ICompletionResult result) => result switch
    {
        FinalCompletionResult f => f.Payload,
        StreamCompletionResult s => s.Chunks,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "unknown completion result kind"),
    };

    /// <summary>Awaits the submit task, then unwraps its result (for Task-returning adapters).</summary>
    public static async Task<object?> UnwrapAsync(Task<ICompletionResult> submit)
        => Unwrap(await submit.ConfigureAwait(false));
}

public interface IWorkerScheduler
{
    string? LastDispatchedNode { get; }
    /// <summary>Alias of the model that served the most recent request (M-Perf.9 #289).</summary>
    public string? LastDispatchedModel { get; }
    /// <summary>Tokenizer of the model that served the most recent request (#470).</summary>
    public string? LastDispatchedTokenizer { get; }
    /// <summary>Display name of the model that served the most recent request (#470).</summary>
    public string? LastDispatchedModelName { get; }
    /// <summary>Quant label of the model that served the most recent request (#470).</summary>
    public string? LastDispatchedModelQuant { get; }
    /// <summary>Capabilities bitmask of the model that served the most recent request (#470).</summary>
    public uint LastDispatchedModelCapabilities { get; }
    Task<ICompletionResult> SubmitAsync(Dictionary<string, object> request, List<Dictionary<string, object>> messages, string sessionId, int estimatedTokens, int maxTokens, string? prefixHash, CancellationToken ct, int systemPromptTokens = 0);
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
    /// <summary>
    /// Stamp the GGUF-derived model identity onto a node's info.
    /// Called by the worker scheduler after a PREFILL response populates
    /// the KV model identity so Gate A can verify identity at DECODE time.
    /// </summary>
    void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities);

    /// <summary>
    /// #592: re-mark a node healthy based on positive liveness evidence that
    /// arrived outside the health-poll cycle — e.g. a successful PREFILL served
    /// by that node, or a direct router liveness probe. A node flagged unhealthy
    /// by <c>health_poll_failed</c> (say, during an inline model swap) must not
    /// keep excluding requests once it demonstrably serves again. Fires
    /// HealthyChanged only on an actual unhealthy→healthy flip.
    /// </summary>
    void MarkHealthy(string nodeName);

    /// <summary>
    /// Fired when a node's Healthy flag flips (to healthy OR to unhealthy). The
    /// scheduler subscribes so queued items get re-checked when a node recovers
    /// — capacity can return without a slot release.
    /// </summary>
    event Action? HealthyChanged;
}
