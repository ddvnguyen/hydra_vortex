using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.TestHelpers;

namespace Tests.Core.Services;

/// <summary>
/// Issue #712: solo prefix reuse — when force_mode="solo" and the session has
/// a prior KV checkpoint in the Store, the coordinator restores the full
/// session KV before PREFILL so the engine's shared-prefix detection only
/// prefills the delta (new tokens since the last turn).
/// </summary>
public sealed class SoloPrefixReuseTests
{
	private static CoordinatorConfig MakeConfig(bool soloPrefixReuse = true) => new()
	{
		UseLlamaEngine = true,
		PrefixCheckpointEnabled = true,
		SoloPrefixReuseEnabled = soloPrefixReuse,
		AtomicThreshold = 2048,
		WarmThreshold = 5120,
		NPastGuardTolerance = 50,
		Workers =
		[
			new() { Name = "rtx", Host = "localhost", RpcPort = 9601,
				LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2,
				PrefillPriority = 1, DecodePriority = 2 },
		],
	};

	private static (WorkerSchedulerService scheduler, SessionLedger ledger, WorkerTracker tracker, FakeStoreClient store)
		MakeScheduler(CoordinatorConfig? cfg = null, FakeStoreClient? engineStore = null)
	{
		cfg ??= MakeConfig();
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
		var proxy = new CompletionProxyService();
		var health = new TestHealthMonitor();
		var store = new FakeStoreClient();
		var sp = new ServiceCollection().BuildServiceProvider();
		var scheduler = new WorkerSchedulerService(
			cfg, ledger, tracker, proxy, health, store, sp, Serilog.Log.Logger);
		if (engineStore != null)
			scheduler.AgentClientFactory = (_, _) => engineStore;
		return (scheduler, ledger, tracker, store);
	}

	private static WorkItem MakeSoloItem(string sessionId, int estimatedTokens = 500)
	{
		var item = new WorkItem(
			new Dictionary<string, object> { ["stream"] = false },
			[
				new() { ["role"] = "system", ["content"] = "You are helpful." },
				new() { ["role"] = "user", ["content"] = "test" },
			],
			sessionId, "trace_1", prefixHash: null, estimatedTokens, estimatedNewTokens: estimatedTokens);
		item.ForceMode = "solo";
		return item;
	}

	// ── 1. cold_atomic route: session with store state → PrefixRestore ──

	[Fact]
	public async Task SoloPrefixReuse_ColdAtomic_WithStoreState_RoutesToPrefixRestore()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_prefix_1";

