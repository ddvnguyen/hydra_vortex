using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Runtime.CompilerServices;

namespace Hydra.Shared;

public class RpcClient : IAsyncDisposable
{
    internal readonly string _host;
    internal readonly int _port;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly object _connectLock = new();
    private readonly TimeSpan _requestTimeout;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    private static readonly int[] RetryDelays = [100, 500, 2000];

    /// <summary>Default per-request timeout. Bounds the whole request (semaphore wait,
    /// connect, send, receive) so a wedged peer cannot poison the shared connection
    /// forever — callers passing CancellationToken.None are still protected.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(180);

    public RpcClient(string host, int port, TimeSpan? requestTimeout = null)
    {
        _host = host;
        _port = port;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
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

    public virtual async Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        await WaitForTurnAsync(op, timeoutCts.Token, ct);
        try
        {
            return await SendAndReceiveAsync(op, key, payload, traceId, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-request: the wire may hold a half-written request or a
            // half-read response — the persistent connection is desynced. Drop it
            // so the next request starts on a fresh socket instead of misframing.
            DropConnection();
            if (!ct.IsCancellationRequested)
                throw NewTimeout(op);
            throw;
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        await WaitForTurnAsync(op, timeoutCts.Token, ct);
        try
        {
            return await SendAndReceiveStreamBodyAsync(op, key, body, bodyLen, traceId, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            DropConnection(); // mid-request cancel — connection desynced
            if (!ct.IsCancellationRequested)
                throw NewTimeout(op);
            throw;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task WaitForTurnAsync(OpCode op, CancellationToken linkedToken, CancellationToken callerCt)
    {
        try
        {
            await _sync.WaitAsync(linkedToken);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            // Timed out waiting for the in-flight request on this connection —
            // no I/O of ours started, so the connection itself is left alone.
            throw NewTimeout(op);
        }
    }

    private TimeoutException NewTimeout(OpCode op) =>
        new($"RPC {op} to {_host}:{_port} timed out after {_requestTimeout.TotalSeconds:F0}s");

    public async IAsyncEnumerable<byte[]> RequestStreamAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);
        var token = timeoutCts.Token;

        await WaitForTurnAsync(op, token, ct);
        var completed = false;
        try
        {
            await EnsureConnectedAsync(token);
            await SendRequestAsync(op, key, payload, traceId, token);

            var headerBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
            await ReadExactAsync(_stream!, headerBuf, token);

            var header = Protocol.ReadResponse(headerBuf);
            if (header.Status != (byte)StatusCode.Ok)
            {
                var meta = await ReadMetaAsync(_stream!, header.MetaLen, token);
                completed = true; // error frame fully consumed — connection still in sync
                throw new InvalidDataException(
                    $"RPC error (status=0x{header.Status:X2}): {meta}");
            }

            if (header.MetaLen > 0)
            {
                var metaBuf = new byte[header.MetaLen];
                await ReadExactAsync(_stream!, metaBuf, token);
            }

            // For streaming RPC (EngineDecode), PayloadLen=0 and tokens are streamed
            // as 4-byte length + N-byte token until connection is closed.
            // For non-streaming RPC, PayloadLen > 0 and we read that many bytes.
            if (header.PayloadLen > 0)
            {
                var remaining = (long)header.PayloadLen;
                var buf = new byte[65536];

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buf.Length, remaining);
                    var read = await _stream!.ReadAsync(buf.AsMemory(0, toRead), token);
                    if (read == 0)
                        throw new EndOfStreamException("Connection closed while reading stream response");
                    remaining -= read;
                    yield return buf[..read];
                }
            }
            else
            {
                // Streaming RPC: read tokens as 4-byte length + N-byte token
                var lenBuf = new byte[4];
                while (true)
                {
                    // Read 4-byte length
                    var lenRead = await _stream!.ReadAsync(lenBuf.AsMemory(0, 4), token);
                    if (lenRead == 0)
                        break; // Connection closed
                    if (lenRead < 4)
                        throw new EndOfStreamException("Incomplete token length");

                    var tokenLen = BitConverter.ToUInt32(lenBuf, 0);
                    if (tokenLen == 0)
                        continue; // Skip empty tokens

                    // Read token bytes
                    var tokenBuf = new byte[tokenLen];
                    var tokenRead = await _stream!.ReadAsync(tokenBuf.AsMemory(0, (int)tokenLen), token);
                    if (tokenRead < tokenLen)
                        throw new EndOfStreamException("Incomplete token data");

                    yield return tokenBuf;
                }
            }

            completed = true;
        }
        finally
        {
            // Incomplete exit (timeout, error, or caller abandoning the enumeration
            // mid-stream) leaves unread payload bytes on the wire — the persistent
            // connection is desynced and must be dropped.
            if (!completed)
                DropConnection();
            _sync.Release();

            if (!completed && token.IsCancellationRequested && !ct.IsCancellationRequested)
                throw NewTimeout(op);
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
        DropConnection();
        await ConnectAsync(ct);
    }

    /// <summary>Dispose the current connection without reconnecting. Used when the
    /// stream may be desynced (partial request/response on the wire); the next
    /// request re-establishes a clean connection via EnsureConnectedAsync.</summary>
    private void DropConnection()
    {
        lock (_connectLock)
        {
            var oldClient = _client;
            _client = null;
            _stream = null;
            oldClient?.Dispose();
        }
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

    public async Task<RpcResponse> EngineInfoAsync(string slotKey, string traceId, CancellationToken ct)
        => await RequestAsync(OpCode.EngineInfo, slotKey, ReadOnlyMemory<byte>.Empty, traceId, ct);

    public async Task<RpcResponse> EngineConfigureAsync(string slotKey, string configJson, string traceId, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(configJson);
        return await RequestAsync(OpCode.EngineConfigure, slotKey, payload, traceId, ct);
    }

    public async Task<RpcResponse> EnginePrefillAsync(string slotKey, string requestJson, string traceId, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(requestJson);
        return await RequestAsync(OpCode.EnginePrefill, slotKey, payload, traceId, ct);
    }

    public async Task<RpcResponse> EngineDecodeAsync(string slotKey, int nPredict, string? requestJson, string traceId, CancellationToken ct)
    {
        // Build JSON payload: {"n_predict": N, "messages": [...] or null}
        var json = $"{{\"n_predict\":{nPredict},\"messages\":{requestJson ?? "null"}}}";
        var payload = Encoding.UTF8.GetBytes(json);
        return await RequestAsync(OpCode.EngineDecode, slotKey, payload, traceId, ct);
    }

    public async IAsyncEnumerable<byte[]> EngineDecodeStreamAsync(
        string slotKey, int nPredict, string? requestJson, string traceId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Build JSON payload: {"n_predict": N, "messages": [...] or null}
        var json = $"{{\"n_predict\":{nPredict},\"messages\":{requestJson ?? "null"}}}";
        var payload = Encoding.UTF8.GetBytes(json);
        await foreach (var chunk in RequestStreamAsync(OpCode.EngineDecode, slotKey, payload, traceId, ct))
            yield return chunk;
    }

    public async Task<RpcResponse> EngineSetExpertModeAsync(string slotKey, string mode, string traceId, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(mode);
        return await RequestAsync(OpCode.EngineSetExpertMode, slotKey, payload, traceId, ct);
    }

    public async Task<RpcResponse> EngineSwapQuantAsync(string slotKey, string quantKey, string tensorPattern, string traceId, CancellationToken ct)
    {
        var quantKeyBytes = Encoding.UTF8.GetBytes(quantKey);
        var patternBytes = Encoding.UTF8.GetBytes(tensorPattern);
        var quantKeyLenBytes = BitConverter.GetBytes((ushort)quantKeyBytes.Length);
        var payload = new byte[quantKeyLenBytes.Length + quantKeyBytes.Length + patternBytes.Length];
        quantKeyLenBytes.CopyTo(payload, 0);
        quantKeyBytes.CopyTo(payload, quantKeyLenBytes.Length);
        patternBytes.CopyTo(payload, quantKeyLenBytes.Length + quantKeyBytes.Length);
        return await RequestAsync(OpCode.EngineSwapQuant, slotKey, payload, traceId, ct);
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
