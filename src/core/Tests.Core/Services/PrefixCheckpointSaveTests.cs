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
	private static CoordinatorConfig MakeConfig(bool prefixEnabled = true, bool enableChunks = false) => new()
	{
		// UseLlamaEngine=true so SaveKvAsync takes the item.KvBlob shortcut
		// instead of calling SaveKvStateCoreAsync (which needs a live llama-server).
		UseLlamaEngine = true,
		EnableChunks = enableChunks,
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

		// The prefix save is fire-and-forget — wait for it.
		await Task.Delay(200);

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

		await Task.Delay(200);

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

	// ── 5. #721: stream-to-store prefill (KvStreamedToStore=true, payload null)
	//    → prefix save must be skipped BEFORE any store op. Pre-fix the miss path
	//    issued Put with the null payload → zero-byte blob under prefix/... (the
	//    "never write an empty blob" invariant) + NRE on the SizeMB log line
	//    (logged as prefix_save_failed), and every later restore of the poisoned
	//    key forwarded an empty STATE_PUT the engine quarantines. ──

	[Fact]
	public async Task PrefixSave_StreamedToStore_SkipsSave_NeverWritesEmptyBlob()
	{
		var fake = new FakeStoreClient();
		// Stat returns NotFound — the pre-fix code took the miss path and issued
		// the prefix Put with the null payload.
		fake.SetResponse(OpCode.Stat, (byte)StatusCode.NotFound);
		// EnableChunks=true so SaveKvAsync recognises the streamed outcome
		// (payload stays null, session KV is already in the Store under the
		// session key) instead of calling the engine StateGet RPC.
		var scheduler = MakeScheduler(fake, MakeConfig(enableChunks: true));
		var item = MakeItem("stream721");
		// Force the EnginePrefillChunkedAndStoreAsync (#470) outcome: KV already
		// streamed to the Store, no in-memory payload.
		item.KvBlob = null;
		item.KvStreamedToStore = true;
		item.KvBytes = 2048;

		var savesBefore = CoordinatorMetrics.PrefixSaves.Value;
		var failuresBefore = CoordinatorMetrics.PrefixSaveFailures.Value;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);
		Assert.Equal(WorkItemState.SaveDone, next);

		// Give any (incorrectly) spawned fire-and-forget task time to run.
		await Task.Delay(200);

		// The invariant: NO store op under the prefix key at all — no Stat,
		// no PutMeta, and crucially no Put with an empty/zero payload.
		var prefixCalls = fake.Calls.Where(c => c.Key.StartsWith("prefix/")).ToList();
		Assert.Empty(prefixCalls);

		// No zero-length Put anywhere (the pre-fix bug wrote one under prefix/).
		Assert.DoesNotContain(fake.Calls, c => c.PayloadLen == 0);

		// The NRE is gone: prefix_save_failed (PrefixSaveFailures) not incremented.
		Assert.Equal(failuresBefore, CoordinatorMetrics.PrefixSaveFailures.Value);
		Assert.Equal(savesBefore, CoordinatorMetrics.PrefixSaves.Value);
	}
}
