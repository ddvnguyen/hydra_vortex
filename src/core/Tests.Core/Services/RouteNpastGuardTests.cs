using System.Collections.Concurrent;
using System.Reflection;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.TestHelpers;

namespace Tests.Core.Services;

/// <summary>
/// Issue #435: synthetic test for the in-RouteAsync n_past guard. Verifies that
/// the new <c>hydra_warm_slot_evicted_for_short_prompt_total{reason="warm_slot_n_past_guard"}</c>
/// counter increments when a warm-slot lookup has <c>n_past</c> much larger
/// than the new request's estimated tokens, and that the request is re-routed
/// (not silently reused against a stale cache).
///
/// The guard sits in <c>WorkerSchedulerService.RouteAsync</c> at the in-RouteAsync
/// n_past check. In a real deployment, the verify-201-moe agent missed this
/// path because <c>_warmLeases</c> accumulated entries without hitting the LRU
/// cap (issue #201 comment). The test bypasses the in-memory fast path by
/// pre-populating both the ledger (so the entry lookup is non-null with
/// n_past=50000) and <c>_warmLeases</c> (per the issue spec), then submits a
/// short follow-up request (estimated_tokens=1000) to force the predicate
/// <c>EstimatedTokens &lt; NPast * NPastGuardThreshold</c> to evaluate true.
/// </summary>
public sealed class RouteNpastGuardTests
{
	private static CoordinatorConfig MakeConfig() => new()
	{
		// No real llama-server in tests — skip the warm-slot HTTP verify so
		// RouteAsync proceeds past the verify step and reaches the n_past guard.
		WarmSlotVerificationEnabled = false,
		// Keep the eviction path simple — no prefix-checkpoint / chunked paths.
		PrefixCheckpointEnabled = false,
		EnableChunks = false,
		// Default AtomicThreshold (2048) and NPastGuardThreshold (0.6) make
		// the guard's predicate (NPast > 2048*4) ∧ (EstimatedTokens < 0.6*NPast)
		// evaluate true for (NPast=50000, EstimatedTokens=1000):
		//   50000 > 8192   ✓
		//   1000  < 30000  ✓
		Workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", Host = "localhost", RpcPort = 9601,
				LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2,
				PrefillPriority = 1, DecodePriority = 2 },
		},
	};

	private static WorkerSchedulerService MakeScheduler(FakeStoreClient fake)
	{
		var cfg = MakeConfig();
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
		var proxy = new CompletionProxyService();
		var health = new TestHealthMonitor();
		var sp = new ServiceCollection().BuildServiceProvider();
		var scheduler = new WorkerSchedulerService(
			cfg, ledger, tracker, proxy, health, fake, sp, Serilog.Log.Logger);
		// Inject the fake for both Agent RPC and the llama binary RPC. The
		// n_past guard's eviction path issues StateGet / StateMeta against the
		// llama RPC; both return Ok with empty payload under FakeStoreClient,
		// which the eviction path tolerates (it only does a Put to Store if
		// stateResp.Payload.Length > 0).
		scheduler.AgentClientFactory = (_, _) => fake;
		return scheduler;
	}

	private static WorkItem MakeItem(string sessionId, int estimatedTokens) => new(
		new Dictionary<string, object> { ["stream"] = false },
		new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } },
		sessionId,
		"trace_npast_guard",
		prefixHash: null,
		estimatedTokens,
		estimatedNewTokens: 50);

	[Fact]
	public async Task RouteAsync_WarmSlotWithLargeNPast_ShortRequest_TriggersGuard()
	{
		// ── Arrange ────────────────────────────────────────────────────────
		// Scheduler + warm slot pre-populated. The issue spec asks to pre-populate
		// _warmLeases with a hot entry; the n_past guard's predicate itself
		// reads from the ledger (entry.NPast), but populating both makes the
		// scenario faithful to the real "warm-slot found, but cache too large"
		// path the guard protects.
		var fake = new FakeStoreClient();
		var scheduler = MakeScheduler(fake);
		const string sessionId = "warm-session-1";
		const int nPast = 50000;
		const int estimatedTokens = 1000;

		var ledger = (SessionLedger)typeof(WorkerSchedulerService)
			.GetField("_ledger", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(scheduler)!;
		var warmLeases = (ConcurrentDictionary<string, SlotLease>)typeof(WorkerSchedulerService)
			.GetField("_warmLeases", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(scheduler)!;
		var tracker = (WorkerTracker)typeof(WorkerSchedulerService)
			.GetField("_tracker", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(scheduler)!;

		// Pre-populate the ledger with a hot entry: large n_past, slot held.
		// NPromptTokens = estimatedTokens makes the *first* n_past guard
		// (the shrinkage check at #428's `guardBaseline` line) NOT fire —
		// only the in-RouteAsync n_past guard we want to test.
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: nPast, prefixHash: null);
		ledger.UpdateNPromptTokens(sessionId, estimatedTokens);

		// Pre-populate _warmLeases with a hot entry for the same session —
		// the issue spec asks for this. The second n_past guard doesn't
		// strictly need it; the entry is here to keep the scenario faithful.
		var lease = new SlotLease("rtx", 0, sessionId, LeaseLifetime.Long, tracker);
		warmLeases.TryAdd(sessionId, lease);

		// Warm slot is detected (sanity-check the setup).
		Assert.True(warmLeases.ContainsKey(sessionId), "warm slot should be pre-populated");

		var counterBefore = CoordinatorMetrics
			.WarmSlotEvictedForShortPrompt
			.WithLabels("warm_slot_n_past_guard")
			.Value;

		// ── Act ────────────────────────────────────────────────────────────
		var item = MakeItem(sessionId, estimatedTokens);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// ── Assert ─────────────────────────────────────────────────────────

		// (1) Counter incremented by exactly 1. Using before/after to stay
		// safe under parallel test execution (static Prometheus counter).
		var counterAfter = CoordinatorMetrics
			.WarmSlotEvictedForShortPrompt
			.WithLabels("warm_slot_n_past_guard")
			.Value;
		Assert.Equal(counterBefore + 1, counterAfter);

		// (2) The warm slot was evicted — the guard's main job. After the
		// guard fires, _ledger.MarkEvicted sets SlotFreed=true on the entry.
		var entryAfter = ledger.Lookup(sessionId);
		Assert.NotNull(entryAfter);
		Assert.True(entryAfter!.SlotFreed, "n_past guard should mark the entry as evicted");

		// (3) The request was re-routed: item.State moved out of None into
		// PickDecode (set by the guard at WorkerSchedulerService.cs:585) and
		// the returned next state is NOT Decode — Decode would mean the warm
		// slot was reused against the stale cache.
		Assert.NotEqual(WorkItemState.Done, next);
		Assert.NotEqual(WorkItemState.Failed, next);
		Assert.NotEqual(WorkItemState.Cancelled, next);
		Assert.NotEqual(WorkItemState.Decode, next);
		Assert.Equal(WorkItemState.PickDecode, item.State);
	}
}
