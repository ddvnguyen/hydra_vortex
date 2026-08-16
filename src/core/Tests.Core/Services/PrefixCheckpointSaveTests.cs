using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Tests.Core.TestHelpers;

namespace Tests.Core.Services;

/// <summary>
/// Issue #246: prefix-checkpoint save path in SaveKvAsync.
/// Tests the fire-and-forget Task.Run that stores prefix KV after a
/// successful SaveKv, covering miss/hit/disabled/exception paths.
/// </summary>
public sealed class PrefixCheckpointSaveTests
{
	private static CoordinatorConfig MakeConfig(bool prefixEnabled = true) => new()
	{
		// UseLlamaEngine=true so SaveKvAsync takes the item.KvBlob shortcut
		// instead of calling SaveKvStateCoreAsync (which needs a live llama-server).
		UseLlamaEngine = true,
		PrefixCheckpointEnabled = prefixEnabled,
		Workers =
		[
			new() { Name = "rtx", Host = "localhost", RpcPort = 9601,
				LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2,
				PrefillPriority = 1, DecodePriority = 2 },
		],
	};

	private static WorkerSchedulerService MakeScheduler(
		RpcClient storeRpc, CoordinatorConfig? cfg = null)
	{
		cfg ??= MakeConfig();
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name);
		var proxy = new CompletionProxyService();
		var health = new TestHealthMonitor();
		var sp = new ServiceCollection().BuildServiceProvider();
		return new WorkerSchedulerService(
			cfg, ledger, tracker, proxy, health, storeRpc, sp, Serilog.Log.Logger);
	}

	private static WorkItem MakeItem(string prefixHash, bool prefixEnabled = true)
	{
		var item = new WorkItem(
			new Dictionary<string, object> { ["stream"] = false },
			[
				new() { ["role"] = "system", ["content"] = "You are helpful." },
				new() { ["role"] = "user", ["content"] = "test" },
			],
			$"sess_{prefixHash}", "trace_1", prefixHash, 10, 50);
		item.KvBlob = new byte[1024];
		item.SystemPromptTokens = 50;
		item.PrefillWorker = new WorkerConfig
		{
			Name = "rtx", Host = "localhost", RpcPort = 9601,
			LlamaUrl = "http://localhost:8080", WorkerType = 3,
		};
		item.PrefillSlot = 0;
		item.State = WorkItemState.SaveKv;
		return item;
	}

	// ── 1. Miss path: Stat returns NotFound → Put called, PrefixSaves incremented ──

	[Fact]
	public async Task PrefixSave_MissPath_IssuesPut_AndIncrementsMetric()
	{
		var fake = new FakeStoreClient();
		// Stat returns NotFound (prefix not yet stored).
		fake.SetResponse(OpCode.Stat, (byte)StatusCode.NotFound);
		var scheduler = MakeScheduler(fake);
		var item = MakeItem("abc123");

		var before = CoordinatorMetrics.PrefixSaves.Value;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.SaveDone, next);

		// The prefix save is fire-and-forget — poll until it lands (bounded),
		// instead of a fixed delay that can flake under CI load.
		await WaitUntilAsync(() => CoordinatorMetrics.PrefixSaves.Value >= before + 1);

		// Exactly one Put to "prefix/abc123.kv" with payload > 0.
		var putCalls = fake.Calls.Where(c =>
			c.Op == OpCode.Put && c.Key == "prefix/abc123.kv").ToList();
		Assert.Single(putCalls);
		Assert.True(putCalls[0].PayloadLen > 0);

		// PrefixSaves incremented by exactly 1.
		Assert.Equal(before + 1, CoordinatorMetrics.PrefixSaves.Value);
	}

	// ── 2. Hit path: Stat returns Ok → Put skipped, PrefixSaves unchanged ──

	[Fact]
	public async Task PrefixSave_HitPath_SkipsPut()
	{
		var fake = new FakeStoreClient();
		// Stat returns Ok (prefix already stored — first-writer-wins).
		fake.SetResponse(OpCode.Stat, (byte)StatusCode.Ok);
		var scheduler = MakeScheduler(fake);
		var item = MakeItem("abc456");

		var before = CoordinatorMetrics.PrefixSaves.Value;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.SaveDone, next);

		// Fire-and-forget prefix save performs one more Stat (→ Ok) — poll for
		// it so the "no prefix Put / metric unchanged" asserts aren't a false
		// pass before the background task has run.
		var statAfterMain = fake.CallCount(OpCode.Stat);
		await WaitUntilAsync(() => fake.CallCount(OpCode.Stat) > statAfterMain);

		// No prefix-keyed Put call (main KV save's Put is expected).
		var prefixPuts = fake.Calls.Where(c =>
			c.Op == OpCode.Put && c.Key.StartsWith("prefix/")).ToList();
		Assert.Empty(prefixPuts);

		// PrefixSaves did NOT increment.
		Assert.Equal(before, CoordinatorMetrics.PrefixSaves.Value);
	}

	// ── 3. Disabled/null hash → no Store calls at all ──

	[Fact]
	public async Task PrefixSave_DisabledOrNullHash_NoStoreCalls()
	{
		// Subcase a: PrefixCheckpointEnabled = false.
		{
			var fake = new FakeStoreClient();
			var scheduler = MakeScheduler(fake, MakeConfig(prefixEnabled: false));
			// Item has a valid PrefixHash, but the feature is off.
			var item = MakeItem("abc789");
			var before = CoordinatorMetrics.PrefixSaves.Value;

			var next = await scheduler.DispatchAsync(item, CancellationToken.None);
			Assert.Equal(WorkItemState.SaveDone, next);
			await Task.Delay(200);

			Assert.Equal(0, fake.CallCount(OpCode.Stat));
			// No prefix-keyed Put (main KV save's Put is expected).
			var prefixPuts = fake.Calls.Where(c =>
				c.Op == OpCode.Put && c.Key.StartsWith("prefix/")).ToList();
			Assert.Empty(prefixPuts);
			Assert.Equal(before, CoordinatorMetrics.PrefixSaves.Value);
		}

		// Subcase b: PrefixHash = null.
		{
			var fake = new FakeStoreClient();
			var scheduler = MakeScheduler(fake);
			// Create item with null PrefixHash directly.
			var item = new WorkItem(
				new Dictionary<string, object> { ["stream"] = false },
				[
					new() { ["role"] = "system", ["content"] = "You are helpful." },
					new() { ["role"] = "user", ["content"] = "test" },
				],
				"sess_null", "trace_1", prefixHash: null, 10, 50);
			item.KvBlob = new byte[1024];
			item.PrefillWorker = new WorkerConfig
			{
				Name = "rtx", Host = "localhost", RpcPort = 9601,
				LlamaUrl = "http://localhost:8080", WorkerType = 3,
			};
			item.PrefillSlot = 0;
			item.State = WorkItemState.SaveKv;

			var before = CoordinatorMetrics.PrefixSaves.Value;

			var next = await scheduler.DispatchAsync(item, CancellationToken.None);
			Assert.Equal(WorkItemState.SaveDone, next);
			await Task.Delay(200);

			Assert.Equal(0, fake.CallCount(OpCode.Stat));
			// No prefix-keyed Put (main KV save's Put is expected).
			var prefixPuts2 = fake.Calls.Where(c =>
				c.Op == OpCode.Put && c.Key.StartsWith("prefix/")).ToList();
			Assert.Empty(prefixPuts2);
			Assert.Equal(before, CoordinatorMetrics.PrefixSaves.Value);
		}
	}

	// ── 4. Store throws → logged, no crash, prefix save path exercised ──

	[Fact]
	public async Task PrefixSave_StoreThrows_LoggedNotCrashed()
	{
		var fake = new FakeStoreClient();
		// Stat throws IOException — simulates Store unreachable.
		fake.SetException(OpCode.Stat, new IOException("Connection refused"));
		var scheduler = MakeScheduler(fake);
		var item = MakeItem("throw1");
		var before = CoordinatorMetrics.PrefixSaveFailures.Value;

		// Must not throw — the prefix save is fire-and-forget with its own catch.
		var next = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.SaveDone, next);

		// Wait for the background Task.Run to execute and hit the exception.
		for (int i = 0; i < 10; i++)
		{
			await Task.Delay(200);
			if (fake.CallCount(OpCode.Stat) > 0) break;
		}

		// Stat was attempted (the prefix save path ran).
		Assert.True(fake.CallCount(OpCode.Stat) >= 1,
			$"Expected Stat call, got {fake.CallCount(OpCode.Stat)}");

		// No prefix Put was attempted (Stat threw before reaching Put).
		var prefixPuts = fake.Calls.Where(c =>
			c.Op == OpCode.Put && c.Key.StartsWith("prefix/")).ToList();
		Assert.Empty(prefixPuts);

		var after = CoordinatorMetrics.PrefixSaveFailures.Value;
		Assert.Equal(1, after - before);
	}

	/// <summary>
	/// Poll until <paramref name="cond"/> holds or the timeout elapses, instead of
	/// a fixed delay. The prefix save is fire-and-forget (Task.Run), so fixed
	/// delays can flake under CI load when the background task is slow to start.
	/// </summary>
	private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 3000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (!cond() && Environment.TickCount64 < deadline)
			await Task.Delay(50);
	}
}
