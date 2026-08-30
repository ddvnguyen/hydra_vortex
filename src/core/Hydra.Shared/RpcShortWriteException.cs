namespace Hydra.Shared;

/// <summary>
/// #716: Thrown when an RPC sender detects a byte-count mismatch between the
/// declared payload length in the request header and the actual bytes available
/// or written. Subclasses InvalidOperationException so existing catch blocks
/// that handle general exceptions still work; callers that need to distinguish
/// this failure mode catch this type explicitly.
/// </summary>
public sealed class RpcShortWriteException : InvalidOperationException
{
    public string Op { get; }
    public long Declared { get; }
    public long Written { get; }
    public int TotalShortWrites { get; }

    public RpcShortWriteException(
        string op, long declared, long written, int totalShortWrites)
        : base($"RPC {op} short write: declared {declared} bytes, wrote {written} " +
               $"({totalShortWrites} total short writes)")
    {
        Op = op;
        Declared = declared;
        Written = written;
        TotalShortWrites = totalShortWrites;
    }

    public RpcShortWriteException(
        string op, string host, int port, long declared, long written, int totalShortWrites)
        : base($"RPC {op} short write to {host}:{port}: declared {declared} bytes, " +
               $"wrote {written} ({totalShortWrites} total short writes)")
    {
        Op = op;
        Declared = declared;
        Written = written;
        TotalShortWrites = totalShortWrites;
    }
}