		// Simulate prior turn: session had KV saved to Store
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[1024]);

		var item = MakeSoloItem(sessionId, estimatedTokens: 500);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// cold_atomic + HasStoreState → PrefixRestore
		Assert.Equal(WorkItemState.PrefixRestore, next);
	}

	// ── 2. cold_concurrency route: large prompt → PrefixRestore ──

	[Fact]
	public async Task SoloPrefixReuse_ColdConcurrency_RoutesToPrefixRestore()
	{
		var (scheduler, ledger, tracker, store) = MakeScheduler();
		var item = MakeSoloItem("sess_solo_large", estimatedTokens: 3500);

		// cold_concurrency with UseLlamaEngine=true → PrefixRestore
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.PrefixRestore, next);
	}

	// ── 3. PrefixRestoreAsync: Store hit → restores KV, PrefixCacheHit=true ──

	[Fact]
	public async Task SoloPrefixReuse_StoreHit_RestoresAndReturnsPrefill()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_prefix_hit";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = MakeSoloItem(sessionId, estimatedTokens: 3500);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.True(item.PrefixCacheHit);
		Assert.Equal(3000, item.PrefixNPast);
	}

	// ── 4. PrefixRestoreAsync: Store miss → falls back to full Prefill ──

	[Fact]
	public async Task SoloPrefixReuse_StoreMiss_FallsBackToPrefill()
	{
		var engineStore = new FakeStoreClient();
		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_prefix_miss";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.NotFound);

		var item = MakeSoloItem(sessionId, estimatedTokens: 3500);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.False(item.PrefixCacheHit);
	}

	// ── 5. n_past guard: estimated < n_past → skip restore ──

	[Fact]
	public async Task SoloPrefixReuse_NPastGuard_SkipsRestore()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":5000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_prefix_guard";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 5000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[1024]);

		// Use a gap that exceeds even the new proportional tolerance:
		// tolerance = max(128, 5000*0.05) = 250 → 4500 + 250 = 4750 < 5000 → skip
		var item = MakeSoloItem(sessionId, estimatedTokens: 4500);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// n_past guard fires: estimated 4500 + tolerance 250 = 4750 < 5000 = skip
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.False(item.PrefixCacheHit);
		// StatePut was NOT called (guard prevented restore)
		Assert.Equal(0, engineStore.CallCount(OpCode.StatePut));
	}

	// ── 6. Feature flag off → original behaviour ──

	[Fact]
	public async Task SoloPrefixReuse_Disabled_RoutesToPrefill()
	{
		var cfg = MakeConfig(soloPrefixReuse: false);
		var (scheduler, ledger, tracker, store) = MakeScheduler(cfg);
		var sessionId = "sess_solo_prefix_disabled";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);

		var item = MakeSoloItem(sessionId, estimatedTokens: 500);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// With SoloPrefixReuseEnabled=false, cold_atomic returns Prefill
		Assert.Equal(WorkItemState.Prefill, next);
	}

	// ── 7. StatePut returns non-Ok → fallback to Prefill, no false cache hit ──

	[Fact]
	public async Task SoloPrefixReuse_StatePutFailed_FallsBackToPrefill()
	{
		var engineStore = new FakeStoreClient();
		// Store GET succeeds, but engine StatePut returns a non-Ok status
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Error);

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_put_fail";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[1024]);

		var item = MakeSoloItem(sessionId, estimatedTokens: 3500);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// StatePut failed → clean fallback to Prefill, no false cache hit
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.False(item.PrefixCacheHit);
		// StatePut was attempted (Store GET succeeded)
		Assert.Equal(1, engineStore.CallCount(OpCode.StatePut));
	}

	// ── 8. First solo turn (no ledger entry) → no-op, straight to Prefill ──

	[Fact]
	public async Task SoloPrefixReuse_FirstTurn_NoLedgerEntry_RoutesToPrefill()
	{
		var (scheduler, ledger, tracker, store) = MakeScheduler();
		var sessionId = "sess_solo_first_turn";

		// No ledger.Register — session has never been seen before.
		// ColdRouteAsync should route to cold_atomic; HasStoreState is
		// false (no entry), so SoloPrefixReuseEnabled gate is skipped.

		var item = MakeSoloItem(sessionId, estimatedTokens: 500);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// cold_atomic + no ledger entry → Prefill (no restore attempted)
		Assert.Equal(WorkItemState.Prefill, next);
	}

	// ── 9. HasStoreState=true + PrefixHash!=null + PrefixCheckpointEnabled=true ──
	//     → session-KV restore takes priority over prefix-checkpoint path

	[Fact]
	public async Task SoloPrefixReuse_HasStoreState_PrioritizesSessionKvOverCheckpoint()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_priority";

		// Session has prior KV saved (HasStoreState=true) AND a prefix hash
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);

		// Store has the session KV blob (for TryRestoreSessionKvAsync)
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// Create item with PrefixHash set via constructor (WorkItem.PrefixHash is read-only)
		var item = new WorkItem(
			new Dictionary<string, object> { ["stream"] = false },
			[
				new() { ["role"] = "system", ["content"] = "You are helpful." },
				new() { ["role"] = "user", ["content"] = "test" },
			],
			sessionId, "trace_1", prefixHash: "abc123", estimatedTokens: 3500, estimatedNewTokens: 3500);
		item.ForceMode = "solo";
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Session-KV restore must have been called (not the prefix-checkpoint path).
		// The session KV is a superset of the system-prompt checkpoint.
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.True(item.PrefixCacheHit);
		Assert.Equal(3000, item.PrefixNPast);
		// Store Get was called with the session KV key, not the prefix key
		var getCalls = store.Calls.Where(c => c.Op == OpCode.Get).ToList();
		Assert.Contains(getCalls, c => c.Key == $"{sessionId}.kv");
		Assert.DoesNotContain(getCalls, c => c.Key.StartsWith("prefix/"));
	}

	// ── 10. Empty-payload restore → fail fast, no false cache hit ──

	[Fact]
	public async Task SoloPrefixReuse_EmptyStorePayload_FailsFast()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_empty";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		// Store returns Ok but with zero-length payload
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: Array.Empty<byte>());

		var item = MakeSoloItem(sessionId, estimatedTokens: 3500);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Empty payload → clean fallback to Prefill, no false cache hit
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.False(item.PrefixCacheHit);
		// StatePut was NOT attempted (empty payload caught before wire)
		Assert.Equal(0, engineStore.CallCount(OpCode.StatePut));
	}

	// ── 11. n_past tolerance: NPast = Est + 65 (one ACK turn) → must restore ──

	[Fact]
	public async Task SoloPrefixReuse_NPastTolerance_RestoresWithAckTurnGrowth()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3065}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_tolerance";

		// NPast = 3065 (3000 prompt + 65 ACK tokens from prior turn)
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3065);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// EstimatedTokens = 3000 (current prompt size, same as prior turn's prompt)
		// Old tolerance (50): 3000 + 50 = 3050 < 3065 → would SKIP (false positive)
		// New tolerance: max(128, 3065*0.05) = max(128, 153) = 153 → 3000 + 153 = 3153 > 3065 → restores
		var item = MakeSoloItem(sessionId, estimatedTokens: 3000);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Must restore (not skip) — the ACK growth is within proportional tolerance
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.True(item.PrefixCacheHit);
		Assert.Equal(3065, item.PrefixNPast);
	}

	// ── 12. n_past tolerance: NPast >> Est (truncated history) → must skip ──

	[Fact]
	public async Task SoloPrefixReuse_NPastTolerance_SkipsTruncatedHistory()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":10000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_skip";

		// NPast = 10000 (large prior session)
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 10000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// EstimatedTokens = 500 (client truncated history significantly)
		// tolerance = max(128, 10000*0.05) = 500 → 500 + 500 = 1000 < 10000 → must skip
		var item = MakeSoloItem(sessionId, estimatedTokens: 500);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Must skip restore — the prompt is far shorter than cached NPast
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.False(item.PrefixCacheHit);
	}

	// ── 13. Delta prefill: TruncateMessagesForDelta cuts messages at PrefixNPast ──

	[Fact]
	public async Task PrefillAsync_DeltaPrefill_TruncatesMessagesForDelta()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_delta";

		// Prior turn: 3000 tokens saved to Store
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// 4 messages: system (~20 tok) + user_x (~3000 tok) + assistant (~3 tok) + user_y (~500 tok)
		// Total ~3523 tokens. PrefixNPast=3000 → delta starts mid user_x message.
		var item = new WorkItem(
			new Dictionary<string, object> { ["stream"] = false },
			[
				new() { ["role"] = "system",    ["content"] = "You are a helpful assistant." },
				new() { ["role"] = "user",      ["content"] = new string('x', 12000) },
				new() { ["role"] = "assistant",  ["content"] = "I understand." },
				new() { ["role"] = "user",      ["content"] = new string('y', 2000) },
			],
			sessionId, "trace_1", prefixHash: null, estimatedTokens: 3500, estimatedNewTokens: 3500);
		item.ForceMode = "solo";
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		// Run PrefixRestore → sets PrefixCacheHit=true, PrefixNPast=3000
		var afterRestore = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.Prefill, afterRestore);
		Assert.True(item.PrefixCacheHit);
		Assert.Equal(3000, item.PrefixNPast);

		// Verify truncation: with 4 messages and PrefixNPast=3000, the system message
		// (~20 tokens) is below threshold, user_x (~3000 tokens) straddles the boundary
		// so it becomes the first delta message. Expected: messages[1..] (3 messages).
		var delta = WorkerSchedulerService.TruncateMessagesForDeltaPublic(
			item.Messages, item.PrefixNPast);
		// System msg (~20 tok) < 3000 → skipped. user_x straddles → included.
		Assert.Equal(3, delta.Count);
		Assert.Equal("user", delta[0].GetValueOrDefault("role"));
	}
}
