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
	Cancelled = 30
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
	public TaskCompletionSource<IAsyncEnumerable<byte[]>> StreamCompletion { get; }
	/// <summary>Signalled by the controller when streaming response completes — the decode slot can be released.</summary>
	public ChannelWriter<bool> StreamDone { get; }
	private readonly ChannelWriter<bool> _streamDoneWriter;

	public WorkItemState State { get; set; } = WorkItemState.None;
	private volatile bool _cancelled;
	public bool IsCancelled => _cancelled || Completion.Task.IsCanceled;

	public Exception? Error { get; set; }
	public object? Response { get; set; }
	public WorkerConfig? PrefillWorker { get; set; }
	public WorkerConfig? DecodeWorker { get; set; }
	public int? PrefillSlot { get; set; }
	public int? DecodeSlot { get; set; }
	public SlotLease? PrefillLease { get; set; }
	public SlotLease? DecodeLease { get; set; }
	/// <summary>Two-engine "work together": the mode chosen for this request (None = solo).</summary>
	public MultiEngineMode MultiMode { get; set; } = MultiEngineMode.None;
	/// <summary>Lease on the recruited peer engine, held for the request's duration (None = solo).</summary>
	public SlotLease? PeerLease { get; set; }
	/// <summary>Name of the peer engine recruited (for status/metrics/logging).</summary>
	public string? MultiPeer { get; set; }
	/// <summary>The --override-tensor split pushed to the peer (for status surfacing).</summary>
	public string? MultiSplit { get; set; }
	/// <summary>True when the chosen multi-engine mode could not be activated and we ran solo.</summary>
	public bool MultiFellBack { get; set; }
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
	public Dictionary<string, long> Phases { get; } = new();
	private readonly long _startTimestamp = Stopwatch.GetTimestamp();
	public long ElapsedMs => (Stopwatch.GetTimestamp() - _startTimestamp) * 1000 / Stopwatch.Frequency;
	private long _lastCheckpointMs;
	/// <summary>Cumulative ms at decode dispatch — lets streaming finalize compute true decode duration.</summary>
	public long DecodeStartMs { get; set; }

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
