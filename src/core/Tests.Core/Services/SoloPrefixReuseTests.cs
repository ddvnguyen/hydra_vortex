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

		var item = MakeSoloItem(sessionId, estimatedTokens: 4900);
		item.State = WorkItemState.PrefixRestore;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// n_past guard fires: estimated 4900 + tolerance 50 = 4950 < 5000 = skip
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
}
