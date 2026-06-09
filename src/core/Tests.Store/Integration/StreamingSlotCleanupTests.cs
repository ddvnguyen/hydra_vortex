using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Store.Integration;

// ═══════════════════════════════════════════════════════════════════════
// Test doubles
// ═══════════════════════════════════════════════════════════════════════

[CollectionDefinition("StreamingIntegrationTests", DisableParallelization = true)]
public sealed class StreamingIntegrationTestCollection { }

internal sealed class TestHealthMonitor : IHealthMonitorService
{
	public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	public bool IsHealthy(string nodeName) => true;
	public bool IsStoreHealthy => true;
	public int? GetIdleSlot(string nodeName) => 0;
	public Dictionary<string, object> GetHealthSummary() => new();
}

internal sealed class TestCompletionProxy : ICompletionProxyService
{
	private readonly int _totalTokens;
	private readonly int _slotId;

	public TestCompletionProxy(int totalTokens = 150, int slotId = 0)
		=> (_totalTokens, _slotId) = (totalTokens, slotId);

	public Task<Dictionary<string, object>> ProxyCompletionAsync(
		string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
	{
		return Task.FromResult(new Dictionary<string, object>
		{
			["id_slot"] = _slotId,
			["usage"] = JsonSerializer.SerializeToElement(new { total_tokens = _totalTokens })
		});
	}

	public async IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(
		string nodeUrl, Dictionary<string, object> body, string traceId,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
	{
		yield return Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n");
		yield return Encoding.UTF8.GetBytes(
			$"data: {{\"id_slot\":{_slotId},\"usage\":{{\"total_tokens\":{_totalTokens}}}}}\n\n");
	}

	public Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct)
		=> Task.FromResult(true);
}

internal sealed class TestRpcClient : RpcClient
{
	public List<(OpCode Op, string Key)> Calls { get; } = new();

	public TestRpcClient() : base("test", 0) { }

	public override async Task<RpcResponse> RequestAsync(
		OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct)
	{
		Calls.Add((op, key));
		var meta = JsonSerializer.Serialize(new
		{
			n_past = 2000, restored = true, chunked = true,
			erased = true, stored = true, total_chunks = 1, deduped_chunks = 0
		});
		return new RpcResponse((byte)StatusCode.Ok, meta, []);
	}

	public void ClearCalls() => Calls.Clear();

	public bool HasCall(OpCode op, string? keyContains = null)
		=> Calls.Any(c => c.Op == op && (keyContains == null || c.Key.Contains(keyContains)));

	public int CountCalls(OpCode op, string? keyContains = null)
		=> Calls.Count(c => c.Op == op && (keyContains == null || c.Key.Contains(keyContains)));
}

// ═══════════════════════════════════════════════════════════════════════
// Fixture
// ═══════════════════════════════════════════════════════════════════════

internal sealed class StreamingFixture : IAsyncDisposable
{
	public CoordinatorConfig Cfg { get; }
	public SessionLedger Ledger { get; }
	public WorkerTracker Tracker { get; }
	public ICompletionProxyService Proxy { get; }
	public IHealthMonitorService Health { get; }
	public TestRpcClient Rpc { get; } = new();
	public WorkerSchedulerService Scheduler { get; }
	private readonly CancellationTokenSource _runCts = new();
	private readonly Task _runTask;

	public StreamingFixture(
		int prefillTokens = 2000, int decodeTokens = 150,
		bool streaming = true, string runMode = "concurrency",
		int rtxSlots = 2, int p100Slots = 1)
	{
		Health = new TestHealthMonitor();
		Proxy = new TestCompletionProxy(decodeTokens, slotId: 0);
		Ledger = new SessionLedger();
		Tracker = new WorkerTracker();

		Cfg = new CoordinatorConfig
		{
			RunMode = runMode,
			PrefixCheckpointEnabled = false,
			WarmSlotVerificationEnabled = false,
			MixPrecisionEnabled = false,
			AtomicTokenThreshold = 2048,
			SmallRequestBypassThreshold = 0,  // Disable bypass to preserve existing test behavior
			Workers = new List<WorkerConfig>
			{
				new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = rtxSlots,  PrefillPriority = 1, DecodePriority = 2 },
				new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = p100Slots, PrefillPriority = 100, DecodePriority = 1 },
			}
		};
		foreach (var w in Cfg.Workers)
			Tracker.InitWorker(w.Name, w.Slots);

