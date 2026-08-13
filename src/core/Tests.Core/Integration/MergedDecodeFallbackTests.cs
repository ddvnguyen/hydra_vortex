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
// Merged-decode (DECODE 0x43) responses with tokens but neither content nor
// reasoning_content (both are now delivered in the DONE result, engine
// 097d13e): the coordinator detects that condition and re-issues the ORIGINAL
// request body ONCE via the HTTP /v1/chat/completions proxy, bounded to a
// single fallback attempt. #642: a reasoning-only reply (empty content,
// non-empty reasoning_content) must NOT trigger the fallback — re-issuing
// would run the completion a second time and double decode_ms.
//
// #588 — tool_calls passthrough (engine fix b95c228b): the engine emits the
// OpenAI tool_calls array in merged DONE results (buffered message.tool_calls
// and streamed delta.tool_calls). The coordinator never re-shapes it — it is
// JSON passthrough. Three requirements are covered here: (1) a tool-call-only
// merged result must NOT trigger the empty-content fallback re-issue (it must
// relay verbatim — re-issuing would discard the engine's tool_calls and run
// the completion a second time); (2) a stream whose DONE delta carries only
// tool_calls must NOT trigger the fallback either (delta.tool_calls counts as
// content seen); (3) when the fallback DOES fire, BuildFallbackSseChunk must
// carry the fallback's message.tool_calls into the emitted SSE delta verbatim.
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

    [Fact]
    public async Task MergedDecode_Buffered_ReasoningContentOnly_NoFallback()
    {
        // #642 (buffered twin of the streaming regression, smoke #10 2026-08-12):
        // a merged-decode result with empty content but non-empty
        // reasoning_content (a reasoning-only completion — the model stopped
        // mid-reasoning at max_tokens) must NOT be re-issued via the HTTP proxy.
        // The engine (097d13e) delivers message.reasoning_content in the DONE
        // result, so the empty-content gate must require BOTH fields blank;
        // otherwise every reasoning-only reply would run the completion twice.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedResult = MergedDecodeResult(
            content: "", completionTokens: 50, reasoningContent: "deep reasoning text");

        var result = await f.SubmitAsync("sess_fb642", 500, 100, stream: false);

        // No HTTP re-issue — the merged result is returned as-is, reasoning
        // content intact.
        Assert.Empty(f.Proxy.NonStreamingCalls);
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        var choices = Assert.IsType<JsonElement>(dict["choices"]);
        Assert.Equal("deep reasoning text",
            choices[0].GetProperty("message").GetProperty("reasoning_content").GetString());
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Buffered_ToolCallsOnly_NoFallback()
    {
        // #588: a merged-decode result with empty content but a non-empty
        // message.tool_calls array (a tool-call reply — the model requests the
        // calculator instead of answering) must NOT be re-issued via the HTTP
        // proxy: that would discard the engine's tool_calls and run the
        // completion a second time. The engine (b95c228b) delivers
        // message.tool_calls in the DONE result, so the empty-content gate
        // must treat tool_calls as content delivered.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedResult = MergedDecodeResult(
            content: "", completionTokens: 50, toolCallsJson: ToolCallJson);

        var result = await f.SubmitAsync("sess_fb588", 500, 100, stream: false);

        // No HTTP re-issue — the merged result is returned as-is, tool_calls
        // intact (verbatim JSON passthrough, nothing re-shaped).
        Assert.Empty(f.Proxy.NonStreamingCalls);
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        var choices = Assert.IsType<JsonElement>(dict["choices"]);
        var toolCalls = choices[0].GetProperty("message").GetProperty("tool_calls");
        Assert.Equal(1, toolCalls.GetArrayLength());
        Assert.Equal("calculator",
            toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"a\":1234,\"b\":5678}",
            toolCalls[0].GetProperty("function").GetProperty("arguments").GetString());
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
        Assert.DoesNotContain("tool_calls", fallbackChunk);
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
    public async Task MergedDecode_Stream_ReasoningContentOnly_DecodeMsGtZero_NoFallback()
    {
        // #642 regression (smoke #10, 2026-08-12): the "Reason step by step..."
        // smoke prompt produced ALL reasoning tokens with empty final content
        // (the model stopped mid-reasoning at max_tokens). HasNonEmptyContentDelta
        // only inspected delta.content, so sawContent stayed false and the gate
        // (engineGenerated && !sawContent) re-issued the whole request via the
        // HTTP proxy → the engine ran the completion a second time → decode_ms
        // was exactly 2× engine time (58965+58840≈118416, 11.9 t/s apparent vs
        // 23.95 t/s engine). The engine (097d13e) delivers reasoning_content in
        // the merged DONE delta, so a non-empty delta.reasoning_content must
        // count as content seen and the fallback must NOT fire.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\",\"reasoning_content\":\"Let me reason step by step about this.\"}}],\"hydra_metrics\":{\"decode_ms\":58965,\"prompt_ms\":120,\"n_past\":2048,\"decode_request_id\":11373,\"id_slot\":0}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs642");

        // No re-issue: neither the HTTP-proxy fallback NOR the DONE-state GET
        // probe fires (sawContent is true, decode_ms reached Phases from the
        // stream itself). Every chunk relays in original order, reasoning text
        // intact.
        Assert.Empty(f.Proxy.NonStreamingCalls);
        Assert.Empty(f.Proxy.PollDecodeResultCalls);
        Assert.Contains("reasoning_content", Encoding.UTF8.GetString(chunks[1]));
        var expected = string.Concat(
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}")),
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\",\"reasoning_content\":\"Let me reason step by step about this.\"}}],\"hydra_metrics\":{\"decode_ms\":58965,\"prompt_ms\":120,\"n_past\":2048,\"decode_request_id\":11373,\"id_slot\":0}}")),
            Encoding.UTF8.GetString(Sse("data: [DONE]")));
        Assert.Equal(expected, string.Join("", chunks.Select(c => Encoding.UTF8.GetString(c))));
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
    }

    [Fact]
    public async Task MergedDecode_Stream_ToolCallsOnly_DecodeMsGtZero_NoFallback()
    {
        // #588: a merged stream whose DONE delta carries ONLY tool_calls
        // (content "" — a tool-call reply, the model requests the calculator)
        // must NOT trigger the empty-content fallback re-issue: the engine
        // (b95c228b) delivers delta.tool_calls in the merged DONE delta, so
        // HasNonEmptyContentDelta must count it as content seen — otherwise
        // the gate (engineGenerated && !sawContent) would run the completion
        // a second time. The tool_calls deltas relay verbatim.
        await using var f = new MergedDecodeFixture();
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\",\"tool_calls\":[{\"id\":\"call_abc123\",\"type\":\"function\",\"function\":{\"name\":\"calculator\",\"arguments\":\"{\\\"a\\\":1234,\\\"b\\\":5678}\"}}]}}],\"hydra_metrics\":{\"decode_ms\":58965,\"prompt_ms\":120,\"n_past\":2048,\"decode_request_id\":11373,\"id_slot\":0}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs588");

        // No re-issue: neither the HTTP-proxy fallback NOR the DONE-state GET
        // probe fires (sawContent is true from the tool_calls delta). Every
        // chunk relays in original order, tool_calls intact.
        Assert.Empty(f.Proxy.NonStreamingCalls);
        Assert.Empty(f.Proxy.PollDecodeResultCalls);
        Assert.Contains("tool_calls", Encoding.UTF8.GetString(chunks[1]));
        Assert.Contains("calculator", Encoding.UTF8.GetString(chunks[1]));
        var expected = string.Concat(
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}")),
            Encoding.UTF8.GetString(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\",\"tool_calls\":[{\"id\":\"call_abc123\",\"type\":\"function\",\"function\":{\"name\":\"calculator\",\"arguments\":\"{\\\"a\\\":1234,\\\"b\\\":5678}\"}}]}}],\"hydra_metrics\":{\"decode_ms\":58965,\"prompt_ms\":120,\"n_past\":2048,\"decode_request_id\":11373,\"id_slot\":0}}")),
            Encoding.UTF8.GetString(Sse("data: [DONE]")));
        Assert.Equal(expected, string.Join("", chunks.Select(c => Encoding.UTF8.GetString(c))));
        Assert.DoesNotContain(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_empty_content_fallback"));
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
        Assert.DoesNotContain("tool_calls", fallbackChunk);
    }

    [Fact]
    public async Task MergedDecode_Stream_FallbackCarriesToolCalls()
    {
        // #588: when the empty-content fallback DOES fire, the HTTP-proxy
        // response may carry message.tool_calls (the engine's tool-call reply
        // — re-issued exactly because the merged result had neither content
        // nor reasoning_content). BuildFallbackSseChunk must copy
        // message.tool_calls into the emitted delta VERBATIM — JSON
        // passthrough, nothing re-shaped — mirroring the reasoning_content
        // handling.
        await using var f = new MergedDecodeFixture();
        f.Proxy.FallbackResult = MergedDecodeResult(
            content: "", completionTokens: 50, toolCallsJson: ToolCallJson);
        f.Proxy.MergedStreamChunks =
        [
            Sse("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}"),
            Sse("data: {\"choices\":[{\"delta\":{\"content\":\"\"}}],\"hydra_metrics\":{\"decode_ms\":5000}}"),
            Sse("data: [DONE]"),
        ];

        var chunks = await CollectAsync(f, "sess_fs588fb");

        Assert.Single(f.Proxy.NonStreamingCalls);
        var fallbackChunk = Encoding.UTF8.GetString(chunks[1]);
        Assert.Contains("chat.completion.chunk", fallbackChunk);
        Assert.Contains("\"content\":\"\"", fallbackChunk);
        using var chunkDoc = JsonDocument.Parse(fallbackChunk[6..]);
        var delta = chunkDoc.RootElement.GetProperty("choices")[0].GetProperty("delta");
        Assert.True(delta.TryGetProperty("tool_calls", out var toolCalls),
            "fallback chunk delta must carry the fallback's tool_calls");
        Assert.Equal(1, toolCalls.GetArrayLength());
        Assert.Equal("calculator",
            toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"a\":1234,\"b\":5678}",
            toolCalls[0].GetProperty("function").GetProperty("arguments").GetString());
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

    // ── #470 Fix 1: model-agnostic sessions pin to auto_routing.default_model ──

    /// <summary>Production-like loader: auto_routing default = moe-35b-solo,
    /// rtx can host it as a head worker (mirrors infra models.json).</summary>
    private static void UseDefaultModelLoader()
    {
        var models = new Dictionary<string, ModelTemplate>
        {
            ["moe-35b-solo"] = new ModelTemplate
            {
                Description = "solo",
                PrefillAlias = "qwen3.6-35B-mini",
                DecodeAlias  = "qwen3.6-35B-mini",
                LoadTimeS = 40,
                QualityTier = 1,
                Requirements = new ModelRequirements
                {
                    MinVramMb = 8000,
                    RequiredCapabilities = GpuCapabilities.FlashAttn,
                },
                Routing = new RoutingRule
                {
                    AutoEligible = true,
                    MinPromptTokens = 0,
                    MaxPromptTokens = 2048,
                    MaxContextTokens = 128000,
                },
            },
        };
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            AutoRouting = new AutoRoutingPolicy { Enabled = true, DefaultModel = "moe-35b-solo", SwapCostBudgetS = 30 },
            Models = models,
            ModelFileAliases = new Dictionary<string, string>
            {
                ["qwen3.6-35B-mini"] = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
            },
        };
        ModelConfigLoader.Reset();
        ModelConfigLoader.SetInstance(ModelConfigLoader.Create(config));
    }

    [Fact]
    public async Task ModelAgnosticRequest_PinnedToDefaultModel()
    {
        // #470 Fix 1: a request with NO model field must be pinned to
        // auto_routing.default_model (moe-35b-solo) — NOT prefilled with
        // whatever model is left resident on the picked worker.
        await using var f = new MergedDecodeFixture();
        UseDefaultModelLoader();
        try
        {
            var result = await f.SubmitModelAgnosticAsync("sess_pin1", 500, 100, stream: false);

            Assert.Contains(f.Events,
                e => e.MessageTemplate.Text.Contains("model_agnostic_pinned_to_default")
                    && e.Properties.TryGetValue("Model", out var mv)
                    && mv.ToString().Contains("moe-35b-solo"));
            // The pinned request still completes through the pipeline.
            var dict = Assert.IsType<Dictionary<string, object>>(result);
            Assert.True(dict.ContainsKey("choices"), "pinned request must produce a completion");
        }
        finally { ModelConfigLoader.Reset(); }
    }

    [Fact]
    public async Task ModelAgnosticRequest_BoundToDifferentModel_NotPinned()
    {
        // #470 Fix 1: a session already bound to a DIFFERENT model (e.g. an
        // earlier explicit dense-27b-combined request) must NOT be re-routed
        // to the default mid-conversation — that would trip the merged-decode
        // Gate A cross-model guard on the next turn.
        await using var f = new MergedDecodeFixture();
        UseDefaultModelLoader();
        try
        {
            f.Ledger.Register("sess_pin2", "rtx", 0, 0, null).BoundModel = "dense-27b-combined";
            var result = await f.SubmitModelAgnosticAsync("sess_pin2", 500, 100, stream: false);

            Assert.DoesNotContain(f.Events,
                e => e.MessageTemplate.Text.Contains("model_agnostic_pinned_to_default"));
            Assert.IsType<Dictionary<string, object>>(result);
        }
        finally { ModelConfigLoader.Reset(); }
    }

    [Fact]
    public async Task ModelAgnosticRequest_BoundToDefaultModel_Pinned()
    {
        // #470 Fix 1: a session bound to the default model keeps getting the
        // pin — re-asserting the session's own model is always safe.
        await using var f = new MergedDecodeFixture();
        UseDefaultModelLoader();
        try
        {
            f.Ledger.Register("sess_pin3", "rtx", 0, 0, null).BoundModel = "moe-35b-solo";
            var result = await f.SubmitModelAgnosticAsync("sess_pin3", 500, 100, stream: false);

            Assert.Contains(f.Events,
                e => e.MessageTemplate.Text.Contains("model_agnostic_pinned_to_default"));
            Assert.IsType<Dictionary<string, object>>(result);
        }
        finally { ModelConfigLoader.Reset(); }
    }

    // ── #470 Fix 3: merged_decode_transport_fault must not lose the lease ──

    [Fact]
    public async Task MergedDecode_Stream_TransportFault_FallsBackToProxy_LeaseSurvives()
    {
        // #470 Fix 3: the merged DECODE RPC throws (channel drop) — the
        // streaming request must fall back to the HTTP proxy with a NON-EMPTY
        // reply, and the session lease must survive until NotifyStreamComplete
        // releases it (no stream_done_no_lease orphaned slot).
        await using var f = new MergedDecodeFixture();
        f.Rpc.EngineMergedDecodeError = new InvalidOperationException("RPC channel dropped");

        var result = await f.SubmitAsync("sess_ft1", 500, 100, stream: true);
        var stream = Assert.IsAssignableFrom<IAsyncEnumerable<byte[]>>(result);
        var chunks = new List<byte[]>();
        await foreach (var c in stream)
            chunks.Add(c);

        Assert.Contains(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_transport_fault"));
        Assert.NotEmpty(chunks);
        Assert.Contains("Hi", Encoding.UTF8.GetString(chunks[0]));

        // The lease is re-asserted/released on the normal path — no orphan.
        await f.Scheduler.NotifyStreamComplete("sess_ft1");
        for (var i = 0; i < 50 && f.Tracker.FreeSlotCount("rtx") != 2; i++)
            await Task.Delay(50);
        Assert.Equal(2, f.Tracker.FreeSlotCount("rtx"));
        Assert.DoesNotContain(f.Scheduler.GetWarmLeasesSnapshot(), kv => kv.Key == "sess_ft1");
    }

    [Fact]
    public async Task MergedDecode_Buffered_TransportFault_FallsBackToProxy()
    {
        // #470 Fix 3 (non-streaming): the merged DECODE RPC throws — the
        // buffered reply is assembled from the HTTP-proxy result regardless
        // of lease state, and the transport fault is logged + counted.
        await using var f = new MergedDecodeFixture();
        f.Rpc.EngineMergedDecodeError = new InvalidOperationException("RPC channel dropped");

        var result = await f.SubmitAsync("sess_ft2", 500, 100, stream: false);

        Assert.Contains(f.Events, e => e.MessageTemplate.Text.Contains("merged_decode_transport_fault"));
        Assert.Single(f.Proxy.NonStreamingCalls);
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        var choices = Assert.IsType<JsonElement>(dict["choices"]);
        Assert.Equal("Hello from fallback",
            choices[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task MergedDecode_BusyOnce_ThenOk_Succeeds()
    {
        // #470 BUSY-retry (2026-08-13): the engine slot is transiently busy
        // (HYDRA_STATUS_BUSY 0x04 — a concurrent decode in progress). The
        // coordinator must retry (bounded, with backoff) instead of treating
        // the first Busy as a terminal Gate-A rejection → the request succeeds.
        await using var f = new MergedDecodeFixture();
        f.Rpc.BusyThenOkCount = 1; // first merged decode returns Busy, second Ok

        var result = await f.SubmitAsync("sess_busy1", 500, 100, stream: false);

        // The request succeeded through the merged-decode path (no HTTP-proxy
        // fallback — the gate accepted the retried Ok response).
        Assert.Empty(f.Proxy.NonStreamingCalls);
        Assert.Contains(f.Events,
            e => e.MessageTemplate.Text.Contains("merged_decode_busy_retry"));
        Assert.DoesNotContain(f.Events,
            e => e.MessageTemplate.Text.Contains("KV not restored, aborting"));
        var dict = Assert.IsType<Dictionary<string, object>>(result);
        Assert.True(dict.ContainsKey("choices"), "retried busy request must produce a completion");
    }

    [Fact]
    public async Task MergedDecode_BusyAlways_StillFails()
    {
        // #470 BUSY-retry: a slot that stays busy past the bounded retry
        // budget (3 attempts) must still fail via the Gate-A abort — the retry
        // loop must NOT loop forever nor fall through to a wrong decode.
        await using var f = new MergedDecodeFixture();
        f.Rpc.BusyThenOkCount = 99; // busy on every attempt (beyond the 3-attempt budget)

        // Retries exhausted → the gate abort path throws.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.SubmitAsync("sess_busy2", 500, 100, stream: false));
        Assert.Contains("KV not restored, aborting", ex.Message);

        // The HTTP-proxy fallback must NOT have produced a happy completion —
        // the gate rejects the request (transport-fault fallback is for RPC
        // faults; a Gate-A reject is terminal by design).
        Assert.Empty(f.Proxy.NonStreamingCalls);
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

    /// <summary>#588: OpenAI-shaped tool_calls array, the exact shape the
    /// engine (b95c228b) emits in merged DONE results.</summary>
    private const string ToolCallJson =
        "[{\"id\":\"call_abc123\",\"type\":\"function\",\"function\":{\"name\":\"calculator\",\"arguments\":\"{\\\"a\\\":1234,\\\"b\\\":5678}\"}}]";

    /// <summary>Build an OpenAI-style completion result dictionary for the
    /// PollDecodeResultAsync double. toolCallsJson is copied into
    /// message.tool_calls VERBATIM when provided — the shape the engine
    /// (b95c228b) emits in merged DONE results.</summary>
    private static Dictionary<string, object> MergedDecodeResult(
        string content, int completionTokens, string reasoningContent = "",
        string? toolCallsJson = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = content,
            ["reasoning_content"] = reasoningContent,
        };
        if (toolCallsJson != null)
        {
            using var tcDoc = JsonDocument.Parse(toolCallsJson);
            message["tool_calls"] = tcDoc.RootElement.Clone();
        }
        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = completionTokens > 0 ? "stop" : "length",
                }
            },
            ["usage"] = new { prompt_tokens = 10, completion_tokens = completionTokens, total_tokens = 10 + completionTokens },
            ["id_slot"] = 0,
            ["id"] = "chatcmpl-merged",
            ["model"] = "nano",
            ["created"] = 0,
        };
        return JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(payload))!;
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

        /// <summary>#470 Fix 3: when set, EngineMergedDecodeAsync throws —
        /// simulates a merged_decode_transport_fault (RPC channel drop after
        /// the engine may have accepted the decode).</summary>
        public Exception? EngineMergedDecodeError { get; set; }

        /// <summary>#470 BUSY-retry: number of consecutive Busy (0x04) responses
        /// to emit before returning Ok. 0 = never busy (default).</summary>
        public int BusyThenOkCount { get; set; }

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
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
            if (EngineMergedDecodeError != null)
                throw EngineMergedDecodeError;
            if (BusyThenOkCount > 0)
            {
                BusyThenOkCount--;
                return Task.FromResult(new MergedDecodeResponse
                {
                    Status = (byte)StatusCode.Busy,
                    Valid = false,
                    DecodeRequestId = null,
                    NPastAfterRestore = 0,
                });
            }
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
                    new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2, Role = "head", PrefillPriority = 1, DecodePriority = 2 },
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

        /// <summary>#470 Fix 1: submit a model-agnostic request — no `model`
        /// field at all, the shape a plain OpenAI client (no hydra model
        /// alias) sends. Routing must pin it to auto_routing.default_model.</summary>
        public async Task<object?> SubmitModelAgnosticAsync(
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
                ["messages"] = msgs
            };
            return await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
                maxTokens, null, _runCts.Token);
        }
    }
}
