using System.Diagnostics;

namespace Hydra.Store.Models;

public enum WorkItemState
{
    Pending = 0, RouteDecision, WaitingPrefill, ModelLoadPrefill, PrefixRestore,
    Prefill, SaveKv, SaveDone, MarkEvicted, PickDecode, WaitingDecode,
    ModelLoadDecode, RestoreKv, Decode, BgSave, Done, Failed, Cancelled
}

public sealed class WorkItem
{
    public Dictionary<string, object> Request { get; }
    public List<Dictionary<string, object>> Messages { get; }
    public string SessionId { get; }
    public string TraceId { get; }
    public string? PrefixHash { get; }
    public int EstimatedTokens { get; }
    public int EstimatedNewTokens { get; }
    public TaskCompletionSource<object?> Completion { get; }

    public WorkItemState State { get; set; } = WorkItemState.Pending;
    private volatile bool _cancelled;
    public bool IsCancelled => _cancelled || Completion.Task.IsCanceled;

    public Exception? Error { get; set; }
    public object? Response { get; set; }
    public WorkerConfig? PrefillWorker { get; set; }
    public WorkerConfig? DecodeWorker { get; set; }
    public int? PrefillSlot { get; set; }
    public int? DecodeSlot { get; set; }
    public string RouteType { get; set; } = "";
    public SessionEntry? Entry { get; set; }
    public int NPastAfter { get; set; }
    public Dictionary<string, long> Phases { get; } = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    public long ElapsedMs => (Stopwatch.GetTimestamp() - _startTimestamp) * 1000 / Stopwatch.Frequency;
    public IAsyncEnumerable<byte[]>? DecodeChunks { get; set; }
    public int? LastIdSlot { get; set; }

    public WorkItem(Dictionary<string, object> request, List<Dictionary<string, object>> messages,
        string sessionId, string traceId, string? prefixHash, int estimatedTokens, int estimatedNewTokens)
    {
        Request = request; Messages = messages; SessionId = sessionId;
        TraceId = traceId; PrefixHash = prefixHash;
        EstimatedTokens = estimatedTokens; EstimatedNewTokens = estimatedNewTokens;
        Completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Cancel() { _cancelled = true; Completion.TrySetCanceled(); }
    public bool IsStreaming => Request.TryGetValue("stream", out var s) && s is true;
}
