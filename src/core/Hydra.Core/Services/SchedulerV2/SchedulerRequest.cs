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

    /// <summary>n_past of the restored prefix checkpoint (0 = none / not found).</summary>
    public int PrefixNPast { get; set; }

    /// <summary>True when a prefix checkpoint was restored into the prefill slot
    /// (only set on a successful StatePut — a Store hit alone does not count).</summary>
    public bool PrefixCacheHit { get; set; }

    /// <summary>The PHYSICAL slot the prefill wrote the KV into (review #4): the
    /// same-node decode skip is only safe when the held slot matches this — never
    /// decode over a slot that does not hold this request's KV (#469).</summary>
    public int? KvSlotId { get; set; }

    /// <summary>The caller's CancellationToken (review #3): threaded through the
    /// evaluator/pipeline so a client disconnect can abort in-flight RPCs and
    /// release the slot, instead of CancellationToken.None.</summary>
    public CancellationToken CallerToken { get; set; } = CancellationToken.None;

    /// <summary>Model identity of the slot that built the KV (from the engine's
    /// PREFILL response meta). Gate A (DECODE 0x43) sends this as kv_metadata and
    /// the cross-model guard compares it against the restore slot's identity.</summary>
    public ModelIdentity KvIdentity { get; set; } = ModelIdentity.Empty;

    public object? Response { get; set; }
    public IAsyncEnumerable<byte[]>? DecodeChunks { get; set; }
    public int RetryCount { get; set; }
    public Exception? Error { get; set; }

    /// <summary>C4 store-reuse flag: this request's KV comes from the STORE, not
    /// from a fresh prefill in the held slot — RestoreRunner must NOT take the
    /// same-node skip (the slot was never prefilled into).</summary>
    public bool RestoreFromStore { get; set; }

    /// <summary>UtcNow when the decode stream was handed to the caller (set by
    /// DecodeRunner). The streaming reaper finalizes + releases any request whose
    /// stream was handed off but <c>NotifyStreamComplete</c> never arrived within
    /// the handoff timeout. Default (never set) requests are never reaped.</summary>
    public DateTime StreamStartedAt { get; set; }

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
