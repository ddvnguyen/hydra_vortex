using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Hydra.Core.Models;

public enum WorkItemState
{
	None = 0,
	RouteDecision = 2,
	ModelLoadPrefill = 4,
	PrefixRestore = 6,
	Prefill = 8,
	SaveKv = 10,
	SaveDone = 12,
	MarkEvicted = 14,
	PickDecode = 16,
	ModelLoadDecode = 18,
	RestoreKv = 20,
	Decode = 22,
	BgSave = 24,
	Done = 26,
	Failed = 28,
	Cancelled = 30,
	Retry = 32
}

/// <summary>
/// How many GPUs this request needs. The evaluator uses this to check
/// availability before dispatching, avoiding slot contention entirely.
/// </summary>
public enum RequestType
{
	/// <summary>1 GPU: prefill + decode together (small prompt, below AtomicThreshold).</summary>
	Atomic,
	/// <summary>1 GPU: decode only (warm affinity, migration, KV restore).</summary>
	Solo,
	/// <summary>1 GPU: prefill only; will re-enqueue as Decode after prefill completes.</summary>
	Prefill,
	/// <summary>1 GPU: decode only (post-prefill handoff — highest priority).</summary>
	Decode,
	/// <summary>2 GPUs: head slot + peer exclusive (COMBINED/PIPELINE mode).</summary>
	Combined,
}

/// <summary>Wraps a WorkItem with queue metadata for priority ordering.</summary>
public sealed class QueueItem
{
	private static long _sequence;

	public WorkItem WorkItem { get; }
	public RequestType Type { get; set; }
	/// <summary>Lower = higher priority. Post-prefill decode gets 0; prefill gets 40.</summary>
	public int Priority { get; }
	public DateTime EnqueuedAt { get; }
	/// <summary>Monotonic tiebreaker — ensures SortedSet never treats two items as equal.</summary>
	public long Sequence { get; }

	public QueueItem(WorkItem workItem, RequestType type, int priority)
	{
		WorkItem = workItem;
		Type = type;
		Priority = priority;
		EnqueuedAt = DateTime.UtcNow;
		Sequence = Interlocked.Increment(ref _sequence);
	}
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
	/// <summary>Token count of the system prompt portion of the messages (used for prefix-save truncation guard, #245).</summary>
	public int SystemPromptTokens { get; set; }
	/// <summary>n_past of the prefix checkpoint blob read from Store (used for prefix-restore n_past guard, #245).</summary>
	public int PrefixNPast { get; set; }
	public TaskCompletionSource<object?> Completion { get; }
	public TaskCompletionSource<IAsyncEnumerable<byte[]>> StreamCompletion { get; }
	/// <summary>Signalled by the controller when streaming response completes — the decode slot can be released.</summary>
	public ChannelWriter<bool> StreamDone { get; }
	private readonly ChannelWriter<bool> _streamDoneWriter;

	public WorkItemState State { get; set; } = WorkItemState.None;
	/// <summary>Request type for the unified queue evaluator — how many GPUs does this request need?</summary>
	public RequestType RequestType { get; set; } = RequestType.Atomic;
	private volatile bool _cancelled;
	public bool IsCancelled => _cancelled || Completion.Task.IsCanceled;