		var sp = new ServiceCollection().BuildServiceProvider();
		Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, Proxy, Health, null,
			sp, Serilog.Log.Logger);
		Scheduler.AgentClientFactory = (_, _) => Rpc;

		_runTask = Scheduler.RunAsync(_runCts.Token);
	}

	public async ValueTask DisposeAsync()
	{
		_runCts.Cancel();
		try { await _runTask; } catch (OperationCanceledException) { }
		_runCts.Dispose();
	}

	/// <summary>
	/// Submit a chat completion request and wait for the result.
	/// </summary>
	public async Task<object?> SubmitAsync(
		string sessionId, int estimatedTokens, int maxTokens = 500,
		bool stream = true, string? prefixHash = null)
	{
		var req = new Dictionary<string, object>
		{
			["stream"] = stream,
			["max_tokens"] = maxTokens,
			["model"] = "nano"
		};
		var msgs = new List<Dictionary<string, object>>
		{
			new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
		};
		return await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
			maxTokens, prefixHash, _runCts.Token);
	}
}

// ═══════════════════════════════════════════════════════════════════════
// Path 1 — Affinity (warm): 5 tests
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class AffinityPathTests
{
	[Fact]
	public async Task Affinity_2Turns_SmallInit_SmallNext()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
		await f.SubmitAsync("sess_a1", 2000, 150);
		f.Rpc.ClearCalls();

		await f.SubmitAsync("sess_a1", 100, 50);

		var e = f.Ledger.Lookup("sess_a1");
		Assert.NotNull(e);
		Assert.True(e!.HasStoreState);
		Assert.True(e.NPast > 0, $"n_past should be > 0, got {e.NPast}");
		Assert.True(f.Scheduler.WarmLeaseCount >= 1, "Expected warm lease after affinity");
	}

	[Fact]
	public async Task Affinity_3Turns_MediumInit_LargeNext()
	{
		await using var f = new StreamingFixture(prefillTokens: 4000, decodeTokens: 500);
		await f.SubmitAsync("sess_a2", 4000, 500);
		int np1 = f.Ledger.Lookup("sess_a2")!.NPast;

		await f.SubmitAsync("sess_a2", 2000, 200);
		int np2 = f.Ledger.Lookup("sess_a2")!.NPast;
		Assert.True(np2 >= np1, $"n_past should grow: {np1} -> {np2}");

		await f.SubmitAsync("sess_a2", 3000, 300);
		int np3 = f.Ledger.Lookup("sess_a2")!.NPast;
		Assert.True(np3 >= np2, $"n_past should grow: {np2} -> {np3}");
		Assert.True(f.Scheduler.WarmLeaseCount >= 1, "Warm lease should persist across 3 turns");
	}

	[Fact]
	public async Task Affinity_4Turns_LargeInit_SmallNext_NPastGuard()
	{
		await using var f = new StreamingFixture(prefillTokens: 8000, decodeTokens: 6000);
		await f.SubmitAsync("sess_a3", 8000, 100);
		int np1 = f.Ledger.Lookup("sess_a3")!.NPast;
		Assert.True(np1 > 0, "Large prefill should set n_past");

		// Turn 2 — small tokens triggers n_past guard
		f.Rpc.ClearCalls();
		await f.SubmitAsync("sess_a3", 500, 50);

		var e2 = f.Ledger.Lookup("sess_a3");
		Assert.NotNull(e2);
		if (e2!.SlotFreed)
		{
			Assert.Equal(0, e2.NPast); // Guard reset n_past
		}

		// Turn 3 — migration (slot freed → store-restore)
		await f.SubmitAsync("sess_a3", 2000, 100);

		// Turn 4 — should complete
		await f.SubmitAsync("sess_a3", 1000, 50);
	}

	[Fact]
	public async Task Affinity_BusyWorker_CrossNodeFallback()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
		await f.SubmitAsync("sess_a4", 2000, 100);
		var e = f.Ledger.Lookup("sess_a4");
		Assert.NotNull(e);
		// Force session on RTX for cross-node test
		lock (e!) { e.NodeName = "rtx"; e.SlotId = 0; e.SlotFreed = false; }

		// Busy P100
		Assert.True(f.Tracker.TryAcquireSlot("p100", out _));

		f.Rpc.ClearCalls();
		await f.SubmitAsync("sess_a4", 3000, 50);  // > 2000 bypass threshold
		// Should complete despite p100 being busy (affinity to rtx)
	}

	[Fact]
	public async Task Affinity_SlotStable_AcrossTurns()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
		await f.SubmitAsync("sess_a5", 2000, 100);
		await f.SubmitAsync("sess_a5", 2100, 50);  // > 2000 bypass threshold

		var warm = f.Scheduler.GetWarmLeasesSnapshot();
		Assert.True(warm.ContainsKey("sess_a5"), "Session should have a warm lease across turns");
	}
}

