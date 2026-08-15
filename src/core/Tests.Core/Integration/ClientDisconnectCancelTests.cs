using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #613 — the coordinator must cancel the in-flight decode when the client
// disconnects (HttpContext.RequestAborted / stream cancellation): abort the
// completion SSE stream + release the slot lease. Coordinator-side ONLY in
// the first pass (no engine RPC cancel — a later enhancement). Must hold on
// every decode path: HTTP proxy (solo / COMBINED) and merged-decode.
//
// Previously a timed-out request kept generating: the coordinator did not
// abort the decode, so the engine slot stayed busy for the full generation
// and subsequent requests 503'd for minutes (observed: Dense27bCombined hit
// its 300s client timeout, then 5 immediate 503s).
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Proxy double with a gated stream so a test can cancel mid-flight.
/// Faithful to the real CompletionProxyService:
///   * ProxyCompletionStreamAsync blocks until the test opens the gate OR the
///     decode cancellation token fires (models StreamReader.ReadLineAsync(ct)
///     — the HTTP-proxy abort mechanism is closing the request, so the stream
///     simply ends on cancellation; no engine-side cancel exists on this path).
///   * PollDecodeStreamAsync mirrors the merged path: on cancellation it fires
///     CancelDecodeAsync (DELETE /v1/decode/{id}) — the coordinator-side engine
///     abort for merged decode.
/// A disposed-but-never-cancelled token (a sibling request's completion
/// disposing this decode's pipeline cts — the #613 session-map race) leaves
/// the gate blocked forever, exactly like ReadLineAsync(ct) with a token whose
/// source can no longer fire.</summary>
internal sealed class DisconnectTestProxy : ICompletionProxyService
{
	private readonly object _lock = new();
	private readonly List<TaskCompletionSource> _httpGates = new();
	private readonly List<TaskCompletionSource> _pollGates = new();
	private readonly List<int> _cancelledPollStreams = new();

	public int HttpStreamStarts { get; private set; }
	public int PollStreamStarts { get; private set; }
	/// <summary>1-based poll-stream index whose ct fired → CancelDecodeAsync ran.</summary>
	public IReadOnlyList<int> CancelledPollStreams => _cancelledPollStreams;

	public void OpenHttpGate(int index = 0) { lock (_lock) { if (index < _httpGates.Count) _httpGates[index].TrySetResult(); } }
	public void OpenPollGate(int index = 0) { lock (_lock) { if (index < _pollGates.Count) _pollGates[index].TrySetResult(); } }

	public async Task WaitForHttpStreamsAsync(int count, CancellationToken timeoutCt)
	{
		var deadline = Task.Delay(5000, timeoutCt);
		while (!timeoutCt.IsCancellationRequested)
		{
			int n;
			lock (_lock) { n = _httpGates.Count; }
			if (n >= count) return;
			if (deadline.IsCompleted)
				throw new TimeoutException($"expected {count} HTTP streams, saw {n}");
			await Task.Delay(20, timeoutCt);
		}
	}

	public async Task WaitForPollStreamsAsync(int count, CancellationToken timeoutCt)
	{
		var deadline = Task.Delay(5000, timeoutCt);
		while (!timeoutCt.IsCancellationRequested)
		{
			int n;
			lock (_lock) { n = _pollGates.Count; }
			if (n >= count) return;
			if (deadline.IsCompleted)
				throw new TimeoutException($"expected {count} poll streams, saw {n}");
			await Task.Delay(20, timeoutCt);
		}
	}

