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
	private static CoordinatorConfig MakeConfig(bool soloPrefixReuse = true, int soloSaveWaitMs = 30000) => new()
	{
		UseLlamaEngine = true,
		PrefixCheckpointEnabled = true,
		SoloPrefixReuseEnabled = soloPrefixReuse,
		SoloSaveWaitMs = soloSaveWaitMs,
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

	// ── 13. Pin: after successful restore, full message list is preserved ──
	//    (A/B#3 verified: engine N_COMMON handles delta eval; char-based
	//     truncation is unsafe due to tokenizer accuracy, see A/B#4 regression).

	[Fact]
	public async Task SoloPrefixReuse_Restore_PreservesFullMessageListForPrefill()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3000}");

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_solo_pin";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

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

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Restore succeeded → PrefixCacheHit=true, PrefixNPast>0
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.True(item.PrefixCacheHit);
		Assert.Equal(3000, item.PrefixNPast);

		// #715 R4 pin: full message list must be preserved for the PREFILL body.
		// Engine N_COMMON handles delta eval (~300 ms); char-based truncation
		// undercounts real tokens (A/B#4 regression) and is not safe without
		// tokenizer-accurate boundaries.
		Assert.Equal(4, item.Messages.Count);
	}

	// ── #712 skip-PREFILL flow (this PR) ─────────────────────────────────────
	// When the session-KV restore lands on a single-engine cold route, the full
	// PREFILL is skipped and the DECODE is pinned to the restored slot; the
	// engine's completion-path n_common detection prefills only the delta.

	[Fact]
	public async Task SkipPrefill_Restore_WaitsForInFlightWriteStateToStore()
	{
		// Production save path for the streaming decode route: NotifyStreamComplete
		// captures the blob, releases the slot, then fire-and-forget
		// WriteStateToStoreAsync (store Put + MarkStoreState). While that Put is
		// in flight, the next turn's restore must wait — otherwise it reads the
		// previous turn's blob and the delta prefill covers two turns of history
		// (~1.9x baseline TTFT in the #712 P100 A/B).
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_write_wait";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 8386);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// Gate the store Put — the save tail is in flight, not yet committed
		var putGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		store.PreCallHook = op => op == OpCode.Put ? putGate.Task : Task.CompletedTask;

		var saveItem = MakeSoloItem(sessionId, estimatedTokens: 14532);
		// Production sequence: decode dispatch announces the deferred save
		// (_pendingBgSaves + RegisterSessionSave), then stream end captures the
		// blob and fire-and-forgets WriteStateToStoreAsync (store Put).
		scheduler.RegisterSessionSave(sessionId);
		var saveTask = scheduler.WriteStateToStoreAsync(new byte[4096], sessionId, "trace-save", saveItem, 100);
		await Task.Delay(50); // let the save reach the gated Put

		var restoreItem = StagedRestoreItem(sessionId, "cold_concurrency", estimatedTokens: 8000);
		var restoreTask = scheduler.DispatchAsync(restoreItem, CancellationToken.None);
		await Task.Delay(200);
		Assert.False(restoreTask.IsCompleted, "restore must wait for the in-flight store write");

		putGate.TrySetResult();
		await saveTask;
		var next = await restoreTask;

		Assert.Equal(WorkItemState.PickDecode, next);
		Assert.True(restoreItem.SoloKvRestoreHit);

		// Ordering proof: the restore's store GET landed AFTER the save's store PUT.
		// (OrderedCalls, not Calls — ConcurrentBag enumeration is not temporal.)
		var ordered = store.OrderedCalls;
		int putIdx = ordered.FindIndex(c => c.Op == OpCode.Put && c.Key == $"{sessionId}.kv");
		int getIdx = ordered.FindIndex(c => c.Op == OpCode.Get && c.Key == $"{sessionId}.kv");
		Assert.True(putIdx >= 0, "save must have written the session KV to the store");
		Assert.True(getIdx > putIdx, $"restore must read the store after the save committed: {string.Join(" | ", ordered)}");
	}

	[Fact]
	public async Task SkipPrefill_Restore_WaitsForInFlightBgSave()
	{
		// Pipeline-path coverage (WorkItemState.BgSave): the previous turn's
		// BgSave is still streaming the engine state; the restore must not read
		// the store until that save has committed.
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StateGet, (byte)StatusCode.Ok, payload: new byte[4096]);
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);
		var stateGetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		engineStore.PreCallHook = op => op == OpCode.StateGet ? stateGetGate.Task : Task.CompletedTask;

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_save_wait";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 8386);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// Turn N's BgSave in flight (StateGet gated — not yet committed)
		var saveItem = MakeSoloItem(sessionId, estimatedTokens: 14532);
		saveItem.State = WorkItemState.BgSave;
		saveItem.DecodeWorker = RtxWorker();
		saveItem.DecodeSlot = 0;
		saveItem.LastIdSlot = 0;
		var saveTask = scheduler.BgSaveAsync(saveItem);
		await Task.Delay(50); // let BgSaveAsync reach the gated StateGet

		// Turn N+1's restore — must block until the save commits.
		// estimatedTokens 8000 keeps the n_past guard happy against entry.NPast=8386
		// (guard: est + max(128, 5%) >= n_past).
		var restoreItem = StagedRestoreItem(sessionId, "cold_concurrency", estimatedTokens: 8000);
		var restoreTask = scheduler.DispatchAsync(restoreItem, CancellationToken.None);
		await Task.Delay(200);
		Assert.False(restoreTask.IsCompleted, "restore must wait for the in-flight save");

		stateGetGate.TrySetResult();
		await saveTask;
		var next = await restoreTask;

		Assert.Equal(WorkItemState.PickDecode, next);
		Assert.True(restoreItem.SoloKvRestoreHit);

		// Ordering proof: the restore's store GET landed AFTER the save's store PUT.
		// (OrderedCalls, not Calls — ConcurrentBag enumeration is not temporal.)
		var ordered = store.OrderedCalls;
		int putIdx = ordered.FindIndex(c => c.Op == OpCode.Put && c.Key == $"{sessionId}.kv");
		int getIdx = ordered.FindIndex(c => c.Op == OpCode.Get && c.Key == $"{sessionId}.kv");
		Assert.True(putIdx >= 0, "BgSave must have written the session KV to the store");
		Assert.True(getIdx > putIdx, $"restore must read the store after the save committed: {string.Join(" | ", ordered)}");
	}

	private const string FullStatePutMeta =
		"{\"n_past\":3000,\"restored\":true,\"bytes\":2048,\"model_match\":true,"
		+ "\"model_alias\":\"qwen3.5-9b-test\",\"model_path\":\"/mnt/kv_slots/Qwen3.5-9B-Q4_K_M.gguf\","
		+ "\"tokenizer\":\"llama\",\"model_name\":\"Qwen3.5-9B\",\"model_quant\":\"Q4_K\",\"model_capabilities\":1}";

	private static WorkerConfig RtxWorker() => new()
	{
		Name = "rtx", Host = "localhost", RpcPort = 9601,
		LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2,
	};

	private static WorkItem StagedRestoreItem(string sessionId, string routeType, int estimatedTokens = 3500)
	{
		var item = MakeSoloItem(sessionId, estimatedTokens);
		item.State = WorkItemState.PrefixRestore;
		item.RouteType = routeType;
		item.PrefillWorker = RtxWorker();
		item.PrefillSlot = 0;
		return item;
	}


	[Fact]
	public async Task SkipPrefill_ColdConcurrency_StoreHit_RoutesToPickDecodeWithStampedIdentity()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);

		var (scheduler, ledger, _, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_skip_cc";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = StagedRestoreItem(sessionId, "cold_concurrency");
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Restore hit on a solo-eligible route → skip Prefill+SaveKv, go to PickDecode
		Assert.Equal(WorkItemState.PickDecode, next);
		Assert.True(item.PrefixCacheHit);
		Assert.True(item.SoloKvRestoreHit);
		Assert.Equal(3000, item.PrefixNPast);
		Assert.Equal(3000, item.NPastAfter);
		// KV identity stamped from STATE_PUT meta (Gate A kv_metadata source)
		Assert.Equal("llama", item.KvTokenizer);
		Assert.Equal("Qwen3.5-9B", item.KvModelName);
		Assert.Equal("Q4_K", item.KvModelQuant);
		Assert.Equal(1u, item.KvModelCapabilities);
		Assert.Equal("qwen3.5-9b-test", item.KvModelAlias);
	}

	[Fact]
	public async Task SkipPrefill_ColdPd_StoreHit_StillRoutesToPrefill()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);

		var (scheduler, ledger, _, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_skip_pd";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// P/D split: the KV must stream to the decode node via the PREFILL M2
		// relay — skipping PREFILL would leave the decode node without KV.
		var item = StagedRestoreItem(sessionId, "cold_pd");
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.True(item.PrefixCacheHit);
		Assert.False(item.SoloKvRestoreHit);
	}

	[Fact]
	public async Task SkipPrefill_Migration_StoreHit_StillRoutesToPrefill()
	{
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);

		var (scheduler, ledger, _, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_skip_mig";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = StagedRestoreItem(sessionId, "migration");
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.False(item.SoloKvRestoreHit);
	}

	[Fact]
	public async Task SkipPrefill_ModelMismatch_StoreHit_StillRoutesToPrefill()
	{
		// model_match=false: the resident model differs from the KV's model —
		// the PREFILL path performs the inline model swap and must run in full.
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok,
			meta: "{\"n_past\":3000,\"model_match\":false,\"tokenizer\":\"llama\",\"model_name\":\"Other\",\"model_quant\":\"Q4_K\",\"model_capabilities\":1}");

		var (scheduler, ledger, _, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_skip_mismatch";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = StagedRestoreItem(sessionId, "cold_concurrency");
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.False(item.SoloKvRestoreHit);
	}

	[Fact]
	public async Task SkipPrefill_OldEngineMetaWithoutIdentity_StillRoutesToPrefill()
	{
		// Pre-#289 engines omit the identity fields from the STATE_PUT meta —
		// the merged DECODE frame would carry empty kv_metadata and Gate A
		// would reject it, so the full PREFILL path (which stamps identity from
		// the PREFILL result) must be used.
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: "{\"n_past\":3000}");

		var (scheduler, ledger, _, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_skip_oldmeta";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = StagedRestoreItem(sessionId, "cold_concurrency");
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.True(item.PrefixCacheHit);
		Assert.False(item.SoloKvRestoreHit);
	}

	[Fact]
	public async Task PickDecode_SkipHit_PrefillLease_ConvertsAndPinsRestoredSlot()
	{
		var (scheduler, _, tracker, _) = MakeScheduler();
		var sessionId = "sess_pin_convert";

		// cold_concurrency shape: short prefill lease owns the restored slot
		var item = MakeSoloItem(sessionId, 3500);
		item.State = WorkItemState.PickDecode;
		item.RouteType = "solo_prefix_reuse";
		item.SoloKvRestoreHit = true;
		item.PrefillWorker = RtxWorker();
		item.PrefillSlot = 0;
		Assert.True(tracker.TryAcquireSlot("rtx", out _, "prefill", pinnedSlot: 0));
		var prefillLease = new SlotLease("rtx", 0, sessionId, LeaseLifetime.Short, tracker);
		item.PrefillLease = prefillLease;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Decode, next);
		Assert.Equal("rtx", item.DecodeWorker?.Name);
		Assert.Equal(0, item.DecodeSlot);
		Assert.Null(item.PrefillLease);
		// Review #732 finding 1: the decode lease must be a fresh LONG lease —
		// the old code transferred the Short prefill lease object, which
		// FinalizeAsync disposed mid-stream (slot released while generating).
		Assert.NotSame(prefillLease, item.DecodeLease);
		Assert.Equal(LeaseLifetime.Long, item.DecodeLease!.Lifetime);
		Assert.Equal("rtx", item.DecodeLease.WorkerName);
		Assert.Equal(0, item.DecodeLease.SlotId);
		// The slot stayed held exactly once: one acquire (prefill), the Short
		// lease dropped without release, the Long lease will release it once.
		Assert.Equal(1, tracker.TotalSlots("rtx") - tracker.FreeSlotCount("rtx"));
	}

	[Fact]
	public async Task PickDecode_SkipHit_DecodeLease_ReusesAndPinsRestoredSlot()
	{
		var (scheduler, _, tracker, _) = MakeScheduler();
		var sessionId = "sess_pin_reuse";

		// cold_atomic / solo_prefix_restore shape: the Long decode lease already
		// owns the restored slot (ColdRouteAsync acquired it up-front).
		var item = MakeSoloItem(sessionId, 500);
		item.State = WorkItemState.PickDecode;
		item.RouteType = "solo_prefix_reuse";
		item.SoloKvRestoreHit = true;
		item.PrefillWorker = RtxWorker();
		item.PrefillSlot = 1;
		item.DecodeWorker = RtxWorker();
		item.DecodeSlot = 1;
		Assert.True(tracker.TryAcquireSlot("rtx", out _, "decode", pinnedSlot: 1));
		item.DecodeLease = new SlotLease("rtx", 1, sessionId, LeaseLifetime.Long, tracker);

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Decode, next);
		Assert.Equal("rtx", item.DecodeWorker?.Name);
		Assert.Equal(1, item.DecodeSlot);
		Assert.Equal(1, tracker.TotalSlots("rtx") - tracker.FreeSlotCount("rtx"));
	}

	[Fact]
	public async Task PickDecode_SkipHit_NoLeaseHoldsSlot_FreshPinnedAcquire()
	{
		var (scheduler, _, tracker, _) = MakeScheduler();
		var sessionId = "sess_pin_fresh";

		// Defensive shape: the hit is set but no lease owns the restored slot
		// (should not happen in the live flow) — re-acquire it pinned.
		var item = MakeSoloItem(sessionId, 3500);
		item.State = WorkItemState.PickDecode;
		item.RouteType = "solo_prefix_reuse";
		item.SoloKvRestoreHit = true;
		item.PrefillWorker = RtxWorker();
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Decode, next);
		Assert.Equal("rtx", item.DecodeWorker?.Name);
		Assert.Equal(0, item.DecodeSlot);
		Assert.NotNull(item.DecodeLease);
		Assert.Equal(LeaseLifetime.Long, item.DecodeLease?.Lifetime);
	}

	[Fact]
	public async Task RestoreKv_SkipHit_ReturnsDecodeWithoutStoreTraffic()
	{
		var (scheduler, ledger, tracker, store) = MakeScheduler();
		var sessionId = "sess_restore_skip";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);

		var item = MakeSoloItem(sessionId, 3500);
		item.State = WorkItemState.RestoreKv;
		item.RouteType = "solo_prefix_reuse";
		item.SoloKvRestoreHit = true;
		item.DecodeWorker = RtxWorker();
		item.DecodeSlot = 0;
		item.DecodeLease = new SlotLease("rtx", 0, sessionId, LeaseLifetime.Long, tracker);

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// KV already resident via STATE_PUT — no manifest lookup, no re-restore
		Assert.Equal(WorkItemState.Decode, next);
		Assert.Equal(0, store.Calls.Count);
	}

	[Fact]
	public async Task EndToEnd_Atomic_SoloRestoreHit_ReachesDecodeWithoutPrefill()
	{
		// Full solo turn-2 flow (atomic route): cold route → PrefixRestore
		// (STATE_PUT) → PickDecode (pinned) → Decode. No PREFILL RPC is issued.
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);

		var cfg = MakeConfig();
		var atomicCfg = new CoordinatorConfig
		{
			UseLlamaEngine = true,
			PrefixCheckpointEnabled = true,
			SoloPrefixReuseEnabled = true,
			AtomicThreshold = 4000, // 500-token item routes atomic
			WarmThreshold = 5120,
			NPastGuardTolerance = 50,
			Workers = [cfg.Workers[0]],
		};
		var (scheduler, ledger, tracker, store) = MakeScheduler(atomicCfg, engineStore);
		var sessionId = "sess_e2e_atomic";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 450);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = MakeSoloItem(sessionId, estimatedTokens: 500);

		// Turn 2 routing: cold_atomic + HasStoreState → PrefixRestore
		var state1 = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.PrefixRestore, state1);
		Assert.Equal("solo_prefix_restore", item.RouteType);
		Assert.NotNull(item.DecodeLease); // atomic route holds the slot up-front

		item.State = WorkItemState.PrefixRestore;
		var state2 = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.PickDecode, state2); // PREFILL skipped
		Assert.True(item.SoloKvRestoreHit);

		item.State = WorkItemState.PickDecode;
		var state3 = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.Decode, state3);
		Assert.Equal(item.PrefillSlot, item.DecodeSlot); // pinned to the restored slot
		Assert.Equal(LeaseLifetime.Long, item.DecodeLease!.Lifetime);

		// No PREFILL was ever issued for this turn
		Assert.Equal(0, engineStore.CallCount(Hydra.Shared.OpCode.EnginePrefill));

		// Finding 1: FinalizeAsync must STASH the Long decode lease as warm —
		// only Long leases take that path; anything shorter disposes the slot
		// while the stream is still draining.
		await scheduler.FinalizeAsync(item, WorkItemState.Done);
		Assert.Equal(1, scheduler.WarmLeaseCount);
		Assert.Equal(LeaseLifetime.Long, scheduler.GetWarmLeasesSnapshot()[sessionId].Lifetime);
	}

	[Fact]
	public async Task EndToEnd_ColdConcurrency_SoloRestoreHit_ReachesDecodeWithoutPrefill()
	{
		// Full solo turn-N flow (large prompt → cold_concurrency): the short
		// prefill lease is converted to the decode lease and the decode is
		// pinned to the restored slot.
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);

		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_e2e_cc";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = MakeSoloItem(sessionId, estimatedTokens: 3500); // > AtomicThreshold

		var state1 = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.PrefixRestore, state1);
		Assert.Equal("cold_concurrency", item.RouteType);
		Assert.NotNull(item.PrefillLease);
		Assert.Null(item.DecodeLease);

		item.State = WorkItemState.PrefixRestore;
		var state2 = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.PickDecode, state2);
		Assert.True(item.SoloKvRestoreHit);

		item.State = WorkItemState.PickDecode;
		var state3 = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.Decode, state3);
		Assert.Equal("rtx", item.DecodeWorker?.Name);
		Assert.Equal(item.PrefillSlot, item.DecodeSlot);
		Assert.Null(item.PrefillLease); // converted into the decode lease
		Assert.NotNull(item.DecodeLease);
		// Finding 1 (regression guard): the converted decode lease must be
		// Long-lived. Transferring the Short prefill lease object made
		// FinalizeAsync dispose the slot mid-stream on every cold_concurrency
		// restore hit (stream_done_no_lease 6/6 in the v6 A/B run).
		Assert.Equal(LeaseLifetime.Long, item.DecodeLease.Lifetime);
		Assert.Equal(0, engineStore.CallCount(Hydra.Shared.OpCode.EnginePrefill));

		// Finding 1: FinalizeAsync must STASH the Long decode lease as warm —
		// that is what lets the next turn's evict skip the redundant save.
		await scheduler.FinalizeAsync(item, WorkItemState.Done);
		Assert.Equal(1, scheduler.WarmLeaseCount);
		Assert.Equal(LeaseLifetime.Long, scheduler.GetWarmLeasesSnapshot()[sessionId].Lifetime);
	}

	// ── T4 anomaly (P100 A/B): model_path strip on restore hits ──

	[Fact]
	public void HydraConfig_ModelPathStripped_OnRestoreHit_DecodeConfigKeepsT1Keys()
	{
		// The decode HTTP-fallback body carries the alias EngineConfig dict. On
		// a restore hit the model is resident — model_path must not travel (a
		// mismatched path triggers an engine T3 rebuild whose failed rollback
		// wipes the restored slot). T1 slot keys survive.
		var cfg = new Dictionary<string, object>
		{
			["model_path"] = "/models/Qwen3.5-9B-Q4_K_M.gguf",
			["n_ctx"] = 65536,
			["cache_type_k"] = "q8_0",
			["flash_attn"] = true,
		};

		WorkerSchedulerService.StripReloadTriggerForRestoreHit(cfg, Serilog.Log.Logger, "sess_strip");

		Assert.False(cfg.ContainsKey("model_path"), "T3 reload trigger must be stripped");
		Assert.Equal(3, cfg.Count);
		Assert.Equal(65536, cfg["n_ctx"]);
		Assert.Equal("q8_0", cfg["cache_type_k"]);
	}

	[Fact]
	public void HydraConfig_NoModelPath_StripIsNoOp()
	{
		var cfg = new Dictionary<string, object> { ["n_ctx"] = 65536 };
		WorkerSchedulerService.StripReloadTriggerForRestoreHit(cfg, Serilog.Log.Logger, "sess_noop");
		Assert.Equal(1, cfg.Count);
		Assert.Equal(65536, cfg["n_ctx"]);
	}

	// ── Evict-save redundancy (P100 A/B run #2): T2's 43s TTFT ──

	[Fact]
	public async Task EvictWarm_FreshStoreState_SkipsRedundantEvictSave()
	{
		// Turn 2 of a solo session: turn 1's bg save already committed the slot
		// state (StoreNPast == ledger NPast) and the warm lease kept the slot
		// idle. The force-mode evict must NOT run a second STATE_GET+PUT —
		// on P100 that redundant save serialized with the bg save on the
		// engine RPC channel and cost +16s on turn 2's TTFT (43s vs ~27s).
		var engineStore = new FakeStoreClient();
		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_evict_skip";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 8386);
		ledger.MarkStoreState(sessionId, 8386); // bg save committed
		scheduler.SeedWarmLeaseForTest(sessionId, new SlotLease("rtx", 0, sessionId, LeaseLifetime.Long, tracker));
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		var item = MakeSoloItem(sessionId, estimatedTokens: 3500); // cold_concurrency
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.PrefixRestore, next);
		Assert.True(engineStore.CallCount(Hydra.Shared.OpCode.StateGet) == 0,
			$"fresh store state must suppress the redundant evict save (got {engineStore.CallCount(Hydra.Shared.OpCode.StateGet)})");
		Assert.Equal(0, engineStore.CallCount(Hydra.Shared.OpCode.Put));
	}

	[Fact]
	public async Task EvictWarm_StaleStoreState_RunsEvictSave()
	{
		// The bg save failed (or the ledger moved on): the store blob is older
		// than the current slot state (StoreNPast != NPast). The evict save is
		// the only chance to persist the current KV before the slot is freed —
		// it must run.
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StateGet, (byte)StatusCode.Ok, payload: new byte[4096]);
		var (scheduler, ledger, tracker, store) = MakeScheduler(engineStore: engineStore);
		var sessionId = "sess_evict_save";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 8386);
		ledger.MarkStoreState(sessionId, 3000); // stale: store holds an older state
		scheduler.SeedWarmLeaseForTest(sessionId, new SlotLease("rtx", 0, sessionId, LeaseLifetime.Long, tracker));
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);
		store.SetResponse(OpCode.Put, (byte)StatusCode.Ok);

		var item = MakeSoloItem(sessionId, estimatedTokens: 3500);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.PrefixRestore, next);
		Assert.True(engineStore.CallCount(Hydra.Shared.OpCode.StateGet) == 1,
			$"stale store state must trigger the evict save (got {engineStore.CallCount(Hydra.Shared.OpCode.StateGet)})");
	}

	// ── Save-wait timeout branch (finding 2/7) ──

	[Fact]
	public async Task SaveWait_Timeout_RestoreProceedsWithStaleBlob()
	{
		// The in-flight save TCS is registered but NEVER completed (simulates a
		// stuck save). The restore must not block beyond the bounded wait — it
		// proceeds with possibly-stale KV (larger delta prefill, correct output).
		// SoloSaveWaitMs is lifted to CoordinatorConfig so this branch is testable.
		var engineStore = new FakeStoreClient();
		engineStore.SetResponse(OpCode.StatePut, (byte)StatusCode.Ok, meta: FullStatePutMeta);
		var (scheduler, ledger, tracker, store) = MakeScheduler(cfg: MakeConfig(soloSaveWaitMs: 100), engineStore: engineStore);
		var sessionId = "sess_savewait_timeout";

		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 3000);
		ledger.MarkStoreState(sessionId);
		scheduler.RegisterSessionSave(sessionId); // in-flight, never completed
		store.SetResponse(OpCode.Get, (byte)StatusCode.Ok, payload: new byte[2048]);

		// restore hit is gated on RouteType — stage it as cold_concurrency
		var item = StagedRestoreItem(sessionId, "cold_concurrency");

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);
		sw.Stop();

		Assert.Equal(WorkItemState.PickDecode, next);
		Assert.True(item.SoloKvRestoreHit);
		Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
			$"restore must proceed after the bounded wait, not block 30s (elapsed {sw.Elapsed.TotalMilliseconds:F0} ms)");
	}

	// ── Superseded TCS resolution (finding 3/7) ──

	[Fact]
	public async Task RegisterSessionSave_SupersededTCS_ResolvesImmediately()
	{
		// A waiter parked on a superseded TCS must not block the full bounded
		// wait — RegisterSessionSave completes any existing TCS before
		// replacing it (the old comment claimed this; the code didn't).
		var (scheduler, _, _, _) = MakeScheduler();
		var sessionId = "sess_superseded";

		var t1 = scheduler.RegisterSessionSave(sessionId);
		Assert.False(t1.Task.IsCompleted);
		var parkedWaiter = t1.Task.WaitAsync(TimeSpan.FromSeconds(10));

		var t2 = scheduler.RegisterSessionSave(sessionId); // supersedes t1
		await parkedWaiter; // must resolve NOW, not after t2's save completes
		Assert.True(t1.Task.IsCompleted, "superseded TCS must be resolved on replacement");
		Assert.False(t2.Task.IsCompleted);

		scheduler.CompleteSessionSave(sessionId);
		Assert.True(t2.Task.IsCompleted);
	}
}