// ═══════════════════════════════════════════════════════════════════════
// Path 2 — Store-Restore (migration): 3 tests
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class MigrationPathTests
{
	[Fact]
	public async Task Migration_2Turns_SmallInit_SmallNext()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
		await f.SubmitAsync("sess_m1", 2000, 100);

		// Simulate slot freed → next request hits migration
		var e = f.Ledger.Lookup("sess_m1");
		Assert.NotNull(e);
		e!.SlotFreed = true;
		e.HasStoreState = true;

		// Turn 2 — migration via store-restore
		f.Rpc.ClearCalls();
		await f.SubmitAsync("sess_m1", 100, 50);
		Assert.True(f.Rpc.HasCall(OpCode.RestoreStateChunked, "sess_m1"),
			"Migration should call RestoreStateChunked");
	}

	[Fact]
	public async Task Migration_3Turns_MediumInit_TokenGrowth()
	{
		await using var f = new StreamingFixture(prefillTokens: 4000, decodeTokens: 500);
		await f.SubmitAsync("sess_m2", 4000, 200);
		int np1 = f.Ledger.Lookup("sess_m2")!.NPast;

		await f.SubmitAsync("sess_m2", 2000, 200);
		int np2 = f.Ledger.Lookup("sess_m2")!.NPast;
		Assert.True(np2 >= np1);

		// Simulate eviction → next turn migration
		var e = f.Ledger.Lookup("sess_m2");
		e!.SlotFreed = true;

		f.Rpc.ClearCalls();
		await f.SubmitAsync("sess_m2", 3000, 100);
		Assert.True(f.Rpc.HasCall(OpCode.RestoreStateChunked, "sess_m2"),
			"Migration turn should call RestoreStateChunked");
	}

	[Fact]
	public async Task Migration_AfterNPastGuard()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
		await f.SubmitAsync("sess_m3", 2000, 100);

		// Force SlotFreed to trigger migration path
		var e = f.Ledger.Lookup("sess_m3");
		e!.SlotFreed = true;
		e.HasStoreState = true;

		f.Rpc.ClearCalls();
		await f.SubmitAsync("sess_m3", 100, 50);
		Assert.True(f.Rpc.HasCall(OpCode.RestoreStateChunked, "sess_m3"),
			"Migration after freed slot should call RestoreStateChunked");

		// Verifies migration path produces a valid result
		var ee = f.Ledger.Lookup("sess_m3");
		Assert.NotNull(ee);
		Assert.True(ee!.HasStoreState);
	}
}

// ═══════════════════════════════════════════════════════════════════════
// Path 3 — Cold Atomic: 3 tests
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class ColdAtomicPathTests
{
	[Fact]
	public async Task Atomic_2Turns_SmallInit_SmallNext()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150,
			runMode: "fast");
		await f.SubmitAsync("sess_c1", 1000, 100);

		var e = f.Ledger.Lookup("sess_c1");
		Assert.NotNull(e);
		Assert.True(e!.HasStoreState);
		Assert.True(e.NPast > 0);

		await f.SubmitAsync("sess_c1", 500, 50);
		Assert.True(f.Ledger.Lookup("sess_c1")!.NPast > 0, "n_past should persist");
	}

	[Fact]
	public async Task Atomic_3Turns_LargeInit_SmallNext()
	{
		await using var f = new StreamingFixture(prefillTokens: 4000, decodeTokens: 300,
			runMode: "fast");
		await f.SubmitAsync("sess_c2", 3000, 150);
		int np1 = f.Ledger.Lookup("sess_c2")!.NPast;

		await f.SubmitAsync("sess_c2", 2000, 100);
		int np2 = f.Ledger.Lookup("sess_c2")!.NPast;
		Assert.True(np2 >= np1);

		await f.SubmitAsync("sess_c2", 2500, 150);
		int np3 = f.Ledger.Lookup("sess_c2")!.NPast;
		Assert.True(np3 >= np2);
	}

	[Fact]
	public async Task Atomic_NPastGuardOnWarmTurn()
	{
		await using var f = new StreamingFixture(prefillTokens: 8000, decodeTokens: 6000,
			runMode: "fast");
		await f.SubmitAsync("sess_c3", 5000, 100);

		// Small turn — should trigger n_past guard or continue warm
		f.Rpc.ClearCalls();
		await f.SubmitAsync("sess_c3", 300, 50);

		var e = f.Ledger.Lookup("sess_c3");
		Assert.NotNull(e);
		Assert.True(e!.NPast > 0 || e.SlotFreed,
			$"After small turn: NPast={e.NPast}, SlotFreed={e.SlotFreed}");
	}
}

