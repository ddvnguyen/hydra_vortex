using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Runtime.CompilerServices;

namespace Hydra.Shared;

public sealed class RpcClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly object _connectLock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    private static readonly int[] RetryDelays = [100, 500, 2000];

    public RpcClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct);

        lock (_connectLock)
        {
            if (_disposed)
            {
                client.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }

            var oldClient = _client;
            _client = client;
            _stream = client.GetStream();
            oldClient?.Dispose();
        }
    }

    public async Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            return await SendAndReceiveAsync(op, key, payload, traceId, ct);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<RpcResponse> RequestStreamBodyAsync(
        OpCode op, string key, Stream body, long bodyLen,
        string traceId, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            return await SendAndReceiveStreamBodyAsync(op, key, body, bodyLen, traceId, ct);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async IAsyncEnumerable<byte[]> RequestStreamAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            await SendRequestAsync(op, key, payload, traceId, ct);

            var headerBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
            await ReadExactAsync(_stream!, headerBuf, ct);

            var header = Protocol.ReadResponse(headerBuf);
            if (header.Status != (byte)StatusCode.Ok)
            {
                var meta = await ReadMetaAsync(_stream!, header.MetaLen, ct);
                throw new InvalidDataException(
                    $"RPC error (status=0x{header.Status:X2}): {meta}");
            }

            if (header.MetaLen > 0)
            {
                var metaBuf = new byte[header.MetaLen];
                await ReadExactAsync(_stream!, metaBuf, ct);
            }

            var remaining = (long)header.PayloadLen;
            var buf = new byte[65536];

            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buf.Length, remaining);
                var read = await _stream!.ReadAsync(buf.AsMemory(0, toRead), ct);
                if (read == 0)
                    throw new EndOfStreamException("Connection closed while reading stream response");
                remaining -= read;
                yield return buf[..read];
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<RpcResponse> SendAndReceiveAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct)
    {
        var attempts = 0;

        while (true)
        {
            try
            {
                await EnsureConnectedAsync(ct);
                await SendRequestAsync(op, key, payload, traceId, ct);

                var headerBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
                await ReadExactAsync(_stream!, headerBuf, ct);

                var header = Protocol.ReadResponse(headerBuf);
                var meta = header.MetaLen > 0
                    ? await ReadMetaAsync(_stream!, header.MetaLen, ct)
                    : null;

                var payloadBytes = header.PayloadLen > 0
                    ? await ReadPayloadAsync(_stream!, (long)header.PayloadLen, ct)
                    : [];

                return new RpcResponse(header.Status, meta, payloadBytes);
            }
            catch (IOException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                await Task.Delay(RetryDelays[attempts - 1], ct);
                await ReconnectAsync(ct);
            }
            catch (EndOfStreamException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                await Task.Delay(RetryDelays[attempts - 1], ct);
                await ReconnectAsync(ct);
            }
            catch (SocketException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                await Task.Delay(RetryDelays[attempts - 1], ct);
                await ReconnectAsync(ct);
            }
        }
    }

    private async Task<RpcResponse> SendAndReceiveStreamBodyAsync(
        OpCode op, string key, Stream body, long bodyLen,
        string traceId, CancellationToken ct)
    {
        var attempts = 0;

        while (true)
        {
            try
            {
                await EnsureConnectedAsync(ct);

                var keyBytes = Encoding.UTF8.GetBytes(key);
                var traceBytes = Encoding.UTF8.GetBytes(traceId);

                var header = Protocol.CreateRequestHeader(
                    op, (ushort)keyBytes.Length, (ulong)bodyLen, (ushort)traceBytes.Length);

                var headerBuf = new byte[Protocol.REQUEST_HEADER_SIZE];
                Protocol.WriteRequest(headerBuf, header);

                await _stream!.WriteAsync(headerBuf, ct);
                if (keyBytes.Length > 0)
                    await _stream.WriteAsync(keyBytes, ct);
                if (traceBytes.Length > 0)
                    await _stream.WriteAsync(traceBytes, ct);
                await _stream.FlushAsync(ct);

                var buffer = new byte[65536];
                long remaining = bodyLen;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await body.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    if (read == 0)
                        throw new EndOfStreamException(
                            $"Stream ended early ({remaining} bytes remaining)");
                    await _stream.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
                await _stream.FlushAsync(ct);

                var responseHeaderBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
                await ReadExactAsync(_stream, responseHeaderBuf, ct);
                var rh = Protocol.ReadResponse(responseHeaderBuf);
                var meta = rh.MetaLen > 0
                    ? await ReadMetaAsync(_stream, rh.MetaLen, ct)
                    : null;
                var payload = rh.PayloadLen > 0
                    ? await ReadPayloadAsync(_stream, (long)rh.PayloadLen, ct)
                    : [];

                return new RpcResponse(rh.Status, meta, payload);
            }
            catch (IOException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                if (attempts < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempts - 1], ct);
                    await ReconnectAsync(ct);
                }
                else
                {
                    throw;
                }
            }
            catch (EndOfStreamException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                if (attempts < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempts - 1], ct);
                    await ReconnectAsync(ct);
                }
                else
                {
                    throw;
                }
            }
            catch (SocketException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                if (attempts < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempts - 1], ct);
                    await ReconnectAsync(ct);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private async Task SendRequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var traceBytes = Encoding.UTF8.GetBytes(traceId);

        var header = Protocol.CreateRequestHeader(
            op, (ushort)keyBytes.Length, (ulong)payload.Length, (ushort)traceBytes.Length);

        var headerBuf = new byte[Protocol.REQUEST_HEADER_SIZE];
        Protocol.WriteRequest(headerBuf, header);

        await _stream!.WriteAsync(headerBuf, ct);
        if (keyBytes.Length > 0)
            await _stream.WriteAsync(keyBytes, ct);
        if (traceBytes.Length > 0)
            await _stream.WriteAsync(traceBytes, ct);
        if (payload.Length > 0)
            await _stream.WriteAsync(payload, ct);

        await _stream.FlushAsync(ct);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { Connected: true })
            return;
        await ConnectAsync(ct);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        lock (_connectLock)
        {
            var oldClient = _client;
            _client = null;
            _stream = null;
            oldClient?.Dispose();
        }
        await ConnectAsync(ct);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new EndOfStreamException("Connection closed by peer");
            offset += read;
        }
    }

    private static async Task<string> ReadMetaAsync(NetworkStream stream, uint metaLen, CancellationToken ct)
    {
        var buf = new byte[metaLen];
        await ReadExactAsync(stream, buf, ct);
        return Encoding.UTF8.GetString(buf);
    }

    private static async Task<byte[]> ReadPayloadAsync(NetworkStream stream, long payloadLen, CancellationToken ct)
    {
        var buf = new byte[payloadLen];
        await ReadExactAsync(stream, buf, ct);
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_connectLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _sync.Dispose();

        if (_stream is not null)
            await _stream.DisposeAsync();

        _client?.Dispose();
    }
}
