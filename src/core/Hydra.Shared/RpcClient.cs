using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    // #712 M2: holder tracing for _sync. The M2 stall was a threadless ghost —
    // an async state machine suspended on an unbounded read holding the
    // semaphore forever (dotnet-stack showed no thread; only a parked await).
    // Recording who holds _sync and logging the holder when a waiter times out
    // names any future ghost permanently in the logs.
    private static readonly Serilog.ILogger _log = Serilog.Log.ForContext<RpcClient>();

    private sealed record SyncHolder(OpCode Op, string Key, string TraceId, DateTime AcquiredUtc);
    private volatile SyncHolder? _syncHolder;

    /// <summary>#712 M2: clears the holder record and releases the turn. Every
    /// request path's finally block must use this (never a bare _sync.Release)
    /// so a holder can never outlive its request.</summary>
    private void EndHold()
    {
        _syncHolder = null;
        _sync.Release();
    }

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

        // #470: aggressive TCP keepalive bounds half-open socket detection to
        // ~15s when the connection is idle (head container redeploys replace the
        // peer without FIN/RST through pasta forwarding). Data flow resets the
        // idle timer, so long prefill streams are unaffected.
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        if (OperatingSystem.IsLinux())
        {
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 2);
        }

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
        => await RequestAsync(op, key, payload, traceId, ct,
            requestTimeoutOverride: null, payloadIdleBudget: null);

    /// <summary>
    /// #470: overload with a raised ceiling + idle-based payload budget for
    /// PREFILL (large KV responses). VIRTUAL on purpose: the 5-arg virtual above
    /// delegates here, so test doubles override THIS signature — a single
    /// interception point covers both call paths (EnginePrefillAsync goes
    /// straight through here; every other caller funnels through the 5-arg).
    /// </summary>
    public virtual async Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct,
        TimeSpan? requestTimeoutOverride,
        TimeSpan? payloadIdleBudget)
    {
        // #470: PREFILL needs a raised ceiling (compute ~175s at 28K tokens +
        // multi-GB KV transfer) AND an idle-based payload budget (see
        // ReadPayloadIdleAsync). All other callers keep the default 180s.
        var effectiveTimeout = requestTimeoutOverride ?? _requestTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        await WaitForTurnAsync(op, key, traceId, timeoutCts.Token, ct);
        try
        {
            return await SendAndReceiveAsync(op, key, payload, traceId,
                timeoutCts, effectiveTimeout, payloadIdleBudget, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-request: the wire may hold a half-written request or a
            // half-read response — the persistent connection is desynced. Drop it
            // so the next request starts on a fresh socket instead of misframing.
            DropConnection();
            if (!ct.IsCancellationRequested)
                throw NewTimeout(op, effectiveTimeout);
            throw;
        }
        catch (InvalidDataException)
        {
            // Framing error (e.g. response payload length out of range, #594): the
            // frame was rejected without consuming its body, so the persistent
            // socket is misaligned. Retrying on it reads garbage (observed: a
            // 272728361713580032-byte bogus length on the retry). Drop it so the
            // caller's retry logic re-requests on a fresh connection.
            DropConnection();
            throw;
        }
        catch (EndOfStreamException)
        {
            // Mid-response EOF: the peer closed after a partial frame, so socket
            // state is untrustworthy. Drop it; callers retry on a fresh connection.
            DropConnection();
            throw;
        }
        finally
        {
            EndHold();
        }
    }

    public virtual async Task<RpcResponse> RequestStreamBodyAsync(
        OpCode op, string key, Stream body, long bodyLen,
        string traceId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        await WaitForTurnAsync(op, key, traceId, timeoutCts.Token, ct);
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
        catch (InvalidDataException)
        {
            // Framing error — connection desynced, drop before rethrow (see RequestAsync).
            DropConnection();
            throw;
        }
        catch (EndOfStreamException)
        {
            // Mid-response EOF — connection state untrustworthy, drop before rethrow.
            DropConnection();
            throw;
        }
        finally
        {
            EndHold();
        }
    }

    private async Task WaitForTurnAsync(OpCode op, string key, string traceId,
        CancellationToken linkedToken, CancellationToken callerCt)
    {
        var waitStart = DateTime.UtcNow;
        try
        {
            await _sync.WaitAsync(linkedToken);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            // #470: timed out waiting behind the in-flight request. The holder may
            // itself be wedged on a dead/half-open socket; if it never errors, the
            // shared connection stays poisoned and every waiter times out in turn
            // (observed: 6x EnginePrefill timeouts, zero reconnects). Drop the
            // connection NOW: the holder's next I/O throws ODE, its finally
            // releases _sync, and the next request reconnects fresh. Trade-off: a
            // legitimately long concurrent request (cold prefill >180s) may be
            // aborted once and retried by its caller — cheap vs a permanent wedge.
            // #712 M2: name the holder — this line is the permanent record that
            // identifies a ghost (its op + how long it has held _sync), so the
            // next occurrence is diagnosable from logs alone.
            var waited = (DateTime.UtcNow - waitStart).TotalSeconds;
            var holder = _syncHolder;
            if (holder is null)
            {
                _log.Error("rpc_sync_wait_timeout peer={Peer} op={Op} key={Key} waited={Waited:F1}s — holder=NULL (semaphore count corrupted?); dropping connection",
                    $"{_host}:{_port}", op, key, waited);
            }
            else
            {
                _log.Error("rpc_sync_wait_timeout peer={Peer} op={Op} key={Key} waited={Waited:F1}s — holder: op={HolderOp} key={HolderKey} trace={HolderTrace} holding={Held:F1}s; dropping connection",
                    $"{_host}:{_port}", op, key, waited, holder.Op, holder.Key, holder.TraceId,
                    (DateTime.UtcNow - holder.AcquiredUtc).TotalSeconds);
            }
            DropConnection();
            throw NewTimeout(op);
        }

        // #712 M2: record the holder on acquire; log when the turn had to wait —
        // sustained contention is the precursor to a ghost holder.
        var waitedFor = (DateTime.UtcNow - waitStart).TotalSeconds;
        if (waitedFor >= 1.0)
            _log.Warning("rpc_sync_wait peer={Peer} op={Op} key={Key} waited={Waited:F1}s before acquiring _sync",
                $"{_host}:{_port}", op, key, waitedFor);
        _syncHolder = new SyncHolder(op, key, traceId, DateTime.UtcNow);
    }

    private TimeoutException NewTimeout(OpCode op, TimeSpan? effective = null)
    {
        var timeout = effective ?? _requestTimeout;
        return new($"RPC {op} to {_host}:{_port} timed out after {timeout.TotalSeconds:F0}s");
    }

    public async IAsyncEnumerable<byte[]> RequestStreamAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);
        var token = timeoutCts.Token;

        await WaitForTurnAsync(op, key, traceId, token, ct);
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
            // #595: apply the wire sanity bound here too — a garbage ulong length
            // (e.g. ulong.MaxValue → (long) = -1) previously slipped past
            // `remaining > 0` and silently yielded nothing while the peer sent a
            // body, desyncing the connection. Same contract as the other paths:
            // InvalidDataException → connection dropped in finally.
            ValidatePayloadLen((long)header.PayloadLen);
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

                    // #595: tokenLen is a raw uint from the wire — `new byte[tokenLen]`
                    // throws OverflowException for tokenLen > int.MaxValue (checked
                    // uint→int conversion), the same sliver class as the buffered
                    // payload reads, and bypasses the framing-error drop. No legit
                    // engine token is anywhere near 2 GiB; reject above the
                    // materializable bound so InvalidDataException (finally → drop) is
                    // the single failure path.
                    if (tokenLen > Array.MaxLength)
                        throw new InvalidDataException(
                            $"RPC stream token length exceeds the max materializable buffer (Array.MaxLength={Array.MaxLength} bytes): {tokenLen} bytes");

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
            EndHold();

            if (!completed && token.IsCancellationRequested && !ct.IsCancellationRequested)
                throw NewTimeout(op);
        }
    }

    private async Task<RpcResponse> SendAndReceiveAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationTokenSource timeoutCts, TimeSpan effectiveTimeout,
        TimeSpan? payloadIdleBudget, CancellationToken ct)
    {
        // #712 M2: ALL I/O runs on timeoutCts.Token, NOT the caller ct. The
        // ceiling (effectiveTimeout, default 180s) is linked with the caller
        // token, so a caller cancelling is still honored — but a caller passing
        // CancellationToken.None (12 store/engine call sites in
        // WorkerSchedulerService) is now actually bounded by the ceiling. Pre-fix
        // the header/meta/payload reads ran on the caller token: a request
        // parked on a non-responding peer held _sync forever (the M2 ghost),
        // clogging the chunked-PREFILL store push and stalling the engine.
        var attempts = 0;

        while (true)
        {
            try
            {
                await EnsureConnectedAsync(timeoutCts.Token);
                await SendRequestAsync(op, key, payload, traceId, timeoutCts.Token);

                var headerBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
                await ReadExactAsync(_stream!, headerBuf, timeoutCts.Token);

                var header = Protocol.ReadResponse(headerBuf);
                var meta = header.MetaLen > 0
                    ? await ReadMetaAsync(_stream!, header.MetaLen, timeoutCts.Token)
                    : null;

                var payloadBytes = header.PayloadLen > 0
                    ? payloadIdleBudget.HasValue
                        ? await ReadPayloadIdleAsync(_stream!, (long)header.PayloadLen,
                            timeoutCts, payloadIdleBudget.Value, ct)
                        : await ReadPayloadAsync(_stream!, (long)header.PayloadLen, timeoutCts.Token)
                    : [];

                return new RpcResponse(header.Status, meta, payloadBytes);
            }
            catch (IOException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                await Task.Delay(RetryDelays[attempts - 1], timeoutCts.Token);
                await ReconnectAsync(timeoutCts.Token);
            }
            catch (EndOfStreamException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                await Task.Delay(RetryDelays[attempts - 1], timeoutCts.Token);
                await ReconnectAsync(timeoutCts.Token);
            }
            catch (SocketException) when (attempts < RetryDelays.Length)
            {
                attempts++;
                await Task.Delay(RetryDelays[attempts - 1], timeoutCts.Token);
                await ReconnectAsync(timeoutCts.Token);
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
                    // #716: use WriteExactlyAsync to handle short writes on large payloads
                    await WriteExactlyAsync(_stream, buffer.AsMemory(0, read), ct);
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

        // #716: pre-write parity check — the declared size in the header MUST
        // match the actual payload length. This is the real invariant; the
        // post-write check was unreachable dead code because WriteExactlyAsync
        // can only return data.Length or throw.
        var header = Protocol.CreateRequestHeader(
            op, (ushort)keyBytes.Length, (ulong)payload.Length, (ushort)traceBytes.Length);

        var headerBuf = new byte[Protocol.REQUEST_HEADER_SIZE];
        Protocol.WriteRequest(headerBuf, header);

        await _stream!.WriteAsync(headerBuf, ct);
        if (keyBytes.Length > 0)
            await _stream.WriteAsync(keyBytes, ct);
        if (traceBytes.Length > 0)
            await _stream.WriteAsync(traceBytes, ct);

        // #716: write payload through WriteExactlyAsync which throws
        // RpcShortWriteException on terminal failure (sent == 0).
        if (payload.Length > 0)
            await WriteExactlyAsync(_stream, payload, ct);

        await _stream.FlushAsync(ct);
    }

    /// <summary>Total short-write events observed across all RPCs on this client.
    /// Exposed for metrics collection by higher layers.</summary>
    private static int _shortWriteCount;
    internal static int ShortWriteCount => Volatile.Read(ref _shortWriteCount);

    /// <summary>
    /// #716: Write all bytes to the stream, looping on partial completions.
    /// On healthy connections, .NET's internal TryCompleteSendTo loop means
    /// this completes in one iteration. On a genuinely broken connection
    /// (sent == 0), throws <see cref="RpcShortWriteException"/> instead of
    /// a generic EndOfStreamException so callers can distinguish short-write
    /// failures from normal EOF.
    /// </summary>
    internal static async Task WriteExactlyAsync(
        NetworkStream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var socket = stream.Socket;
        var totalToWrite = data.Length;
        var written = 0;
        while (written < totalToWrite)
        {
            var chunk = data[written..];
            var sent = await socket.SendAsync(chunk, SocketFlags.None, ct);
            if (sent == 0)
            {
                var count = Interlocked.Increment(ref _shortWriteCount);
                throw new RpcShortWriteException(
                    "WriteExactly", totalToWrite, written, count);
            }
            written += sent;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        // #470: Socket.Connected returns true for a half-open socket (peer replaced
        // without FIN/RST — e.g. head container redeploy through pasta forwarding),
        // so it is NOT a liveness signal. Additionally, a non-zero Available at
        // request start means leftover unread bytes on the wire (requests are
        // serialized by _sync, so no legit response can be mid-flight here) — the
        // connection is desynced and must be rebuilt. Both cases reconnect fresh.
        if (_client is { Connected: true } && _client.Available == 0)
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

    /// <summary>Sanity bound for a single RPC response payload. The PREFILL response
    /// (opcode 0x42) returns the KV state blob inline per specs/rpc-protocol.md,
    /// and that blob scales with context — ~800 MB at 60-80K tokens (CLAUDE.md),
    /// 827 MB measured at 7.3K tokens. The cap must sit above that. Raised to
    /// 4 GB (2026-08-13, epic #470): the blob is KV + MTP checkpoint (≈2× KV),
    /// so real agent workloads (~43K context → 3.3 GB) exceeded the old 2 GB
    /// cap and every agent turn failed with "RPC payload length out of range".
    /// It still rejects garbage/malformed lengths (negative or absurd values).
    /// NOTE (#470 follow-up): the 10 GB target exceeds this cap — raising it is
    /// a separate change from the timeout fix landed here.
    /// #595 (two-tier cap): this is the WIRE sanity bound — it caps every declared
    /// payload length (negative → ulong wrap, or absurdly large). It is NOT the
    /// materialization bound: the buffered read paths additionally cap at
    /// <see cref="Array.MaxLength"/> (~2 GiB − 8) via
    /// <see cref="ValidateBufferedPayloadLen"/>, because .NET cannot allocate a
    /// single byte[] beyond that (see #595 — the OverflowException/OOM sliver).
    /// Payloads between Array.MaxLength and MaxPayloadLen are legal on the wire and
    /// are served by the STREAMING APIs (RequestChunkedPayloadAsync,
    /// EnginePrefillChunkedAsync, RequestStreamAsync, EngineMergedDecodeStreamKvAsync),
    /// which never materialize the full blob.</summary>
    private const long MaxPayloadLen = 4L * 1024 * 1024 * 1024;

    private static void ValidatePayloadLen(long payloadLen)
    {
        if (payloadLen < 0 || payloadLen > MaxPayloadLen)
            throw new InvalidDataException($"RPC payload length out of range: {payloadLen} bytes");
    }

    /// <summary>
    /// #595: the buffered read paths can only materialize payloads up to
    /// <see cref="Array.MaxLength"/> (0x7FFFFFC7 ≈ 2 GiB − 8). A declared length
    /// above that does a checked conversion in <c>new byte[n]</c> that throws
    /// OUTSIDE the framing-error contract: OverflowException for
    /// n ∈ (int.MaxValue, MaxPayloadLen] (exactly-2 GiB = int.MaxValue + 1 is the
    /// sliver this issue is named after) and OutOfMemoryException for
    /// n ∈ (Array.MaxLength, int.MaxValue]. Neither is caught by the
    /// InvalidDataException handlers in RequestAsync/RequestStreamBodyAsync, so the
    /// desynced connection is never dropped. Rejecting here keeps the sanity check
    /// the single failure path: InvalidDataException → connection dropped → the
    /// caller's retry re-requests on a fresh socket. Payloads at/above this size
    /// must use a streaming API (they are legal on the wire up to MaxPayloadLen).
    /// </summary>
    private static void ValidateBufferedPayloadLen(long payloadLen)
    {
        if (payloadLen > Array.MaxLength)
            throw new InvalidDataException(
                $"RPC payload length exceeds the max materializable buffer (Array.MaxLength={Array.MaxLength} bytes): {payloadLen} bytes — use a streaming RPC API for payloads this large");
    }

    /// <summary>
    /// Buffered single-shot read — returns the WHOLE payload as one byte[]. Only
    /// valid up to <see cref="Array.MaxLength"/> (~2 GiB − 8); larger declared
    /// lengths are rejected by <see cref="ValidateBufferedPayloadLen"/> (see #595)
    /// and must be read via <see cref="RequestChunkedPayloadAsync"/> or
    /// <see cref="EnginePrefillChunkedAsync"/> instead. Used by the generic
    /// RequestAsync path (response contract is <c>RpcResponse.Payload: byte[]</c>),
    /// the RequestStreamBodyAsync response, and the merged-decode response read.
    /// </summary>
    private static async Task<byte[]> ReadPayloadAsync(NetworkStream stream, long payloadLen, CancellationToken ct)
    {
        ValidatePayloadLen(payloadLen);
        ValidateBufferedPayloadLen(payloadLen);
        var buf = new byte[payloadLen];
        await ReadExactAsync(stream, buf, ct);
        return buf;
    }

    /// <summary>
    /// #470: payload read with an idle-based deadline instead of a fixed total
    /// budget. The default RequestAsync budget (180s) races long PREFILL reads:
    /// compute (~175s at 28K tokens) + a multi-GB KV transfer can exceed it, and
    /// cancelling mid-transfer drops the connection — the peer then sees garbage
    /// framing ('RPC payload length out of range: 272728361719849728' = a
    /// misaligned 12B header read). Here the caller's timeout CTS is re-armed to
    /// <paramref name="idleBudget"/> on every successful chunk read: any progress
    /// keeps the exchange alive, while a genuinely wedged engine (no bytes for a
    /// full idle period) still fails fast. The initial <c>CancelAfter</c> set by
    /// RequestAsync remains the ceiling for the whole exchange (compute included).
    /// #595: BUFFERED path — materializes the full payload as one byte[] (the
    /// non-chunked EnginePrefillAsync contract), so it is capped at
    /// <see cref="Array.MaxLength"/> via <see cref="ValidateBufferedPayloadLen"/>
    /// (no OverflowException/OOM sliver; clean InvalidDataException instead).
    /// Large PREFILL blobs must use <see cref="EnginePrefillChunkedAsync"/> —
    /// the production EnableChunks path — which streams via
    /// <see cref="ReadPayloadChunkedAsync"/> and never materializes.
    /// </summary>
    private static async Task<byte[]> ReadPayloadIdleAsync(
        NetworkStream stream, long payloadLen,
        CancellationTokenSource timeoutCts, TimeSpan idleBudget, CancellationToken ct)
    {
        ValidatePayloadLen(payloadLen);
        ValidateBufferedPayloadLen(payloadLen);
        var buf = new byte[payloadLen];
        var offset = 0;
        while (offset < buf.Length)
        {
            // #470: read on timeoutCts.Token (NOT caller ct) so the re-armed
            // idle deadline is actually enforceable — previously a stalled
            // engine (no bytes for a full idle period) kept the read blocked
            // until the caller's own cancellation fired (~300s HTTP deadline),
            // which is how head-side workers parked on unbounded sends while
            // the coordinator never failed fast. The initial CancelAfter
            // (whole-exchange ceiling, compute included) set by RequestAsync
            // covers the first read; each successful chunk re-arms the idle
            // budget for the next one.
            var read = await stream.ReadAsync(buf.AsMemory(offset, buf.Length - offset), timeoutCts.Token);
            if (read == 0)
                throw new EndOfStreamException("Connection closed by peer");
            offset += read;
            timeoutCts.CancelAfter(idleBudget);
        }
        return buf;
    }

    /// <summary>
    /// #470 (Phase 2): streaming payload read — the full blob is NEVER
    /// materialized. Bytes are read into a 1 MiB window and handed to
    /// <paramref name="onChunk"/> as they arrive; the callback must consume the
    /// memory before returning (e.g. write into a Pipe / hash / push to the
    /// Store). Idle-based deadline: the caller's timeout CTS is re-armed to
    /// <paramref name="idleBudget"/> on every successful read. The chunk handler
    /// is invoked synchronously inside the connection's locked section, so
    /// backpressure propagates naturally (a slow consumer throttles the socket).
    /// #470 (run 31760361575): the consumer itself can be the slow side — a
    /// relay channel (decode leg preparing — model load, slot acquisition) or a
    /// Store pipe can fill up, parking this loop inside <paramref name="onChunk"/>.
    /// onChunk MUST be cancellable by the SAME re-armed timeout CTS (not the
    /// caller token): the idle budget/ceiling is the coordinator's authoritative
    /// bound on that park. Pre-fix, onChunk only observed the caller's ct, so a
    /// backpressured read loop sat parked with no deadline — the engine's 30s
    /// SO_SNDTIMEO then killed the stream first (send EAGAIN -> coordinator EOF
    /// -> zero-token turn) even though the coordinator was willing to wait the
    /// full budget. Passing timeoutCts.Token lets the coordinator's own idle
    /// budget decide: it either drains (stream completes) or fails fast and
    /// drops the connection for a clean retry.
    /// </summary>
    private static async Task ReadPayloadChunkedAsync(
        NetworkStream stream, long payloadLen,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        CancellationTokenSource timeoutCts, TimeSpan idleBudget, CancellationToken ct)
    {
        ValidatePayloadLen(payloadLen);
        var buf = new byte[1024 * 1024];
        long remaining = payloadLen;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buf.Length, remaining);
            // #470: read on timeoutCts.Token so the re-armed idle deadline is
            // enforceable (see ReadPayloadIdleAsync). A stalled peer now fails
            // fast after a full idle period instead of blocking until the
            // caller's cancellation — this is what bounds the head-side
            // send_all park (SO_SNDTIMEO there + cancellable read here).
            var read = await stream.ReadAsync(buf.AsMemory(0, toRead), timeoutCts.Token);
            if (read == 0)
                throw new EndOfStreamException("Connection closed by peer");
            remaining -= read;
            // #470: await onChunk on timeoutCts.Token (NOT the caller ct) so a
            // consumer that stalls (relay channel full / Store pipe full) is
            // cancelled by the coordinator's own idle budget/ceiling instead of
            // holding this loop open past the engine's send-side park. A slow
            // but progressing consumer still resets the timer each iteration.
            await onChunk(buf.AsMemory(0, read), timeoutCts.Token);
            timeoutCts.CancelAfter(idleBudget);
        }
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
        // #470: prefill compute (~175s at 28K tokens, scales past the old 180s
        // budget) + multi-GB KV transfer needs a raised ceiling, and the payload
        // read is idle-based so transfer progress keeps the deadline alive — a
        // fixed total budget dropped the connection mid-frame (coordinator read
        // garbage 12B headers afterwards). 600s ceiling / 120s per-chunk idle.
        return await RequestAsync(OpCode.EnginePrefill, slotKey, payload, traceId, ct,
            requestTimeoutOverride: TimeSpan.FromSeconds(600),
            payloadIdleBudget: TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// Overload without <paramref name="onMeta"/> for callers that only need
    /// the streamed payload (the meta comes back in the final response).
    /// Forwards to the full overload with a null meta sink.
    /// </summary>
    public virtual async Task<RpcResponse> EnginePrefillChunkedAsync(
        string slotKey, string requestJson, string traceId, CancellationToken ct,
        Action<long> onPayloadLen,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
    {
        return await EnginePrefillChunkedAsync(slotKey, requestJson, traceId, ct,
            onMeta: null, onPayloadLen, onChunk, requestTimeoutOverride, payloadIdleBudget);
    }

    /// <summary>
    /// #470 (Phase 2): PREFILL whose response payload is streamed chunk-by-chunk
    /// via <paramref name="onChunk"/> — the full blob (2.3 GB today, 10 GB
    /// target) is NEVER materialized in coordinator RAM. The response header's
    /// payload_len is known before the first chunk, so
    /// <paramref name="onPayloadLen"/> (invoked exactly once per attempt, before
    /// the stream) lets the caller start a Store PutChunked push with the exact
    /// frame size. Same 600s ceiling / 120s per-chunk idle as
    /// <see cref="EnginePrefillAsync"/>. Returns the response meta (Payload empty).
    /// NO mid-stream retries: a partial stream cannot be replayed safely (the
    /// caller's store push already started) — transport errors fail the call and
    /// the caller's own retry layer re-prefills the turn.
    /// </summary>
    public virtual async Task<RpcResponse> EnginePrefillChunkedAsync(
        string slotKey, string requestJson, string traceId, CancellationToken ct,
        Action<string>? onMeta,
        Action<long> onPayloadLen,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
    {
        var payload = Encoding.UTF8.GetBytes(requestJson);
        return await RequestChunkedAsync(OpCode.EnginePrefill, slotKey, payload, traceId, ct,
            requestTimeoutOverride ?? TimeSpan.FromSeconds(600),
            payloadIdleBudget ?? TimeSpan.FromSeconds(120),
            onMeta, onPayloadLen, onChunk);
    }

    /// <summary><see cref="RequestAsync"/> twin for streaming payload reads.</summary>
    /// <summary>
    /// #470 Phase 2: generic streaming-payload request (e.g. the Store's
    /// GET_CHUNKED for decode-side KV streaming). Same semantics as
    /// <see cref="EnginePrefillChunkedAsync"/>: <paramref name="onPayloadLen"/>
    /// fires once before the first chunk; <paramref name="onChunk"/> receives the
    /// payload as it arrives; NO full-blob byte[] is materialized. Returns the
    /// response meta (Payload empty). No mid-stream retries.
    /// </summary>
    public virtual async Task<RpcResponse> RequestChunkedPayloadAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct,
        Action<long> onPayloadLen,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
        => await RequestChunkedAsync(op, key, payload, traceId, ct,
            requestTimeoutOverride, payloadIdleBudget, onMeta: null, onPayloadLen, onChunk);

    private async Task<RpcResponse> RequestChunkedAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct,
        TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget,
        Action<string>? onMeta,
        Action<long> onPayloadLen,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk)
    {
        var effectiveTimeout = requestTimeoutOverride ?? _requestTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        await WaitForTurnAsync(op, key, traceId, timeoutCts.Token, ct);
        try
        {
            return await SendAndReceiveChunkedAsync(op, key, payload, traceId,
                timeoutCts, payloadIdleBudget, onMeta, onPayloadLen, onChunk, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-request — the wire may hold a half-read response
            // (and the caller's store push aborts via its own cancellation).
            DropConnection();
            if (!ct.IsCancellationRequested)
                throw NewTimeout(op, effectiveTimeout);
            throw;
        }
        catch (InvalidDataException)
        {
            DropConnection();
            throw;
        }
        catch (EndOfStreamException)
        {
            DropConnection();
            throw;
        }
        catch (SocketException)
        {
            DropConnection();
            throw;
        }
        catch (IOException)
        {
            // #470: a mid-payload IOException (e.g. the caller's onChunk hitting
            // ENOSPC on the L1 tmpfs cache write) must drop the connection — the
            // wire holds a half-consumed frame, and a retry reusing this socket
            // would misread the leftover bytes as a response header (garbage 12B
            // length → ValidatePayloadLen → engine EPIPE → prefill_rpc_error_
            // exhausted). Parity with RequestAsync's transport-error handling;
            // here there is no replayable retry, so drop + rethrow. No
            // ObjectDisposedException catch, mirroring RequestAsync: requests
            // are serialized by _sync, and a concurrent DropConnection already
            // closed the socket (DropConnection is idempotent), so ODE needs no
            // extra hygiene here.
            DropConnection();
            throw;
        }
        finally
        {
            EndHold();
        }
    }

    private async Task<RpcResponse> SendAndReceiveChunkedAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationTokenSource timeoutCts,
        TimeSpan? payloadIdleBudget, Action<string>? onMeta, Action<long> onPayloadLen,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk, CancellationToken ct)
    {
        // #712 M2: connect/send/header/meta I/O on timeoutCts.Token (see
        // SendAndReceiveAsync) — the payload chunks already were (ReadPayload-
        // ChunkedAsync). Pre-fix a wedge on the response HEADER (peer consumed
        // the request but never replied) held _sync with no deadline at all for
        // ct=None callers.
        await EnsureConnectedAsync(timeoutCts.Token);
        await SendRequestAsync(op, key, payload, traceId, timeoutCts.Token);

        var headerBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
        await ReadExactAsync(_stream!, headerBuf, timeoutCts.Token);

        var header = Protocol.ReadResponse(headerBuf);
        var meta = header.MetaLen > 0
            ? await ReadMetaAsync(_stream!, header.MetaLen, timeoutCts.Token)
            : null;

        // The response meta (n_past, state_size, kv_hash_str, model identity)
        // arrives BEFORE the first payload byte — surface it early (OK status
        // only) so the caller can start the DECODE frame (or a relay) without
        // buffering.
        if (onMeta != null && meta != null && header.Status == (byte)StatusCode.Ok)
        {
            onMeta(meta);
        }

        onPayloadLen((long)header.PayloadLen);
        if (header.PayloadLen > 0)
        {
            await ReadPayloadChunkedAsync(_stream!, (long)header.PayloadLen,
                onChunk, timeoutCts, payloadIdleBudget ?? TimeSpan.FromSeconds(60), ct);
        }
        return new RpcResponse(header.Status, meta, []);
    }

    /// <summary>
    /// Engine DECODE (opcode 0x43), non-streaming variant.
    /// Retained for the E2 expert-mode spike (#161-E2): when per-request
    /// expert placement is enabled, the engine may re-decode from a saved
    /// checkpoint via this RPC. Currently no production code path uses it
    /// (engine-mode chat decode is HTTP — see docs/architecture.md §3 and
    /// PR #282 review). Wire-format tests live in Tests.Shared.EngineOpcodeTests.
    /// </summary>
    public async Task<RpcResponse> EngineDecodeAsync(string slotKey, int nPredict, string? requestJson, string traceId, CancellationToken ct)
    {
        // Build JSON payload: {"n_predict": N, "messages": [...] or null}
        var json = $"{{\"n_predict\":{nPredict},\"messages\":{requestJson ?? "null"}}}";
        var payload = Encoding.UTF8.GetBytes(json);
        return await RequestAsync(OpCode.EngineDecode, slotKey, payload, traceId, ct);
    }

    /// <summary>
    /// Engine DECODE (opcode 0x43), streaming variant.
    /// See <see cref="EngineDecodeAsync"/> for the retention rationale.
    /// </summary>
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

    /// <summary>
    /// Framed DECODE (opcode 0x43) — merged decode path (#470).
    /// New segmented wire format (v3):
    ///   [4B hdr_len LE][8B hdr_hash LE][hdr JSON ≤ 32KiB][prompt segment][kv segment]
    /// The control header carries kv_metadata, model_metadata, generation config,
    /// and segment descriptors. The prompt and KV bytes follow as separate segments.
    /// Segments are contiguous: sum(segment.len) == payload_len - 12 - hdr_len.
    /// Returns a parsed <see cref="MergedDecodeResponse"/> with match results and
    /// the decode_request_id for polling GET /v1/decode/{id}.
    /// </summary>
    public virtual async Task<MergedDecodeResponse> EngineMergedDecodeAsync(
        string slotKey,
        int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias,
        string? messagesJson, int nPredict, string? samplingJson, bool stream,
        ReadOnlyMemory<byte> kvBlob,
        string traceId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        await WaitForTurnAsync(OpCode.EngineDecode, slotKey, traceId, timeoutCts.Token, ct);
        try
        {
            await EnsureConnectedAsync(timeoutCts.Token);

            // Build prompt segment (the actual user content, outside the header).
            // Always send the full prompt — Core has no tokenizer, so only the
            // engine can compute a token-accurate delta via get_common_prefix.
            var promptBytes = messagesJson != null
                ? Encoding.UTF8.GetBytes(messagesJson)
                : Array.Empty<byte>();
            var promptHash = XxHash3.HashToUInt64(promptBytes);
            var promptHashStr = $"xxh3:{promptHash:x16}";

            // Build KV segment — hash the memory in place, NO ToArray() copy
            // (#470: the blob is 2.3 GB today, 10 GB target; the old copy
            // doubled coordinator peak RAM during decode).
            var kvLen = kvBlob.Length;
            var kvHash = kvLen > 0 ? XxHash3.HashToUInt64(kvBlob.Span) : 0UL;
            var kvHashStr = kvLen > 0 ? $"xxh3:{kvHash:x16}" : "";

            // Build control header (v3) — 32 KiB cap applies here only.
            // This carries control data, not user content.
            var headerObj = new Dictionary<string, object>
            {
                ["v"] = 3,
                ["model"] = modelAlias ?? "",
                ["kv_metadata"] = new Dictionary<string, object>
                {
                    ["n_past"] = nPast,
                    ["tokenizer"] = kvTokenizer ?? "",
                    ["model_name"] = kvModelName ?? "",
                    ["model_quant"] = kvModelQuant ?? "",
                    ["model_capabilities"] = kvModelCapabilities
                },
                ["model_metadata"] = new Dictionary<string, object>
                {
                    ["tokenizer"] = modelTokenizer ?? "",
                    ["model_name"] = modelName ?? "",
                    ["model_quant"] = modelQuant ?? "",
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
                    ["oaicompat_model"] = modelAlias ?? ""
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
                        ["len"] = kvLen,
                        ["hash"] = kvHashStr
                    }
                }
            };
            var hdrJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headerObj));

            if (hdrJsonBytes.Length > 32768)
                throw new InvalidOperationException(
                    $"Merged DECODE control header exceeds 32 KiB ({hdrJsonBytes.Length} bytes)");
            if (hdrJsonBytes.Length == 0)
                throw new InvalidOperationException("Merged DECODE control header is empty");

            // Compute xxh3-64 hash of the header JSON bytes
            var hdrHash = XxHash3.HashToUInt64(hdrJsonBytes);

            // Build framed payload:
            //   [4B hdr_len LE][8B hdr_hash LE][hdr JSON][prompt segment][kv segment]
            // payload_len = 4 + 8 + hdr_len + prompt_len + kv_len
            var keyBytes = Encoding.UTF8.GetBytes(slotKey);
            var traceBytes = Encoding.UTF8.GetBytes(traceId);
            var totalPayloadLen = 4 + 8 + hdrJsonBytes.Length + promptBytes.Length + kvLen;

            var header = Protocol.CreateRequestHeader(
                OpCode.EngineDecode, (ushort)keyBytes.Length, (ulong)totalPayloadLen, (ushort)traceBytes.Length);
            var headerBuf = new byte[Protocol.REQUEST_HEADER_SIZE];
            Protocol.WriteRequest(headerBuf, header);

            await _stream!.WriteAsync(headerBuf, timeoutCts.Token);
            if (keyBytes.Length > 0)
                await _stream.WriteAsync(keyBytes, timeoutCts.Token);
            if (traceBytes.Length > 0)
                await _stream.WriteAsync(traceBytes, timeoutCts.Token);

            // Write [4B hdr_len LE]
            var hdrLenBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(hdrLenBuf, (uint)hdrJsonBytes.Length);
            await _stream.WriteAsync(hdrLenBuf, timeoutCts.Token);

            // Write [8B hdr_hash LE]
            var hdrHashBuf = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(hdrHashBuf, hdrHash);
            await _stream.WriteAsync(hdrHashBuf, timeoutCts.Token);

            // Write control header JSON
            await _stream.WriteAsync(hdrJsonBytes, timeoutCts.Token);

            // Write prompt segment
            if (promptBytes.Length > 0)
                await _stream.WriteAsync(promptBytes, timeoutCts.Token);

            // Write KV segment (#716: use WriteExactlyAsync for large blobs)
            if (kvLen > 0)
                await WriteExactlyAsync(_stream, kvBlob, timeoutCts.Token);

            await _stream.FlushAsync(timeoutCts.Token);

            // Read response
            var respHeaderBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
            await ReadExactAsync(_stream, respHeaderBuf, timeoutCts.Token);
            var respHeader = Protocol.ReadResponse(respHeaderBuf);

            var meta = respHeader.MetaLen > 0
                ? await ReadMetaAsync(_stream, respHeader.MetaLen, timeoutCts.Token)
                : null;

            if (respHeader.PayloadLen > 0)
                await ReadPayloadAsync(_stream, (long)respHeader.PayloadLen, timeoutCts.Token);

            return MergedDecodeResponse.Parse(respHeader.Status, meta);
        }
        catch (OperationCanceledException)
        {
            DropConnection();
            if (!ct.IsCancellationRequested)
                throw NewTimeout(OpCode.EngineDecode);
            throw;
        }
        catch (InvalidDataException)
        {
            // Framing error (e.g. response payload length out of range, #594) —
            // connection desynced, drop before rethrow (see RequestAsync).
            DropConnection();
            throw;
        }
        catch (EndOfStreamException)
        {
            // Mid-response EOF — connection state untrustworthy, drop before rethrow.
            DropConnection();
            throw;
        }
        finally
        {
            EndHold();
        }
    }

    /// <summary>
    /// #470 Phase 2: merged DECODE whose KV segment is streamed from
    /// <paramref name="kvChunks"/> (an ordered byte stream, e.g. the Store's
    /// GET_CHUNKED response) instead of one buffered blob — no full-blob byte[]
    /// anywhere on the decode path (2.3 GB today, 10 GB target). Same v3 frame
    /// as <see cref="EngineMergedDecodeAsync"/> with kv_len =
    /// <paramref name="kvTotalSize"/>. The whole-segment xxh3 cannot be computed
    /// before the header goes out, so the segments table carries an EMPTY kv
    /// hash — the engine's M2 restore skips whole-segment verification (chunk
    /// integrity is guaranteed by the Store's content-addressed chunks).
    /// </summary>
    public virtual async Task<MergedDecodeResponse> EngineMergedDecodeStreamKvAsync(
        string slotKey,
        int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias,
        string? messagesJson, int nPredict, string? samplingJson, bool stream,
        IAsyncEnumerable<ReadOnlyMemory<byte>> kvChunks, long kvTotalSize,
        string kvHash,
        string traceId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        await WaitForTurnAsync(OpCode.EngineDecode, slotKey, traceId, timeoutCts.Token, ct);
        try
        {
            await EnsureConnectedAsync(timeoutCts.Token);

            var promptBytes = messagesJson != null
                ? Encoding.UTF8.GetBytes(messagesJson)
                : Array.Empty<byte>();
            var promptHash = XxHash3.HashToUInt64(promptBytes);
            var promptHashStr = $"xxh3:{promptHash:x16}";

            var kvLen = kvTotalSize;

            var headerObj = new Dictionary<string, object>
            {
                ["v"] = 3,
                ["model"] = modelAlias ?? "",
                ["kv_metadata"] = new Dictionary<string, object>
                {
                    ["n_past"] = nPast,
                    ["tokenizer"] = kvTokenizer ?? "",
                    ["model_name"] = kvModelName ?? "",
                    ["model_quant"] = kvModelQuant ?? "",
                    ["model_capabilities"] = kvModelCapabilities
                },
                ["model_metadata"] = new Dictionary<string, object>
                {
                    ["tokenizer"] = modelTokenizer ?? "",
                    ["model_name"] = modelName ?? "",
                    ["model_quant"] = modelQuant ?? "",
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
                    ["oaicompat_model"] = modelAlias ?? ""
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
                        ["len"] = kvLen,
                        ["hash"] = kvHash // #470: PREFILL engine's whole-segment xxh3 ("xxh3:HEX") — verify live; "" skips
                    }
                }
            };
            var hdrJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headerObj));

            if (hdrJsonBytes.Length > 32768)
                throw new InvalidOperationException(
                    $"Merged DECODE control header exceeds 32 KiB ({hdrJsonBytes.Length} bytes)");
            if (hdrJsonBytes.Length == 0)
                throw new InvalidOperationException("Merged DECODE control header is empty");

            var hdrHash = XxHash3.HashToUInt64(hdrJsonBytes);

            var keyBytes = Encoding.UTF8.GetBytes(slotKey);
            var traceBytes = Encoding.UTF8.GetBytes(traceId);
            var totalPayloadLen = 4 + 8 + hdrJsonBytes.Length + promptBytes.Length + kvLen;

            var header = Protocol.CreateRequestHeader(
                OpCode.EngineDecode, (ushort)keyBytes.Length, (ulong)totalPayloadLen, (ushort)traceBytes.Length);
            var headerBuf = new byte[Protocol.REQUEST_HEADER_SIZE];
            Protocol.WriteRequest(headerBuf, header);

            await _stream!.WriteAsync(headerBuf, timeoutCts.Token);
            if (keyBytes.Length > 0)
                await _stream.WriteAsync(keyBytes, timeoutCts.Token);
            if (traceBytes.Length > 0)
                await _stream.WriteAsync(traceBytes, timeoutCts.Token);

            var hdrLenBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(hdrLenBuf, (uint)hdrJsonBytes.Length);
            await _stream.WriteAsync(hdrLenBuf, timeoutCts.Token);

            var hdrHashBuf = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(hdrHashBuf, hdrHash);
            await _stream.WriteAsync(hdrHashBuf, timeoutCts.Token);

            await _stream.WriteAsync(hdrJsonBytes, timeoutCts.Token);

            if (promptBytes.Length > 0)
                await _stream.WriteAsync(promptBytes, timeoutCts.Token);

            // Stream the KV segment chunk-by-chunk (backpressure propagates
            // to the Store read). #716: use WriteExactlyAsync per chunk.
            var expectedBytes = 0L;
            await foreach (var chunk in kvChunks.WithCancellation(timeoutCts.Token))
            {
                await WriteExactlyAsync(_stream, chunk, timeoutCts.Token);
                expectedBytes += chunk.Length;
            }
            if (expectedBytes != kvLen)
                throw new InvalidDataException(
                    $"Merged DECODE KV stream short: expected {kvLen} bytes, got {expectedBytes}");

            await _stream.FlushAsync(timeoutCts.Token);

            var respHeaderBuf = new byte[Protocol.RESPONSE_HEADER_SIZE];
            await ReadExactAsync(_stream, respHeaderBuf, timeoutCts.Token);
            var respHeader = Protocol.ReadResponse(respHeaderBuf);

            var meta = respHeader.MetaLen > 0
                ? await ReadMetaAsync(_stream, respHeader.MetaLen, timeoutCts.Token)
                : null;

            if (respHeader.PayloadLen > 0)
                await ReadPayloadAsync(_stream, (long)respHeader.PayloadLen, timeoutCts.Token);

            return MergedDecodeResponse.Parse(respHeader.Status, meta);
        }
        catch (OperationCanceledException)
        {
            DropConnection();
            if (!ct.IsCancellationRequested)
                throw NewTimeout(OpCode.EngineDecode);
            throw;
        }
        catch (InvalidDataException)
        {
            DropConnection();
            throw;
        }
        catch (EndOfStreamException)
        {
            DropConnection();
            throw;
        }
        finally
        {
            EndHold();
        }
    }

    /// <summary>#470 forwarding overload without an explicit whole-segment KV
    /// hash — the empty hash skips whole-segment verification (chunk integrity
    /// is guaranteed by the Store's content-addressed chunks).</summary>
    public virtual Task<MergedDecodeResponse> EngineMergedDecodeStreamKvAsync(
        string slotKey,
        int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias,
        string? messagesJson, int nPredict, string? samplingJson, bool stream,
        IAsyncEnumerable<ReadOnlyMemory<byte>> kvChunks, long kvTotalSize,
        string traceId, CancellationToken ct)
        => EngineMergedDecodeStreamKvAsync(
            slotKey, nPast,
            kvTokenizer, kvModelName, kvModelQuant, kvModelCapabilities,
            modelTokenizer, modelName, modelQuant, modelCapabilities,
            modelAlias,
            messagesJson, nPredict, samplingJson, stream,
            kvChunks, kvTotalSize,
            "", traceId, ct);

    public async Task<RpcResponse> EngineSetExpertModeAsync(string slotKey, string mode, string traceId, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(mode);
        return await RequestAsync(OpCode.EngineSetExpertMode, slotKey, payload, traceId, ct);
    }

    /// <summary>
    /// Tell the head engine to attach <paramref name="peer"/> as a pipeline worker for this slot,
    /// assigning it the tensors matching <paramref name="otSplit"/> (an --override-tensor regex).
    /// The worker loads those tensors from its own local model file; only the assignment crosses
    /// the wire, never the weights. Returns the engine's actual attach result in the response meta.
    /// </summary>
    public async Task<RpcResponse> EnginePipelineAttachAsync(string slotKey, string peer, string otSplit, string traceId, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { peer, ot_split = otSplit });
        var payload = Encoding.UTF8.GetBytes(json);
        return await RequestAsync(OpCode.EnginePipelineAttach, slotKey, payload, traceId, ct);
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

