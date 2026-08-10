using System.Text.Json;
using Hydra.Shared;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #592: worker-health recovery.
//
// Live repro (2026-08-08): a request 503'd at the decode handoff because the
// health monitor flagged the worker unhealthy (3× health_poll_failed during a
// ~150s inline model swap) and the flag was STILL set when the swap finished
// and the PREFILL succeeded — the router excluded the worker even though it
// had just served the request.
//
// Fix mechanisms under test:
//  1. PREFILL success = liveness evidence → clears the stale unhealthy flag
//     BEFORE the decode-handoff routing decision.
//  2. The health monitor re-marks healthy on the first successful poll
//     (covered by HealthMonitorService.PollWorkerAsync — no test here).
//  3. ColdRouteAsync runs a bounded direct liveness probe on free-but-unhealthy
//     workers before excluding them.
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class HealthRecoveryTests
{
	/// <summary>
	/// Health monitor with per-node settable flags and a functional MarkHealthy
	/// (flips the flag + fires HealthyChanged on an actual flip, mirroring the
	/// production HealthMonitorService semantics).
	/// </summary>
	internal sealed class FakeHealthMonitor : IHealthMonitorService
	{
		private readonly Dictionary<string, bool> _healthy = new(StringComparer.Ordinal);
		/// <summary>Count of unhealthy→healthy flips via MarkHealthy.</summary>
		public int RecoveryFlips;

		public void SetHealthy(string nodeName, bool healthy) => _healthy[nodeName] = healthy;
		public bool IsHealthy(string nodeName) => _healthy.TryGetValue(nodeName, out var h) ? h : true;
		public bool IsStoreHealthy => true;
		public int? GetIdleSlot(string nodeName) => null;
		public NodeInfo? GetNodeInfo(string nodeName) => null;
		public Dictionary<string, object> GetHealthSummary() => new();
		public void UpdateNodeModelIdentity(string nodeName, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
		public void MarkHealthy(string nodeName)
		{
			if (IsHealthy(nodeName)) return;
			_healthy[nodeName] = true;
			RecoveryFlips++;
			HealthyChanged?.Invoke();
		}
		public event Action? HealthyChanged;
		public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
		public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	}

	/// <summary>
	/// RPC client that answers EnginePrefill with success and runs an optional
	/// hook at the START of the response — used to flip the health flag while
	/// the prefill is "in flight", simulating a poll-cycle failure during an
	/// inline model swap.
	/// </summary>
	internal sealed class PrefillHealthRpcClient : RpcClient
	{
		/// <summary>Invoked before each EnginePrefill success response is returned.</summary>
		public Action? OnPrefill;

		private readonly List<OpCode> _calls = new();

		public PrefillHealthRpcClient() : base("test", 0) { }

		public bool HasCall(OpCode op) => _calls.Contains(op);

		public override Task<RpcResponse> RequestAsync(
			OpCode op, string key, ReadOnlyMemory<byte> payload,
			string traceId, CancellationToken ct)
		{
			_calls.Add(op);
			if (op == OpCode.EnginePrefill)
			{
				OnPrefill?.Invoke();
				return Task.FromResult(new RpcResponse(
					(byte)StatusCode.Ok,
					JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096 }),
					new byte[4096]));
			}
			return Task.FromResult(new RpcResponse(
				(byte)StatusCode.Ok,
				JsonSerializer.Serialize(new { n_past = 2000, stored = true, restored = true, erased = true }),
				[]));
		}
	}

	internal sealed class StubCompletionProxy : ICompletionProxyService
	{
		public int NonStreamingCalls;

		public Task<Dictionary<string, object>> ProxyCompletionAsync(
			string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
		{
			NonStreamingCalls++;
			return Task.FromResult(new Dictionary<string, object>
			{
				["id_slot"] = 0,
				["usage"] = JsonSerializer.SerializeToElement(new { total_tokens = 100 })
			});
		}

		public async IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(
			string nodeUrl, Dictionary<string, object> body, string traceId,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
		{
			yield break;
		}

		public Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct)
			=> Task.FromResult(true);

		public Task<Dictionary<string, object>> PollDecodeResultAsync(
			string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
			=> Task.FromResult(new Dictionary<string, object> { ["id_slot"] = 0 });

		public async IAsyncEnumerable<byte[]> PollDecodeStreamAsync(
			string nodeUrl, int decodeRequestId, string traceId,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, WorkItem? item = null)
		{
			yield break;
		}

		public Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
			=> Task.CompletedTask;
	}

	private sealed class Fixture : IAsyncDisposable
	{
		public CoordinatorConfig Cfg { get; }
		public SessionLedger Ledger { get; }
		public WorkerTracker Tracker { get; }
		public FakeHealthMonitor Health { get; }
		public PrefillHealthRpcClient Rpc { get; }
		public WorkerSchedulerService Scheduler { get; }
		private readonly CancellationTokenSource _runCts = new();
		private readonly Task _runTask;

		public Fixture(FakeHealthMonitor? health = null)
		{
			Health = health ?? new FakeHealthMonitor();
			Rpc = new PrefillHealthRpcClient();
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
					new() { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },
					new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
				}
			};
			foreach (var w in Cfg.Workers)
				Tracker.InitWorker(w.Name, w.Slots);

			var sp = new ServiceCollection().BuildServiceProvider();
			Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker,
				new StubCompletionProxy(), Health, Rpc, sp, Serilog.Log.Logger);
			Scheduler.AgentClientFactory = (_, _) => Rpc;
			// Hermetic: stub the llama HTTP boundary (probe + state META) so the
			// suite never dials the live engine URLs in Cfg.
			Scheduler.LlamaClientFactory = _ => new TestLlamaClient();
			// Fail busy-retry loops fast in tests.
			Scheduler.BusyTimeoutOverride = (_, _) => (stuckMs: 100, slowMs: 200);

			// Register the "nano" alias used by SubmitAsync so the unknown-model
			// validation passes.
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

		public async Task<object?> SubmitAsync(
			string sessionId, int estimatedTokens, int maxTokens = 100)
		{
			var req = new Dictionary<string, object>
			{
				["stream"] = false,
				["max_tokens"] = maxTokens,
				["model"] = "nano"
			};
			var msgs = new List<Dictionary<string, object>>
			{
				new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
			};
			return await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
				maxTokens, null, _runCts.Token);
		}
	}

		// ── Fix 1: PREFILL success clears a stale unhealthy flag ──────────────

	[Fact]
	public async Task PrefillSuccess_ClearsStaleUnhealthyFlag_BeforeDecodeHandoff()
	{
		await using var f = new Fixture();
		var health = f.Health;
		var rpc = f.Rpc;

		// Simulate the #592 live repro: the item is ROUTED while the worker is
		// healthy, then the health flag flips unhealthy DURING the prefill
		// (3× health_poll_failed while the engine does its inline model swap),
		// and the prefill still completes successfully.
		health.SetHealthy("rtx", true);
		rpc.OnPrefill = () => health.SetHealthy("rtx", false);

		var result = await f.SubmitAsync("sess_recover_1", 500);

		// The request must NOT be 503'd at the decode handoff: the successful
		// PREFILL on rtx is liveness evidence, so the flag is cleared before
		// the handoff routing decision.
		Assert.True(health.IsHealthy("rtx"),
			"successful PREFILL must re-mark the worker healthy even when the health flag flipped during the prefill");
		Assert.Equal(1, health.RecoveryFlips);
		Assert.NotNull(result);
		Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill), "prefill must have run on the worker");
	}

	[Fact]
	public async Task PrefillSuccess_WhenAlreadyHealthy_NoRedundantFlip()
	{
		await using var f = new Fixture();
		var health = f.Health;
		health.SetHealthy("rtx", true);

		await f.SubmitAsync("sess_recover_2", 500);

		// The recovery path is a no-op when the flag is already healthy — no
		// spurious flip (and no spurious HealthyChanged → evaluator wake).
		Assert.Equal(0, health.RecoveryFlips);
	}

	// ── Fix 3: cold-route liveness probe on free-but-unhealthy workers ────
	//
	// NB: these drive the item pipeline directly instead of SubmitAsync. In
	// production the #592 repro is an item already IN FLIGHT (past the
	// CanServeRequest admission gate) that re-enters ColdRouteAsync after the
	// health flag flipped — queued items are gated on IsHealthy and wait for
	// the monitor's own poll, which is a separate (already-correct) path.

	[Fact]
	public async Task ColdRoute_StaleUnhealthyFreeWorker_RoutesWhenDirectProbeSucceeds()
	{
		await using var f = new Fixture();
		var health = f.Health;
		// The exact #592 handoff state: worker is FREE but flagged unhealthy
		// (the health monitor hasn't polled since the flag flipped). No
		// prefill-success recovery happened for this request yet — the probe
		// must clear the flag so the route can proceed.
		health.SetHealthy("rtx", false);

		var item = MakeItem("sess_probe_1");
		await f.Scheduler.RunItemPipeline(item, RequestType.Atomic, CancellationToken.None);
		// The SaveDone→PickDecode handoff re-enqueues for the evaluator; wait
		// for the full pipeline to finish before asserting.
		var completed = await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.NotNull(completed);
		Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
			"stale-unhealthy + free worker must be probed and then serve the request");
		Assert.True(health.IsHealthy("rtx"),
			"a successful direct liveness probe must clear the stale unhealthy flag");
		Assert.Equal(1, health.RecoveryFlips);
	}

	[Fact]
	public async Task ColdRoute_StaleUnhealthyFreeWorker_StaysExcluded_WhenProbeFails()
	{
		await using var f = new Fixture();
		var health = f.Health;
		health.SetHealthy("rtx", false);
		health.SetHealthy("p100", false);
		// Probe returns negative — the worker stays excluded and routing finds
		// no prefill-capable worker instead of dispatching onto an engine that
		// direct probing says is down.
		f.Scheduler.LlamaClientFactory = _ => new ProbeFailingLlamaClient();

		var item = MakeItem("sess_probe_2");
		item.State = WorkItemState.RouteDecision;
		var next = await f.Scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.None, next);
		Assert.False(health.IsHealthy("rtx"),
			"a failed liveness probe must NOT clear the unhealthy flag");
		Assert.Equal(0, health.RecoveryFlips);
		Assert.False(f.Rpc.HasCall(OpCode.EnginePrefill),
			"no prefill may be dispatched to a worker whose direct probe failed");
	}

	// ── #597: probes run in parallel and coalesce across concurrent callers ──

	[Fact]
	public async Task ColdRoute_ConcurrentRequests_ShareOneInFlightProbe()
	{
		await using var f = new Fixture();
		var health = f.Health;
		health.SetHealthy("rtx", false);
		// Probe #1 blocks on a gate; if a second probe ever fires for the same
		// worker it would count and fail the assertion below. The probe timeout
		// is widened so the gate (not the 5s production bound) governs release.
		var probe = new GatedProbeLlamaClient();
		f.Scheduler.LlamaClientFactory = _ => probe;
		f.Scheduler.LivenessProbeTimeout = TimeSpan.FromSeconds(60);

		var item1 = MakeItem("sess_coalesce_1");
		var item2 = MakeItem("sess_coalesce_2");
		var t1 = Task.Run(() => f.Scheduler.RunItemPipeline(item1, RequestType.Atomic, CancellationToken.None));
		// Wait until probe #1 is actually in flight (blocked on the gate).
		await probe.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

		var t2 = Task.Run(() => f.Scheduler.RunItemPipeline(item2, RequestType.Atomic, CancellationToken.None));
		// Grace window for item2 to reach the shared probe. Probe #1 is held
		// open on the gate, so while it is in the dictionary no probe #2 can
		// start — item2 must observe the same in-flight task.
		await Task.Delay(TimeSpan.FromSeconds(2));

		probe.Release.TrySetResult();
		await Task.WhenAll(t1, t2);

		Assert.Equal(1, probe.HealthCalls);
		Assert.True(health.IsHealthy("rtx"));
		Assert.Equal(1, health.RecoveryFlips);
		Assert.NotNull(await item1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
		Assert.NotNull(await item2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	private static WorkItem MakeItem(string sessionId) => new(
		new Dictionary<string, object>
		{
			["stream"] = false,
			["max_tokens"] = 100,
			["model"] = "nano"
		},
		new List<Dictionary<string, object>>
		{
			new() { ["role"] = "user", ["content"] = new string('x', 500) }
		},
		sessionId,
		"trace_probe",
		null,
		500,
		100
	);

	/// <summary>LlamaClient whose direct liveness probe (GET /health) always fails.</summary>
	private sealed class ProbeFailingLlamaClient : TestLlamaClient
	{
		public override Task<bool> HealthAsync(CancellationToken ct) => Task.FromResult(false);
	}

	/// <summary>
	/// LlamaClient whose first liveness probe counts the call, signals
	/// <see cref="FirstEntered"/> and then blocks until <see cref="Release"/> is
	/// set — used to hold a probe in flight while a second cold request
	/// arrives, so coalescing is observable (HealthCalls stays 1).
	/// </summary>
	private sealed class GatedProbeLlamaClient : TestLlamaClient
	{
		public int HealthCalls;
		public readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async Task<bool> HealthAsync(CancellationToken ct)
		{
			Interlocked.Increment(ref HealthCalls);
			FirstEntered.TrySetResult();
			await Release.Task.WaitAsync(ct);
			return true;
		}
	}
}
