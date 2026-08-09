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
        // Sanity bound for a single RPC response payload. The PREFILL response
        // (opcode 0x42) returns the KV state blob inline per specs/rpc-protocol.md,
        // and that blob scales with context — ~800 MB at 60-80K tokens (CLAUDE.md),
        // 827 MB measured at 7.3K tokens. The cap must sit above that: 2 GB. It
        // still rejects garbage/malformed lengths (negative or absurd values).
        const long maxPayloadLen = 2L * 1024 * 1024 * 1024;
        if (payloadLen < 0 || payloadLen > maxPayloadLen)
            throw new InvalidDataException($"RPC payload length out of range: {payloadLen} bytes");
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

        await WaitForTurnAsync(OpCode.EngineDecode, timeoutCts.Token, ct);
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

            // Build KV segment
            var kvBytes = kvBlob.Length > 0 ? kvBlob.ToArray() : Array.Empty<byte>();
            var kvHash = kvBytes.Length > 0 ? XxHash3.HashToUInt64(kvBytes) : 0UL;
            var kvHashStr = kvBytes.Length > 0 ? $"xxh3:{kvHash:x16}" : "";

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
                        ["len"] = kvBytes.Length,
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
            var totalPayloadLen = 4 + 8 + hdrJsonBytes.Length + promptBytes.Length + kvBytes.Length;

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

            // Write KV segment
            if (kvBytes.Length > 0)
                await _stream.WriteAsync(kvBlob, timeoutCts.Token);

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
            _sync.Release();
        }
    }

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