/// <summary>
/// Parsed response from the framed DECODE 0x43 merged decode RPC.
/// Carries the engine-side model identity match results and the
/// decode_request_id for polling GET /v1/decode/{id}.
/// </summary>
public sealed class MergedDecodeResponse
{
    public byte Status { get; init; }
    public bool Valid { get; init; }
    public int? DecodeRequestId { get; init; }
    public int NPastAfterRestore { get; init; }
    public double RestoreSlotMs { get; init; }
    public double DecodeInitMs { get; init; }
    public double ModelLoadMs { get; init; }
    public bool ModelFallback { get; init; }
    public string? Error { get; init; }

    // Model identity match fields
    public bool TokenizerMatch { get; init; }
    public bool ModelNameMatch { get; init; }
    public bool ModelCapabilitiesMatch { get; init; }
    public uint CapabilitiesXor { get; init; }
    public bool ModelQuantMatch { get; init; }
    public bool ModelAliasMatch { get; init; }

    public static MergedDecodeResponse Parse(byte status, string? meta)
    {
        if (status != (byte)StatusCode.Ok)
            return new MergedDecodeResponse { Status = status, Valid = false };

        if (string.IsNullOrEmpty(meta))
            return new MergedDecodeResponse { Status = status, Valid = false };

        try
        {
            using var doc = JsonDocument.Parse(meta);
            var root = doc.RootElement;
            var valid = root.TryGetProperty("valid", out var vEl) && vEl.GetBoolean();
            int? decodeRequestId = root.TryGetProperty("decode_request_id", out var drEl)
                ? drEl.GetInt32() : null;
            var nPastAfter = root.TryGetProperty("n_past_after_restore", out var npEl)
                ? npEl.GetInt32() : 0;
            var restoreSlotMs = root.TryGetProperty("restore_slot_ms", out var rsEl)
                ? rsEl.GetDouble() : 0;
            var decodeInitMs = root.TryGetProperty("decode_init_ms", out var diEl)
                ? diEl.GetDouble() : 0;
            var modelLoadMs = root.TryGetProperty("model_load_ms", out var mlEl)
                ? mlEl.GetDouble() : 0;
            var modelFallback = root.TryGetProperty("model_fallback", out var mfEl)
                && mfEl.GetBoolean();

            // Parse match object
            bool tokenizerMatch = false, modelNameMatch = false, modelCapabilitiesMatch = false,
                 modelQuantMatch = false, modelAliasMatch = false;
            uint capabilitiesXor = 0;
            if (root.TryGetProperty("match", out var matchEl) && matchEl.ValueKind == JsonValueKind.Object)
            {
                tokenizerMatch = matchEl.TryGetProperty("tokenizer_match", out var tm) && tm.GetBoolean();
                modelNameMatch = matchEl.TryGetProperty("model_name_match", out var nm) && nm.GetBoolean();
                modelCapabilitiesMatch = matchEl.TryGetProperty("model_capabilities_match", out var cm) && cm.GetBoolean();
                capabilitiesXor = matchEl.TryGetProperty("capabilities_xor", out var cx)
                    ? cx.GetUInt32() : 0;
                modelQuantMatch = matchEl.TryGetProperty("model_quant_match", out var qm) && qm.GetBoolean();
                modelAliasMatch = matchEl.TryGetProperty("model_alias_match", out var am) && am.GetBoolean();
            }

            return new MergedDecodeResponse
            {
                Status = status,
                Valid = valid,
                DecodeRequestId = decodeRequestId,
                NPastAfterRestore = nPastAfter,
                RestoreSlotMs = restoreSlotMs,
                DecodeInitMs = decodeInitMs,
                ModelLoadMs = modelLoadMs,
                ModelFallback = modelFallback,
                TokenizerMatch = tokenizerMatch,
                ModelNameMatch = modelNameMatch,
                ModelCapabilitiesMatch = modelCapabilitiesMatch,
                CapabilitiesXor = capabilitiesXor,
                ModelQuantMatch = modelQuantMatch,
                ModelAliasMatch = modelAliasMatch
            };
        }
        catch
        {
            return new MergedDecodeResponse { Status = status, Valid = false, Error = "malformed response" };
        }
    }
}
