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
	public string RouteType { get; set; } = "";
	public SessionEntry? Entry { get; set; }
	public int NPastAfter { get; set; }
	public Dictionary<string, long> Phases { get; } = new();
	private readonly long _startTimestamp = Stopwatch.GetTimestamp();
	public long ElapsedMs => (Stopwatch.GetTimestamp() - _startTimestamp) * 1000 / Stopwatch.Frequency;
	public IAsyncEnumerable<byte[]>? DecodeChunks { get; set; }
	public int? LastIdSlot { get; set; }

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
