using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #616 — merged-decode empty-content → HTTP proxy fallback.
//
// Merged-decode (DECODE 0x43) responses drop reasoning_content (engine bug,
// fix deferred): the coordinator receives tokens (n_decoded>0) but the final
// visible content is empty for reasoning models. The interim coordinator fix
// detects that condition (tokens>0 + empty content) in BOTH completion paths
// and re-issues the ORIGINAL request body ONCE via the HTTP
// /v1/chat/completions proxy (which preserves reasoning_content), bounded to
// a single fallback attempt.
//
// These tests drive the full scheduler pipeline in engine mode with the
// merged_decode capability advertised, so DecodeAsync really enters the
// merged path (EngineMergedDecodeAsync mocked to succeed) and the poll /
// stream results are injected by the proxy double.
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class MergedDecodeFallbackTests
{
    // ── Buffered path ──────────────────────────────────────────────────

    [Fact]
    public async Task MergedDecode_Buffered_EmptyContentWithTokens_FallsBackOnce_OriginalBody()
    {
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedResult = MergedDecodeResult(content: "", completionTokens: 50);

        var result = await f.SubmitAsync("sess_fb1", 500, 100, stream: false);

        // The HTTP proxy was re-issued exactly ONCE with the original body.
        Assert.Single(f.Proxy.NonStreamingCalls);
        var (nodeUrl, body, _) = f.Proxy.NonStreamingCalls[0];
        Assert.Equal("http://localhost:8080", nodeUrl);
        Assert.Equal("nano", body["model"]);
        Assert.Equal(100, body["max_tokens"]);
        Assert.True(body.ContainsKey("messages"), "fallback must carry the original messages");
        Assert.False((bool)body["stream"], "fallback must keep the original non-streaming flag");

        // The fallback's response is what the caller receives.
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        var choices = Assert.IsType<JsonElement>(dict["choices"]);
        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        Assert.Equal("Hello from fallback", content);

        // The WRN log fired with the token count.
        Assert.Contains(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Buffered_NonEmptyContent_NoFallback()
    {
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedResult = MergedDecodeResult(content: "Hi there", completionTokens: 50);

        var result = await f.SubmitAsync("sess_fb2", 500, 100, stream: false);

        Assert.Empty(f.Proxy.NonStreamingCalls);
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        var choices = Assert.IsType<JsonElement>(dict["choices"]);
        Assert.Equal("Hi there", choices[0].GetProperty("message").GetProperty("content").GetString());
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Buffered_ZeroTokens_NoFallback()
    {
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedResult = MergedDecodeResult(content: "", completionTokens: 0);

        var result = await f.SubmitAsync("sess_fb3", 500, 100, stream: false);

        // Tokens==0 → the empty content is legitimate (e.g. stop-immediately):
        // no fallback, the merged result is returned as-is.
        Assert.Empty(f.Proxy.NonStreamingCalls);
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        Assert.Equal(0, WorkerSchedulerService.ExtractUsageInt(dict, "completion_tokens"));
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    // ── Streaming path ─────────────────────────────────────────────────

    [Fact]
    public async Task MergedDecode_Stream_EmptyContentWithTokens_YieldsFallbackChunkThenDone()
    {
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\"}}],\"usage\":{\"completion_tokens\":50,\"prompt_tokens\":10,\"total_tokens\":60}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs1");

        // One HTTP re-issue with the original streaming request body.
        Assert.Single(f.Proxy.NonStreamingCalls);
        Assert.True((bool)f.Proxy.NonStreamingCalls[0].Body["stream"]);

        // The empty-content data chunks are relayed live (first chunk), the
        // final empty-content+usage chunk is REPLACED by the fallback's
        // content chunk, then [DONE] closes the stream.
        Assert.Equal(3, chunks.Count);
        Assert.Equal(
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}",
            Encoding.UTF8.GetString(chunks[0]).Trim());
        var fallbackChunk = Encoding.UTF8.GetString(chunks[1]);
        Assert.StartsWith("data: {", fallbackChunk);
        Assert.Contains("chat.completion.chunk", fallbackChunk);
        Assert.Contains("Hello from fallback", fallbackChunk);
        Assert.Contains("finish_reason", fallbackChunk);
        Assert.Contains("usage", fallbackChunk);
        Assert.Equal("data: [DONE]", Encoding.UTF8.GetString(chunks[2]).Trim());
        Assert.Contains(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Stream_NonEmptyContent_NoFallback()
    {
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"usage\":{\"completion_tokens\":2,\"prompt_tokens\":10,\"total_tokens\":12}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs2");

        // No fallback: every chunk relays in original order (incl. [DONE]).
        Assert.Empty(f.Proxy.NonStreamingCalls);
        var expected = string.Concat(
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}")),
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"usage\":{\"completion_tokens\":2,\"prompt_tokens\":10,\"total_tokens\":12}}")),
            Encoding.UTF8.GetString(Sse("data: [DONE]")));
        Assert.Equal(expected, string.Join("", chunks.Select(c => Encoding.UTF8.GetString(c))));
    }

    private static async Task<List<byte[]>> CollectAsync(MergedDecodeFixture f, string sessionId)
    {
        var result = await f.SubmitAsync(sessionId, 500, 100, stream: true);
        var stream = Assert.IsAssignableFrom<IAsyncEnumerable<byte[]>>(result);
        var chunks = new List<byte[]>();
        await foreach (var c in stream)
            chunks.Add(c);
        return chunks;
    }

    private static byte[] Sse(string dataLine)
        => Encoding.UTF8.GetBytes($"{dataLine}\n\n");

    /// <summary>Build an OpenAI-style completion result dictionary for the
    /// PollDecodeResultAsync double.</summary>
    private static Dictionary<string, object> MergedDecodeResult(string content, int completionTokens)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = completionTokens > 0 ? "stop" : "length",
                }
            },
            usage = new { prompt_tokens = 10, completion_tokens = completionTokens, total_tokens = 10 + completionTokens },
            id_slot = 0,
            id = "chatcmpl-merged",
            model = "nano",
            created = 0,
        }));
        return JsonSerializer.Deserialize<Dictionary<string, object>>(doc.RootElement.GetRawText())!;
    }

    // ── Doubles ─────────────────────────────────────────────────────────

    /// <summary>Health monitor advertising merged_decode capability so the
    /// scheduler's DecodeAsync enters the merged path.</summary>
    private sealed class MergedCapableHealthMonitor : IHealthMonitorService
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
            SlotsTotal = 2,
            SlotsIdle = 2,
            EngineCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Protocol.CapMergedDecode
            },
        };
        public Dictionary<string, object> GetHealthSummary() => new();
        public event Action? HealthyChanged;
        public void UpdateNodeModelIdentity(string nodeName, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
        public void MarkHealthy(string nodeName) { }
    }

    /// <summary>Proxy double: injectable merged poll/stream results, recorded
    /// HTTP proxy calls, and a fixed fallback response.</summary>
    private sealed class MergedDecodeTestProxy : ICompletionProxyService
    {
        public Dictionary<string, object> MergedResult { get; set; } = MergedDecodeResult(content: "", completionTokens: 0);
        public byte[][] MergedStreamChunks { get; set; } = [];
        public List<(string NodeUrl, Dictionary<string, object> Body, string TraceId)> NonStreamingCalls { get; } = new();

        public Task<Dictionary<string, object>> ProxyCompletionAsync(
            string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
        {
            NonStreamingCalls.Add((nodeUrl, new Dictionary<string, object>(body), traceId));
            return Task.FromResult(MergedDecodeResult(content: "Hello from fallback", completionTokens: 50));
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
            foreach (var c in MergedStreamChunks)
            {
                yield return c;
                await Task.CompletedTask;
            }
        }

        public Task<Dictionary<string, object>> PollDecodeResultAsync(
            string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
            => Task.FromResult(MergedResult);

        public Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>RPC double: Ok for every op (Store + engine binary RPCs) and a
    /// successful framed merged DECODE.</summary>
    private sealed class MergedDecodeRpcClient : RpcClient
    {
        public MergedDecodeRpcClient() : base("test", 0) { }

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct)
        {
            var response = op switch
            {
                OpCode.EnginePrefill => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096 }),
                    new byte[4096]),

                OpCode.StateGet => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000 }),
                    new byte[2048]),

                _ => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, stored = true, restored = true, erased = true }),
                    [])
            };
            return Task.FromResult(response);
        }

        public override Task<MergedDecodeResponse> EngineMergedDecodeAsync(
            string slotKey, int nPast,
            string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
            string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
            string? modelAlias,
            string? messagesJson, int nPredict, string? samplingJson, bool stream,
            ReadOnlyMemory<byte> kvBlob,
            string traceId, CancellationToken ct)
        {
            return Task.FromResult(new MergedDecodeResponse
            {
                Status = (byte)StatusCode.Ok,
                Valid = true,
                DecodeRequestId = 7,
                NPastAfterRestore = nPast,
                TokenizerMatch = true,
                ModelNameMatch = true,
                ModelCapabilitiesMatch = true,
                ModelQuantMatch = true,
                ModelAliasMatch = true,
            });
        }
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    private sealed class MergedDecodeFixture : IAsyncDisposable
    {
        public CoordinatorConfig Cfg { get; }
        public SessionLedger Ledger { get; }
        public WorkerTracker Tracker { get; }
        public MergedDecodeTestProxy Proxy { get; } = new();
        public IHealthMonitorService Health { get; } = new MergedCapableHealthMonitor();
        public MergedDecodeRpcClient Rpc { get; } = new();
        public WorkerSchedulerService Scheduler { get; }
        public List<LogEvent> Events { get; } = new();
        private readonly CancellationTokenSource _runCts = new();
        private readonly Task _runTask;

        public MergedDecodeFixture()
        {
            Ledger = new SessionLedger();
            Tracker = new WorkerTracker();

            Cfg = new CoordinatorConfig
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                PrefixCheckpointEnabled = false,
                WarmSlotVerificationEnabled = false,
                MixPrecisionEnabled = false,
                AtomicThreshold = 2048,
                Workers = new List<WorkerConfig>
                {
                    new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, PrefillPriority = 1, DecodePriority = 2 },
                    new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
                }
            };
            foreach (var w in Cfg.Workers)
                Tracker.InitWorker(w.Name, w.Slots);

            var sp = new ServiceCollection().BuildServiceProvider();
            var log = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Sink(new CollectingSink(Events))
                .CreateLogger();
            Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, Proxy, Health, Rpc,
                sp, log);
            Scheduler.AgentClientFactory = (_, _) => Rpc;
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
        }

        public async Task<object?> SubmitAsync(
            string sessionId, int estimatedTokens, int maxTokens = 500, bool stream = false)
        {
            var msgs = new List<Dictionary<string, object>>
            {
                new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
            };
            // The original client body carries the messages (the real controller
            // forwards the client's body verbatim, minus session_id).
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
