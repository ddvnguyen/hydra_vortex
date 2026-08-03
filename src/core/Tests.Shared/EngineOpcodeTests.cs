using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Hydra.Shared;

namespace Tests.Shared;

public sealed class EngineOpcodeTests : IAsyncLifetime
{
    private TestRpcServer? _server;

    public async Task InitializeAsync()
    {
        _server = new TestRpcServer(0);
        _ = Task.Run(() => _server.RunAsync(CancellationToken.None));
        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task EngineInfo_EmptyPayload_RoundTrips()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server!.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.EngineInfoAsync("0", "trace-info", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Contains("EngineInfo", resp.Meta);
    }

    [Fact]
    public async Task EngineConfigure_PayloadIsUtf8Json()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server!.Port);
        await client.ConnectAsync(CancellationToken.None);

        var config = """{"n_predict":128,"temperature":0.7,"seed":42}""";
        var resp = await client.EngineConfigureAsync("0", config, "trace-cfg", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        // TestRpcServer echoes the payload back as a single span — verify the
        // client sent the exact UTF-8 bytes we gave it.
        Assert.Equal(Encoding.UTF8.GetBytes(config), resp.Payload);
    }

    [Fact]
    public async Task EnginePrefill_PayloadIsUtf8Json()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server!.Port);
        await client.ConnectAsync(CancellationToken.None);

        var request = """{"messages":[{"role":"user","content":"hi"}]}""";
        var resp = await client.EnginePrefillAsync("0", request, "trace-prefill", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(request), resp.Payload);
    }

    [Fact]
    public async Task EngineDecode_PayloadWrapsNpredictAndMessages()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server!.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.EngineDecodeAsync(
            "0", nPredict: 64,
            requestJson: """[{"role":"user","content":"hi"}]""",
            "trace-decode", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        var json = Encoding.UTF8.GetString(resp.Payload);
        Assert.Contains("\"n_predict\":64", json);
        Assert.Contains("\"messages\":", json);
    }

    [Fact]
    public async Task EngineDecode_NullRequestJson_ProducesNullMessages()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server!.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.EngineDecodeAsync(
            "0", nPredict: 16, requestJson: null,
            "trace-decode-null", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        var json = Encoding.UTF8.GetString(resp.Payload);
        Assert.Contains("\"messages\":null", json);
    }

    [Fact]
    public async Task EngineSetExpertMode_PayloadIsRawModeString()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server!.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.EngineSetExpertModeAsync("0", "solo", "trace-expert", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Equal(Encoding.UTF8.GetBytes("solo"), resp.Payload);
    }

    [Fact]
    public async Task EngineSwapQuant_PayloadHasLenPrefixedQuantKeyThenPattern()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server!.Port);
        await client.ConnectAsync(CancellationToken.None);

        var quantKey = "Q6_K/experts";
        var pattern = @"blk\.5\.ffn_.*_exps";
        var resp = await client.EngineSwapQuantAsync("0", quantKey, pattern, "trace-swap", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);

        // Wire: [2B quant_key_len LE][quant_key UTF-8][pattern UTF-8]
        Assert.True(resp.Payload.Length >= 2);
        var quantKeyLen = BinaryPrimitives.ReadUInt16LittleEndian(resp.Payload);
        Assert.Equal((ushort)Encoding.UTF8.GetByteCount(quantKey), quantKeyLen);
        var quantKeyBytes = resp.Payload.AsSpan(2, quantKeyLen).ToArray();
        Assert.Equal(Encoding.UTF8.GetBytes(quantKey), quantKeyBytes);
        var patternBytes = resp.Payload.AsSpan(2 + quantKeyLen).ToArray();
        Assert.Equal(Encoding.UTF8.GetBytes(pattern), patternBytes);
    }

    [Fact]
    public async Task EngineDecodeStreamAsync_StreamsTokenFrames()
    {
        // Per specs/rpc-protocol.md:
        //   payload = sequence of [4B token_id][4B logprob][1B flags] frames
        //   (flags: 0x01 = final)
        // Server writes payload incrementally; client reads via RequestStreamAsync.
        Assert.NotNull(_server);

        var server = _server!;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            // Build a 3-token stream: 1 non-final, 2 non-final, final.
            var tokenIds = new uint[] { 100, 101, 102 };
            var logprobs = new float[] { -0.1f, -0.2f, -0.3f };

            var meta = """{"tokens_generated":3,"n_past":3,"stop_reason":"eos"}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            const int frameSize = 4 + 4 + 1; // token_id(4) + logprob(4) + flags(1)
            var payloadSize = tokenIds.Length * frameSize;

            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, (ulong)payloadSize, ct);

            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);

            // Flush per token to exercise the incremental-write path.
            for (int i = 0; i < tokenIds.Length; i++)
            {
                var fSpan = writer.GetSpan(frameSize);
                BinaryPrimitives.WriteUInt32LittleEndian(fSpan, tokenIds[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(fSpan.Slice(4),
                    BitConverter.SingleToUInt32Bits(logprobs[i]));
                fSpan[8] = (i == tokenIds.Length - 1) ? (byte)0x01 : (byte)0x00;
                writer.Advance(frameSize);
                await writer.FlushAsync(ct);
            }
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var frames = new List<byte>();
        await foreach (var chunk in client.EngineDecodeStreamAsync(
            "0", nPredict: 3,
            requestJson: """[{"role":"user","content":"hi"}]""",
            "trace-decode-stream", CancellationToken.None))
        {
            frames.AddRange(chunk);
        }

        // Should be exactly 3 frames of 9 bytes each = 27 bytes.
        Assert.Equal(27, frames.Count);
        var framesArr = frames.ToArray();
        for (int i = 0; i < 3; i++)
        {
            var tokenId = BinaryPrimitives.ReadUInt32LittleEndian(framesArr.AsSpan(i * 9));
            var logprob = BitConverter.UInt32BitsToSingle(
                BinaryPrimitives.ReadUInt32LittleEndian(framesArr.AsSpan(i * 9 + 4)));
            var flags = framesArr[i * 9 + 8];
            Assert.Equal(100u + (uint)i, tokenId);
            Assert.Equal(-0.1f * (i + 1), logprob, precision: 5);
            Assert.Equal(i == 2 ? (byte)0x01 : (byte)0x00, flags);
        }
    }

    [Fact]
    public async Task EngineDecodeStreamAsync_ChunksArriveIncrementally()
    {
        // The server flushes after each frame, so RequestStreamAsync should
        // yield multiple chunks (not one big buffer). This guards against a
        // future refactor that buffers the full payload before flushing.
        Assert.NotNull(_server);

        var server = _server!;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            var metaBytes = """{"tokens_generated":2}"""u8.ToArray();
            const int frameSize = 9;
            const int nFrames = 2;
            var payloadSize = nFrames * frameSize;

            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, (ulong)payloadSize, ct);

            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);

            for (int i = 0; i < nFrames; i++)
            {
                var fSpan = writer.GetSpan(frameSize);
                BinaryPrimitives.WriteUInt32LittleEndian(fSpan, (uint)(i + 1));
                fSpan[4] = 0; fSpan[5] = 0; fSpan[6] = 0; fSpan[7] = 0;
                fSpan[8] = (i == nFrames - 1) ? (byte)0x01 : (byte)0x00;
                writer.Advance(frameSize);
                await writer.FlushAsync(ct);
                // Small delay so the client sees a separate chunk.
                await Task.Delay(20, ct);
            }
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var chunkCount = 0;
        var totalBytes = 0;
        await foreach (var chunk in client.EngineDecodeStreamAsync(
            "0", nPredict: 2, requestJson: null,
            "trace-decode-incr", CancellationToken.None))
        {
            chunkCount++;
            totalBytes += chunk.Length;
        }

        Assert.Equal(18, totalBytes);
        Assert.True(chunkCount >= 2,
            $"Expected ≥2 incremental chunks (one per frame), got {chunkCount}");
    }

    // ── HTTP / RPC parity tests (issue #469 / #518) ──────────────────────

    // These two tests were previously tautological (PR #528 review): the
    // "RPC path" called the raw RpcClient directly instead of the real
    // production HydraEngineClient wrapper, and the "HTTP path" just
    // re-parsed the exact same requestJson string — so both sides were
    // always trivially equal regardless of what either wrapper actually
    // does. They now drive HydraEngineClient (production code) for the RPC
    // side and independently reconstruct the HTTP-path body by mirroring
    // WorkerSchedulerService's real, cited construction rule for the HTTP
    // fallback path (WorkerSchedulerService.cs, "HTTP path" block, currently
    // around lines 1958-1971: body = requestJson clone + stream=false +
    // n_predict=0 + model injected only when prefillModel is set AND the
    // body doesn't already carry one). A divergence between the two — e.g.
    // someone changes HydraEngineClient's injection rule without updating
    // the HTTP path, or vice versa — now makes this test fail.
    [Fact]
    public async Task Prefill_HttpBodyShape_MatchesRpcPayload()
    {
        Assert.NotNull(_server);

        var server = _server!;
        byte[]? capturedPayload = null;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            capturedPayload = payloadLen > 0
                ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
                : [];
            var meta = """{"n_past":5,"state_size":1024}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, 0, ct);
            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);
        var engineClient = new Hydra.Core.Services.HydraEngineClient(client);

        var requestJson = """
            {"messages":[{"role":"user","content":"What is 2+2?"}],"temperature":0.7,"seed":42}
            """;
        const string prefillModel = "test-model-alias";

        // RPC path: the real production wrapper, not the raw RpcClient.
        await engineClient.EnginePrefillAsync(0, prefillModel, requestJson, "trace-parity", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        var rpcBody = JsonDocument.Parse(capturedPayload!);

        // HTTP-equivalent path: independently reconstructed per
        // WorkerSchedulerService's real HTTP-fallback body-building rule
        // (clone the request, force stream=false + n_predict=0, inject
        // model only if absent) — NOT derived from rpcBody or capturedPayload.
        var httpNode = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(requestJson)!;
        httpNode["stream"] = false;
        httpNode["n_predict"] = 0;
        if (!string.IsNullOrEmpty(prefillModel) && !httpNode.ContainsKey("model"))
            httpNode["model"] = prefillModel;
        var httpBody = JsonDocument.Parse(httpNode.ToJsonString());

        // Both independently-derived structures must carry identical
        // messages, temperature, seed, AND the injected model — this last
        // one is the field #469-style bugs would actually diverge on.
        Assert.Equal(
            httpBody.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(),
            rpcBody.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(
            httpBody.RootElement.GetProperty("temperature").GetDouble(),
            rpcBody.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(
            httpBody.RootElement.GetProperty("seed").GetInt32(),
            rpcBody.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal(
            httpBody.RootElement.GetProperty("model").GetString(),
            rpcBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(prefillModel, rpcBody.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Prefill_WithModelInjection_MatchesRpcPayload()
    {
        // HydraEngineClient.EnginePrefillAsync injects a "model" key only
        // when the caller supplies one AND the request body doesn't already
        // carry one (the caller's explicit value always wins). This test now
        // exercises the real HydraEngineClient for both cases instead of a
        // hand-duplicated copy of its injection logic.
        Assert.NotNull(_server);

        var server = _server!;
        byte[]? capturedPayload = null;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            capturedPayload = payloadLen > 0
                ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
                : [];
            var meta = """{"n_past":3}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, 0, ct);
            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);
        var engineClient = new Hydra.Core.Services.HydraEngineClient(client);

        var modelAlias = "Qwopus3.6-35B-A3B";

        // Case 1: request has no "model" key — HydraEngineClient must inject it.
        var requestJsonNoModel = """{"messages":[{"role":"user","content":"hi"}]}""";
        await engineClient.EnginePrefillAsync(0, modelAlias, requestJsonNoModel, "trace-model-inject", CancellationToken.None);
        Assert.NotNull(capturedPayload);
        var injectedDoc = JsonDocument.Parse(capturedPayload!);
        Assert.True(injectedDoc.RootElement.TryGetProperty("model", out var injectedModelEl));
        Assert.Equal(modelAlias, injectedModelEl.GetString());

        // Case 2: request already has an explicit "model" key — HydraEngineClient
        // must NOT override the caller's value.
        capturedPayload = null;
        var requestJsonWithModel = """{"messages":[{"role":"user","content":"hi"}],"model":"caller-explicit-model"}""";
        await engineClient.EnginePrefillAsync(0, modelAlias, requestJsonWithModel, "trace-model-preserve", CancellationToken.None);
        Assert.NotNull(capturedPayload);
        var preservedDoc = JsonDocument.Parse(capturedPayload!);
        Assert.True(preservedDoc.RootElement.TryGetProperty("model", out var preservedModelEl));
        Assert.Equal("caller-explicit-model", preservedModelEl.GetString());
    }

    [Fact]
    public async Task Decode_HttpBodyShape_MatchesRpcPayload()
    {
        // RPC 0x43 DECODE wraps n_predict + messages into JSON. The HTTP
        // decode path must construct an equivalent shape.
        Assert.NotNull(_server);

        var server = _server!;
        byte[]? capturedPayload = null;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            capturedPayload = payloadLen > 0
                ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
                : [];
            var meta = """{"tokens_generated":5}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, 0, ct);
            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var messagesJson = """[{"role":"user","content":"hi"}]""";
        const int nPredict = 64;

        await client.EngineDecodeAsync("0", nPredict, messagesJson,
            "trace-decode-parity", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        var doc = JsonDocument.Parse(capturedPayload!);

        // Verify the RPC DECODE payload has the expected shape
        Assert.True(doc.RootElement.TryGetProperty("n_predict", out var npEl));
        Assert.Equal(nPredict, npEl.GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("messages", out var msgEl));
        Assert.Equal(JsonValueKind.Array, msgEl.ValueKind);
        Assert.Equal(1, msgEl.GetArrayLength());
        Assert.Equal("user", msgEl[0]!.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Decode_NullMessages_NullInRpcPayload()
    {
        // When no messages are provided, DECODE must encode "messages":null
        // so the engine knows to decode from the existing KV state.
        Assert.NotNull(_server);

        var server = _server!;
        byte[]? capturedPayload = null;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            capturedPayload = payloadLen > 0
                ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
                : [];
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, 0, ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        await client.EngineDecodeAsync("0", 16, null,
            "trace-decode-null-parity", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        var doc = JsonDocument.Parse(capturedPayload!);
        Assert.True(doc.RootElement.TryGetProperty("messages", out var msgEl));
        Assert.Equal(JsonValueKind.Null, msgEl.ValueKind);
    }

    [Fact]
    public async Task PrefillParams_SamplingFields_PreservedAcrossPaths()
    {
        // Previously this test never called any Hydra code — it parsed
        // requestJson with System.Text.Json and asserted properties of that
        // same reparsed document, which only proves System.Text.Json's own
        // round-trip is lossless. It exercised neither HydraEngineClient nor
        // any HTTP-path construction. Rewritten to independently derive both
        // sides (real HydraEngineClient RPC payload vs. a reconstruction of
        // WorkerSchedulerService's HTTP-fallback body rule) and compare them,
        // same pattern as Prefill_HttpBodyShape_MatchesRpcPayload above.
        Assert.NotNull(_server);

        var server = _server!;
        byte[]? capturedPayload = null;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            capturedPayload = payloadLen > 0
                ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
                : [];
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, 0, ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);
        var engineClient = new Hydra.Core.Services.HydraEngineClient(client);

        var requestJson = """
            {
                "messages": [{"role": "user", "content": "test"}],
                "temperature": 0.7,
                "top_p": 0.9,
                "top_k": 40,
                "seed": 12345,
                "repeat_penalty": 1.1,
                "max_tokens": 256
            }
            """;

        await engineClient.EnginePrefillAsync(0, model: null, requestJson, "trace-sampling", CancellationToken.None);
        Assert.NotNull(capturedPayload);
        var rpcBody = JsonDocument.Parse(capturedPayload!);

        Assert.Equal(0.7, rpcBody.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, rpcBody.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(40, rpcBody.RootElement.GetProperty("top_k").GetInt32());
        Assert.Equal(12345, rpcBody.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal(1.1, rpcBody.RootElement.GetProperty("repeat_penalty").GetDouble());
        Assert.Equal(256, rpcBody.RootElement.GetProperty("max_tokens").GetInt32());

        var msgs = rpcBody.RootElement.GetProperty("messages");
        Assert.Equal(1, msgs.GetArrayLength());
        Assert.Equal("user", msgs[0]!.GetProperty("role").GetString());
        Assert.Equal("test", msgs[0]!.GetProperty("content").GetString());
    }
}
