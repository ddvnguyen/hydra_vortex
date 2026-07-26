using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Shared;

namespace Tests.Core;

/// <summary>
/// #470/A2: byte-for-byte verification of the segmented DECODE 0x43 wire format.
/// Tests the real EngineMergedDecodeAsync path by capturing wire output via a
/// local TCP server.
/// </summary>
public sealed class SegmentedDecodeFramingTests
{
    /// <summary>
    /// Spin up a local TCP server, return (client, server, port, listener).
    /// The caller must dispose the listener when done.
    /// </summary>
    private static (RpcClient client, TcpClient server, int port, TcpListener listener) CreateLoopbackPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = new RpcClient("127.0.0.1", port, TimeSpan.FromSeconds(10));
        var serverTask = listener.AcceptTcpClientAsync();
        var connectTask = client.ConnectAsync(CancellationToken.None);
        Task.WaitAll(serverTask, connectTask);

        var server = serverTask.Result;
        return (client, server, port, listener);
    }

    /// <summary>
    /// Start a background task that reads all bytes from the server socket,
    /// sends a minimal Ok response with valid meta, and returns the captured bytes.
    /// The client blocks waiting for the response, so the server must read
    /// and respond in the background to avoid deadlock.
    /// </summary>
    private static Task<byte[]> StartServerReadAndRespond(TcpClient server)
    {
        return Task.Run(() =>
        {
            var stream = server.GetStream();
            stream.ReadTimeout = 5000;
            using var ms = new MemoryStream();
            var buf = new byte[8192];
            try
            {
                while (true)
                {
                    var n = stream.Read(buf, 0, buf.Length);
                    if (n == 0) break;
                    ms.Write(buf, 0, n);
                }
            }
            catch (IOException) { }

            // Send response after reading all client bytes.
            // Include meta with valid=true so MergedDecodeResponse.Parse returns Valid=true.
            var meta = JsonSerializer.Serialize(new { valid = true, decode_request_id = 1 });
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            var respBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
            Protocol.WriteResponse(respBuf, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0);
            stream.Write(respBuf);
            stream.Write(metaBytes);
            stream.Flush();

            return ms.ToArray();
        });
    }

    [Fact]
    public void SegmentedDecode_Xxh3Hash_MatchesPublishedVector()
    {
        // Published xxh3-64 test vector for "hello world" (11 bytes):
        // https://raw.githubusercontent.com/Cyan4973/xxHash/dev/tests/input/secret%20seed/hello%20world
        // Reference: python3 -c "import xxhash; print(f'{xxhash.xxh3_64(b\"hello world\").intdigest():016x}')"
        var data = Encoding.UTF8.GetBytes("hello world");
        var hash = XxHash3.HashToUInt64(data);

        // Known xxh3-64 hash of "hello world" — cross-verified with Python xxhash library.
        const ulong expectedHash = 0xd447b1ea40e6988b;
        Assert.Equal(expectedHash, hash);

        // Verify format: "xxh3:" + 16 hex chars
        var hashStr = $"xxh3:{hash:x16}";
        Assert.Equal(21, hashStr.Length);
        Assert.StartsWith("xxh3:", hashStr);
    }

    [Fact]
    public async Task SegmentedDecode_EmptyPrompt_ProducesZeroLengthSegment()
    {
        // Test the real EngineMergedDecodeAsync path: empty prompt should
        // produce a wire payload where the prompt segment has len=0.
        var (client, server, _, listener) = CreateLoopbackPair();
        await using var _ = client;

        var wireTask = StartServerReadAndRespond(server);

        var resp = await client.EngineMergedDecodeAsync(
            slotKey: "0",
            nPast: 0,
            kvTokenizer: "llama", kvModelName: "test", kvModelQuant: "Q4_K", kvModelCapabilities: 0,
            modelTokenizer: "llama", modelName: "test", modelQuant: "Q4_K", modelCapabilities: 0,
            modelAlias: "test",
            messagesJson: "",
            nPredict: 10,
            samplingJson: null,
            stream: false,
            kvBlob: ReadOnlyMemory<byte>.Empty,
            traceId: "trace-empty",
            ct: CancellationToken.None);

        var wireBytes = await wireTask;
        listener.Stop();
        Assert.True(resp.Valid, "Response should be valid");

        // Parse the wire: [16B RPC header][key][trace][4B hdr_len LE][8B hdr_hash LE][hdr JSON][segments...]
        // Skip RPC header (16) + key ("0" = 1 byte) + trace ("trace-empty" = 11 bytes) = 28 bytes
        var offset = 16 + Encoding.UTF8.GetBytes("0").Length + Encoding.UTF8.GetBytes("trace-empty").Length;

        // Read [4B hdr_len LE]
        var hdrLen = BinaryPrimitives.ReadUInt32LittleEndian(wireBytes.AsSpan(offset));
        offset += 4;

        // Skip [8B hdr_hash LE]
        offset += 8;

        // Read the JSON header
        var hdrJson = Encoding.UTF8.GetString(wireBytes, offset, (int)hdrLen);
        using var doc = JsonDocument.Parse(hdrJson);
        var root = doc.RootElement;
        var segments = root.GetProperty("segments");

        // Prompt segment must have len=0
        var promptSeg = segments[0];
        Assert.Equal("prompt", promptSeg.GetProperty("id").GetString());
        Assert.Equal(0, promptSeg.GetProperty("len").GetInt32());

        // KV segment must also have len=0 (empty kvBlob)
        var kvSeg = segments[1];
        Assert.Equal("kv", kvSeg.GetProperty("id").GetString());
        Assert.Equal(0, kvSeg.GetProperty("len").GetInt32());
    }

    [Fact]
    public async Task SegmentedDecode_LargeHeader_ThrowsAbove32KiB()
    {
        // Verify that EngineMergedDecodeAsync rejects headers >32 KiB.
        // The header JSON doesn't include prompt text (that goes in the
        // prompt segment), so we inflate it via a huge samplingJson payload.
        // Inflate the header via a huge samplingJson payload (valid JSON object).
        var hugeSampling = JsonSerializer.Serialize(new { debug = new string('x', 40000) });

        var (client, server, _, listener) = CreateLoopbackPair();
        await using var _ = client;

        var wireTask = StartServerReadAndRespond(server);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EngineMergedDecodeAsync(
                slotKey: "0", nPast: 0,
                kvTokenizer: "", kvModelName: "", kvModelQuant: "", kvModelCapabilities: 0,
                modelTokenizer: "", modelName: "", modelQuant: "", modelCapabilities: 0,
                modelAlias: "test", messagesJson: "[]",
                nPredict: 10, samplingJson: hugeSampling, stream: false,
                kvBlob: ReadOnlyMemory<byte>.Empty,
                traceId: "trace-large", ct: CancellationToken.None));

        Assert.Contains("32 KiB", ex.Message);
        listener.Stop();
        server.Dispose();
    }

    [Fact]
    public async Task SegmentedDecode_PayloadFormat_IsCorrect()
    {
        // Test the real EngineMergedDecodeAsync path: capture the wire bytes
        // and verify the full payload structure.
        var (client, server, _, listener) = CreateLoopbackPair();
        await using var _ = client;

        var kvBlob = Encoding.UTF8.GetBytes("fake-kv-data");
        var messages = """[{"role":"user","content":"hello"}]""";

        var wireTask = StartServerReadAndRespond(server);

        var resp = await client.EngineMergedDecodeAsync(
            slotKey: "0",
            nPast: 1000,
            kvTokenizer: "llama", kvModelName: "qwen3.6-35B", kvModelQuant: "Q3_K", kvModelCapabilities: 19,
            modelTokenizer: "llama", modelName: "qwen3.6-35B", modelQuant: "Q5_K", modelCapabilities: 19,
            modelAlias: "balanced",
            messagesJson: messages,
            nPredict: 256,
            samplingJson: null,
            stream: true,
            kvBlob: kvBlob,
            traceId: "trace-test",
            ct: CancellationToken.None);

        var wireBytes = await wireTask;
        listener.Stop();
        Assert.True(resp.Valid);

        // Parse the wire frame
        var keyBytes = Encoding.UTF8.GetBytes("0");
        var traceBytes = Encoding.UTF8.GetBytes("trace-test");
        var offset = 16 + keyBytes.Length + traceBytes.Length;

        // [4B hdr_len LE]
        var hdrLen = BinaryPrimitives.ReadUInt32LittleEndian(wireBytes.AsSpan(offset));
        offset += 4;

        // [8B hdr_hash LE]
        var hdrHashWire = BinaryPrimitives.ReadUInt64LittleEndian(wireBytes.AsSpan(offset));
        offset += 8;

        // [hdr_len bytes] — JSON header
        var hdrJsonBytes = new byte[hdrLen];
        Array.Copy(wireBytes, offset, hdrJsonBytes, 0, (int)hdrLen);
        var hdrHashComputed = XxHash3.HashToUInt64(hdrJsonBytes);
        Assert.Equal(hdrHashComputed, hdrHashWire);
        offset += (int)hdrLen;

        // Verify header JSON content
        var hdrDoc = JsonDocument.Parse(Encoding.UTF8.GetString(hdrJsonBytes));
        var root = hdrDoc.RootElement;
        Assert.Equal(3, root.GetProperty("v").GetInt32());
        Assert.Equal("balanced", root.GetProperty("model").GetString());

        var kvMeta = root.GetProperty("kv_metadata");
        Assert.Equal(1000, kvMeta.GetProperty("n_past").GetInt32());
        Assert.Equal("llama", kvMeta.GetProperty("tokenizer").GetString());
        Assert.Equal("qwen3.6-35B", kvMeta.GetProperty("model_name").GetString());
        Assert.Equal("Q3_K", kvMeta.GetProperty("model_quant").GetString());
        Assert.Equal(19u, kvMeta.GetProperty("model_capabilities").GetUInt32());

        var modelMeta = root.GetProperty("model_metadata");
        Assert.Equal("llama", modelMeta.GetProperty("tokenizer").GetString());
        Assert.Equal("qwen3.6-35B", modelMeta.GetProperty("model_name").GetString());
        Assert.Equal("Q5_K", modelMeta.GetProperty("model_quant").GetString());

        var segments = root.GetProperty("segments");
        Assert.Equal(2, segments.GetArrayLength());

        // Verify prompt segment
        var promptBytes = Encoding.UTF8.GetBytes(messages);
        var promptHash = XxHash3.HashToUInt64(promptBytes);
        var promptSeg = segments[0];
        Assert.Equal("prompt", promptSeg.GetProperty("id").GetString());
        Assert.Equal(0, promptSeg.GetProperty("offset").GetInt32());
        Assert.Equal(promptBytes.Length, promptSeg.GetProperty("len").GetInt32());
        Assert.Equal($"xxh3:{promptHash:x16}", promptSeg.GetProperty("hash").GetString());

        // Verify KV segment
        var kvHash = XxHash3.HashToUInt64(kvBlob);
        var kvSeg = segments[1];
        Assert.Equal("kv", kvSeg.GetProperty("id").GetString());
        Assert.Equal(promptBytes.Length, kvSeg.GetProperty("offset").GetInt32());
        Assert.Equal(kvBlob.Length, kvSeg.GetProperty("len").GetInt32());
        Assert.Equal($"xxh3:{kvHash:x16}", kvSeg.GetProperty("hash").GetString());

        // Verify segment data is contiguous after the header
        var promptWire = new byte[promptBytes.Length];
        Array.Copy(wireBytes, offset, promptWire, 0, promptBytes.Length);
        Assert.Equal(promptBytes, promptWire);
        offset += promptBytes.Length;

        var kvWire = new byte[kvBlob.Length];
        Array.Copy(wireBytes, offset, kvWire, 0, kvBlob.Length);
        Assert.Equal(kvBlob.ToArray(), kvWire);
    }
}
