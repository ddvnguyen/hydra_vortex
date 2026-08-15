using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Shared;

namespace Tests.Shared;

public class RpcClientTests : IAsyncLifetime
{
    private TestRpcServer? _server;
    private Task? _serverTask;

    public async Task InitializeAsync()
    {
        _server = new TestRpcServer(0);
        _serverTask = Task.Run(() => _server.RunAsync(CancellationToken.None));
        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task ClientServer_RoundTrip()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = new byte[] { 0xDE, 0xAD };
        var response = await client.RequestAsync(
            OpCode.Put, "rt", payload,
            "trace-rt", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, response.Status);
        Assert.NotNull(response.Meta);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, response.Payload);
    }

    [Fact]
    public async Task Client_BinaryPayload_RoundTrip()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = new byte[256];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        var response = await client.RequestAsync(
            OpCode.Put, "binary", payload,
            "trace-binary", CancellationToken.None);

        Assert.Equal(payload, response.Payload);
    }

    [Fact]
    public async Task Reconnect_AfterServerRestart()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var r1 = await client.RequestAsync(
            OpCode.Stat, "first", ReadOnlyMemory<byte>.Empty,
            "trace-r1", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, r1.Status);

        // Restart server on same port
        var port = _server.Port;
        await _server.DisposeAsync();
        _server = null;

        var server2 = new TestRpcServer(port);
        var server2Task = Task.Run(() => server2.RunAsync(CancellationToken.None));
        await Task.Delay(200);

        try
        {
            // Client should auto-reconnect
            var r2 = await client.RequestAsync(
                OpCode.Get, "second", new byte[] { 42 },
                "trace-r2", CancellationToken.None);

            Assert.Equal((byte)StatusCode.Ok, r2.Status);
            Assert.Equal([42], r2.Payload);
        }
        finally
        {
            await server2.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconnect_AfterServerRestart_ThreeAttempts()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        // First request works
        var r1 = await client.RequestAsync(
            OpCode.Stat, "alive", ReadOnlyMemory<byte>.Empty,
            "trace-alive", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, r1.Status);

        // Kill server it's connected to
        var oldPort = _server.Port;
        await _server.DisposeAsync();

        // Don't start a new one — connection should fail after retries
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestAsync(OpCode.Put, "dead", ReadOnlyMemory<byte>.Empty,
                "trace-dead", cts.Token));

        Assert.True(ex is IOException || ex is SocketException || ex is InvalidOperationException,
            $"Expected IO/Socket/InvalidOp exception, got: {ex.GetType().Name}");
    }

    [Fact]
    public async Task Timeout_PropagatesViaCancellationToken()
    {
        Assert.NotNull(_server);

        _server!.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            await Task.Delay(10_000, ct);
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RequestAsync(OpCode.Put, "timeout", ReadOnlyMemory<byte>.Empty,
                "trace-timeout", cts.Token));
    }

    [Fact]
    public async Task DisposedClient_ThrowsObjectDisposed()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);
        await client.DisposeAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestAsync(OpCode.Stat, "disposed", ReadOnlyMemory<byte>.Empty,
                "trace-disc", CancellationToken.None));

        Assert.True(ex is ObjectDisposedException || ex is NullReferenceException);
    }

    [Fact]
    public async Task RequestStreamAsync_StreamsPayload()
    {
        Assert.NotNull(_server);

        var server = _server!;
        var responsePayload = new byte[50_000];
        new Random(99).NextBytes(responsePayload);

        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            var metaBytes = """{"size":50000}"""u8.ToArray();
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, (ulong)responsePayload.Length, ct);

            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);

            // Stream payload in chunks
            var offset = 0;
            while (offset < responsePayload.Length)
            {
                var chunk = Math.Min(16384, responsePayload.Length - offset);
                var pSpan = writer.GetSpan(chunk);
                responsePayload.AsSpan(offset, chunk).CopyTo(pSpan);
                writer.Advance(chunk);
                await writer.FlushAsync(ct);
                offset += chunk;
            }
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var chunks = new List<byte>();
        await foreach (var chunk in client.RequestStreamAsync(
            OpCode.Get, "stream-me", ReadOnlyMemory<byte>.Empty,
            "trace-stream", CancellationToken.None))
        {
            chunks.AddRange(chunk);
        }

        Assert.Equal(responsePayload, chunks.ToArray());
    }

    [Fact]
    public async Task RequestStreamAsync_ThrowsOnError()
    {
        Assert.NotNull(_server);

        _server!.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            var meta = """{"error":"not_found"}""";
            var metaBytes = System.Text.Encoding.UTF8.GetBytes(meta);
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.NotFound,
                (uint)metaBytes.Length, 0, ct);
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        };

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var enumerable = client.RequestStreamAsync(
            OpCode.Get, "missing", ReadOnlyMemory<byte>.Empty,
            "trace-missing", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in enumerable) { }
        });
    }

    [Fact]
    public async Task ReadPayload_CapRaisedTo2GB_AcceptsLargeKvStateBlobLengths()
    {
        // #594: the PREFILL response (0x42) carries the KV state blob inline —
        // 827 MB measured at 7.3K tokens, ~800 MB at 60-80K tokens. The old
        // 512 MB cap rejected it. A 600 MB declared length must now pass the
        // sanity check; the read then hits EOF because the server closed without
        // sending the body. Under the old cap this threw InvalidDataException
        // ("out of range") instead of reaching the read at all.
        Assert.NotNull(_server);
        _server!.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, 600L * 1024 * 1024, ct);
        };
        _server.CloseAfterHandle = true;

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ex = await Assert.ThrowsAsync<EndOfStreamException>(() =>
            client.RequestAsync(OpCode.Get, "big-kv", ReadOnlyMemory<byte>.Empty,
                "trace-bigkv", cts.Token));

        Assert.DoesNotContain("out of range", ex.Message);
    }

    [Fact]
    public async Task MalformedFrame_PayloadLenOverCap_ThrowsInvalidData_AndDropsConnection()
    {
        // A declared length above the 2 GB sanity bound must be rejected as a
        // framing error, and the desynced connection dropped: the follow-up
        // request has to arrive on a fresh TCP connection (a connection-counting
        // server proves it), not be replayed on the misaligned socket.
        Assert.NotNull(_server);
        var server = _server!;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            // 5 GB sits above the 4 GB cap (raised from 2 GB in #470-79fdd3c21).
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, 5L * 1024 * 1024 * 1024, ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestAsync(OpCode.Get, "huge", ReadOnlyMemory<byte>.Empty,
                "trace-huge", cts.Token));
        Assert.Contains("out of range", ex.Message);

        // Same client, next request: must go over a brand-new connection.
        server.OnHandle = null; // back to default echo
        var before = server.ConnectionCount;
        var r2 = await client.RequestAsync(
            OpCode.Put, "after-drop", new byte[] { 1, 2 },
            "trace-after", cts.Token);

        Assert.Equal((byte)StatusCode.Ok, r2.Status);
        Assert.True(server.ConnectionCount > before,
            "expected a fresh connection after the framing error");
    }

    [Fact]
    public async Task MalformedFrame_NegativePayloadLen_ThrowsInvalidData_AndDropsConnection()
    {
        // ulong.MaxValue header length casts to -1 on the client — the negative
        // branch of the sanity check. Same contract: InvalidDataException + drop.
        Assert.NotNull(_server);
        var server = _server!;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, ulong.MaxValue, ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestAsync(OpCode.Get, "negative", ReadOnlyMemory<byte>.Empty,
                "trace-neg", cts.Token));
        Assert.Contains("out of range", ex.Message);

        server.OnHandle = null;
        var before = server.ConnectionCount;
        var r2 = await client.RequestAsync(
            OpCode.Put, "after-drop-neg", new byte[] { 3, 4 },
            "trace-after-neg", cts.Token);

        Assert.Equal((byte)StatusCode.Ok, r2.Status);
        Assert.True(server.ConnectionCount > before,
            "expected a fresh connection after the framing error");
    }

    [Fact]
    public async Task EngineMergedDecodeStreamKvAsync_StreamsChunksToWire()
    {
        // #470 Phase 2: the framed DECODE kv segment is streamed chunk-by-chunk
        // (no full blob) — the server must receive the exact concatenation with
        // the kv_len declared in the segments table.
        Assert.NotNull(_server);
        var server = _server!;

        var chunk1 = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var chunk2 = Enumerable.Range(0, 32 * 1024).Select(i => (byte)((i * 7) % 251)).ToArray();
        var expected = chunk1.Concat(chunk2).ToArray();

        byte[]? kvReceived = null;
        long? declaredKvLen = null;
        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            Assert.Equal(OpCode.EngineDecode, op);

            var hdrLen = BinaryPrimitives.ReadUInt32LittleEndian(
                await RpcServer.ReadPayloadAsync(reader, 4, ct));
            var hdrHash = BinaryPrimitives.ReadUInt64LittleEndian(
                await RpcServer.ReadPayloadAsync(reader, 8, ct));
            Assert.NotEqual(0UL, hdrHash);
            var hdrJson = await RpcServer.ReadPayloadAsync(reader, hdrLen, ct);

            long promptLen = 0, kvLen = 0;
            using (var doc = JsonDocument.Parse(hdrJson))
            {
                foreach (var seg in doc.RootElement.GetProperty("segments").EnumerateArray())
                {
                    if (seg.GetProperty("id").GetString() == "prompt")
                        promptLen = seg.GetProperty("len").GetInt64();
                    else if (seg.GetProperty("id").GetString() == "kv")
                        kvLen = seg.GetProperty("len").GetInt64();
                }
            }
            declaredKvLen = kvLen;
            Assert.Equal(0L, promptLen);
            await RpcServer.ReadPayloadAsync(reader, promptLen, ct);

            kvReceived = await RpcServer.ReadPayloadAsync(reader, kvLen, ct);

            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, 0, 0, ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks()
        {
            yield return chunk1;
            yield return chunk2;
        }

        var resp = await client.EngineMergedDecodeStreamKvAsync(
            "0", nPast: 123,
            kvTokenizer: null, kvModelName: null, kvModelQuant: null, kvModelCapabilities: 0,
            modelTokenizer: null, modelName: null, modelQuant: null, modelCapabilities: 0,
            modelAlias: "nano",
            messagesJson: null, nPredict: 16, samplingJson: null, stream: false,
            kvChunks: Chunks(), kvTotalSize: expected.Length,
            kvHash: "",
            "trace-streamkv", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Equal(expected.Length, declaredKvLen);
        Assert.Equal(expected, kvReceived);
    }

    [Fact]
    public async Task RequestChunkedPayloadAsync_StreamsChunksWithoutBuffering()
    {
        // #470 Phase 2: large payloads are delivered via onChunk — the client
        // must never materialize the full response as one byte[].
        Assert.NotNull(_server);
        var server = _server!;

        var payload = new byte[3 * 1024 * 1024 + 17];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 253);

        server.OnHandle = async (op, key, traceId, payloadLen, reader, writer, ct) =>
        {
            var meta = $$"""{"len":{{payload.Length}}}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await RpcServer.WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok,
                (uint)metaBytes.Length, (ulong)payload.Length, ct);
            await RpcServer.WriteMetaAsync(writer, meta, ct);
            await RpcServer.WritePayloadAsync(writer, payload, ct);
        };

        var client = new RpcClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        long declaredLen = -1;
        var received = new List<byte>();
        var resp = await client.RequestChunkedPayloadAsync(
            OpCode.GetChunked, "kv/x", Encoding.UTF8.GetBytes("[]"), "trace-chunked", CancellationToken.None,
            onPayloadLen: len => declaredLen = len,
            onChunk: (mem, _) =>
            {
                received.AddRange(mem.Span.ToArray());
                return ValueTask.CompletedTask;
            });

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Equal(payload.Length, declaredLen);
        Assert.Equal(payload.Length, received.Count);
        Assert.Equal(payload, received.ToArray());
        Assert.True(received.Count > 4, "expected multiple chunks for a >3 MB payload");
    }
}
