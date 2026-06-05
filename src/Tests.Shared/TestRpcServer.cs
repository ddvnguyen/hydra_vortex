using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Hydra.Shared;

namespace Tests.Shared;

internal sealed class TestRpcServer : RpcServer
{
    public Func<OpCode, string, string, long, PipeReader, PipeWriter, CancellationToken, Task>? OnHandle { get; set; }

    public TestRpcServer(int port = 0)
        : base("127.0.0.1", port)
    {
    }

    protected override async Task HandleAsync(
        OpCode op, string key, string traceId, long payloadLen,
        PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
    {
        if (OnHandle != null)
        {
            await OnHandle(op, key, traceId, payloadLen, reader, writer, ct);
            return;
        }

        // Default: echo payload back
        var payload = payloadLen > 0
            ? await ReadPayloadAsync(reader, payloadLen, ct)
            : [];

        var meta = $$"""{"op":"{{op}}","key":"{{key}}","trace":"{{traceId}}"}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);

        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
        if (metaBytes.Length > 0)
        {
            var metaSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(metaSpan);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        }
        if (payload.Length > 0)
        {
            var paySpan = writer.GetSpan(payload.Length);
            payload.AsSpan().CopyTo(paySpan);
            writer.Advance(payload.Length);
            await writer.FlushAsync(ct);
        }
    }
}
