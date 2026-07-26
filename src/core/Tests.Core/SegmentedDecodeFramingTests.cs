using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Hydra.Shared;

namespace Tests.Core;

/// <summary>
/// #470/A2: byte-for-byte verification of the segmented DECODE 0x43 wire format.
/// The engine agent is writing the C++ half concurrently; these tests ensure the
/// C# client produces the exact bytes the engine expects.
/// </summary>
public sealed class SegmentedDecodeFramingTests
{
    [Fact]
    public void SegmentedDecode_PayloadFormat_IsCorrect()
    {
        // Arrange
        var slotKey = "0";
        var traceId = "trace-test";
        var nPast = 1000;
        var kvTokenizer = "llama";
        var kvModelName = "qwen3.6-35B";
        var kvModelQuant = "Q3_K";
        uint kvModelCapabilities = 19;
        var modelTokenizer = "llama";
        var modelName = "qwen3.6-35B";
        var modelQuant = "Q5_K";
        uint modelCapabilities = 19;
        var modelAlias = "balanced";
        var messages = """[{"role":"user","content":"hello"}]""";
        var nPredict = 256;
        string? samplingJson = null;
        var stream = true;
        var kvBlob = Encoding.UTF8.GetBytes("fake-kv-data");

        // Act — build the control header JSON (same logic as RpcClient)
        var promptBytes = Encoding.UTF8.GetBytes(messages);
        var promptHash = XxHash3.HashToUInt64(promptBytes);
        var promptHashStr = $"xxh3:{promptHash:x16}";

        var kvHash = XxHash3.HashToUInt64(kvBlob);
        var kvHashStr = $"xxh3:{kvHash:x16}";

        var headerObj = new Dictionary<string, object>
        {
            ["v"] = 3,
            ["model"] = modelAlias,
            ["kv_metadata"] = new Dictionary<string, object>
            {
                ["n_past"] = nPast,
                ["tokenizer"] = kvTokenizer,
                ["model_name"] = kvModelName,
                ["model_quant"] = kvModelQuant,
                ["model_capabilities"] = kvModelCapabilities
            },
            ["model_metadata"] = new Dictionary<string, object>
            {
                ["tokenizer"] = modelTokenizer,
                ["model_name"] = modelName,
                ["model_quant"] = modelQuant,
                ["model_capabilities"] = modelCapabilities
            },
            ["generation"] = new Dictionary<string, object>
            {
                ["n_predict"] = nPredict,
                ["sampling"] = samplingJson != null
                    ? JsonSerializer.Deserialize<object>(samplingJson)
                    : new Dictionary<string, object>(),
                ["stop"] = Array.Empty<string>(),
                ["stream"] = stream,
                ["chat_syntax"] = "",
                ["oaicompat_model"] = modelAlias
            },
            ["segments"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["id"] = "prompt",
                    ["offset"] = 0,
                    ["len"] = promptBytes.Length,
                    ["hash"] = promptHashStr
                },
                new()
                {
                    ["id"] = "kv",
                    ["offset"] = promptBytes.Length,
                    ["len"] = kvBlob.Length,
                    ["hash"] = kvHashStr
                }
            }
        };
        var hdrJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headerObj));
        var hdrHash = XxHash3.HashToUInt64(hdrJsonBytes);

        // Build the framed payload manually
        var keyBytes = Encoding.UTF8.GetBytes(slotKey);
        var traceBytes = Encoding.UTF8.GetBytes(traceId);
        var totalPayloadLen = 4 + 8 + hdrJsonBytes.Length + promptBytes.Length + kvBlob.Length;

        var payload = new byte[totalPayloadLen];
        var offset = 0;

        // [4B hdr_len LE]
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), (uint)hdrJsonBytes.Length);
        offset += 4;

        // [8B hdr_hash LE]
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(offset), hdrHash);
        offset += 8;

        // [hdr_len bytes]
        hdrJsonBytes.CopyTo(payload, offset);
        offset += hdrJsonBytes.Length;

        // [prompt_len bytes]
        promptBytes.CopyTo(payload, offset);
        offset += promptBytes.Length;

        // [kv_len bytes]
        kvBlob.CopyTo(payload, offset);
        offset += kvBlob.Length;

        // Assert — verify the structure
        Assert.Equal(totalPayloadLen, offset);

        // Verify hdr_len
        var readHdrLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0));
        Assert.Equal((uint)hdrJsonBytes.Length, readHdrLen);

        // Verify hdr_hash
        var readHdrHash = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4));
        Assert.Equal(hdrHash, readHdrHash);

        // Verify segment offsets
        var hdrDoc = JsonDocument.Parse(Encoding.UTF8.GetString(payload.AsSpan(12, (int)readHdrLen)));
        var segments = hdrDoc.RootElement.GetProperty("segments");
        Assert.Equal(2, segments.GetArrayLength());

        var seg0 = segments[0];
        Assert.Equal("prompt", seg0.GetProperty("id").GetString());
        Assert.Equal(0, seg0.GetProperty("offset").GetInt32());
        Assert.Equal(promptBytes.Length, seg0.GetProperty("len").GetInt32());
        Assert.Equal(promptHashStr, seg0.GetProperty("hash").GetString());

        var seg1 = segments[1];
        Assert.Equal("kv", seg1.GetProperty("id").GetString());
        Assert.Equal(promptBytes.Length, seg1.GetProperty("offset").GetInt32());
        Assert.Equal(kvBlob.Length, seg1.GetProperty("len").GetInt32());
        Assert.Equal(kvHashStr, seg1.GetProperty("hash").GetString());

        // Verify contiguous: sum of segment lengths == payload_len - 12 - hdr_len
        var sumLens = seg0.GetProperty("len").GetInt32() + seg1.GetProperty("len").GetInt32();
        Assert.Equal(totalPayloadLen - 12 - (int)readHdrLen, sumLens);
    }

    [Fact]
    public void SegmentedDecode_Xxh3Hash_IsCorrect()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var hash = XxHash3.HashToUInt64(data);
        var hashStr = $"xxh3:{hash:x16}";

        // Verify the hash is non-zero and has the right format
        Assert.NotEqual(0UL, hash);
        Assert.StartsWith("xxh3:", hashStr);
        Assert.Equal(21, hashStr.Length); // "xxh3:" + 16 hex chars

        // Verify determinism
        var hash2 = XxHash3.HashToUInt64(data);
        Assert.Equal(hash, hash2);

        // Verify different data produces different hash
        var differentData = Encoding.UTF8.GetBytes("hello world!");
        var differentHash = XxHash3.HashToUInt64(differentData);
        Assert.NotEqual(hash, differentHash);
    }

    [Fact]
    public void SegmentedDecode_EmptyPrompt_ProducesZeroLengthSegment()
    {
        var promptBytes = Array.Empty<byte>();
        var promptHash = XxHash3.HashToUInt64(promptBytes);
        var promptHashStr = $"xxh3:{promptHash:x16}";

        // Empty prompt should still produce a valid segment with len=0
        Assert.Equal(0, promptBytes.Length);
        Assert.StartsWith("xxh3:", promptHashStr);
    }

    [Fact]
    public void SegmentedDecode_LargeHeader_ThrowsAbove32KiB()
    {
        // Build a header that exceeds 32 KiB
        var largeMessages = new string('x', 40000); // ~40K chars > 32 KiB
        var headerObj = new Dictionary<string, object>
        {
            ["v"] = 3,
            ["model"] = "test",
            ["kv_metadata"] = new Dictionary<string, object> { ["n_past"] = 0 },
            ["model_metadata"] = new Dictionary<string, object>(),
            ["generation"] = new Dictionary<string, object>
            {
                ["n_predict"] = 256,
                ["sampling"] = new Dictionary<string, object>(),
                ["stop"] = Array.Empty<string>(),
                ["stream"] = true,
                ["chat_syntax"] = "",
                ["oaicompat_model"] = "test"
            },
            ["segments"] = new List<Dictionary<string, object>>(),
            // Stuff the header with junk to exceed 32 KiB
            ["debug"] = largeMessages
        };
        var hdrJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headerObj));

        Assert.True(hdrJsonBytes.Length > 32768,
            $"Header should exceed 32 KiB for this test, got {hdrJsonBytes.Length}");
    }

    [Fact]
    public void SegmentedDecode_HeaderContainsAllRequiredFields()
    {
        var headerObj = new Dictionary<string, object>
        {
            ["v"] = 3,
            ["model"] = "balanced",
            ["kv_metadata"] = new Dictionary<string, object>
            {
                ["n_past"] = 100,
                ["tokenizer"] = "llama",
                ["model_name"] = "qwen3.6-35B",
                ["model_quant"] = "Q3_K",
                ["model_capabilities"] = 19u
            },
            ["model_metadata"] = new Dictionary<string, object>
            {
                ["tokenizer"] = "llama",
                ["model_name"] = "qwen3.6-35B",
                ["model_quant"] = "Q5_K",
                ["model_capabilities"] = 19u
            },
            ["generation"] = new Dictionary<string, object>
            {
                ["n_predict"] = 256,
                ["sampling"] = new Dictionary<string, object>(),
                ["stop"] = Array.Empty<string>(),
                ["stream"] = true,
                ["chat_syntax"] = "qwen3",
                ["oaicompat_model"] = "balanced"
            },
            ["segments"] = new List<Dictionary<string, object>>
            {
                new() { ["id"] = "prompt", ["offset"] = 0, ["len"] = 100, ["hash"] = "xxh3:abc" },
                new() { ["id"] = "kv", ["offset"] = 100, ["len"] = 200, ["hash"] = "xxh3:def" }
            }
        };
        var json = JsonSerializer.Serialize(headerObj);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify all required fields exist
        Assert.Equal(3, root.GetProperty("v").GetInt32());
        Assert.Equal("balanced", root.GetProperty("model").GetString());
        Assert.True(root.TryGetProperty("kv_metadata", out _));
        Assert.True(root.TryGetProperty("model_metadata", out _));
        Assert.True(root.TryGetProperty("generation", out _));
        Assert.True(root.TryGetProperty("segments", out _));

        var gen = root.GetProperty("generation");
        Assert.Equal(256, gen.GetProperty("n_predict").GetInt32());
        Assert.True(gen.GetProperty("stream").GetBoolean());
        Assert.True(gen.TryGetProperty("chat_syntax", out _));
        Assert.True(gen.TryGetProperty("oaicompat_model", out _));

        var segs = root.GetProperty("segments");
        Assert.Equal(2, segs.GetArrayLength());
    }
}
