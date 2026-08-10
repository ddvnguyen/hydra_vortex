using System.Text.Json;
using Hydra.Core;
using Hydra.Core.Caching;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core;

// ═══════════════════════════════════════════════════════════════════════
// M-Perf / Issue #336: PushMissingChunksAsync must surface PUSH_CHUNKS
// failures (status + meta), not silently let the cascade fall through to
// the PUT_MANIFEST "manifest references N unresident chunks" error.
// ═══════════════════════════════════════════════════════════════════════

public sealed class PushMissingChunksTests
{
	/// <summary>RpcClient that returns a configurable per-op response.
	/// For PUSH_CHUNKS we return Partial with a meta that mirrors a real
	/// store rejection ("disk full" or similar). All other ops return Ok
	/// so the surrounding flow can exercise as much as the test needs.</summary>
	internal sealed class FakeStoreClient : RpcClient
	{
		public List<(OpCode Op, string Key, int PayloadLen)> Calls { get; } = new();
		public Dictionary<OpCode, (byte Status, string? Meta)> Responses { get; } = new();
		/// <summary>Per-op response sequences (e.g. ENOSPC then Ok) — consumed
		/// before <see cref="Responses"/> is consulted.</summary>
		public Dictionary<OpCode, Queue<(byte Status, string? Meta)>> ResponseQueues { get; } = new();

		public FakeStoreClient() : base("test", 0) { }

		public override Task<RpcResponse> RequestAsync(
			OpCode op, string key, ReadOnlyMemory<byte> payload,
			string traceId, CancellationToken ct)
		{
			Calls.Add((op, key, payload.Length));
			if (ResponseQueues.TryGetValue(op, out var q) && q.Count > 0)
			{
				var queued = q.Dequeue();
				return Task.FromResult(new RpcResponse(queued.Status, queued.Meta, []));
			}
			if (Responses.TryGetValue(op, out var resp))
				return Task.FromResult(new RpcResponse(resp.Status, resp.Meta, []));
			return Task.FromResult(new RpcResponse(
				(byte)StatusCode.Ok, JsonSerializer.Serialize(new { stored = true }), []));
		}
	}

	private const int TestChunkSize = 1024; // 1 KB — keeps the test payload tiny

	// EnableChunks is intentionally FALSE here. The WorkerSchedulerService
	// constructor mutates the static ChunkEngine.CHUNK_SIZE /
	// ChunkConstants.ChunkSize when EnableChunks is true, and
	// ChunkEngineTests reads those globals — so mutating them would race
	// the other test class when both run in parallel. The function under
	// test (PushMissingChunksAsync) uses _cfg.ChunkSize directly, so a
	// small value still drives the slicing, but no global state changes.
	private static CoordinatorConfig MakeConfig() => new()
	{
		EnableChunks = false,
		ChunkSize = TestChunkSize,
		Workers = new List<WorkerConfig>
		{
			new() { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },
		},
	};

