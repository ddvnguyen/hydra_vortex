using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Hydra.Shared;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct RequestHeader(
    ushort Magic,       // 0x4859
    byte   Op,
    byte   Flags,
    ushort KeyLen,
    ulong  PayloadLen,
    ushort TraceLen
);

public readonly record struct ResponseHeader(
    byte  Status,
    uint  MetaLen,
    ulong PayloadLen
);

public enum OpCode : byte
{
    Put           = 0x01,
    Get           = 0x02,
    Del           = 0x03,
    Stat          = 0x04,
    List          = 0x05,
    PutChunked    = 0x10,
    GetChunked    = 0x11,
    SyncMissing   = 0x12,  // delta-save: which of these hashes does the store lack? (was SyncPlan)
    PushChunks    = 0x13,
    PutMeta       = 0x14,
    PutManifest   = 0x15,  // delta-save: write the authoritative ordered manifest
    SaveState     = 0x20,
    RestoreState  = 0x21,
    SlotStatus    = 0x22,
    SlotErase     = 0x23,
    NodeHealth    = 0x24,
    // 0x25 retired: completions go Coordinator→llama-server over HTTP, not via Agent RPC.
    SaveStateChunked    = 0x26,
    RestoreStateChunked = 0x27,
    StateGet      = 0x30,
    StatePut      = 0x31,
    StateMeta     = 0x32,
    GetManifest   = 0x33,
    EngineConfigure = 0x40,
    EngineInfo      = 0x41,
    EnginePrefill   = 0x42,
    EngineDecode    = 0x43,
    EngineSetExpertMode = 0x44,
    EngineSwapQuant = 0x45,
}

public enum StatusCode : byte
{
    Ok         = 0x00,
    NotFound   = 0x01,
    Error      = 0x02,
    BadRequest = 0x05,
    Partial    = 0x03,
    Busy       = 0x04,
}

public static class Protocol
{
    public const ushort MAGIC = 0x4859;
    public const int REQUEST_HEADER_SIZE  = 16;
    public const int RESPONSE_HEADER_SIZE = 12;

    public static RequestHeader ReadRequest(ReadOnlySpan<byte> buf)
    {
        return MemoryMarshal.Read<RequestHeader>(buf);
    }

    public static void WriteRequest(Span<byte> buf, RequestHeader header)
    {
        MemoryMarshal.Write(buf, in header);
    }

    public static ResponseHeader ReadResponse(ReadOnlySpan<byte> buf)
    {
        var status = buf[0];
        var metaLen = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16));
        var payloadLen = BinaryPrimitives.ReadUInt64LittleEndian(buf[4..]);
        return new ResponseHeader(status, metaLen, payloadLen);
    }

    public static void WriteResponse(Span<byte> buf, byte status, uint metaLen, ulong payloadLen)
    {
        buf[0] = status;
        buf[1] = (byte)(metaLen & 0xFF);
        buf[2] = (byte)((metaLen >> 8) & 0xFF);
        buf[3] = (byte)((metaLen >> 16) & 0xFF);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[4..], payloadLen);
    }

    public static byte[] SerializeKey(string key)
    {
        return Encoding.UTF8.GetBytes(key);
    }

    public static byte[] SerializeTraceId(string traceId)
    {
        return Encoding.UTF8.GetBytes(traceId);
    }

    public static RequestHeader CreateRequestHeader(OpCode op, ushort keyLen, ulong payloadLen, ushort traceLen, byte flags = 0)
    {
        return new RequestHeader(MAGIC, (byte)op, flags, keyLen, payloadLen, traceLen);
    }
}
