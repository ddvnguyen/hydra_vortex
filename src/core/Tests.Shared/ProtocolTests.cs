using System.Runtime.InteropServices;
using Hydra.Shared;

namespace Tests.Shared;

public class ProtocolTests
{
    [Fact]
    public void RequestHeader_Size_Is16Bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<RequestHeader>());
        Assert.Equal(16, Protocol.REQUEST_HEADER_SIZE);
    }

    [Fact]
    public void ResponseHeader_Size_Is12Bytes()
    {
        Assert.Equal(12, Protocol.RESPONSE_HEADER_SIZE);
    }

    [Fact]
    public void RoundTrip_RequestHeader_AllFields()
    {
        var header = new RequestHeader(
            Magic: 0x4859,
            Op: 0x01,
            Flags: 0x80,
            KeyLen: 5,
            PayloadLen: 1_000_000_000,
            TraceLen: 10
        );

        Span<byte> buf = stackalloc byte[16];
        Protocol.WriteRequest(buf, header);
        var parsed = Protocol.ReadRequest(buf);

        Assert.Equal(header.Magic, parsed.Magic);
        Assert.Equal(header.Op, parsed.Op);
        Assert.Equal(header.Flags, parsed.Flags);
        Assert.Equal(header.KeyLen, parsed.KeyLen);
        Assert.Equal(header.PayloadLen, parsed.PayloadLen);
        Assert.Equal(header.TraceLen, parsed.TraceLen);
    }

    [Fact]
    public void RoundTrip_RequestHeader_MaxValues()
    {
        var header = new RequestHeader(
            Magic: 0x4859,
            Op: 0xFF,
            Flags: 0xFF,
            KeyLen: ushort.MaxValue,
            PayloadLen: ulong.MaxValue,
            TraceLen: ushort.MaxValue
        );

        Span<byte> buf = stackalloc byte[16];
        Protocol.WriteRequest(buf, header);
        var parsed = Protocol.ReadRequest(buf);

        Assert.Equal(0x4859, parsed.Magic);
        Assert.Equal(0xFF, parsed.Op);
        Assert.Equal(0xFF, parsed.Flags);
        Assert.Equal(ushort.MaxValue, parsed.KeyLen);
        Assert.Equal(ulong.MaxValue, parsed.PayloadLen);
        Assert.Equal(ushort.MaxValue, parsed.TraceLen);
    }

    [Fact]
    public void RoundTrip_RequestHeader_ZeroValues()
    {
        var header = new RequestHeader(0, 0, 0, 0, 0, 0);

        Span<byte> buf = stackalloc byte[16];
        Protocol.WriteRequest(buf, header);
        var parsed = Protocol.ReadRequest(buf);

        Assert.Equal(0u, parsed.Magic);
        Assert.Equal(0, parsed.Op);
        Assert.Equal(0, parsed.Flags);
        Assert.Equal(0, parsed.KeyLen);
        Assert.Equal(0ul, parsed.PayloadLen);
        Assert.Equal(0, parsed.TraceLen);
    }

    [Fact]
    public void RoundTrip_ResponseHeader_AllFields()
    {
        var header = new ResponseHeader(
            Status: 0x00,
            MetaLen: 500,
            PayloadLen: 2_000_000_000
        );

        Span<byte> buf = stackalloc byte[12];
        Protocol.WriteResponse(buf, header.Status, header.MetaLen, header.PayloadLen);
        var parsed = Protocol.ReadResponse(buf);

        Assert.Equal(header.Status, parsed.Status);
        Assert.Equal(header.MetaLen, parsed.MetaLen);
        Assert.Equal(header.PayloadLen, parsed.PayloadLen);
    }

    [Fact]
    public void RoundTrip_ResponseHeader_MaxMetaLen()
    {
        // 3 bytes max = 0xFFFFFF = 16,777,215
        const uint maxMetaLen = 0xFF_FFFF;

        Span<byte> buf = stackalloc byte[12];
        Protocol.WriteResponse(buf, 0x00, maxMetaLen, 100);
        var parsed = Protocol.ReadResponse(buf);

        Assert.Equal(maxMetaLen, parsed.MetaLen);
    }

    [Fact]
    public void RoundTrip_ResponseHeader_MaxPayloadLen()
    {
        Span<byte> buf = stackalloc byte[12];
        Protocol.WriteResponse(buf, 0x02, 0, ulong.MaxValue);
        var parsed = Protocol.ReadResponse(buf);

        Assert.Equal(ulong.MaxValue, parsed.PayloadLen);
    }

    [Fact]
    public void CreateRequestHeader_ProducesCorrectBytes()
    {
        var header = Protocol.CreateRequestHeader(OpCode.Put, 3, 100, 4);

        Assert.Equal(Protocol.MAGIC, header.Magic);
        Assert.Equal((byte)OpCode.Put, header.Op);
        Assert.Equal(0, header.Flags);
        Assert.Equal(3, header.KeyLen);
        Assert.Equal(100ul, header.PayloadLen);
        Assert.Equal(4, header.TraceLen);
    }

    [Fact]
    public void WriteResponse_EncodesThreeByteMetaLen()
    {
        Span<byte> buf = stackalloc byte[12];

        // MetaLen = 0xABCDEF
        Protocol.WriteResponse(buf, 0x01, 0xABCDEF, 0);

        // Check individual bytes are LE
        Assert.Equal(0x01, buf[0]);                 // status
        Assert.Equal(0xEF, buf[1]);                 // meta_len LSB
        Assert.Equal(0xCD, buf[2]);                 // meta_len byte 1
        Assert.Equal(0xAB, buf[3]);                 // meta_len byte 2
        Assert.Equal(0x00, buf[4]);                 // payload_len starts
    }

    [Fact]
    public void SerializeKey_EncodesUtf8()
    {
        var key = "kv/test-key";
        var bytes = Protocol.SerializeKey(key);
        Assert.Equal(key, System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void OpCode_Values_MatchSpec()
    {
        Assert.Equal(0x01, (byte)OpCode.Put);
        Assert.Equal(0x02, (byte)OpCode.Get);
        Assert.Equal(0x03, (byte)OpCode.Del);
        Assert.Equal(0x04, (byte)OpCode.Stat);
        Assert.Equal(0x05, (byte)OpCode.List);
        Assert.Equal(0x10, (byte)OpCode.PutChunked);
        Assert.Equal(0x11, (byte)OpCode.GetChunked);
        Assert.Equal(0x12, (byte)OpCode.SyncMissing);
        Assert.Equal(0x15, (byte)OpCode.PutManifest);
        Assert.Equal(0x13, (byte)OpCode.PushChunks);
        Assert.Equal(0x14, (byte)OpCode.PutMeta);
        Assert.Equal(0x20, (byte)OpCode.SaveState);
        Assert.Equal(0x21, (byte)OpCode.RestoreState);
        Assert.Equal(0x22, (byte)OpCode.SlotStatus);
        Assert.Equal(0x23, (byte)OpCode.SlotErase);
        Assert.Equal(0x24, (byte)OpCode.NodeHealth);
        Assert.Equal(0x26, (byte)OpCode.SaveStateChunked);
        Assert.Equal(0x27, (byte)OpCode.RestoreStateChunked);
        Assert.Equal(0x30, (byte)OpCode.StateGet);
        Assert.Equal(0x31, (byte)OpCode.StatePut);
        Assert.Equal(0x32, (byte)OpCode.StateMeta);
        Assert.Equal(0x33, (byte)OpCode.GetManifest);
        Assert.Equal(0x40, (byte)OpCode.EngineConfigure);
        Assert.Equal(0x41, (byte)OpCode.EngineInfo);
        Assert.Equal(0x42, (byte)OpCode.EnginePrefill);
        Assert.Equal(0x43, (byte)OpCode.EngineDecode);
        Assert.Equal(0x44, (byte)OpCode.EngineSetExpertMode);
        Assert.Equal(0x45, (byte)OpCode.EngineSwapQuant);
        Assert.Equal(0x46, (byte)OpCode.EnginePipelineAttach);
    }

    [Fact]
    public void OpCode_EnginePipelineAttach_IsAt0x46()
    {
        // M-Perf.9 (#289): two-engine "work together" attach. The opcode
        // lives in the same 0x40-0x46 range as the rest of the engine
        // control plane; the C++ side stubs the handler with
        // NOT_IMPLEMENTED until issue #287 lands.
        Assert.Equal(0x46, (byte)OpCode.EnginePipelineAttach);
    }

    [Fact]
    public void OpCode_EngineOpcodes_DoNotCollideWithStoreOrAgentRanges()
    {
        // Guard: the engine block (0x40-0x45) was deliberately placed after
        // GetManifest (0x33) and before the 0x80+ reserved band. A future
        // edit that reuses 0x33-0x3F for a different layer would silently
        // alias the engine block on the wire.
        Assert.True((byte)OpCode.EngineConfigure >= 0x40);
        Assert.True((byte)OpCode.EngineSwapQuant <= 0x45);
    }

    [Fact]
    public void StatusCode_Values_MatchSpec()
    {
        Assert.Equal(0x00, (byte)StatusCode.Ok);
        Assert.Equal(0x01, (byte)StatusCode.NotFound);
        Assert.Equal(0x02, (byte)StatusCode.Error);
        Assert.Equal(0x03, (byte)StatusCode.Partial);
        Assert.Equal(0x04, (byte)StatusCode.Busy);
    }
}
