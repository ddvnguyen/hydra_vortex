using System.Text.Json;
using Hydra.Core;
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

		public FakeStoreClient() : base("test", 0) { }

		public override Task<RpcResponse> RequestAsync(
			OpCode op, string key, ReadOnlyMemory<byte> payload,
			string traceId, CancellationToken ct)
		{
			Calls.Add((op, key, payload.Length));
			if (Responses.TryGetValue(op, out var r))
				return Task.FromResult(new RpcResponse(r.Status, r.Meta, []));
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

	private static WorkerSchedulerService MakeScheduler(RpcClient storeRpc)
	{
		var cfg = MakeConfig();
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name);
		var proxy = new CompletionProxyService();
		var health = new TestHealthMonitor();
		var sp = new ServiceCollection().BuildServiceProvider();
		return new WorkerSchedulerService(cfg, ledger, tracker, proxy, health, storeRpc, sp, Serilog.Log.Logger);
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
}
