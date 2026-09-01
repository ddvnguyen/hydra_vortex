using System.Reflection;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
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

	private static (WorkerSchedulerService scheduler, SessionLedger ledger, FakeStoreClient fake, WorkerTracker tracker) SetupScheduler(
		string sessionId, string boundModel, int nPast,
		bool fastPathEnabled = true, bool warmVerifyEnabled = true,
		string? residentModel = null, bool workerHealthy = true,
		List<SlotInfo>? slots = null, int residentSlot = 0)
	{
		var cfg = MakeConfig(fastPathEnabled, warmVerifyEnabled);
		var fake = new FakeStoreClient();
		var health = new WarmFastPathHealthMonitor(
			workerHealthy, residentModel ?? boundModel,
			slots ?? [new SlotInfo { Id = residentSlot, NPast = nPast }]);
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
		var proxy = new CompletionProxyService();
		var sp = new ServiceCollection().BuildServiceProvider();
		var scheduler = new WorkerSchedulerService(
			cfg, ledger, tracker, proxy, health, fake, sp, Serilog.Log.Logger);
		scheduler.AgentClientFactory = (_, _) => fake;
		scheduler.LlamaClientFactory = _ => new TestLlamaClient();

		// Pre-populate: warm session with HasStoreState=true, SlotFreed=false,
		// resident on slot `residentSlot`.
		ledger.Register(sessionId, "rtx", slotId: residentSlot, nPast: nPast, prefixHash: null);
		var entry = ledger.Lookup(sessionId)!;
		lock (entry) { entry.HasStoreState = true; entry.BoundModel = boundModel; }
		// Set NPromptTokens to match estimatedTokens so the shrinkage guard
		// (estimatedTokens + tolerance < guardBaseline) does NOT fire.
		ledger.UpdateNPromptTokens(sessionId, 300);

		return (scheduler, ledger, fake, tracker);
	}

	/// <summary>
	/// (a) Warm residency hit → fast path taken: RouteType=warm_slot_fastpath,
	/// PrefixCacheHit=true, Prefill on bound worker, NO Store round-trip (CallCount(Get)==0).
	/// DecodeLease is NOT released (keeps the slot through Prefill→Decode).
	/// </summary>
	[Fact]
	public async Task WarmResidency_VerifyFails_FastPath_Taken()
	{
		const string sessionId = "warm-fastpath-1";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger, fake, _) = SetupScheduler(sessionId, boundModel, nPast);
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

		// BLOCKER fix: DecodeLease must be NON-null — kept through Prefill→Decode
		Assert.NotNull(item.DecodeLease);

		// No Store round-trip at all — the whole point of the fast path
		Assert.Equal(0, fake.CallCount(OpCode.Get));
	}

	/// <summary>
	/// (b) Solo regression: SlotFreed=true (post-MarkEvicted) + healthy worker with
	/// the slot still present in nodeInfo.Slots → fast path fires via the
	/// MIGRATION-block interception (site 1 — the solo post-MarkEvicted flow only
	/// reaches ColdRouteAsync when the migration block falls through). No Store
	/// Get — the engine's live /slots poll is the real residency truth, not
	/// SlotFreed.
	/// </summary>
	[Fact]
	public async Task SoloPostMarkEvicted_MigrationSite_TakesFastPath()
	{
		const string sessionId = "warm-fastpath-6";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger, fake, tracker) = SetupScheduler(sessionId, boundModel, nPast);

		// Simulate post-MarkEvicted: SlotFreed=true, HasStoreState=true.
		// The fast path works because the engine's live /slots poll still lists the slot.
		ledger.MarkEvicted(sessionId);
		var entry = ledger.Lookup(sessionId)!;
		Assert.True(entry.SlotFreed, "precondition: SlotFreed=true after MarkEvicted");

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// Fast path → Prefill via migration-block interception (site 1)
		Assert.Equal(WorkItemState.Prefill, next);
		Assert.Equal("warm_slot_fastpath", item.RouteType);
		Assert.True(item.PrefixCacheHit, "PrefixCacheHit must be true on fast path");
		Assert.Equal(nPast, item.PrefixNPast);
		Assert.Equal("rtx", item.PrefillWorker?.Name);
		Assert.Equal(0, item.PrefillSlot);
		// The resident slot must be leased (pinned acquire) — no second slot rented.
		Assert.NotNull(item.PrefillLease);
		Assert.Equal(0, item.PrefillLease!.SlotId);
		Assert.Equal(1, tracker.FreeSlotCount("rtx"));

		// No Store round-trip — the whole point of the fast path
		Assert.Equal(0, fake.CallCount(OpCode.Get));
	}

	/// <summary>
	/// #718 round-3, site 2 (ColdRouteAsync interception) regression: a ForceMode
	/// request on a session with prior residency goes RouteAsync →
	/// EvictWarmAndColdRouteAsync → ColdRouteAsync, bypassing the migration block
	/// entirely. The fast path fires and MUST hold a lease on the resident slot
	/// (PrefillLease non-null) — without one, another concurrent route can rent
	/// entry.SlotId while this Prefill is in flight (the round-1
	/// unprotected-slot race). No Store Get.
	/// </summary>
	[Fact]
	public async Task ColdRouteSite_ForceMode_LeaseHeld_NoStoreGet()
	{
		const string sessionId = "warm-fastpath-7";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger, fake, tracker) = SetupScheduler(sessionId, boundModel, nPast);
		Assert.True(tracker.FreeSlotCount("rtx") == 2, "precondition: fresh 2-slot pool");

		var item = MakeItem(sessionId, 300);
		item.ForceMode = "pd"; // RouteAsync's first line → EvictWarmAndColdRouteAsync → ColdRouteAsync (site 2)
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.Equal("warm_slot_fastpath", item.RouteType);
		Assert.True(item.PrefixCacheHit);
		Assert.Equal(nPast, item.PrefixNPast);
		Assert.Equal("rtx", item.PrefillWorker?.Name);
		Assert.Equal(0, item.PrefillSlot);
		// Round-1 blocker: a lease must protect the resident slot for the Prefill duration.
		Assert.NotNull(item.PrefillLease);
		Assert.Equal(0, item.PrefillLease!.SlotId);
		Assert.Equal("rtx", item.PrefillLease.WorkerName);
		// Exactly one slot leased (the resident one) — no second slot rented.
		Assert.Equal(1, tracker.FreeSlotCount("rtx"));
		// No Store round-trip — the whole point of the fast path.
		Assert.Equal(0, fake.CallCount(OpCode.Get));
	}

	/// <summary>
	/// #718 round-3, site 1 (migration interception) multi-slot pinning: the
	/// session's KV is resident on slot 1 while a fresh 2-slot pool's generic
	/// TryRent would hand out slot 0 (lowest free). The fast path must pin
	/// PrefillSlot + lease to the RESIDENT slot 1, not the lowest free slot.
	/// </summary>
	[Fact]
	public async Task MultiSlot_MigrationSite_PinsResidentSlot_NotLowestFree()
	{
		const string sessionId = "warm-fastpath-8";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;
		const int residentSlot = 1;

		var (scheduler, ledger, fake, tracker) = SetupScheduler(
			sessionId, boundModel, nPast, residentSlot: residentSlot);
		Assert.True(tracker.FreeSlotCount("rtx") == 2, "precondition: fresh 2-slot pool");

		// Post-MarkEvicted (SlotFreed=true, HasStoreState=true) → migration block (site 1)
		ledger.MarkEvicted(sessionId);

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.Equal("warm_slot_fastpath", item.RouteType);
		Assert.True(item.PrefillSlot == residentSlot, "must pin the resident slot, not the lowest free (0)");
		Assert.NotNull(item.PrefillLease);
		Assert.Equal(residentSlot, item.PrefillLease!.SlotId);
		// Resident slot leased; the other slot untouched (no second slot rented).
		Assert.Equal(1, tracker.FreeSlotCount("rtx"));
		Assert.Equal(0, fake.CallCount(OpCode.Get));
	}

	/// <summary>
	/// #718 round-3, site 2 (ColdRouteAsync interception) multi-slot pinning:
	/// same scenario as MultiSlot_MigrationSite_PinsResidentSlot_NotLowestFree but
	/// driven through ForceMode so the item reaches ColdRouteAsync directly.
	/// </summary>
	[Fact]
	public async Task MultiSlot_ColdRouteSite_PinsResidentSlot_NotLowestFree()
	{
		const string sessionId = "warm-fastpath-9";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;
		const int residentSlot = 1;

		var (scheduler, ledger, fake, tracker) = SetupScheduler(
			sessionId, boundModel, nPast, residentSlot: residentSlot);
		Assert.True(tracker.FreeSlotCount("rtx") == 2, "precondition: fresh 2-slot pool");

		var item = MakeItem(sessionId, 300);
		item.ForceMode = "pd"; // → EvictWarmAndColdRouteAsync → ColdRouteAsync (site 2)
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Prefill, next);
		Assert.Equal("warm_slot_fastpath", item.RouteType);
		Assert.True(item.PrefillSlot == residentSlot, "must pin the resident slot, not the lowest free (0)");
		Assert.NotNull(item.PrefillLease);
		Assert.Equal(residentSlot, item.PrefillLease!.SlotId);
		Assert.Equal(1, tracker.FreeSlotCount("rtx"));
		Assert.Equal(0, fake.CallCount(OpCode.Get));
	}

	/// <summary>
	/// #718 round-3 fallback: the resident slot is already rented by another
	/// request → the pinned acquire fails → the fast path must NOT take a second
	/// slot; the item falls through to the normal restore path.
	/// </summary>
	[Fact]
	public async Task ResidentSlotBusy_PinnedAcquireFails_FallsBackToRestore()
	{
		const string sessionId = "warm-fastpath-10";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger, fake, tracker) = SetupScheduler(sessionId, boundModel, nPast);
		// Another session rents the resident slot (0) before this dispatch.
		Assert.True(tracker.TryAcquireSlot("rtx", out var otherSlot, "decode", pinnedSlot: 0));
		Assert.Equal(0, otherSlot);

		// Post-MarkEvicted → migration block (site 1)
		ledger.MarkEvicted(sessionId);

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// NOT the fast-path Prefill — resident slot unavailable, fell through to
		// the normal restore path (PickDecodeAsync).
		Assert.NotEqual(WorkItemState.Prefill, next);
		Assert.True(item.PrefillLease is null, "fast path must not lease a slot it did not pin-acquire");
		// The other session's slot (0) stays leased — no double-rent, and the
		// fallback may lease at most one additional slot (decode) of the 2-slot pool.
		Assert.True(tracker.FreeSlotCount("rtx") is 0 or 1);
		if (item.DecodeSlot is int ds)
			Assert.True(ds != 0, "fallback must not take the other session's slot");
	}

	/// <summary>
	/// (c) Stale residency (SlotFreed=true) + worker unhealthy → falls back to
	/// migration/restore path. The fast path gate requires nodeInfo.Healthy.
	/// </summary>
	[Fact]
	public async Task StaleResidency_WorkerUnhealthy_FallsBack()
	{
		const string sessionId = "warm-fastpath-2";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		var (scheduler, ledger, _, _) = SetupScheduler(
			sessionId, boundModel, nPast,
			workerHealthy: false);

		// Mark the session as evicted (SlotFreed=true) AND worker unhealthy
		ledger.MarkEvicted(sessionId);

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// NOT the fast path — worker unhealthy
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

		var (scheduler, ledger, _, _) = SetupScheduler(
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

		var (scheduler, ledger, _, _) = SetupScheduler(
			sessionId, boundModel, nPast,
			fastPathEnabled: false);

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// NOT the fast path — flag disabled
		Assert.NotEqual("warm_slot_fastpath", item.RouteType);
		Assert.False(item.PrefixCacheHit);
	}

	/// <summary>
	/// (e) Restarted worker: nodeInfo.Slots does NOT contain entry.SlotId → fast
	/// path skipped. The engine restart guard catches stale slot IDs from before
	/// the restart (small reused int may collide with another session's slot).
	/// </summary>
	[Fact]
	public async Task RestartedWorker_SlotNotInList_FallsBack()
	{
		const string sessionId = "warm-fastpath-5";
		const string boundModel = "moe-35b-solo";
		const int nPast = 5000;

		// Slot list has Id=1 and Id=2 — but entry.SlotId=0 is missing (restart scenario)
		var slots = new List<SlotInfo>
		{
			new() { Id = 1, NPast = 3000 },
			new() { Id = 2, NPast = 4000 },
		};
		var (scheduler, ledger, _, _) = SetupScheduler(
			sessionId, boundModel, nPast,
			slots: slots);

		var item = MakeItem(sessionId, 300);
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		// NOT the fast path — slot not in engine's /slots list
		Assert.NotEqual("warm_slot_fastpath", item.RouteType);
		Assert.False(item.PrefixCacheHit);
	}
}

/// <summary>
/// Health monitor for warm-slot fast path tests. Returns configurable health,
/// node info with CurrentModel, and a per-slot list for the slot-presence gate.
/// </summary>
internal sealed class WarmFastPathHealthMonitor : IHealthMonitorService
{
	private readonly bool _healthy;
	private readonly string _currentModel;
	private readonly List<SlotInfo> _slots;

	public WarmFastPathHealthMonitor(bool healthy, string currentModel, List<SlotInfo>? slots = null)
	{
		_healthy = healthy;
		_currentModel = currentModel;
		_slots = slots ?? [new SlotInfo { Id = 0, NPast = 5000 }]; // default: slot 0 present
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
		Slots = _slots,
	};
	public Dictionary<string, object> GetHealthSummary() => new();
	public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
	public void MarkHealthy(string nodeName) { }
	public event Action? HealthyChanged;
}