	public Exception? Error { get; set; }
	public int NoWorkerRetries { get; set; }
	public int RetryCount { get; set; }
	public const int MaxRetries = 3;
	public object? Response { get; set; }
	public WorkerConfig? PrefillWorker { get; set; }
	public WorkerConfig? DecodeWorker { get; set; }
	public int? PrefillSlot { get; set; }
	public int? DecodeSlot { get; set; }
	public SlotLease? PrefillLease { get; set; }
	public SlotLease? DecodeLease { get; set; }
	/// <summary>Two-engine "work together": the mode chosen for this request (None = solo).</summary>
	public MultiEngineMode MultiMode { get; set; } = MultiEngineMode.None;
	/// <summary>Reservation on the recruited peer engine, held for the request's duration (None = solo).
	/// Either a <see cref="SlotLease"/> (legacy per-slot) or an
	/// <see cref="ExclusivePeerReservation"/> (P3.0+ per-GPU exclusivity, no slot).</summary>
	public IPeerReservation? PeerLease { get; set; }
	/// <summary>Name of the peer engine recruited (for status/metrics/logging).</summary>
	public string? MultiPeer { get; set; }
	/// <summary>
	/// Stock-params-shaped engine config the plan selected. Phase 2a
	/// (ddvnguyen/llama.cpp#36): the C# side derives this from
	/// <see cref="Services.ModelRegistry"/> via <see cref="WorkerConfig.ModelAlias"/>.
	/// The translator layer in <c>WorkerSchedulerService.ApplyMultiEngineAsync</c>
	/// projects this to the existing <c>0x44</c>/<c>0x46</c> wire payloads.
	/// </summary>
	public EngineConfig? MultiEngineConfig { get; set; }
	/// <summary>True when the chosen multi-engine mode could not be activated and we ran solo.</summary>
	public bool MultiFellBack { get; set; }
	/// <summary>
	/// Tri-state flag tracking hydra_config delivery via PrefillAsync:
	///   null  — not yet delivered this turn (auto-multiengine path); ApplyMultiEngineAsync sends its own activation PREFILL.
	///   true  — delivered and PREFILL succeeded; ApplyMultiEngineAsync skips the redundant empty-body PREFILL and records success telemetry.
	///   false — delivered but PREFILL fell back (NotImplemented / null); ApplyMultiEngineAsync skips the redundant empty-body PREFILL and records fallback telemetry.
	/// Cleared to null by ApplyMultiEngineAsync after consuming the value.
	/// </summary>
	public bool? HydraConfigDeliveredSucceeded { get; set; }

	/// <summary>
	/// Per-request engine overrides (T1 keys: sampling, n_predict, seed,
	/// stop). Phase 2b (ddvnguyen/llama.cpp#36). Populated by
	/// <c>WorkerSchedulerService.SubmitAsync</c> from the request body
	/// (mirror of the <c>force_mode</c> extraction); emitted as a 0x40
	/// EngineConfigure in <c>DecodeAsync</c> before
	/// <c>ApplyMultiEngineAsync</c>. <c>null</c> or
	/// <see cref="EngineRequestOverrides.IsEmpty"/> → no 0x40 call.
	/// </summary>
	public EngineRequestOverrides? RequestOverrides { get; set; }

	/// <summary>Debug: force a specific engine mode for this single request, bypassing
	/// MultiEngineRouter.Select. Set via <c>force_mode</c> in the request body (auto|solo|combined|pipeline).</summary>
	public string ForceMode { get; set; } = "";
	public string RouteType { get; set; } = "";
	public SessionEntry? Entry { get; set; }
	public int NPastAfter { get; set; }
	/// <summary>Prompt tokens (input) reported by llama-server usage; surfaced on the timeline.</summary>
	public int TokensIn { get; set; }
	/// <summary>Completion tokens (output) reported by llama-server usage; surfaced on the timeline.</summary>
	public int TokensOut { get; set; }
	/// <summary>Size of the KV state blob (KV + native checkpoint) saved/restored for this request, bytes.</summary>
	public long KvBytes { get; set; }
	/// <summary>KV state blob held in memory between Prefill→SaveKv and RestoreKv→Decode (engine mode).</summary>
	public byte[]? KvBlob { get; set; }
	/// <summary>True when RestoreKv loaded KV into the slot before Decode (engine mode cross-GPU).</summary>
	public bool KvRestoredForDecode { get; set; }
	/// <summary>Whether the prefix checkpoint was found in Store and restored before prefill.</summary>
	public bool PrefixCacheHit { get; set; }

	// ── Engine model identity (M-Perf.9 #289 / #470) ──
	/// <summary>Alias of the model that built the KV for this slot, e.g. "balanced".</summary>
	public string? KvModelAlias { get; set; }
	/// <summary>GGUF-derived tokenizer family, e.g. "llama", "gpt2".</summary>
	public string? KvTokenizer { get; set; }
	/// <summary>GGUF display name (from general.base_model.0.name or general.name).</summary>
	public string? KvModelName { get; set; }
	/// <summary>GGUF quantization label, e.g. "Q5_K".</summary>
	public string? KvModelQuant { get; set; }
	/// <summary>Bitwise capabilities from GGUF metadata (bit0=MTP, bit1=VISION, etc.).</summary>
	public uint KvModelCapabilities { get; set; }
	/// <summary>Full path of the GGUF file the KV was built with, e.g. "/models/.../Balanced.gguf".</summary>
	public string? KvModelPath { get; set; }
	/// <summary>True when the engine received a `model` value it could not resolve and fell back to the resident model.</summary>
	public bool KvModelFallback { get; set; }