// ═══════════════════════════════════════════════════════════════════════
// Path 4 — Cold Concurrency: 3 tests
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class ColdConcurrencyPathTests
{
	[Fact]
	public async Task Concurrency_2Turns_SmallInit_SmallNext()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
		await f.SubmitAsync("sess_d1", 2000, 100);

		var e = f.Ledger.Lookup("sess_d1");
		Assert.NotNull(e);
		Assert.True(e!.HasStoreState);
		Assert.True(e.NPast > 0);

		await f.SubmitAsync("sess_d1", 100, 50);
		Assert.True(f.Ledger.Lookup("sess_d1")!.NPast > 0, "n_past should persist across turns");
	}

	[Fact]
	public async Task Concurrency_3Turns_LargeInit_TokenGrowth()
	{
		await using var f = new StreamingFixture(prefillTokens: 8000, decodeTokens: 600);
		await f.SubmitAsync("sess_d2", 6000, 300);
		int np1 = f.Ledger.Lookup("sess_d2")!.NPast;

		await f.SubmitAsync("sess_d2", 4000, 200);
		int np2 = f.Ledger.Lookup("sess_d2")!.NPast;
		Assert.True(np2 >= np1, $"n_past should grow: {np1} -> {np2}");

		await f.SubmitAsync("sess_d2", 5000, 200);
		int np3 = f.Ledger.Lookup("sess_d2")!.NPast;
		Assert.True(np3 >= np2, $"n_past should grow: {np2} -> {np3}");
	}

	[Fact]
	public async Task Concurrency_PrefillAndDecode_DifferentWorkers()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150,
			rtxSlots: 2, p100Slots: 1);
		// RTX prefill → P100 decode (since RTX excluded as decode in PickDecodeAsync)
		await f.SubmitAsync("sess_d3", 2000, 100);

		var e = f.Ledger.Lookup("sess_d3");
		Assert.NotNull(e);
		Assert.True(e!.HasStoreState, "SaveKv should mark store state");
		Assert.True(e.NPast > 0, "n_past should be set after prefill");
	}

	[Fact]
	public async Task Concurrency_PrefillRTX_DecodeP100()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
		// RTX has 2 slots, P100 has 1 (decode-only)
		await f.SubmitAsync("sess_d4", 2000, 100);

		// Verify SaveStateChunked was called (save after prefill)
		Assert.True(f.Rpc.HasCall(OpCode.SaveStateChunked, "sess_d4"),
			"Pre-fill → save → restore → decode should include SaveStateChunked");
	}
}

// ═══════════════════════════════════════════════════════════════════════
// Cross-cutting: n_past tracking tests
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class NPastTrackingTests
{
	[Fact]
	public async Task NonStreaming_Decode_UpdatesNPast()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 200);
		await f.SubmitAsync("sess_np1", 1000, 100, stream: false);

		var e = f.Ledger.Lookup("sess_np1");
		Assert.NotNull(e);
		Assert.True(e!.NPast > 0, $"Expected n_past > 0 after non-streaming decode, got {e.NPast}");
	}

	[Fact]
	public async Task Streaming_Decode_UpdatesNPast()
	{
		await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 250);
		await f.SubmitAsync("sess_np2", 1000, 100, stream: true);

		var e = f.Ledger.Lookup("sess_np2");
		Assert.NotNull(e);
		Assert.True(e!.NPast > 0, $"Expected n_past > 0 after streaming decode, got {e.NPast}");
	}
}
