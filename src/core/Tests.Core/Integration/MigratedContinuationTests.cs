using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #631 — cross-quant /migrate continuation → 503: the merged-DECODE KV
// identity handshake for NON-RESIDENT (migrated/evicted) sessions.
//
// A migrated session's continuation routes through the "migration" branch
// (RouteType="migration", PrefillWorker derived from the stale ledger entry).
// When the primary decode pick is busy, PickBestDecodeWorker falls back to
// item.PrefillWorker — decode lands on the SAME node as the stale prefill
// node. Pre-#631 the same-node-skip fired, returning Decode WITHOUT running
// RestoreKvAsync: the fresh WorkItem carried EMPTY KV identity (KvTokenizer/
// KvModelName/... never repopulated from the store blob manifest) and no KV
// blob, so the merged DECODE 0x43 sent empty kv_metadata + no blob → Gate A
// rejected (Tok=False Name=False) → 503 "KV not restored, aborting".
//
// Post-fix the same-node-skip is blocked for RouteType=="migration": the
// continuation re-enters RestoreKvAsync, the blob MANIFEST repopulates the
// WorkItem KV identity (source model: Mini), and the merged DECODE carries
// real kv_metadata + the assembled KV blob. The decode `model` alias resolves
// to the TARGET's resident model (STATE_META alias first; health-stamped
// CurrentModel second) so Gate A's #589 name fallback fires on cross-quant
// restores. This test drives the full scheduler pipeline hermetically and
// asserts the exact merged-DECODE frame the coordinator sends.
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class MigratedContinuationTests
{
    // ── The core #631 scenario ─────────────────────────────────────────

    [Fact]
    public async Task MigratedContinuation_SameNodeFallback_RestoresKv_AndSendsTargetResidentAlias()
    {
        await using var f = new MigratedContinuationFixture();
        f.Ledger.Register("sess_mig", "p100", slotId: 0, nPast: 2000, prefixHash: null);
        f.Ledger.MarkStoreState("sess_mig");
        f.Ledger.MarkEvicted("sess_mig"); // post-/migrate state: non-resident on target

        // The real-world trigger: p100 (the migrated target / stale prefill
        // node) is the ONLY decode-capable worker, so PickDecodeAsync's
        // PickBestDecodeWorker (which excludes the prefill node) finds no
        // candidate and falls back to item.PrefillWorker — decode lands on
        // the SAME node as the stale prefill node. Pre-#631 this hit the
        // same-node-skip and skipped the KV restore entirely.
        var result = await f.SubmitAsync("sess_mig", 3000, 50, stream: false);

        // The continuation completed (no 503 "KV not restored").
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        Assert.True(dict.ContainsKey("choices"), "migrated continuation must produce a completion");

        // The merged DECODE frame was sent exactly once, carrying:
        //  (a) kv_metadata populated from the STORE BLOB MANIFEST (the Mini
        //      identity that built the KV on the source node) — NOT empty.
        var call = Assert.Single(f.Rpc.MergedDecodeCalls);
        Assert.Equal("llama", call.KvTokenizer);
        Assert.Equal("Qwopus3.6-35B-A3B-v1-APEX-I-Mini", call.KvModelName);
        Assert.Equal("Q3_K", call.KvModelQuant);
        Assert.Equal(2u, call.KvModelCapabilities);
        //  (b) the assembled KV blob carried in the frame (restore ran).
        Assert.True(call.KvBlob.Length > 0, "merged DECODE must carry the restored KV blob");
        Assert.Equal(2000, call.NPast);
        //  (c) model_metadata from the decode node's STATE_META (resident
        //      Balanced identity — the frame's Gate-A target side).
        Assert.Equal("llama", call.ModelTokenizer);
        Assert.Equal("Qwopus3.6-35B-A3B-v1-APEX-I-Balanced", call.ModelName);
        Assert.Equal("Q5_K", call.ModelQuant);
        //  (d) the decode `model` alias resolves to the TARGET's resident
        //      model (health-stamped CurrentModel, since the STATE_META
        //      alias is deliberately empty in this fixture) — the alias that
        //      maps through p100's preset to its resident path, so Gate A's
        //      #589 fallback fires on the cross-quant restore.
        Assert.Equal("qwen3.6-35B-balanced", call.ModelAlias);
    }

    [Fact]
    public async Task MigratedContinuation_NonStreaming_ManifestRestore_ProvesStoreGetAndAssemble()
    {
        // The restore path must actually run: the fixture's RPC double records
        // the Store ops. A pre-#631 same-node-skip would make ZERO Store
        // calls (no GetManifest, no GetChunked) — asserting them proves the
        // continuation re-entered the KV restore path.
        await using var f = new MigratedContinuationFixture();
        f.Ledger.Register("sess_mig2", "p100", slotId: 0, nPast: 2000, prefixHash: null);
        f.Ledger.MarkStoreState("sess_mig2");
        f.Ledger.MarkEvicted("sess_mig2");

        var result = await f.SubmitAsync("sess_mig2", 3000, 50, stream: false);

        Assert.IsType<Dictionary<string, object>>(result);
        Assert.True(f.Rpc.HasCall(OpCode.GetManifest, "sess_mig2"),
            "migrated continuation must read the KV blob manifest from the Store");
        Assert.True(f.Rpc.HasCall(OpCode.GetChunked),
            "migrated continuation must assemble the KV blob from Store chunks");
    }

    // ── Doubles ─────────────────────────────────────────────────────────

    /// <summary>Health monitor advertising merged_decode on every worker and a
    /// pre-stamped CurrentModel for p100 — the target's resident alias as the
    /// health monitor knows it from prior engine STATE_META/prefill reports.
    /// The resident ALIAS is deliberately absent from STATE_META in this
    /// fixture (see LlamaClientFactory) so the coordinator must fall back to
    /// the health-stamped resident alias — exercising the #631 fallback.</summary>
    private sealed class MigratedContinuationHealthMonitor : IHealthMonitorService
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
            EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Protocol.CapMergedDecode
            },
            CurrentModel = nodeName == "p100" ? "qwen3.6-35B-balanced" : "",
        };

        public Dictionary<string, object> GetHealthSummary() => new();
        public event Action? HealthyChanged;
        public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
        public void MarkHealthy(string nodeName) { }
    }

    /// <summary>Records the merged-DECODE 0x43 frame args so the test can
    /// assert the exact kv_metadata / model_metadata / alias the coordinator
    /// sent. Serves the Store ops the chunked restore path needs: GET_MANIFEST
    /// (identity-bearing manifest) and GET_CHUNKED (the KV blob chunk).</summary>
    private sealed class MigratedContinuationRpcClient : RpcClient
    {
        public sealed record MergedDecodeCall(
            string SlotKey, int NPast,
            string? KvTokenizer, string? KvModelName, string? KvModelQuant, uint KvModelCapabilities,
            string? ModelTokenizer, string? ModelName, string? ModelQuant, uint ModelCapabilities,
            string? ModelAlias, ReadOnlyMemory<byte> KvBlob);

        public List<(OpCode Op, string Key)> Calls { get; } = new();
        public List<MergedDecodeCall> MergedDecodeCalls { get; } = new();

        public MigratedContinuationRpcClient() : base("test", 0) { }

        public bool HasCall(OpCode op, string? keyContains = null)
            => Calls.Any(c => c.Op == op && (keyContains == null || c.Key.Contains(keyContains)));

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
        {
            Calls.Add((op, key));
            var response = op switch
            {
                // The KV blob manifest written by SaveKvAsync on the source
                // node: carries the SOURCE model identity (Mini) — the exact
                // thing RestoreKvAsync must repopulate onto the WorkItem.
                // The manifest JSON rides in the PAYLOAD (RestoreKvAsync
                // JsonDocument.Parse's manifestResp.Payload).
                OpCode.GetManifest => new RpcResponse(
                    (byte)StatusCode.Ok,
                    "manifest",
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

                // GET_CHUNKED payload: [idx i32][size i32][data].
                OpCode.GetChunked => new RpcResponse(
                    (byte)StatusCode.Ok,
                    "stored",
                    ChunkedPayload()),

                _ => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, stored = true, restored = true, erased = true }),
                    []),
            };
            return Task.FromResult(response);
        }

        private static readonly byte[] ChunkData = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

        private static byte[] ChunkedPayload()
        {
            var buf = new byte[8 + ChunkData.Length];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), 0);          // chunk index
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), ChunkData.Length); // size
            ChunkData.CopyTo(buf, 8);
            return buf;
        }

        public override Task<MergedDecodeResponse> EngineMergedDecodeAsync(
            string slotKey, int nPast,
            string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
            string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
            string? modelAlias,
            string? messagesJson, int nPredict, string? samplingJson, bool stream,
            ReadOnlyMemory<byte> kvBlob,
            string traceId, CancellationToken ct)
            => Task.FromResult(EmulateMergedDecode(slotKey, nPast,
                kvTokenizer, kvModelName, kvModelQuant, kvModelCapabilities,
                modelTokenizer, modelName, modelQuant, modelCapabilities,
                modelAlias, kvBlob.ToArray()));

        /// <summary>#470 Phase 2: the streaming DECODE variant — the KV arrives
        /// as an ordered chunk stream instead of one blob; buffered here (test
        /// double) so assertions can inspect the exact bytes the coordinator
        /// streamed.</summary>
        public override async Task<MergedDecodeResponse> EngineMergedDecodeStreamKvAsync(
            string slotKey, int nPast,
            string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
            string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
            string? modelAlias,
            string? messagesJson, int nPredict, string? samplingJson, bool stream,
            IAsyncEnumerable<ReadOnlyMemory<byte>> kvChunks, long kvTotalSize,
            string traceId, CancellationToken ct)
        {
            using var ms = new MemoryStream((int)kvTotalSize);
            await foreach (var chunk in kvChunks.WithCancellation(ct))
                await ms.WriteAsync(chunk, ct);
            return EmulateMergedDecode(slotKey, nPast,
                kvTokenizer, kvModelName, kvModelQuant, kvModelCapabilities,
                modelTokenizer, modelName, modelQuant, modelCapabilities,
                modelAlias, ms.ToArray());
        }

        /// <summary>#470 Phase 2: the chunked-payload request twin (the Store's
        /// GET_CHUNKED for decode-side KV streaming) — serves the framed chunk
        /// payload through onChunk, recording the call like RequestAsync.</summary>
        public override Task<RpcResponse> RequestChunkedPayloadAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct,
            Action<long> onPayloadLen,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
            TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
        {
            Calls.Add((op, key));
            var chunked = op switch
            {
                OpCode.GetChunked => ChunkedPayload(),
                _ => Array.Empty<byte>(),
            };
            onPayloadLen(chunked.Length);
            if (chunked.Length > 0)
                onChunk(chunked, ct).GetAwaiter().GetResult();
            return Task.FromResult(new RpcResponse(
                (byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 2000, stored = true }),
                []));
        }

        private MergedDecodeResponse EmulateMergedDecode(
            string slotKey, int nPast,
            string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
            string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
            string? modelAlias, byte[] kvBlob)
        {
            MergedDecodeCalls.Add(new MergedDecodeCall(
                slotKey, nPast,
                kvTokenizer, kvModelName, kvModelQuant, kvModelCapabilities,
                modelTokenizer, modelName, modelQuant, modelCapabilities,
                modelAlias, kvBlob));

            // ── Emulate the engine's merged-DECODE Gate A ────────────────
            // valid = tokenizer_match && model_name_match && !(caps_xor & 0x3)
            // (#589: on model-name mismatch the frame's `model` alias may
            // substitute when it maps to the node's resident path — here the
            // only resident-mapping alias is the target's own qwen3.6-35B-
            // balanced). This mirrors server-context.cpp so the test is a
            // genuine regression test: pre-#631 the coordinator sent EMPTY
            // kv_metadata (tokenizer "" != "llama") → Gate A rejects → 503.
            var tokenizerMatch = string.Equals(kvTokenizer, modelTokenizer, StringComparison.Ordinal);
            var nameMatch = string.Equals(kvModelName, modelName, StringComparison.Ordinal)
                || string.Equals(modelAlias, "qwen3.6-35B-balanced", StringComparison.Ordinal);
            var capsXor = kvModelCapabilities ^ modelCapabilities;
            var valid = tokenizerMatch && nameMatch && (capsXor & 0x3) == 0;

            return new MergedDecodeResponse
            {
                Status = (byte)StatusCode.Ok,
                Valid = valid,
                DecodeRequestId = valid ? 7 : null,
                NPastAfterRestore = valid ? nPast : 0,
                TokenizerMatch = tokenizerMatch,
                ModelNameMatch = nameMatch,
                ModelCapabilitiesMatch = valid,
                CapabilitiesXor = capsXor,
                ModelQuantMatch = string.Equals(kvModelQuant, modelQuant, StringComparison.Ordinal),
                ModelAliasMatch = true,
            };
        }
    }

    /// <summary>Proxy double: the buffered merged-decode poll returns a real
    /// completion (non-empty content — no #616 fallback re-issue).</summary>
    private sealed class MigratedContinuationProxy : ICompletionProxyService
    {
        public List<(string NodeUrl, Dictionary<string, object> Body, string TraceId)> NonStreamingCalls { get; } = new();

        public Task<Dictionary<string, object>> ProxyCompletionAsync(
            string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
        {
            NonStreamingCalls.Add((nodeUrl, new Dictionary<string, object>(body), traceId));
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
            Hydra.Core.Models.WorkItem? item = null)
        {
            yield return Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n");
            await Task.CompletedTask;
        }

        public Task<Dictionary<string, object>> PollDecodeResultAsync(
            string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
            => Task.FromResult(Result());

        public Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
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
                        message = new { role = "assistant", content = "Hello from migrated continuation" },
                        finish_reason = "stop",
                    }
                },
                usage = new { prompt_tokens = 2100, completion_tokens = 50, total_tokens = 2150 },
                id_slot = 0,
                id = "chatcmpl-migrated",
                model = "nano",
                created = 0,
            }));
            return JsonSerializer.Deserialize<Dictionary<string, object>>(doc.RootElement.GetRawText())!;
        }
    }

    private sealed class MigratedContinuationFixture : IAsyncDisposable
    {
        public CoordinatorConfig Cfg { get; }
        public SessionLedger Ledger { get; }
        public WorkerTracker Tracker { get; }
        public MigratedContinuationProxy Proxy { get; } = new();
        public IHealthMonitorService Health { get; } = new MigratedContinuationHealthMonitor();
        public MigratedContinuationRpcClient Rpc { get; } = new();
        public WorkerSchedulerService Scheduler { get; }
        private readonly CancellationTokenSource _runCts = new();
        private readonly Task _runTask;

        public MigratedContinuationFixture()
        {
            Ledger = new SessionLedger();
            Tracker = new WorkerTracker();

            Cfg = new CoordinatorConfig
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                EnableChunks = true,   // chunked restore: manifest carries the KV identity
                PrefixCheckpointEnabled = false,
                WarmSlotVerificationEnabled = false,
                MixPrecisionEnabled = false,
                AtomicThreshold = 2048,
                Workers = new List<WorkerConfig>
                {
                    // rtx = PREFILL-only (WorkerType 1): mirrors production where
                    // the migrated session's decode cannot land on the source
                    // node; p100 is the only decode worker — so PickDecodeAsync's
                    // exclude-prefill-node pick finds no candidate and falls back
                    // to item.PrefillWorker (p100), reproducing the same-node
                    // fallback that pre-#631 skipped the KV restore on.
                    new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 1, Slots = 2, Role = "head", PrefillPriority = 1, DecodePriority = 2 },
                    new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
                }
            };
            foreach (var w in Cfg.Workers)
                Tracker.InitWorker(w.Name, w.Slots);

            var sp = new ServiceCollection().BuildServiceProvider();
            Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, Proxy, Health, Rpc,
                sp, Serilog.Log.Logger);
            Scheduler.AgentClientFactory = (_, _) => Rpc;
            // The decode node's STATE_META reports its RESIDENT identity
            // (Balanced: tokenizer/name/quant/caps) but deliberately NO
            // model_alias — forcing the coordinator onto the #631
            // health-stamped resident-alias fallback for the frame's `model`.
            Scheduler.LlamaClientFactory = _ => new TestLlamaClient(new SlotMeta
            {
                SlotId = 0,
                NPast = 2000,
                IsProcessing = false,
                Tokenizer = "llama",
                ModelName = "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced",
                ModelQuant = "Q5_K",
                ModelCapabilities = 2,
                ModelAlias = "", // deliberately empty (see above)
            });

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
