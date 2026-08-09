using Hydra.Core.Models;
using Hydra.Core.Services;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// The v2 request model — deliberately SMALL and typed (the rewrite's "simple
/// model" rule). It carries only what the state runners + orchestrator need;
/// the legacy <c>WorkItem</c> (285 lines of accumulated plumbing) is not used
/// by v2. State runners read and mutate this as the machine advances.
/// </summary>
public sealed class SchedulerRequest
{
    /// <summary>Max re-routes after a transient engine fault (mirrors the hydra model).</summary>
    public const int MaxRetries = 3;

    public ChatRequest Chat { get; }
    public RequestType Type { get; }
    public int Priority { get; }
    public RouteDecision Plan { get; set; }

    public WorkItemState State { get; set; } = WorkItemState.RouteDecision;
    public WorkerConfig? PrefillWorker { get; set; }
    public WorkerConfig? DecodeWorker { get; set; }
    public SlotLease? PrefillLease { get; set; }
    public SlotLease? DecodeLease { get; set; }

    public int NPastAfter { get; set; }
    public byte[]? KvBlob { get; set; }
    public object? Response { get; set; }
    public IAsyncEnumerable<byte[]>? DecodeChunks { get; set; }
    public int RetryCount { get; set; }
    public Exception? Error { get; set; }

    private readonly Dictionary<string, long> _phaseMs = new();

    public string SessionId => Chat.SessionId;
    public string TraceId => Chat.TraceId;
    public bool IsStreaming => Chat.Stream;
    public bool IsTerminal => State is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled;

    /// <summary>Resolves the non-streaming completion; <see cref="StreamReady"/> resolves streaming.</summary>
    public TaskCompletionSource<ICompletionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<IAsyncEnumerable<byte[]>> StreamReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SchedulerRequest(ChatRequest chat, RequestType type, int priority)
    {
        Chat = chat;
        Type = type;
        Priority = priority;
        Plan = new RouteDecision(type, PrefillWorker: null, DecodeWorker: null, ReuseStoreState: false, priority);
    }

    public void RecordPhase(string key, long elapsedMs) => _phaseMs[key] = elapsedMs;
    public IReadOnlyDictionary<string, long> Phases => _phaseMs;
}
