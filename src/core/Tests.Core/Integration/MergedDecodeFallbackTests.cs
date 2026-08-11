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
// detects that condition and re-issues the ORIGINAL request body ONCE via the
// HTTP /v1/chat/completions proxy (which preserves reasoning_content),
// bounded to a single fallback attempt.
//
// #622 — the STREAMING detection signal is decode_ms, not usage: the merged
// COMPLETION DONE SSE delta carries hydra_metrics (decode_ms > 0 once the
// engine generated) but NO usage (include_usage never propagates through
// merged COMPLETION), so TokensOut stays 0 and the fallback gate must arm on
// decode_ms > 0 + empty content instead of TokensOut > 0.
//
// #622 follow-up (relay gap) — when the live SSE relay path is active the
// stream's terminal chunk is content="" with NO hydra_metrics at all (relayed
// partials + bare [DONE] carry only content+timings), so decode_ms never
// reaches the coordinator and the stream-level gate can't arm. The coordinator
// then issues ONE final GET to the DONE-state result endpoint (the buffered
// path's PollDecodeResultAsync, same decode id) as a SECOND signal source; if
// that DONE JSON reports hydra_metrics.decode_ms > 0 the engine generated and
// the HTTP-proxy fallback fires. Fetch failure or decode_ms == 0 → held chunks
// relay verbatim.
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

        // The HTTP proxy was re-issued exactly ONCE with the CLEAN original
        // client body: same model/max_tokens/messages/stream, and NONE of the
        // coordinator-injected fields (id_slot / hydra_config / stream_options).
        Assert.Single(f.Proxy.NonStreamingCalls);
        var (nodeUrl, body, _) = f.Proxy.NonStreamingCalls[0];
        Assert.Equal("http://localhost:8080", nodeUrl);
        Assert.Equal("nano", ((JsonElement)body["model"]).GetString());
        Assert.Equal(100, ((JsonElement)body["max_tokens"]).GetInt32());
        Assert.True(body.TryGetValue("messages", out var msgs)
            && ((JsonElement)msgs).ValueKind == JsonValueKind.Array,
            "fallback must carry the original messages");
        Assert.False(BodyBool(body, "stream"), "fallback must keep the original non-streaming flag");
        Assert.DoesNotContain("id_slot", body.Keys);
        Assert.DoesNotContain("hydra_config", body.Keys);
        Assert.DoesNotContain("stream_options", body.Keys);

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
    public async Task MergedDecode_Stream_EmptyContentNoUsage_DecodeMsGtZero_FallsBack()
    {
        // #622 regression (run #31460310245): the merged COMPLETION DONE SSE
        // delta carries hydra_metrics (decode_ms > 0 — the engine generated,
        // 66866 ms live) but NO usage — include_usage never propagates through
        // merged COMPLETION — so TokensOut stays 0 and the pre-#622 gate
        // (TokensOut > 0 && !sawContent) never fired, relaying an empty
        // response. decode_ms > 0 must arm the gate instead.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\"}}],\"hydra_metrics\":{\"decode_ms\":66866,\"prompt_ms\":120,\"n_past\":2048,\"kv_bytes\":838860800,\"decode_request_id\":11373,\"id_slot\":0}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs1");

        // One HTTP re-issue. The mock engine REJECTS stream:true and
        // stream_options bodies (they'd return SSE, not a JSON dict), so this
        // asserts the fallback forced non-stream on the CLEAN client body.
        Assert.Single(f.Proxy.NonStreamingCalls);
        var fallbackBody = f.Proxy.NonStreamingCalls[0].Body;
        Assert.False(BodyBool(fallbackBody, "stream"),
            "streaming fallback must force stream:false");
        Assert.DoesNotContain("stream_options", fallbackBody.Keys);
        Assert.DoesNotContain("id_slot", fallbackBody.Keys);
        Assert.DoesNotContain("hydra_config", fallbackBody.Keys);

        // The empty-content data chunks are relayed live (first chunk), the
        // final empty-content DONE delta (decode_ms>0, no usage) is REPLACED
        // by the fallback's content chunk, then [DONE] closes the stream.
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
    public async Task MergedDecode_Stream_EmptyContent_DecodeMsZero_NoFallback()
    {
        // #622: no engine-generation evidence (decode_ms == 0 in hydra_metrics)
        // + no content → no fallback — the empty-but-nothing-generated stream
        // relays verbatim, unchanged behavior.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\"}}],\"hydra_metrics\":{\"decode_ms\":0}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs5");

        Assert.Empty(f.Proxy.NonStreamingCalls);
        var expected = string.Concat(
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}")),
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\"}}],\"hydra_metrics\":{\"decode_ms\":0}}")),
            Encoding.UTF8.GetString(Sse("data: [DONE]")));
        Assert.Equal(expected, string.Join("", chunks.Select(c => Encoding.UTF8.GetString(c))));
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Stream_NonEmptyContent_NoFallback()
    {
        // #622 shape: the DONE delta carries decode_ms > 0 (engine generated)
        // but content WAS seen → the content check still applies, no fallback.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"hydra_metrics\":{\"decode_ms\":1200}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs2");

        // No fallback: every chunk relays in original order (incl. [DONE]).
        Assert.Empty(f.Proxy.NonStreamingCalls);
        var expected = string.Concat(
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}")),
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"hydra_metrics\":{\"decode_ms\":1200}}")),
            Encoding.UTF8.GetString(Sse("data: [DONE]")));
        Assert.Equal(expected, string.Join("", chunks.Select(c => Encoding.UTF8.GetString(c))));
    }

    [Fact]
    public async Task MergedDecode_Stream_FallbackCarriesReasoningContent()
    {
        // #616 QA: the fallback response may carry ONLY reasoning_content
        // (content empty — the very bug the fallback fixes). The emitted SSE
        // chunk must propagate delta.reasoning_content, mirroring
        // server-chat.cpp's "emit message when either field exists".
        await using var f = new MergedDecodeFixture();
        f.Proxy.FallbackResult = MergedDecodeResult(
            content: "", completionTokens: 50, reasoningContent: "deep reasoning text");
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\"}}],\"hydra_metrics\":{\"decode_ms\":5000}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs3");

        Assert.Single(f.Proxy.NonStreamingCalls);
        var fallbackChunk = Encoding.UTF8.GetString(chunks[1]);
        Assert.Contains("reasoning_content", fallbackChunk);
        Assert.Contains("deep reasoning text", fallbackChunk);
        Assert.Contains("\"content\":\"\"", fallbackChunk);
    }

    [Fact]
    public async Task MergedDecode_Stream_EngineOmitsDone_YieldsSyntheticDone()
    {
        // #616 QA: the engine's DONE-state stream emits a single delta chunk
        // and closes WITHOUT `data: [DONE]`. When no fallback fires, the
        // coordinator must synthesize the terminator so streaming clients
        // always see one.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"usage\":{\"completion_tokens\":2,\"prompt_tokens\":10,\"total_tokens\":12}}"),
        ];

        var chunks = await CollectAsync(f, "sess_fs4");

        Assert.Empty(f.Proxy.NonStreamingCalls);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}",
            Encoding.UTF8.GetString(chunks[0]).Trim());
        Assert.Contains("\"content\":\"!\"", Encoding.UTF8.GetString(chunks[1]));
        Assert.Equal("data: [DONE]", Encoding.UTF8.GetString(chunks[2]).Trim());
    }

    [Fact]
    public async Task MergedDecode_Stream_NoMetrics_EmptyContent_DoneStateDecodeMsGtZero_FallsBack()
    {
        // #622 relay gap (live retest 15:07, engine 1d227f7b8): the relayed
        // stream's terminal chunk is content="" with NO usage and NO
        // hydra_metrics — the relay-branch chunks carry only content+timings,
        // so decode_ms never reaches Phases and the stream-level gate can't
        // arm. The coordinator must issue ONE final GET to the DONE-state
        // result endpoint (same decode id) as a SECOND signal source; the
        // DONE JSON carries hydra_metrics.decode_ms > 0 (the engine DID
        // generate) → the existing HTTP-proxy fallback fires.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: [DONE]"),
        ];
        f.Proxy.MergedResult = MergedDecodeDoneState(decodeMs: 66866);

        var chunks = await CollectAsync(f, "sess_fs6");

        // Exactly ONE DONE-state GET against the decode worker with the merged
        // decode id (7 from the mocked EngineMergedDecodeAsync)...
        var pollCall = Assert.Single(f.Proxy.PollDecodeResultCalls);
        Assert.Equal("http://localhost:8080", pollCall.NodeUrl);
        Assert.Equal(7, pollCall.DecodeRequestId);
        // ...then exactly ONE HTTP fallback re-issue, forced non-stream.
        Assert.Single(f.Proxy.NonStreamingCalls);
        Assert.False(BodyBool(f.Proxy.NonStreamingCalls[0].Body, "stream"),
            "streaming fallback must force stream:false");

        // Held chunks relay: first delta live, fallback chunk replaces the
        // empty terminal delta, then [DONE].
        Assert.Equal(3, chunks.Count);
        Assert.Equal(
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}",
            Encoding.UTF8.GetString(chunks[0]).Trim());
        var fallbackChunk = Encoding.UTF8.GetString(chunks[1]);
        Assert.Contains("Hello from fallback", fallbackChunk);
        Assert.Contains("finish_reason", fallbackChunk);
        Assert.Equal("data: [DONE]", Encoding.UTF8.GetString(chunks[2]).Trim());
        Assert.Contains(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Stream_NoMetrics_EmptyContent_DoneStateDecodeMsZero_NoFallback()
    {
        // #622 follow-up: the DONE-state GET fires (the stream lacked the
        // signal) but returns decode_ms == 0 — no engine-generation evidence
        // → no fallback, the held chunks relay verbatim.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: [DONE]"),
        ];
        f.Proxy.MergedResult = MergedDecodeDoneState(decodeMs: 0);

        var chunks = await CollectAsync(f, "sess_fs7");

        Assert.Single(f.Proxy.PollDecodeResultCalls);
        Assert.Empty(f.Proxy.NonStreamingCalls);
        var expected = string.Concat(
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}")),
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"\"}}]}")),
            Encoding.UTF8.GetString(Sse("data: [DONE]")));
        Assert.Equal(expected, string.Join("", chunks.Select(c => Encoding.UTF8.GetString(c))));
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Stream_NoMetrics_EmptyContent_DoneStateFetchFails_NoFallback()
    {
        // #622 follow-up: the DONE-state GET itself fails (timeout / 404
        // exhaustion / transport) → no fallback, the held chunks relay
        // verbatim, and the fetch failure is logged.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: [DONE]"),
        ];
        f.Proxy.PollDecodeResultError = new TimeoutException("GET /v1/decode/7 timed out");

        var chunks = await CollectAsync(f, "sess_fs8");

        Assert.Single(f.Proxy.PollDecodeResultCalls);
        Assert.Empty(f.Proxy.NonStreamingCalls);
        Assert.Equal(
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}",
            Encoding.UTF8.GetString(chunks[0]).Trim());
        Assert.Equal("data: [DONE]", Encoding.UTF8.GetString(chunks[2]).Trim());
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
        Assert.Contains(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_done_state_fetch_failed"));
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

    /// <summary>Read a bool request-body field — the deep-cloned clean body
    /// holds JsonElement values while the streaming fallback's forced
    /// stream:false override is a plain bool.</summary>
    private static bool BodyBool(Dictionary<string, object> body, string key)
        => body[key] switch
        {
            bool b => b,
            JsonElement je => je.GetBoolean(),
            _ => throw new InvalidOperationException($"unexpected value type for body key '{key}'"),
        };

    private static byte[] Sse(string dataLine)
        => Encoding.UTF8.GetBytes($"{dataLine}\n\n");

    /// <summary>Build an OpenAI-style completion result dictionary for the
    /// PollDecodeResultAsync double.</summary>
    private static Dictionary<string, object> MergedDecodeResult(string content, int completionTokens, string reasoningContent = "")
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content, reasoning_content = reasoningContent },
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

    /// <summary>#622 follow-up: the engine's DONE-state result JSON — the
    /// shape the final GET returns when the relayed stream carried no
    /// hydra_metrics. Only decode_ms matters to the streaming gate.</summary>
    private static Dictionary<string, object> MergedDecodeDoneState(long decodeMs)
    {
        var result = MergedDecodeResult(content: "", completionTokens: 0);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            hydra_metrics = new { decode_ms = decodeMs }
        }));
        result["hydra_metrics"] = doc.RootElement.GetProperty("hydra_metrics").Clone();
        return result;
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
        public void UpdateNodeModelIdentity(string nodeName, string modelAlias, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
        public void MarkHealthy(string nodeName) { }
    }

    /// <summary>Proxy double: injectable merged poll/stream results, recorded
    /// HTTP proxy calls, and a configurable fallback response. Realistic
    /// engine behavior: a stream:true (or stream_options-carrying) body
    /// returns SSE bytes, which ProxyCompletionAsync's JSON deserialization
    /// would choke on — the mock rejects such bodies exactly like the real
    /// engine would make the deserializer fail.</summary>
    private sealed class MergedDecodeTestProxy : ICompletionProxyService
    {
        public Dictionary<string, object> MergedResult { get; set; } = MergedDecodeResult(content: "", completionTokens: 0);
        public byte[][] MergedStreamChunks { get; set; } = [];
        public Dictionary<string, object> FallbackResult { get; set; } = MergedDecodeResult(content: "Hello from fallback", completionTokens: 50);
        public List<(string NodeUrl, Dictionary<string, object> Body, string TraceId)> NonStreamingCalls { get; } = new();
        public List<(string NodeUrl, int DecodeRequestId, string TraceId)> PollDecodeResultCalls { get; } = new();
        public Exception? PollDecodeResultError { get; set; }

        public Task<Dictionary<string, object>> ProxyCompletionAsync(
            string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
        {
            // #616 QA: the fallback must force stream:false and strip
            // stream_options — the real engine answers SSE for stream:true
            // bodies (a JSON-dict parse would fail on those bytes).
            if (IsTrue(body.TryGetValue("stream", out var st) ? st : null))
                throw new InvalidOperationException("mock engine: stream:true body returns SSE, not a JSON dict");
            if (body.ContainsKey("stream_options"))
                throw new InvalidOperationException("mock engine: stream_options on a non-stream request");
            NonStreamingCalls.Add((nodeUrl, new Dictionary<string, object>(body), traceId));
            return Task.FromResult(FallbackResult);
        }

        private static bool IsTrue(object? v) => v switch
        {
            bool b => b,
            JsonElement je => je.ValueKind == JsonValueKind.True,
            _ => false,
        };

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
        {
            PollDecodeResultCalls.Add((nodeUrl, decodeRequestId, traceId));
            if (PollDecodeResultError != null)
                return Task.FromException<Dictionary<string, object>>(PollDecodeResultError);
            return Task.FromResult(MergedResult);
        }

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
