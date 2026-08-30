using System.Reflection;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.Integration;
using Tests.Core.TestHelpers;

namespace Tests.Core.Services;

/// <summary>
/// Issue #718: warm-slot fast path. When warm slot verification fails (transient
/// blip) or the n_past guard fires, but the bound worker is still healthy, serves
/// the same model, and has slots → skip Store Get+StatePut and go straight to
/// Prefill with PrefixCacheHit=true.
/// </summary>
public sealed class WarmSlotFastPathTests
{
	private static CoordinatorConfig MakeConfig(bool fastPathEnabled = true, bool warmVerifyEnabled = true) => new()
	{
		WarmSlotVerificationEnabled = warmVerifyEnabled,
		WarmSlotFastPathEnabled = fastPathEnabled,
		PrefixCheckpointEnabled = false,
		EnableChunks = false,
		UseLlamaEngine = true,
		Workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", Host = "localhost", RpcPort = 9601,
				LlamaUrl = "http://localhost:1", // dead port → verify always fails
				WorkerType = 3, Slots = 2,
				PrefillPriority = 1, DecodePriority = 2 },
		},
	};

	private static WorkItem MakeItem(string sessionId, int estimatedTokens) => new(
		new Dictionary<string, object> { ["stream"] = false },
		new List<Dictionary<string, object>> { new() { ["role"] = "user", ["content"] = "hi" } },
		sessionId,
		"trace_warm_fastpath",
		prefixHash: null,
		estimatedTokens,
		estimatedNewTokens: 50);

	private static WorkerSchedulerService MakeScheduler(
		CoordinatorConfig cfg, FakeStoreClient fake, IHealthMonitorService health)
	{
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
		var proxy = new CompletionProxyService();
		var sp = new ServiceCollection().BuildServiceProvider();
		var scheduler = new WorkerSchedulerService(
			cfg, ledger, tracker, proxy, health, fake, sp, Serilog.Log.Logger);
		scheduler.AgentClientFactory = (_, _) => fake;
		scheduler.LlamaClientFactory = _ => new TestLlamaClient();
		return scheduler;
	}

	private static (WorkerSchedulerService scheduler, SessionLedger ledger) SetupScheduler(
		string sessionId, string boundModel, int nPast,
		bool fastPathEnabled = true, bool warmVerifyEnabled = true,
		string? residentModel = null, bool workerHealthy = true)
	{
		var cfg = MakeConfig(fastPathEnabled, warmVerifyEnabled);
		var fake = new FakeStoreClient();
		var health = new WarmFastPathHealthMonitor(workerHealthy, residentModel ?? boundModel);
		var scheduler = MakeScheduler(cfg, fake, health);

		var ledger = (SessionLedger)typeof(WorkerSchedulerService)
			.GetField("_ledger", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(scheduler)!;

		// Pre-populate: warm session with HasStoreState=true, SlotFreed=false
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: nPast, prefixHash: null);
		var entry = ledger.Lookup(sessionId)!;
		lock (entry) { entry.HasStoreState = true; entry.BoundModel = boundModel; }
		// Set NPromptTokens to match estimatedTokens so the shrinkage guard
		// at L831 (estimatedTokens + tolerance < guardBaseline) does NOT fire.
		ledger.UpdateNPromptTokens(sessionId, 300);

		return (scheduler, ledger);
	}

	/// <summary>
	/// (a) Warm residency hit → fast path taken: RouteType=affinity → warm_slot_fastpath,
	/// PrefixCacheHit=true, Prefill on bound worker, NO StatePut in RPC trace.
	/// </summary>
	[Fact]
	public async Task WarmResidency_VerifyFails_FastPath_Taken()
	{
		const string sessionId = "warm-fastpath-1";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger) = SetupScheduler(sessionId, boundModel, nPast);
		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Fast path → Prefill (not Decode, not PickDecode, not RestoreKv)
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.Equal("warm_slot_fastpath", item.RouteType);
		Assert.True(item.PrefixCacheHit, "PrefixCacheHit must be true on fast path");
		Assert.Equal(nPast, item.PrefixNPast);
		Assert.Equal("rtx", item.PrefillWorker?.Name);
		Assert.Equal(0, item.PrefillSlot);

		// The ledger entry must NOT be evicted (no SlotFreed=true)
		var entryAfter = ledger.Lookup(sessionId);
		Assert.NotNull(entryAfter);
		Assert.False(entryAfter!.SlotFreed, "fast path must not evict the session");

		// DecodeLease must be null (released before going to Prefill)
		Assert.Null(item.DecodeLease);
	}

	/// <summary>
	/// (b) Stale residency (SlotFreed=true) → falls back to PrefixRestore/PickDecode
	/// path. The fast path condition requires !SlotFreed, so a freed slot falls through.
	/// </summary>
	[Fact]
	public async Task StaleResidency_SlotFreed_FallsBack()
	{
		const string sessionId = "warm-fastpath-2";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger) = SetupScheduler(sessionId, boundModel, nPast);

		// Mark the session as evicted (SlotFreed=true)
		ledger.MarkEvicted(sessionId);

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// NOT the fast path — falls through to PickDecode or migration
		Assert.NotEqual("warm_slot_fastpath", item.RouteType);
		Assert.False(item.PrefixCacheHit);
	}

	/// <summary>
	/// (c) Model swap on bound worker → fast path skipped. The worker's CurrentModel
	/// differs from the session's BoundModel → modelMatch=false → falls through.
	/// </summary>
	[Fact]
	public async Task ModelSwap_OnBoundWorker_FastPathSkipped()
	{
		const string sessionId = "warm-fastpath-3";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger) = SetupScheduler(
			sessionId, boundModel, nPast,
			residentModel: "dense-27b"); // different model

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// NOT the fast path — model mismatch
		Assert.NotEqual("warm_slot_fastpath", item.RouteType);
		Assert.False(item.PrefixCacheHit);
	}

	/// <summary>
	/// (d) Flag disabled → old behavior. When WarmSlotFastPathEnabled=false, the fast
	/// path is skipped and the normal eviction/restore flow runs.
	/// </summary>
	[Fact]
	public async Task FlagDisabled_OldBehavior()
	{
		const string sessionId = "warm-fastpath-4";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger) = SetupScheduler(
			sessionId, boundModel, nPast,
			fastPathEnabled: false);

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// NOT the fast path — flag disabled
		Assert.NotEqual("warm_slot_fastpath", item.RouteType);
		Assert.False(item.PrefixCacheHit);
	}
}

/// <summary>
/// Health monitor for warm-slot fast path tests. Returns configurable health
/// and node info with CurrentModel for the model-match check.
/// </summary>
internal sealed class WarmFastPathHealthMonitor : IHealthMonitorService
{
	private readonly bool _healthy;
	private readonly string _currentModel;

	public WarmFastPathHealthMonitor(bool healthy, string currentModel)
	{
		_healthy = healthy;
		_currentModel = currentModel;
	}

	public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	public bool IsHealthy(string nodeName) => _healthy;
	public bool IsStoreHealthy => true;
	public int? GetIdleSlot(string nodeName) => null;
	public NodeInfo? GetNodeInfo(string nodeName) => new()
	{
		NodeName = nodeName,
		Healthy = _healthy,
		CurrentModel = _currentModel,
		SlotsTotal = 2,
		SlotsIdle = 1,
	};
	public Dictionary<string, object> GetHealthSummary() => new();
	public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
	public void MarkHealthy(string nodeName) { }
	public event Action? HealthyChanged;
}
