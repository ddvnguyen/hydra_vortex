using System.Text.Json;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Hydra.Shared;
using Tests.Core.Harness;
using Xunit;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// Chunked-save wire parity (epic #591 WP3): when <c>EnableChunks</c> is set the
/// v2 <see cref="SaveKvRunner"/> must reproduce the LEGACY chunked wire instead of
/// the plain Put — SYNC_MISSING the ordered hash list, PUSH_CHUNKS the missing
/// bodies, then PUT_MANIFEST the authoritative manifest. The differential goldens
/// (<c>chunked_save</c> / <c>chunked_save_with_pushes</c>) pin the exact payload
/// lengths: SyncMissing 269 (4×64-hex hashes), PushChunks 1028 (4-byte header +
/// one 1024-byte chunk), PutManifest 540 (empty model identity, n_past=2000).
///
/// <para>The fixture drives the REAL v2 scheduler end-to-end over the harness
/// <see cref="ScenarioRpcClient"/> (ordered RPC log + deterministic prefill KV),
/// so these pins cover the full Route → Prefill → SaveKv → Decode → BgSave path —
/// including the golden invariant that the post-decode BgSave stays a PLAIN Put
/// even when the save itself is chunked.</para>
/// </summary>
public sealed class ChunkedSaveTests
{
    private const int PrefillKvSize = 4096; // ScenarioRpcClient.PrefillKvBlob
    private const int ChunkSize = 1024;     // chunked scenarios use a tiny chunk size

    private static CoordinatorConfig Config(bool enableChunks) => new()
    {
        RunMode = "fast",
        UseLlamaEngine = true,
        EnableChunks = enableChunks,
        ChunkSize = ChunkSize,
        AtomicThreshold = 2048, // estimatedTokens 500 → Atomic (same-node rtx)
        LlamaRequestTimeoutS = 30,
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },
            new() { Name = "p100", LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
        },
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public CoordinatorConfig Cfg { get; }
        public ScenarioRpcClient Rpc { get; } = new();
        public WorkerTracker Tracker { get; }
        public SessionLedger Ledger { get; } = new();
        public FakeCompletionProxy Proxy { get; } = new();
        public WorkerSchedulerV2 Scheduler { get; }

        private readonly CancellationTokenSource _runCts = new();

        public Fixture(CoordinatorConfig cfg, Action<ScenarioRpcClient>? configureRpc = null)
        {
            Cfg = cfg;
            Tracker = new WorkerTracker();
            foreach (var w in cfg.Workers) Tracker.InitWorker(w.Name, w.Slots);

            // NB: no global chunk-size static syncing here — the v2 chunked save
            // passes the configured chunk size EXPLICITLY (SaveKvRunner →
            // ChunkEngine.ChunkAndHash(kv, cfg.ChunkSize)), so it neither reads
            // nor mutates the ambient ChunkEngine.CHUNK_SIZE static (which other
            // suites' legacy chunked scenarios mutate — a parallel-execution race).

            // The SAME recording fake serves as the per-worker engine channel AND the
            // store (harness pattern) so the ordered RPC stream is deterministic.
            var channels = new Dictionary<string, IEngineRpcClient>
            {
                ["rtx"] = new EngineRpcClientAdapter(Rpc),
                ["p100"] = new EngineRpcClientAdapter(Rpc),
            };
            var store = new StoreGateway(Rpc);
            var engine = new EngineRpcGateway(channels);
            var leases = new LeaseManager(Tracker);
            var health = new FakeHealthMonitor();
            var runners = new WorkerStateRunner[]
            {
                new PlanRunner(new RoutePlanner(), leases, Ledger, cfg.Workers, Tracker, health, cfg, new FakeWarmSlotVerifier()),
                new PrefillRunner(engine, Proxy),
                new PrefixRestoreRunner(cfg, store, engine, Ledger),
                new SaveKvRunner(store, Ledger, engine, cfg),
                new RestoreRunner(store, engine, Ledger, leases, Proxy, cfg),
                new DecodeRunner(Proxy, engine, Ledger, cfg, health),
                new BgSaveRunner(engine, store, Ledger),
            };
            Scheduler = new WorkerSchedulerV2(
                cfg, Ledger, Tracker, health,
                new RequestClassifier(), new RoutePlanner(), leases, runners, new TimelineEmitter(),
                engine, store, Proxy);

            configureRpc?.Invoke(Rpc);
            _ = Scheduler.RunAsync(_runCts.Token);
        }