	/// <summary>Build a <see cref="ModelIdentity"/> from the per-field KV identity properties.</summary>
	public ModelIdentity GetKvModelIdentity() => new()
	{
		Tokenizer = KvTokenizer ?? "",
		ModelName = KvModelName ?? "",
		ModelQuant = KvModelQuant ?? "",
		ModelCapabilities = KvModelCapabilities,
	};

	/// <summary>Populate per-field KV identity properties from a <see cref="ModelIdentity"/>.</summary>
	public void SetKvModelIdentity(ModelIdentity id)
	{
		KvTokenizer = id.Tokenizer;
		KvModelName = id.ModelName;
		KvModelQuant = id.ModelQuant;
		KvModelCapabilities = id.ModelCapabilities;
	}
	public Dictionary<string, long> Phases { get; } = new();
	private readonly long _startTimestamp = Stopwatch.GetTimestamp();
	public long ElapsedMs => (Stopwatch.GetTimestamp() - _startTimestamp) * 1000 / Stopwatch.Frequency;

	/// <summary>ElapsedMs at the moment of the first prefill attempt. Used by the
	/// BUSY guard to measure actual stuck-in-BUSY time, not total time-in-system.</summary>
	public long PrefillFirstAttemptMs { get; set; }

	/// <summary>Last known progress from the busy slot (0.0-1.0). Used by the
	/// progress-aware BUSY guard to distinguish stuck from slow.</summary>
	public float LastBusyProgress { get; set; }

	private long _lastCheckpointMs;
	/// <summary>Cumulative ms at decode dispatch — lets streaming finalize compute true decode duration.</summary>
	public long DecodeStartMs { get; set; }
	/// <summary>Engine-reported prefill ms (from timings.prompt_ms). When set, FinalizeStreamPhases
	/// splits decode_ms into prefill + pure-decode so the Grafana timeline bars don't double-count.</summary>
	public long EnginePrefillMs { get; set; }

	/// <summary>
	/// Record the duration of a phase as the time since the previous checkpoint
	/// (request start for the first phase). Phases[] holds per-phase durations,
	/// not cumulative elapsed time — stacked timeline bars sum to total_ms.
	/// </summary>
	public long RecordPhase(string name)
	{
		var now = ElapsedMs;
		var duration = now - _lastCheckpointMs;
		Phases[name] = duration;
		_lastCheckpointMs = now;
		return duration;
	}
	public IAsyncEnumerable<byte[]>? DecodeChunks { get; set; }
	public int? LastIdSlot { get; set; }
	public CancellationToken HttpCancellationToken { get; set; }
	public CancellationTokenSource? PipelineCts { get; set; }

	public WorkItem(
	  	Dictionary<string, object> request,
	  	List<Dictionary<string, object>> messages,
		string sessionId,
		string traceId,
		string? prefixHash,
		int estimatedTokens,
		int estimatedNewTokens)
	{
		Request = request;
		Messages = messages;
		SessionId = sessionId;
		TraceId = traceId;
		PrefixHash = prefixHash;
		EstimatedTokens = estimatedTokens;
		EstimatedNewTokens = estimatedNewTokens;
		Completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		StreamCompletion = new TaskCompletionSource<IAsyncEnumerable<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
		var streamDone = Channel.CreateBounded<bool>(1);
		StreamDone = streamDone.Writer;
		_streamDoneWriter = streamDone.Writer;
	}

	public void Cancel() { _cancelled = true; Completion.TrySetCanceled(); }
	public bool IsStreaming => Request.TryGetValue("stream", out var s) && IsTruthy(s);

	private static bool IsTruthy(object? v) => v switch
	{
		true => true,
		false => false,
		JsonElement je when je.ValueKind == JsonValueKind.True => true,
		JsonElement je when je.ValueKind == JsonValueKind.False => false,
		_ => false
	};
}
