using System.Net.Sockets;
using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #635 fix 2: prefill RPC retry budget is crash-blind.
//
// Live repro (smoke #8): MaxRetries=3 instant retries ~100ms apart burned the
// whole budget in ~4s — sized for transient errors, not a crashed engine, with
// no wait-for-restart and no backoff. A retry hitting the worker's RPC port
// while the engine is down (connection refused) must treat it as "engine
// restarting" and back off longer.
//
// Fix under test: PrefillRetryBackoff returns 500ms/2s/8s for ordinary RPC
// errors (~10.5s for 3 retries) and 1s/4s/16s (~21s) when the exception chain
// contains SocketError.ConnectionRefused — giving the engine time to come back
// before the budget expires. RetryBackoffOverride is the test seam (near-zero
// here so crash-retry loops fail fast).
//
// ALSO covers #635 fix 3's slot-leak regression: each failed prefill attempt
// must release the cold_atomic DecodeLease (the route holds the prefill slot
// via DecodeLease, PrefillLease is null) — otherwise the tracker accumulates
// phantom-busy slots, IsFree flips false, and the re-enqueued item strands in
// the queue after Retries=2 (the observed smoke #8 symptom).
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class PrefillRetryBackoffTests
{
	// ── Unit: the default backoff table ─────────────────────────────────

	[Theory]
	[InlineData(1, false, 500)]   // first retry → 500ms
	[InlineData(2, false, 2000)]  // second retry → 2s
	[InlineData(3, false, 8000)]  // third retry → 8s
	[InlineData(1, true, 1000)]   // engine restarting → 1s
	[InlineData(2, true, 4000)]   // engine restarting → 4s
	[InlineData(3, true, 16000)]  // engine restarting → 16s
	public void PrefillRetryBackoff_ReturnsExpectedTable(int retryCount, bool restarting, int expectedMs)
	{
		var delay = WorkerSchedulerService.PrefillRetryBackoff(retryCount, restarting);
		Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
	}

	[Fact]
	public void PrefillRetryBackoff_ClampsOutOfRangeRetryCount()
	{
		// Defensive: never index out of bounds regardless of RetryCount drift.
		// retryCount<=0 (not reached in production — the catch increments first)
		// clamps to the first entry; oversized values clamp to the last.
		Assert.Equal(TimeSpan.FromMilliseconds(500), WorkerSchedulerService.PrefillRetryBackoff(0, engineRestarting: false));
		Assert.Equal(TimeSpan.FromSeconds(8), WorkerSchedulerService.PrefillRetryBackoff(99, engineRestarting: false));
		Assert.Equal(TimeSpan.FromSeconds(16), WorkerSchedulerService.PrefillRetryBackoff(99, engineRestarting: true));
	}

	// ── Integration: the retry loop honors the seam + restart detection ──

	[Fact]
	public async Task ConnectionRefusedPrefill_TreatsAsEngineRestarting_AndRetriesThenFails()
	{
		await using var f = new Fixture(new ConnectionRefusedRpcClient());
		var backoffCalls = new List<(int RetryCount, bool Restarting)>();
		// Near-zero delay: the crash-retry loop must fail fast in tests.
		f.Scheduler.RetryBackoffOverride = (retryCount, restarting) =>
		{
			backoffCalls.Add((retryCount, restarting));
			return TimeSpan.FromMilliseconds(1);
		};

		var ex = await Assert.ThrowsAsync<SocketException>(
			() => f.SubmitAsync("sess_refused", 500));

		// ConnectionRefused on the worker RPC port = engine down → every retry
		// must be classified as engine-restarting (longer backoff).
		Assert.Equal(3, backoffCalls.Count);
		Assert.All(backoffCalls, c => Assert.True(c.Restarting,
			"connection-refused retries must be classified as engine-restarting"));
		Assert.Equal(new[] { 1, 2, 3 }, backoffCalls.Select(c => c.RetryCount).ToArray());
		Assert.IsType<SocketException>(ex);

		// #635 fix 3 regression: every failed prefill attempt must have
		// released the cold_atomic DecodeLease — no phantom-busy slots may
		// accumulate and gate the next attempt/queued item.
		Assert.Equal(2, f.Tracker.FreeSlotCount("rtx"));
		Assert.True(f.Tracker.FreeSlotCount("rtx") == 2,
			"failed prefill retries must not leak the cold_atomic DecodeLease (tracker free slots restored)");
	}

	[Fact]
	public async Task TransientPrefillError_UsesNormalBackoff_NotEngineRestarting()
	{
		await using var f = new Fixture(new ThrowingRpcClient(new InvalidOperationException("transient RPC glitch")));
		var backoffCalls = new List<(int RetryCount, bool Restarting)>();
		f.Scheduler.RetryBackoffOverride = (retryCount, restarting) =>
		{
			backoffCalls.Add((retryCount, restarting));
			return TimeSpan.FromMilliseconds(1);
		};

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => f.SubmitAsync("sess_transient", 500));

		// A non-socket RPC error is NOT connection-refused → normal schedule.
		Assert.Equal(3, backoffCalls.Count);
		Assert.All(backoffCalls, c => Assert.False(c.Restarting,
			"a transient (non-socket) RPC error must use the normal backoff"));
		Assert.IsType<InvalidOperationException>(ex);
		Assert.Equal(2, f.Tracker.FreeSlotCount("rtx"));
	}

	// ── Doubles / fixture ───────────────────────────────────────────────

	private sealed class ConnectionRefusedRpcClient : RpcClient
	{
		public int PrefillCalls;
		public ConnectionRefusedRpcClient() : base("test", 0) { }

		public override Task<RpcResponse> RequestAsync(
			OpCode op, string key, ReadOnlyMemory<byte> payload,
			string traceId, CancellationToken ct)
		{
			if (op == OpCode.EnginePrefill)
				Interlocked.Increment(ref PrefillCalls);
			throw new SocketException((int)SocketError.ConnectionRefused);
		}
	}

	private sealed class ThrowingRpcClient : RpcClient
	{
		private readonly Exception _ex;
		public ThrowingRpcClient(Exception ex) : base("test", 0) => _ex = ex;

		public override Task<RpcResponse> RequestAsync(
			OpCode op, string key, ReadOnlyMemory<byte> payload,
			string traceId, CancellationToken ct)
			=> throw _ex;
	}

	private sealed class Fixture : IAsyncDisposable
	{
		public WorkerSchedulerService Scheduler { get; }
		public WorkerTracker Tracker { get; }
		private readonly CancellationTokenSource _runCts = new();
		private readonly Task _runTask;

		public Fixture(RpcClient rpc)
		{
			var cfg = new CoordinatorConfig
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
				}
			};
			var ledger = new SessionLedger();
			Tracker = new WorkerTracker();
			foreach (var w in cfg.Workers) Tracker.InitWorker(w.Name, w.Slots);

			var sp = new ServiceCollection().BuildServiceProvider();
			Scheduler = new WorkerSchedulerService(cfg, ledger, Tracker,
				new TestCompletionProxy(), new TestHealthMonitor(), rpc, sp, Serilog.Log.Logger);
			Scheduler.AgentClientFactory = (_, _) => rpc;
			// Hermetic: stub the llama HTTP boundary (probe + state META).
			Scheduler.LlamaClientFactory = _ => new TestLlamaClient();
			Scheduler.BusyTimeoutOverride = (_, _) => (stuckMs: 100, slowMs: 200);

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
			ModelRegistry.ClearForTest();
		}

		public async Task<object?> SubmitAsync(string sessionId, int estimatedTokens)
		{
			var req = new Dictionary<string, object>
			{
				["stream"] = false,
				["max_tokens"] = 100,
				["model"] = "nano"
			};
			var msgs = new List<Dictionary<string, object>>
			{
				new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
			};
			return await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
				100, null, _runCts.Token);
		}
	}
}
