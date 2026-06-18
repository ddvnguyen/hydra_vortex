using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
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
}
