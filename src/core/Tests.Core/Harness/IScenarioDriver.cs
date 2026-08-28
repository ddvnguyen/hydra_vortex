using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Tests.Core.Integration;

namespace Tests.Core.Harness;

/// <summary>
/// Shared normalized-trace capture, used by both the legacy
/// (<see cref="SchedulerScenarioRunner"/>) and v2 (<see cref="V2ScenarioDriver"/>)
/// drivers so the differential gate diffs IDENTICAL trace shapes. Strips
/// time-varying fields (trace ids, timestamps, wall-clock busy seconds) and
/// keeps the deterministic contract: ordered RPC stream, merged-decode calls,
/// HTTP-proxy calls, final state, ledger snapshot, and normalized per-worker
/// busy signal (0 idle / 1 outstanding-warm-lease-or-leak).
/// </summary>
internal static class ScenarioTraceCapture
{
    public static ScenarioTrace Capture(
        ScenarioRpcClient rpc,
        TestCompletionProxy proxy,
        SessionLedger ledger,
        IReadOnlyList<WorkerConfig> workers,
        WorkerTracker tracker,
        string sessionId,
        OutcomeClass outcome,
        Exception? error = null)
    {
        _ = error;
        var rpcTrace = NormalizeRpcTrace(rpc.RpcCalls);
        var merged = rpc.MergedDecodeCalls
            .Select(c => new TraceMergedDecode(c.SlotKey, c.ModelName, c.Stream)).ToList();

        var proxyCalls = new List<TraceProxyCall>();
        foreach (var (url, body, _) in proxy.NonStreamingCalls)
            proxyCalls.Add(SummarizeProxy(url, body, stream: false));
        foreach (var (url, body, _) in proxy.StreamingCalls)
            proxyCalls.Add(SummarizeProxy(url, body, stream: true));

        var entry = ledger.Lookup(sessionId);
        var ledgerTrace = entry == null
            ? null
            : new TraceLedger(entry.NodeName, entry.SlotId, entry.NPast, entry.HasStoreState, entry.SlotFreed);

        // 0/1 busy signal (see ScenarioTrace doc): 1 == a sanctioned warm lease
        // or a leak; the LeaseInvariantTests disambiguate which.
        var busy = new Dictionary<string, double>();
        foreach (var w in workers)
            busy[w.Name] = tracker.GetElapsedSeconds(w.Name) == 0d ? 0d : 1d;

        var finalState = outcome == OutcomeClass.RetriedThenDone ? "Done" : outcome.ToString();
        return new ScenarioTrace(rpcTrace, merged, proxyCalls, finalState, ledgerTrace, busy);
    }

    private static TraceProxyCall SummarizeProxy(string url, Dictionary<string, object> body, bool stream)
    {
        string? model = null;
        int? maxTokens = null;
        int? nPredict = null;
        if (body.TryGetValue("model", out var m) && m is string ms) model = ms;
        if (body.TryGetValue("max_tokens", out var mt) && mt is int mti) maxTokens = mti;
        if (body.TryGetValue("n_predict", out var np) && np is int npi) nPredict = npi;
        return new TraceProxyCall(url, stream, model, maxTokens, nPredict);
    }

    /// <summary>
    /// Normalize the raw RPC call list to produce a deterministic trace.
    ///
    /// Problem: WorkerSchedulerService.EnginePrefillChunkedAndStoreAsync kicks
    /// off Store.PutChunked on a background Task.Run inside the onPayloadLen
    /// callback, while the main thread continues with onChunk and records
    /// EnginePrefill after chunk delivery completes. Both add to the same
    /// RpcCalls list, so their order depends on thread scheduling and is
    /// non-deterministic across runs.
    ///
    /// Fix: Normalize the EnginePrefill + PutChunked pair so PutChunked always
    /// appears first (consistent with the chunked-save golden ordering). This
    /// is a single-element swap; all other ops remain in their original
    /// completion order.
    /// </summary>
    private static IReadOnlyList<TraceRpcCall> NormalizeRpcTrace(
        List<(OpCode Op, string Key, int PayloadLen, string Status)> raw)
    {
        var calls = raw.Select(c => new TraceRpcCall(c.Op.ToString(), c.Key, c.PayloadLen, c.Status)).ToList();

        // Normalize the EnginePrefill + PutChunked race:
        // PutChunked always before EnginePrefill (matching chunked_save golden).
        int prefillIdx = -1;
        int putChunkedIdx = -1;
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i].Op == "EnginePrefill" && prefillIdx < 0)
                prefillIdx = i;
            else if (calls[i].Op == "PutChunked" && putChunkedIdx < 0)
                putChunkedIdx = i;
        }
        if (prefillIdx >= 0 && putChunkedIdx >= 0 && prefillIdx < putChunkedIdx)
        {
            // EnginePrefill appears before PutChunked — swap so PutChunked is first.
            (calls[prefillIdx], calls[putChunkedIdx]) = (calls[putChunkedIdx], calls[prefillIdx]);
        }

        return calls;
    }
}

/// <summary>
/// Implementation-agnostic scenario driver (epic #591 WP3): the SAME catalog
/// specs run against both the legacy scheduler and the v2 scheduler. Legacy
/// direct-drive seams (DispatchAsync / RunItemPipeline / CreateWorkItem) are
/// legacy-only and marked <c>LegacyOnly</c> on their specs.
/// </summary>
internal interface IScenarioDriver : IAsyncDisposable
{
    string SessionId { get; }
    SessionLedger Ledger { get; }
    int WarmLeaseCount { get; }

    Task<object?> SubmitAsync(
        string sessionId, int estimatedTokens, int maxTokens = 100,
        bool stream = false, string? prefixHash = null, string? forceMode = null,
        int systemPromptTokens = 0, CancellationToken ct = default);

    Task SettleAsync();
    ScenarioTrace CaptureTrace(string sessionId, OutcomeClass outcome, Exception? error = null);
}