	private static async Task WaitGateAsync(TaskCompletionSource gate, CancellationToken ct)
	{
		try
		{
			using var reg = ct.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), gate);
			await gate.Task;
			ct.ThrowIfCancellationRequested();
		}
		catch (ObjectDisposedException)
		{
			// The token's source was disposed without firing (a sibling request's
			// NotifyStreamComplete disposed this decode's pipeline cts). Faithful to
			// ReadLineAsync(ct): the read never unblocks because the token can never
			// fire — the decode keeps generating, the slot stays busy.
			await gate.Task;
		}
	}

	private static byte[] Sse(string data) => Encoding.UTF8.GetBytes($"{data}\n\n");

	public async IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(
		string nodeUrl, Dictionary<string, object> body, string traceId,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
	{
		TaskCompletionSource gate;
		lock (_lock) { _httpGates.Add(gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)); HttpStreamStarts++; }
		await WaitGateAsync(gate, ct);
		yield return Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}");
		yield return Sse("data: {\"id_slot\":0,\"usage\":{\"total_tokens\":10}}");
	}

	public async IAsyncEnumerable<byte[]> PollDecodeStreamAsync(
		string nodeUrl, int decodeRequestId, string traceId,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
		WorkItem? item = null)
	{
		int index;
		TaskCompletionSource gate;
		lock (_lock)
		{
			index = _pollGates.Count;
			_pollGates.Add(gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
			PollStreamStarts++;
		}
		try
		{
			await WaitGateAsync(gate, ct);
			yield return Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}");
			yield return Sse("data: {\"id_slot\":0,\"usage\":{\"total_tokens\":10}}");
		}
		finally
		{
			// Mirrors the real PollDecodeStreamAsync: the merged-path engine abort.
			if (ct.IsCancellationRequested)
			{
				lock (_lock) _cancelledPollStreams.Add(index + 1);
				await CancelDecodeAsync(nodeUrl, decodeRequestId, traceId, CancellationToken.None);
			}
		}
	}

	public Task<Dictionary<string, object>> ProxyCompletionAsync(
		string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
		=> Task.FromResult(new Dictionary<string, object>
		{
			["id_slot"] = 0,
			["usage"] = JsonSerializer.SerializeToElement(new { total_tokens = 10 })
		});

	public Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct)
		=> Task.FromResult(true);

	public Task<Dictionary<string, object>> PollDecodeResultAsync(
		string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
		=> Task.FromResult(new Dictionary<string, object>
		{
			["id_slot"] = 0,
			["usage"] = JsonSerializer.SerializeToElement(new { total_tokens = 10 })
		});

	public Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
		=> Task.CompletedTask;
}

/// <summary>Plain health monitor (GetNodeInfo → null): DecodeAsync takes the
/// HTTP-proxy streaming path (no merged_decode capability).</summary>
internal sealed class PlainHealthMonitor : IHealthMonitorService
{
	public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	public bool IsHealthy(string nodeName) => true;
	public bool IsStoreHealthy => true;
	public int? GetIdleSlot(string nodeName) => 0;
	public NodeInfo? GetNodeInfo(string nodeName) => null;
	public Dictionary<string, object> GetHealthSummary() => new();
	public event Action? HealthyChanged;
	public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
	public void MarkHealthy(string nodeName) { }
}

/// <summary>Health monitor advertising merged_decode so DecodeAsync enters the
/// merged path (PollDecodeStreamAsync + CancelDecodeAsync).</summary>
internal sealed class MergedCapableHealthMonitor : IHealthMonitorService
{
	public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	public bool IsHealthy(string nodeName) => true;
	public bool IsStoreHealthy => true;
	public int? GetIdleSlot(string nodeName) => 0;
	public NodeInfo? GetNodeInfo(string nodeName) => new()
	{
		NodeName = nodeName,
		Healthy = true,
		SlotsTotal = 2,
		SlotsIdle = 2,
		EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			Protocol.CapMergedDecode
		},
	};
	public Dictionary<string, object> GetHealthSummary() => new();
	public event Action? HealthyChanged;
	public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
	public void MarkHealthy(string nodeName) { }
}

/// <summary>RPC double: Ok for every op (Store + engine binary RPCs) and a
/// successful framed merged DECODE (DecodeRequestId = 7).</summary>
internal sealed class DisconnectRpcClient : RpcClient
{
	public DisconnectRpcClient() : base("test", 0) { }

	public override Task<RpcResponse> RequestAsync(
		OpCode op, string key, ReadOnlyMemory<byte> payload,
		string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
	{
		var response = op switch
		{
			OpCode.EnginePrefill => new RpcResponse(
				(byte)StatusCode.Ok,
				JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096 }),
				new byte[4096]),
			OpCode.StateGet => new RpcResponse(
				(byte)StatusCode.Ok,
				JsonSerializer.Serialize(new { n_past = 2000 }),
				new byte[2048]),
			_ => new RpcResponse(
				(byte)StatusCode.Ok,
				JsonSerializer.Serialize(new { n_past = 2000, stored = true, restored = true, erased = true }),
				[]),
		};
		return Task.FromResult(response);
	}

	public override Task<MergedDecodeResponse> EngineMergedDecodeAsync(
		string slotKey, int nPast,
		string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
		string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
		string? modelAlias,
		string? messagesJson, int nPredict, string? samplingJson, bool stream,
		ReadOnlyMemory<byte> kvBlob,
		string traceId, CancellationToken ct)
		=> Task.FromResult(new MergedDecodeResponse
		{
			Status = (byte)StatusCode.Ok,
			Valid = true,
			DecodeRequestId = 7,
			NPastAfterRestore = nPast,
			TokenizerMatch = true,
			ModelNameMatch = true,
			ModelCapabilitiesMatch = true,
			ModelQuantMatch = true,
			ModelAliasMatch = true,
		});
}