        /// <summary>Submit one small (Atomic) request and wait for completion.</summary>
        public async Task SubmitAsync(string sessionId = "sess_h", int estimatedTokens = 500)
        {
            var req = new Dictionary<string, object>
            {
                ["stream"] = false, ["max_tokens"] = 100, ["model"] = "nano",
            };
            var msgs = new List<Dictionary<string, object>>
            {
                new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) },
            };
            var result = await Scheduler.SubmitAsync(
                req, msgs, sessionId, estimatedTokens, 100, null, CancellationToken.None);
            CompletionResults.Unwrap(result);
        }

        public ValueTask DisposeAsync()
        {
            _runCts.Cancel();
            _runCts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ChunkedSave_Emits_SyncMissing_And_PutManifest_No_PushChunks_When_All_Present()
    {
        // SYNC_MISSING default response is Ok with an EMPTY payload → every chunk
        // already resident → no PUSH_CHUNKS, straight to PUT_MANIFEST (golden
        // chunked_save: SyncMissing 269 → PutManifest 540).
        await using var fx = new Fixture(Config(enableChunks: true));
        await fx.SubmitAsync();

        var storeCalls = fx.Rpc.RpcCalls.Where(c => c.Key == "sess_h.kv").ToList();
        Assert.Contains(storeCalls, c => c.Op == OpCode.SyncMissing && c.PayloadLen == 269);
        Assert.Contains(storeCalls, c => c.Op == OpCode.PutManifest && c.PayloadLen == 540);
        Assert.DoesNotContain(storeCalls, c => c.Op == OpCode.PushChunks);

        // Ordering (the RpcCalls list is insertion-ordered): SyncMissing then PutManifest.
        var syncIdx = storeCalls.FindIndex(c => c.Op == OpCode.SyncMissing);
        var manifestIdx = storeCalls.FindIndex(c => c.Op == OpCode.PutManifest);
        Assert.True(syncIdx >= 0 && manifestIdx > syncIdx, "SYNC_MISSING must precede PUT_MANIFEST");

        // Golden invariant: the post-decode BgSave stays a PLAIN Put (2048-byte StateGet).
        Assert.Contains(storeCalls, c => c.Op == OpCode.Put && c.PayloadLen == 2048);
    }

    [Fact]
    public async Task ChunkedSave_With_Missing_Chunk_Emits_PushChunks_Between_SyncMissing_And_PutManifest()
    {
        await using var fx = new Fixture(Config(enableChunks: true));

        // Inject a SYNC_MISSING response that reports chunk 0 as missing → the
        // chunk body must be pushed (4-byte length header + 1024 bytes = 1028)
        // BETWEEN SyncMissing and PutManifest (golden chunked_save_with_pushes).
        // Hash chunk 0 from the fixture's deterministic prefill KV blob (default:
        // 4096 zero bytes @ 1024 chunk size → 4 chunks).
        var firstChunkHash = ChunkEngine.ComputeHash(fx.Rpc.PrefillKvBlob.AsSpan(0, ChunkSize));
        var missing = JsonSerializer.SerializeToUtf8Bytes(new { missing_hashes = new[] { firstChunkHash } });
        fx.Rpc.SetKeyResponse("sess_h.kv", OpCode.SyncMissing, (byte)StatusCode.Ok, null, missing);

        await fx.SubmitAsync();

        var storeCalls = fx.Rpc.RpcCalls.Where(c => c.Key == "sess_h.kv").ToList();
        Assert.Contains(storeCalls, c => c.Op == OpCode.SyncMissing && c.PayloadLen == 269);
        Assert.Contains(storeCalls, c => c.Op == OpCode.PushChunks && c.PayloadLen == 1028);
        Assert.Contains(storeCalls, c => c.Op == OpCode.PutManifest && c.PayloadLen == 540);

        var syncIdx = storeCalls.FindIndex(c => c.Op == OpCode.SyncMissing);
        var pushIdx = storeCalls.FindIndex(c => c.Op == OpCode.PushChunks);
        var manifestIdx = storeCalls.FindIndex(c => c.Op == OpCode.PutManifest);
        Assert.True(syncIdx >= 0 && pushIdx > syncIdx && manifestIdx > pushIdx,
            "expected order SYNC_MISSING → PUSH_CHUNKS → PUT_MANIFEST, got "
            + string.Join(",", storeCalls.Select(c => $"{c.Op}:{c.PayloadLen}")));
    }

    [Fact]
    public async Task NonChunked_Save_Emits_Plain_Put_And_No_Chunk_Opcodes()
    {
        // EnableChunks=false → the legacy non-chunked wire: a plain Put of the full
        // KV blob (4096) and NOTHING chunked (golden cold_atomic_engine counterpart).
        await using var fx = new Fixture(Config(enableChunks: false));
        await fx.SubmitAsync();

        var storeCalls = fx.Rpc.RpcCalls.Where(c => c.Key == "sess_h.kv").ToList();
        Assert.Contains(storeCalls, c => c.Op == OpCode.Put && c.PayloadLen == PrefillKvSize);
        Assert.DoesNotContain(storeCalls, c => c.Op is OpCode.SyncMissing or OpCode.PushChunks or OpCode.PutManifest);
        // BgSave still runs its plain Put after decode.
        Assert.Contains(storeCalls, c => c.Op == OpCode.Put && c.PayloadLen == 2048);
    }
}
