using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Hydra.Core;
using Hydra.Core.Caching;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #720 P1 — non-merged chunked decode restore: the sm_60 (P100) path.
//
// Engines without merged_decode capability take the phase-3 else branch:
// STATE_PUT via the state RPC client. P1 replaced the coordinator-side
// AssembleFromChunksAsync (full-blob byte[]) with OrderedKvStateStream
// streamed into RequestStreamBodyAsync. This test drives the full
// scheduler pipeline hermetically with a non-merged health monitor and
// asserts:
//   (a) GET_MANIFEST + GET_CHUNKED hit the store (restore actually ran),
//   (b) exactly ONE STATE_PUT, its streamed body == the chunked store
//       payload reassembled in manifest order, declared length == total,
//   (c) the STATE_PUT response meta's n_past lands in the ledger
//       (restore_kv_timing path consumed the streamed response), and
//   (d) decode completes (the item was not aborted by the guard).
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class NonMergedChunkedRestoreTests
{
    [Fact]
    public async Task NonMergedDecode_RestoresKvViaStreamedStatePut()
    {
        await using var f = new NonMergedFixture();
        f.Ledger.Register("sess_nm", "p100", slotId: 0, nPast: 2000, prefixHash: null);
        f.Ledger.MarkStoreState("sess_nm");
        f.Ledger.MarkEvicted("sess_nm"); // non-resident → decode must restore

        var result = await f.SubmitAsync("sess_nm", 3000, 50, stream: false);

        // (d) the turn completed — no cross-model abort, no restore failure.
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        Assert.True(dict.ContainsKey("choices"), "non-merged continuation must produce a completion");

        // (a) restore actually ran against the store.
        Assert.True(f.Rpc.HasCall(OpCode.GetManifest, "sess_nm.kv"),
            "non-merged chunked restore must read the KV manifest from the store");
        Assert.True(f.Rpc.HasCall(OpCode.GetChunked, "sess_nm.kv"),
            "non-merged chunked restore must fetch missing chunks via GET_CHUNKED");

        // (b) exactly one STATE_PUT, streamed body == manifest-ordered blob.
        var put = Assert.Single(f.Rpc.StatePutCalls);
        Assert.Equal(0, int.Parse(put.SlotKey));
        Assert.Equal(NonMergedRpcClient.ChunkData, put.Body);
        Assert.Equal(NonMergedRpcClient.ChunkData.Length, put.DeclaredLen);

        // (c) n_past from the STATE_PUT meta propagated to the ledger.
        var entry = f.Ledger.Lookup("sess_nm");
        Assert.NotNull(entry);
        Assert.True(entry!.NPast > 2000, $"ledger n_past should advance past the restored 2000 (was {entry.NPast})");
    }

    // ── Doubles ─────────────────────────────────────────────────────────

    /// <summary>Health monitor advertising NO merged_decode — routes the
    /// restore through the phase-3 STATE_PUT (streamed) branch.</summary>
    private sealed class NoMergedHealthMonitor : IHealthMonitorService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public bool IsHealthy(string nodeName) => true;
        public bool IsStoreHealthy => true;
        public int? GetIdleSlot(string nodeName) => 0;

        public NodeInfo? GetNodeInfo(string nodeName) => new()
        {
            NodeName = nodeName,
            Healthy = true,
            SlotsTotal = nodeName == "rtx" ? 2 : 1,
            SlotsIdle = nodeName == "rtx" ? 2 : 1,
            EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase), // no merged_decode
            CurrentModel = "",
        };

        public Dictionary<string, object> GetHealthSummary() => new();
        public event Action? HealthyChanged;
        public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
        public void MarkHealthy(string nodeName) { }
    }

    /// <summary>Store + engine RPC double. Serves the manifest and the chunked
    /// payload; records the streamed STATE_PUT body (the P1 contract).</summary>
    internal sealed class NonMergedRpcClient : RpcClient
    {
        public static readonly byte[] ChunkData =
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

        public List<(OpCode Op, string Key)> Calls { get; } = new();
        public List<(string SlotKey, byte[] Body, long DeclaredLen)> StatePutCalls
            => StatePutBodies;

        private readonly List<(string SlotKey, byte[] Body, long DeclaredLen)> StatePutBodies = new();

        /// <summary>STATE_PUT response meta — the identity must MATCH the
        /// manifest's (Mini) so CrossModelGuard.Decide proceeds.</summary>
        public string StatePutMeta { get; set; } = JsonSerializer.Serialize(new
        {
            n_past = 2000,
            model_match = true,
            tokenizer = "llama",
            model_name = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini",
            model_quant = "Q3_K",
            model_capabilities = 2,
            model_alias = "qwen3.6-35B-mini",
        });

        public NonMergedRpcClient() : base("test", 0) { }

        public bool HasCall(OpCode op, string? keyContains = null)
            => Calls.Any(c => c.Op == op && (keyContains == null || c.Key.Contains(keyContains)));

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
        {
            Calls.Add((op, key));
            var response = op switch
            {
                OpCode.GetManifest => new RpcResponse(
                    (byte)StatusCode.Ok, "manifest",
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        n_past = 2000,
                        total_size = ChunkData.Length,
                        model_alias = "qwen3.6-35B-mini",
                        tokenizer = "llama",
                        model_name = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini",
                        model_quant = "Q3_K",
                        model_capabilities = 2,
                        model_path = "/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                        chunks = new[]
                        {
                            new { index = 0, hash = "chunk-hash-0", size = ChunkData.Length },
                        },
                    })),
                _ => new RpcResponse((byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, stored = true, restored = true, erased = true }), []),
            };
            return Task.FromResult(response);
        }

        /// <summary>GET_CHUNKED via the streaming entry point — OrderedKvStateStream
        /// fetches through RequestChunkedPayloadAsync, NOT RequestAsync.</summary>
        public override Task<RpcResponse> RequestChunkedPayloadAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct,
            Action<long> onPayloadLen,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
            TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
        {
            Calls.Add((op, key));
            var wire = new byte[8 + ChunkData.Length];
            BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(0), 0);              // chunk index
            BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(4), ChunkData.Length); // size
            ChunkData.CopyTo(wire, 8);
            onPayloadLen(wire.Length);
            if (wire.Length > 0)
                onChunk(wire, ct).GetAwaiter().GetResult();
            return Task.FromResult(new RpcResponse((byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 2000 }), []));
        }

        /// <summary>The P1 seam: the scheduler streams the STATE_PUT body here.
        /// Drain it, record the exact bytes, return the configured meta.</summary>
        public override async Task<RpcResponse> RequestStreamBodyAsync(
            OpCode op, string key, Stream body, long bodyLen,
            string traceId, CancellationToken ct)
        {
            Calls.Add((op, key));
            using var ms = new MemoryStream();
            await body.CopyToAsync(ms, ct);
            if (op == OpCode.StatePut)
                StatePutBodies.Add((key, ms.ToArray(), bodyLen));
            return new RpcResponse((byte)StatusCode.Ok, op == OpCode.StatePut ? StatePutMeta : null, []);
        }
    }

    /// <summary>Proxy double: non-merged decode lands here via the node URL.</summary>
    private sealed class NonMergedProxy : ICompletionProxyService
    {
        public List<(string NodeUrl, Dictionary<string, object> Body)> Calls { get; } = new();

        public Task<Dictionary<string, object>> ProxyCompletionAsync(
            string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
        {
            Calls.Add((nodeUrl, new Dictionary<string, object>(body)));
            return Task.FromResult(Result());
        }

        public async IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(
            string nodeUrl, Dictionary<string, object> body, string traceId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n");
            await Task.CompletedTask;
        }

        public Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct)
            => Task.FromResult(true);

        public async IAsyncEnumerable<byte[]> PollDecodeStreamAsync(
            string nodeUrl, int decodeRequestId, string traceId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
            WorkItem? item = null)
        {
            yield return Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n");
            await Task.CompletedTask;
        }

        public Task<Dictionary<string, object>> PollDecodeResultAsync(
            string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
            => Task.FromResult(Result());

        public Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
            => Task.CompletedTask;

        public Task EraseSlotAsync(string nodeUrl, int slotId, CancellationToken ct)
            => Task.CompletedTask;

        private static Dictionary<string, object> Result()
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "Hello from non-merged restore" },
                        finish_reason = "stop",
                    }
                },
                usage = new { prompt_tokens = 5000, completion_tokens = 50, total_tokens = 5050 },
                id_slot = 0,
                id = "chatcmpl-nonmerged",
                model = "nano",
                created = 0,
            }));
            return JsonSerializer.Deserialize<Dictionary<string, object>>(doc.RootElement.GetRawText())!;
        }
    }

    // ── Fixture (mirrors MigratedContinuationFixture, non-merged) ───────

    private sealed class NonMergedFixture : IAsyncDisposable
    {
        public SessionLedger Ledger { get; }
        public WorkerTracker Tracker { get; }
        public NoMergedHealthMonitor Health { get; } = new();
        public NonMergedRpcClient Rpc { get; } = new();
        public NonMergedProxy Proxy { get; } = new();
        public WorkerSchedulerService Scheduler { get; }
        private readonly CancellationTokenSource _runCts = new();
        private readonly Task _runTask;

        public NonMergedFixture()
        {
            Ledger = new SessionLedger();
            Tracker = new WorkerTracker();

            var cfg = new CoordinatorConfig
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                EnableChunks = true,
                PrefixCheckpointEnabled = false,
                WarmSlotVerificationEnabled = false,
                MixPrecisionEnabled = false,
                AtomicThreshold = 2048,
                Workers = new List<WorkerConfig>
                {
                    new() { Name = "rtx",  Host = "localhost", RpcPort = 9611, LlamaUrl = "http://localhost:8080", WorkerType = 1, Slots = 2, Role = "head", PrefillPriority = 1, DecodePriority = 2 },
                    new() { Name = "p100", Host = "localhost", RpcPort = 9612, LlamaUrl = "http://localhost:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
                },
            };
            foreach (var w in cfg.Workers)
                Tracker.InitWorker(w.Name, w.Slots);

            var sp = new ServiceCollection().BuildServiceProvider();
            Scheduler = new WorkerSchedulerService(cfg, Ledger, Tracker, Proxy, Health, Rpc,
                sp, Log.Logger);
            Scheduler.AgentClientFactory = (_, _) => Rpc; // routes llama + state RPC to the double
            Scheduler.LlamaClientFactory = _ => new TestLlamaClient();

            ModelRegistry.ClearForTest();
            ModelRegistry.RegisterForTest(new EngineConfig(
                ModelAlias: "nano",
                ModelPath: "/dev/null",
                NGpuLayers: 0, NCtx: 2048,
                ContBatching: true, Fit: false, UbatchSize: 512,
                SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));

            _runTask = Scheduler.RunAsync(_runCts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _runCts.Cancel();
            try { await _runTask; } catch (OperationCanceledException) { }
            _runCts.Dispose();
            ModelRegistry.ClearForTest();
        }

        public async Task<object?> SubmitAsync(
            string sessionId, int estimatedTokens, int maxTokens = 500, bool stream = false)
        {
            var msgs = new List<Dictionary<string, object>>
            {
                new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
            };
            var req = new Dictionary<string, object>
            {
                ["stream"] = stream,
                ["max_tokens"] = maxTokens,
                ["model"] = "nano",
                ["messages"] = msgs
            };
            return await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
                maxTokens, null, _runCts.Token);
        }
    }
}