	private static WorkerSchedulerService MakeScheduler(RpcClient storeRpc, LocalChunkCache? chunkCache = null)
	{
		var cfg = MakeConfig();
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name);
		var proxy = new CompletionProxyService();
		var health = new TestHealthMonitor();
		var sp = new ServiceCollection().BuildServiceProvider();
		return new WorkerSchedulerService(cfg, ledger, tracker, proxy, health, storeRpc, sp, Serilog.Log.Logger, chunkCache);
	}

	// Build a real L1 cache that is ALREADY over its byte cap (raw files
	// seeded before the ctor so RebuildFromDisk counts them; the at-write
	// eviction path never gets a chance to run). This is the stale-over-cap
	// state the ENOSPC eviction must free bytes from.
	private static LocalChunkCache MakeOverCapCache(out long usedBytes)
	{
		var dir = Path.Combine(Path.GetTempPath(), $"hydra-l1-enospc-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		var hash = new string('c', 64);
		File.WriteAllBytes(Path.Combine(dir, $"sess_old.{hash}"), new byte[6 * 1024]);
		File.WriteAllBytes(Path.Combine(dir, $"sess_new.{hash}"), new byte[6 * 1024]);
		var l1 = new LocalFsChunkCache(dir, maxBytes: 10 * 1024);
		usedBytes = l1.L1UsedBytes;
		return new LocalChunkCache(l1);
	}

	// Build the test data by hand. The function under test only needs
	// (Index, Hash) on each ChunkRef and a byte[] large enough to slice at
	// Index * _cfg.ChunkSize — no need to call ChunkEngine.ChunkAndHash,
	// which would (a) read the global CHUNK_SIZE and (b) force us to set
	// the global to a non-default value.
	private static (List<ChunkRef> chunks, byte[] stateData) MakeThreeChunks()
	{
		var stateData = new byte[TestChunkSize * 3];
		new Random(42).NextBytes(stateData);
		var chunks = new List<ChunkRef>
		{
			new(0, "h0", TestChunkSize),
			new(1, "h1", TestChunkSize),
			new(2, "h2", TestChunkSize),
		};
		return (chunks, stateData);
	}

	[Fact]
	public async Task PushMissingChunks_PushChunksReturnsPartial_ThrowsWithPushChunksReason()
	{
		var fake = new FakeStoreClient
		{
			Responses = { [OpCode.PushChunks] = ((byte)StatusCode.Partial, "disk full: tmpfs 100% used") },
		};
		var scheduler = MakeScheduler(fake);
		var (chunks, stateData) = MakeThreeChunks();
		var missing = chunks.Select(c => c.Hash).ToList();

		var ex = await Assert.ThrowsAsync<InvalidDataException>(
			() => scheduler.PushMissingChunksAsync(
				storeKey: "sess_336.kv", sessionId: "sess_336",
				missing, chunks, stateData, traceId: "trace_336", ct: default));

		// The throw message names the actual RPC, not the cascading manifest error.
		Assert.Contains("PUSH_CHUNKS failed", ex.Message);
		Assert.Contains("0x03", ex.Message, StringComparison.OrdinalIgnoreCase); // StatusCode.Partial = 0x03
		Assert.Contains("disk full", ex.Message);

		// Exactly one PUSH_CHUNKS call was made (it failed on the first batch).
		var pushCalls = fake.Calls.Where(c => c.Op == OpCode.PushChunks).ToList();
		Assert.Single(pushCalls);

		// Cascade prevention: PUT_MANIFEST was never reached. The throw
		// happens inside the batch flush, before PushMissingChunksAsync
		// returns, so any caller that would have invoked PutManifestAsync
		// next never gets to run. (We do not wire OpCode.PutManifest in
		// the fake's Responses map; the structural guarantee — throw
		// before any caller code runs — is the contract.)
	}

	[Fact]
	public async Task PushMissingChunks_PushChunksReturnsError_ThrowsAndIncrementsCounter()
	{
		// Issue #336 introduced hydra_push_chunks_failures_total{reason}.
		// Reason label is derived from the StatusCode byte: Error → "error".
		var fake = new FakeStoreClient
		{
			Responses = { [OpCode.PushChunks] = ((byte)StatusCode.Error, "store: write failed (EIO)") },
		};
		var scheduler = MakeScheduler(fake);
		var (chunks, stateData) = MakeThreeChunks();
		var missing = chunks.Select(c => c.Hash).ToList();

		var ex = await Assert.ThrowsAsync<InvalidDataException>(
			() => scheduler.PushMissingChunksAsync(
				storeKey: "sess_336.kv", sessionId: "sess_336",
				missing, chunks, stateData, traceId: "trace_336", ct: default));

		Assert.Contains("PUSH_CHUNKS failed", ex.Message);
		Assert.Contains("0x02", ex.Message, StringComparison.OrdinalIgnoreCase); // Error = 0x02

		// The error counter is labelled by reason; the (only) child with the
		// "error" reason must be at least 1 after the throw.
		var labelled = CoordinatorMetrics.PushChunksFailures.WithLabels("error");
		Assert.True(labelled.Value >= 1, $"expected PushChunksFailures{{reason=error}} >= 1, was {labelled.Value}");

		// Issue #615: EIO is NOT ENOSPC — no eviction, no retry. Exactly one
		// PUSH_CHUNKS call; the meta never contained "No space left on device".
		var pushCalls = fake.Calls.Where(c => c.Op == OpCode.PushChunks).ToList();
		Assert.Single(pushCalls);
	}

	[Fact]
	public async Task PushMissingChunks_AllPushesSucceed_ReturnsChunkCount()
	{
		// Regression: the happy path must still work and return the count of
		// successfully pushed chunks. PUSH_CHUNKS returns Ok with empty meta.
		var fake = new FakeStoreClient(); // default Ok for every op
		var scheduler = MakeScheduler(fake);
		var (chunks, stateData) = MakeThreeChunks();
		var missing = chunks.Select(c => c.Hash).ToList();

		var pushed = await scheduler.PushMissingChunksAsync(
			storeKey: "sess_336_ok.kv", sessionId: "sess_336_ok",
			missing, chunks, stateData, traceId: "trace_336_ok", ct: default);

		Assert.Equal(3, pushed);
		var pushCalls = fake.Calls.Where(c => c.Op == OpCode.PushChunks).ToList();
		Assert.Single(pushCalls); // all 3 chunks fit in one 1 KB × 3 batch (well under 32 MB)
	}

	[Fact]
	public async Task PushMissingChunks_NoMissing_ShortCircuitsAndDoesNotCallStore()
	{
		// When SyncMissingAsync reports no missing chunks, the function must
		// not call PUSH_CHUNKS at all. This is the existing early-return;
		// the test pins it so the new error-handling code cannot accidentally
		// trigger a store call on the empty-missing path.
		var fake = new FakeStoreClient();
		var scheduler = MakeScheduler(fake);
		var (chunks, stateData) = MakeThreeChunks();

		var pushed = await scheduler.PushMissingChunksAsync(
			storeKey: "sess_336_empty.kv", sessionId: "sess_336_empty",
			missing: new List<string>(), chunks, stateData, traceId: "trace_336_empty", ct: default);

		Assert.Equal(0, pushed);
		Assert.DoesNotContain(fake.Calls, c => c.Op == OpCode.PushChunks);
	}

	// ── Issue #615: evict-on-ENOSPC ─────────────────────────────────────
	// The L1 tmpfs chunk cache and the Store's chunk dir share the
	// /mnt/llm-ram mount, so when PUSH_CHUNKS is rejected with ENOSPC the
	// coordinator must evict the L1 LRU immediately and retry the batch once.

	private const string EnospcMeta =
		"push_chunks failed: No space left on device : '/mnt/llm-ram/store/chunks/abcd.tmp'";

	[Fact]
	public async Task PushMissingChunks_Enospc_EvictsL1AndRetriesOnce_ThenThrows()
	{
		var fake = new FakeStoreClient
		{
			Responses = { [OpCode.PushChunks] = ((byte)StatusCode.Error, EnospcMeta) },
		};
		var cache = MakeOverCapCache(out var usedBefore);
		try
		{
			var scheduler = MakeScheduler(fake, cache);
			var (chunks, stateData) = MakeThreeChunks();
			var missing = chunks.Select(c => c.Hash).ToList();

			var ex = await Assert.ThrowsAsync<InvalidDataException>(
				() => scheduler.PushMissingChunksAsync(
					storeKey: "sess_615.kv", sessionId: "sess_615",
					missing, chunks, stateData, traceId: "trace_615", ct: default));

			Assert.Contains("No space left", ex.Message);

			// Exactly ONE retry: 2 PUSH_CHUNKS calls total (evict + retry once).
			var pushCalls = fake.Calls.Where(c => c.Op == OpCode.PushChunks).ToList();
			Assert.Equal(2, pushCalls.Count);

			// The eviction actually freed tmpfs bytes from the over-cap L1.
			Assert.True(cache.L1UsedBytes < usedBefore,
				$"expected L1 usage to drop below {usedBefore}, was {cache.L1UsedBytes}");
		}
		finally
		{
			cache.Dispose();
		}
	}

	[Fact]
	public async Task PushMissingChunks_Enospc_RetrySucceeds_ReturnsChunkCount()
	{
		var fake = new FakeStoreClient();
		fake.ResponseQueues[OpCode.PushChunks] = new Queue<(byte Status, string? Meta)>();
		fake.ResponseQueues[OpCode.PushChunks].Enqueue(((byte)StatusCode.Error, EnospcMeta));
		fake.ResponseQueues[OpCode.PushChunks].Enqueue(((byte)StatusCode.Ok, null));
		var scheduler = MakeScheduler(fake);
		var (chunks, stateData) = MakeThreeChunks();
		var missing = chunks.Select(c => c.Hash).ToList();

		var pushed = await scheduler.PushMissingChunksAsync(
			storeKey: "sess_615_ok.kv", sessionId: "sess_615_ok",
			missing, chunks, stateData, traceId: "trace_615_ok", ct: default);

		Assert.Equal(3, pushed);
		var pushCalls = fake.Calls.Where(c => c.Op == OpCode.PushChunks).ToList();
		Assert.Equal(2, pushCalls.Count); // original + one retry
	}
}