internal sealed class DisconnectFixture : IAsyncDisposable
{
	public CoordinatorConfig Cfg { get; }
	public SessionLedger Ledger { get; }
	public WorkerTracker Tracker { get; }
	public DisconnectTestProxy Proxy { get; } = new();
	public IHealthMonitorService Health { get; }
	public DisconnectRpcClient Rpc { get; } = new();
	public WorkerSchedulerService Scheduler { get; }
	private readonly CancellationTokenSource _runCts = new();
	private readonly Task _runTask;

	public DisconnectFixture(bool mergedCapable)
	{
		Health = mergedCapable ? new MergedCapableHealthMonitor() : new PlainHealthMonitor();
		Ledger = new SessionLedger();
		Tracker = new WorkerTracker();

		Cfg = new CoordinatorConfig
		{
			RunMode = "fast",
			UseLlamaEngine = true,
			PrefixCheckpointEnabled = false,
			WarmSlotVerificationEnabled = false,
			MixPrecisionEnabled = false,
			AtomicThreshold = 2048,
			Workers = new List<WorkerConfig>
			{
				new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, Role = "head", PrefillPriority = 1, DecodePriority = 2 },
				new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
			}
		};
		foreach (var w in Cfg.Workers)
			Tracker.InitWorker(w.Name, w.Slots);

		var sp = new ServiceCollection().BuildServiceProvider();
		Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, Proxy, Health, Rpc,
			sp, Serilog.Log.Logger);
		Scheduler.AgentClientFactory = (_, _) => Rpc;
		Scheduler.LlamaClientFactory = _ => new TestLlamaClient();

		ModelRegistry.ClearForTest();
		ModelRegistry.RegisterForTest(new EngineConfig(
			ModelAlias: "nano",
			ModelPath: "/dev/null",
			NGpuLayers: 0, NCtx: 2048,
			ContBatching: true, Fit: false, UbatchSize: 512,
			SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));

		_runTask = Scheduler.RunAsync(_runCts.Token);
	}

	public async ValueTask DisposeAsync()
	{
		_runCts.Cancel();
		try { await _runTask; } catch (OperationCanceledException) { }
		_runCts.Dispose();
	}

	/// <summary>Submit a streaming request and return the SSE chunk stream. The
	/// caller owns the http ct — cancelling it simulates a client disconnect
	/// (RequestAborted). Blocks until the decode phase produces the stream.</summary>
	public async Task<IAsyncEnumerable<byte[]>> SubmitStreamingAsync(
		string sessionId, int estimatedTokens, int maxTokens, CancellationToken httpCt)
	{
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
		};
		var req = new Dictionary<string, object>
		{
			["stream"] = true,
			["max_tokens"] = maxTokens,
			["model"] = "nano",
			["messages"] = msgs,
		};
		var result = await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
			maxTokens, null, httpCt);
		return Assert.IsAssignableFrom<IAsyncEnumerable<byte[]>>(result);
	}
}

// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class ClientDisconnectCancelTests
{
	private static readonly TimeSpan AbortTimeout = TimeSpan.FromSeconds(5);

	/// <summary>Controller-style drain (no WithCancellation — the abort must come
	/// from the scheduler's own decode cancellation, not the client-side token).</summary>
	private static async Task DrainRawAsync(IAsyncEnumerable<byte[]> stream)
	{
		try
		{
			await foreach (var chunk in stream) { _ = chunk.Length; }
		}
		catch (OperationCanceledException)
		{
			// Aborted by the scheduler on disconnect — expected.
		}
	}

	/// <summary>Wait until the slot lease is gone and the tracker slot is free.</summary>
	private static async Task WaitForLeaseReleaseAsync(DisconnectFixture f)
	{
		for (var i = 0; i < 50 && (f.Scheduler.WarmLeaseCount != 0 || f.Tracker.FreeSlotCount("rtx") != 2); i++)
			await Task.Delay(20);
	}

	[Fact]
	public async Task Disconnect_MidStream_AbortsStream_AndReleasesLease_HttpPath()
	{
		await using var f = new DisconnectFixture(mergedCapable: false);
		var httpCts = new CancellationTokenSource();
		var stream = await f.SubmitStreamingAsync("sess_dc1", 2000, 100, httpCts.Token);

		// Mid-generation: the coordinator is draining the (gated) engine stream.
		var drain = Task.Run(() => DrainRawAsync(stream));
		await f.Proxy.WaitForHttpStreamsAsync(1, httpCts.Token);
		await Task.Delay(100);

		// Client disconnects → RequestAborted.
		httpCts.Cancel();

		// The scheduler must abort the completion SSE stream promptly — the
		// decode cancellation token fires, the proxy read ends, the stream closes.
		var aborted = await Task.WhenAny(drain, Task.Delay(AbortTimeout));
		Assert.True(aborted == drain, "HTTP-path SSE stream must abort on client disconnect");

		// Controller finally: NotifyStreamComplete releases the slot lease.
		await f.Scheduler.NotifyStreamComplete("sess_dc1");
		await WaitForLeaseReleaseAsync(f);

		Assert.Equal(0, f.Scheduler.WarmLeaseCount);
		Assert.Equal(2, f.Tracker.FreeSlotCount("rtx"));
		Assert.Empty(f.Scheduler._pipelineCts);
	}

	[Fact]
	public async Task Disconnect_MidStream_CancelsEngineDecode_AndReleasesLease_MergedPath()
	{
		await using var f = new DisconnectFixture(mergedCapable: true);
		var httpCts = new CancellationTokenSource();
		var stream = await f.SubmitStreamingAsync("sess_dc2", 2000, 100, httpCts.Token);

		var drain = Task.Run(() => DrainRawAsync(stream));
		await f.Proxy.WaitForPollStreamsAsync(1, httpCts.Token);
		await Task.Delay(100);

		httpCts.Cancel();

		var aborted = await Task.WhenAny(drain, Task.Delay(AbortTimeout));
		Assert.True(aborted == drain, "merged SSE stream must abort on client disconnect");

		// The merged-path engine abort fired: DELETE /v1/decode/{id} on the 1st poll stream.
		Assert.Contains(1, f.Proxy.CancelledPollStreams);

		await f.Scheduler.NotifyStreamComplete("sess_dc2");
		await WaitForLeaseReleaseAsync(f);

		Assert.Equal(0, f.Scheduler.WarmLeaseCount);
		Assert.Equal(2, f.Tracker.FreeSlotCount("rtx"));
		Assert.Empty(f.Scheduler._pipelineCts);
	}

	[Fact]
	public async Task ConcurrentSameSession_FirstDisconnect_MustNotKillSecondsAbortPath()
	{
		// The #613 session-map race: _pipelineCts was keyed by SessionId, so the
		// first request's NotifyStreamComplete disposed the pipeline cts of a
		// CONCURRENT request on the same session — that request's linked token
		// could never fire on ITS client disconnect, so its engine decode kept
		// generating (slot busy → 503 cascade for minutes). With the per-request
		// (TraceId) key, one request's completion can never kill another's abort.
		await using var f = new DisconnectFixture(mergedCapable: true);

		var aCts = new CancellationTokenSource();
		var bCts = new CancellationTokenSource();
		var streamA = await f.SubmitStreamingAsync("sess_dc3", 2000, 100, aCts.Token);
		var drainA = Task.Run(() => DrainRawAsync(streamA));
		await f.Proxy.WaitForPollStreamsAsync(1, aCts.Token);

		// Second concurrent request on the SAME session — B takes the free rtx
		// slot (2-slot head) while A still streams on its gated decode.
		var streamB = await f.SubmitStreamingAsync("sess_dc3", 2000, 100, bCts.Token);
		var drainB = Task.Run(() => DrainRawAsync(streamB));
		await f.Proxy.WaitForPollStreamsAsync(2, aCts.Token);
		await Task.Delay(100);

		// Both requests hold their own per-request pipeline cts.
		Assert.Equal(2, f.Scheduler._pipelineCts.Count);

		// A's client disconnects; the controller runs NotifyStreamComplete.
		aCts.Cancel();
		var doneA = await Task.WhenAny(drainA, Task.Delay(AbortTimeout));
		Assert.True(doneA == drainA, "request A's stream must abort on its disconnect");
		await f.Scheduler.NotifyStreamComplete("sess_dc3");

		// A's completion must NOT have disposed B's pipeline cts — B's abort
		// path stays live, keyed by its own TraceId.
		Assert.Equal(1, f.Scheduler._pipelineCts.Count);

		// B's client disconnects → B's stream must abort (its decode cts still fires).
		bCts.Cancel();
		var doneB = await Task.WhenAny(drainB, Task.Delay(AbortTimeout));
		Assert.True(doneB == drainB,
			"request B's decode must abort when ITS client disconnects — a sibling request's completion must not kill its abort path");

		await f.Scheduler.NotifyStreamComplete("sess_dc3");
		await WaitForLeaseReleaseAsync(f);
		Assert.Equal(0, f.Scheduler.WarmLeaseCount);
	}
}
