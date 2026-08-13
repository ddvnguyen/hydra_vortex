using System.Text.Json;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #635 fix 4: migration-manifest staleness (real bug).
//
// Live repro (smoke #8): MigrationContinuation turn 2 restored the 501-token
// PRE-decode blob — the post-decode bg_saved ('bg_saved Sid=... (engine,
// KvBlob)') never updated the chunk manifest used by the restore path, so the
// engine re-prefilled ~1300 tokens → prompt_ms=5094 > 5000 budget.
//
// Two mechanisms were broken:
//   1. The post-decode bg_save wrote only a plain blob (OpCode.Put) — the
//      chunk manifest kept referencing the stale PRE-decode chunks.
//   2. In engine mode BgSaveAsync wrote item.KvBlob, which on merged-decode
//      routes is the PRE-decode RESTORE blob (RestoreKvAsync sets it) — the
//      stored KV itself never advanced past the pre-decode state.
//
// Fix under test: BgSaveAsync always pulls the CURRENT slot state via StateGet
// (the true post-decode state) and PersistKvToStoreAsync keeps the chunk
// manifest in sync with it (chunks + manifest n_past = the post-decode total
// from the ledger). Assertions prove the manifest ADVANCES to reference the
// post-decode blob — a later restore reads the latest state.
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class BgSaveManifestTests
{
	private static readonly byte[] PostDecodeBlob = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
	private static readonly byte[] StalePreDecodeBlob = Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray();

	/// <summary>
	/// RPC double recording Store manifest ops. Serves StateGet with the
	/// post-decode blob; SyncMissing reports nothing missing (so the push is
	/// skipped and the test asserts purely on the recorded PUT_MANIFEST);
	/// records every PUT_MANIFEST payload for inspection.
	/// </summary>
	private sealed class ManifestRecordingRpc : RpcClient
	{
		public List<string> PutManifestPayloads { get; } = new();
		public List<(OpCode Op, string Key)> Calls { get; } = new();

		public ManifestRecordingRpc() : base("test", 0) { }

		public override Task<RpcResponse> RequestAsync(
			OpCode op, string key, ReadOnlyMemory<byte> payload,
			string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
		{
			Calls.Add((op, key));
			return op switch
			{
				OpCode.StateGet => Task.FromResult(new RpcResponse(
					(byte)StatusCode.Ok,
					JsonSerializer.Serialize(new { n_past = 2150, stored = true }),
					PostDecodeBlob)),
				OpCode.SyncMissing => Task.FromResult(new RpcResponse(
					(byte)StatusCode.Ok, null,
					JsonSerializer.SerializeToUtf8Bytes(new { missing_hashes = new string[] { } }))),
				OpCode.PutManifest => RecordManifest(payload),
				_ => Task.FromResult(new RpcResponse(
					(byte)StatusCode.Ok,
					JsonSerializer.Serialize(new { stored = true }),
					[])),
			};
		}

		private Task<RpcResponse> RecordManifest(ReadOnlyMemory<byte> payload)
		{
			PutManifestPayloads.Add(System.Text.Encoding.UTF8.GetString(payload.ToArray()));
			return Task.FromResult(new RpcResponse(
				(byte)StatusCode.Ok,
				JsonSerializer.Serialize(new { stored = true }),
				[]));
		}

		public bool HasPutManifest() => PutManifestPayloads.Count > 0;

		/// <summary>Parsed JSON of the most recent PUT_MANIFEST payload, or default.</summary>
		public JsonElement LatestManifest()
		{
			if (PutManifestPayloads.Count == 0) return default;
			using var doc = JsonDocument.Parse(PutManifestPayloads[^1]);
			return doc.RootElement.Clone();
		}
	}

	private static (WorkerSchedulerService Scheduler, SessionLedger Ledger, ManifestRecordingRpc Rpc, CoordinatorConfig Cfg) Create()
	{
		var cfg = new CoordinatorConfig
		{
			RunMode = "fast",
			UseLlamaEngine = true,
			EnableChunks = true, // chunked store: manifest is the restore path's source of truth
			PrefixCheckpointEnabled = false,
			WarmSlotVerificationEnabled = false,
			MixPrecisionEnabled = false,
			AtomicThreshold = 2048,
			Workers = new List<WorkerConfig>
			{
				new() { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },
			}
		};
		var ledger = new SessionLedger();
		var tracker = new WorkerTracker();
		foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);

		var rpc = new ManifestRecordingRpc();
		var sp = new ServiceCollection().BuildServiceProvider();
		var scheduler = new WorkerSchedulerService(cfg, ledger, tracker,
			new TestCompletionProxy(), new TestHealthMonitor(), rpc, sp, Serilog.Log.Logger);
		scheduler.AgentClientFactory = (_, _) => rpc;
		scheduler.LlamaClientFactory = _ => new TestLlamaClient();
		return (scheduler, ledger, rpc, cfg);
	}

	private static WorkItem MakeItem(string sessionId) => new(
		new Dictionary<string, object>
		{
			["stream"] = false,
			["max_tokens"] = 50,
			["model"] = "nano"
		},
		new List<Dictionary<string, object>>
		{
			new() { ["role"] = "user", ["content"] = new string('x', 500) }
		},
		sessionId,
		"trace_bgsave",
		null,
		500,
		50
	);

	[Fact]
	public async Task BgSave_UpdatesChunkManifest_WithPostDecodeState()
	{
		var (scheduler, ledger, rpc, cfg) = Create();

		// A migrated session's continuation has run: the ledger holds the
		// POST-decode total (updated by TrackAfterCompletion/Stream before the
		// pipeline reaches BgSave).
		var sessionId = "sess_bgsave_1";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 2150);
		ledger.MarkStoreState(sessionId);

		var item = MakeItem(sessionId);
		item.DecodeWorker = cfg.Workers[0];
		// Simulate the merged-route state: item.KvBlob still holds the
		// PRE-decode RESTORE blob (RestoreKvAsync sets it). Pre-#635 BgSaveAsync
		// wrote THIS blob; the fix must ignore it and StateGet the live slot.
		item.KvBlob = StalePreDecodeBlob;
		item.State = WorkItemState.BgSave;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Done, next);
		Assert.True(rpc.HasPutManifest(), "the bg_save must write a chunk manifest");

		// The manifest must reference the POST-decode state:
		//  - n_past = the post-decode ledger total (2150), NOT the stale 501.
		//  - chunks = SHA256 of the StateGet (post-decode) blob, NOT the stale
		//    pre-decode KvBlob the engine-mode shortcut used to write.
		var manifest = rpc.LatestManifest();
		Assert.Equal(2150, manifest.GetProperty("n_past").GetInt32());
		Assert.Equal(PostDecodeBlob.Length, manifest.GetProperty("total_size").GetInt64());
		var chunks = manifest.GetProperty("chunks");
		Assert.Equal(1, chunks.GetArrayLength());
		var hash = chunks[0].GetProperty("hash").GetString();
		Assert.Equal(ChunkEngine.ComputeHash(PostDecodeBlob), hash);
		Assert.True(hash != ChunkEngine.ComputeHash(StalePreDecodeBlob),
			"the manifest must NOT reference the stale pre-decode KvBlob the engine-mode shortcut used to write");
	}

	[Fact]
	public async Task BgSave_WritesPlainBlob_ForMigrationStoreGet_EvenInChunkMode()
	{
		var (scheduler, ledger, rpc, cfg) = Create();
		var sessionId = "sess_bgsave_2";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 2150);

		var item = MakeItem(sessionId);
		item.DecodeWorker = cfg.Workers[0];
		item.State = WorkItemState.BgSave;

		await scheduler.DispatchAsync(item, CancellationToken.None);

		// MigrateSessionAsync's Store Get reads the full blob file (no
		// chunk-aware path) — the plain Put must be retained in chunk mode.
		Assert.Contains(rpc.Calls, c => c.Op == OpCode.Put && c.Key == $"{sessionId}.kv");
	}

	[Fact]
	public async Task BgSave_ManifestNpast_FallsBackToItemNPastAfter_WhenLedgerEmpty()
	{
		var (scheduler, ledger, rpc, cfg) = Create();
		var sessionId = "sess_bgsave_3";

		var item = MakeItem(sessionId);
		item.DecodeWorker = cfg.Workers[0];
		item.NPastAfter = 900; // no ledger entry — item's post-decode count is used
		item.State = WorkItemState.BgSave;

		var next = await scheduler.DispatchAsync(item, CancellationToken.None);

		Assert.Equal(WorkItemState.Done, next);
		Assert.Equal(900, rpc.LatestManifest().GetProperty("n_past").GetInt32());
	}

	[Fact]
	public void PersistHelper_KeepsManifestInSyncWithBlob_EvenWithStaleItemIdentity()
	{
		// Identity sanity: when the item carries no model identity (e.g. the
		// streaming path's fire-and-forget write), the manifest is still
		// written — with empty identity fields the restore-side cross-model
		// guard treats "both empty" as skip, same as pre-#470 callers.
		var (scheduler, ledger, rpc, cfg) = Create();
		var sessionId = "sess_bgsave_4";
		ledger.Register(sessionId, "rtx", slotId: 0, nPast: 700);

		var item = MakeItem(sessionId);
		item.DecodeWorker = cfg.Workers[0];
		item.State = WorkItemState.BgSave;

		_ = scheduler.DispatchAsync(item, CancellationToken.None).GetAwaiter().GetResult();

		var manifest = rpc.LatestManifest();
		Assert.Equal(700, manifest.GetProperty("n_past").GetInt32());
		Assert.Equal("", manifest.GetProperty("model_alias").GetString());
		Assert.Equal(ChunkEngine.ComputeHash(PostDecodeBlob), manifest.GetProperty("chunks")[0].GetProperty("hash").GetString());
	}
}
