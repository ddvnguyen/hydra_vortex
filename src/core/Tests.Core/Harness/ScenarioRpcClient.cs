using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using Hydra.Shared;

namespace Tests.Core.Harness;

/// <summary>
/// Single recording + fault-injecting RPC fake used by the differential
/// harness (<see cref="SchedulerScenarioRunner"/>). It is deliberately a
/// consolidated version of the two legacy test doubles it supersedes:
/// <list type="bullet">
/// <item><see cref="Tests.Core.TestHelpers.FakeStoreClient"/> — per-op
/// response/exception overrides for Store opcodes (Put/Get/Stat/…) and the
/// ordered (OpCode, key, payloadLen) call log.</item>
/// <item><c>EngineModeTests.EngineTestRpcClient</c> — the engine-side failure
/// switches (#279 NotImplemented, StatePut mismatch, merged-decode Gate-A
/// reject/transport-fault, multi-engine attach fallback) and the engine-mode
/// default responses (EnginePrefill → 4096-byte KV blob, StateGet → 2048-byte
/// blob, StatePut → model_match=true, …).</item>
/// </list>
///
/// One instance is wired into BOTH the store slot and <c>AgentClientFactory</c>
/// (the same pattern every integration fixture uses, e.g. <c>EngineFixture</c>
/// passes a single <c>Rpc</c> for both roles). A single instance keeps the
/// recorded call sequence naturally ordered — the differential gate compares
/// this ordered list byte-for-byte between legacy and v2, so a shared counter
/// is required, and per-role fakes could not interleave deterministically.
/// </summary>
internal sealed class ScenarioRpcClient : RpcClient
{
    /// <summary>Master ordered log of every binary RPC the scheduler issued (store + engine),
    /// with the response status (StatusCode name, or "Throw" when the call raised).</summary>
    public List<(OpCode Op, string Key, int PayloadLen, string Status)> RpcCalls { get; } = new();

    /// <summary>Merged-decode (0x43 framed) calls, recorded separately for gate assertions.</summary>
    public List<(string SlotKey, string ModelName, bool Stream)> MergedDecodeCalls { get; } = new();

    private readonly ConcurrentDictionary<OpCode, (byte Status, string? Meta, byte[]? Payload)> _responses = new();
    private readonly ConcurrentDictionary<OpCode, Exception?> _exceptions = new();
    private readonly ConcurrentDictionary<string, (byte Status, string? Meta, byte[]? Payload)> _keyResponses = new(StringComparer.Ordinal);
    private readonly object _busyLock = new();

    // ── Engine failure switches (mirror EngineTestRpcClient) ─────────────
    public bool FailMultiEngineAttach { get; set; }
    public bool FailMultiEngineAttachFallback { get; set; }
    public bool MakeEnginePrefillFail { get; set; }
    public bool MakeEnginePrefillNotImplemented { get; set; }
    public bool MakeEnginePrefillThrowCancellation { get; set; }
    /// <summary>Number of initial EnginePrefill calls that return BUSY (0 = never).</summary>
    public int BusyPrefillAttempts { get; set; }
    public bool MakeStatePutFail { get; set; }
    public bool MakeStatePutMismatch { get; set; }
    public bool MakeStatePutHashMismatch { get; set; }
    public bool MakeMergedDecodeReject { get; set; }
    public bool MakeMergedDecodeThrow { get; set; }

    /// <summary>Deterministic KV blob returned by EnginePrefill (drives chunk hashes + payload sizes).</summary>
    public byte[] PrefillKvBlob { get; set; } = new byte[4096];
    /// <summary>Deterministic KV blob returned by StateGet (BgSave / stream save payload size).</summary>
    public byte[] StateGetBlob { get; set; } = new byte[2048];

    private int _statePutMismatchFired;
    private int _statePutHashMismatchFired;
    private int _mergedDecodeRejectFired;
    private int _mergedDecodeThrowFired;
    private int _busyFired;

    public ScenarioRpcClient() : base("harness", 0) { }

    // ── Configuration API (mirrors FakeStoreClient.SetResponse/SetException) ──

    /// <summary>Set a fixed response for an opcode (overrides the engine-mode default).</summary>
    public void SetResponse(OpCode op, byte status, string? meta = null, byte[]? payload = null)
        => _responses[op] = (status, meta, payload);

    /// <summary>Set a response for any call whose key starts with <paramref name="keyPrefix"/>.</summary>
    public void SetKeyResponse(string keyPrefix, OpCode op, byte status, string? meta = null, byte[]? payload = null)
        => _keyResponses[$"{op}:{keyPrefix}"] = (status, meta, payload);

    /// <summary>Make an opcode throw on every call.</summary>
    public void SetException(OpCode op, Exception ex)
        => _exceptions[op] = ex;

    public void ClearCalls()
    {
        RpcCalls.Clear();
        MergedDecodeCalls.Clear();
    }

    public bool HasCall(OpCode op, string? keyContains = null)
        => RpcCalls.Any(c => c.Op == op && (keyContains == null || c.Key.Contains(keyContains)));

    public int CountCalls(OpCode op) => RpcCalls.Count(c => c.Op == op);

    // ── RequestAsync (both store + engine roles land here) ──
    // Override the 7-param overload (the 5-param base delegates here, and
    // EnginePrefillAsync calls the7-param directly with timeout/idle overrides).

    public override Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload,
        string traceId, CancellationToken ct,
        TimeSpan? requestTimeoutOverride,
        TimeSpan? payloadIdleBudget)
    {
        try
        {
            var resp = ComputeResponse(op, key, payload);
            RpcCalls.Add((op, key, payload.Length, ((StatusCode)resp.Status).ToString()));
            return Task.FromResult(resp);
        }
        catch (Exception)
        {
            RpcCalls.Add((op, key, payload.Length, "Throw"));
            throw;
        }
    }

    // ── RequestStreamBodyAsync (streaming store writes, #470) ──
    // The base class connects to real TCP. Override to feed through the mock.

    public override Task<RpcResponse> RequestStreamBodyAsync(
        OpCode op, string key, Stream body, long bodyLen,
        string traceId, CancellationToken ct)
    {
        try
        {
            var resp = ComputeResponse(op, key, ReadOnlyMemory<byte>.Empty);
            RpcCalls.Add((op, key, (int)bodyLen, ((StatusCode)resp.Status).ToString()));
            return Task.FromResult(resp);
        }
        catch (Exception)
        {
            RpcCalls.Add((op, key, (int)bodyLen, "Throw"));
            throw;
        }
    }

    // ── EnginePrefillChunkedAsync (streaming chunked prefill, #470) ──
    // The base class calls private RequestChunkedAsync which opens a real TCP
    // connection. Override here to feed deterministic chunks through the callbacks.

    public override async Task<RpcResponse> EnginePrefillChunkedAsync(
        string slotKey, string requestJson, string traceId, CancellationToken ct,
        Action<string>? onMeta,
        Action<long> onPayloadLen,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        TimeSpan? requestTimeoutOverride = null, TimeSpan? payloadIdleBudget = null)
    {
        var meta = JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096 });
        onMeta?.Invoke(meta);
        onPayloadLen(PrefillKvBlob.Length);
        await onChunk(PrefillKvBlob, ct);
        RpcCalls.Add((OpCode.EnginePrefill, slotKey, PrefillKvBlob.Length, StatusCode.Ok.ToString()));
        return new RpcResponse((byte)StatusCode.Ok, meta, []);
    }

    private RpcResponse ComputeResponse(OpCode op, string key, ReadOnlyMemory<byte> payload)
    {
        if (_exceptions.TryGetValue(op, out var ex) && ex != null)
            throw ex;

        if (TryKeyResponse(op, key, out var kr))
            return new RpcResponse(kr.Status, kr.Meta, kr.Payload ?? []);

        if (_responses.TryGetValue(op, out var r))
            return new RpcResponse(r.Status, r.Meta, r.Payload ?? []);

        return DefaultResponse(op, key, payload);
    }

    private bool TryKeyResponse(OpCode op, string key, out (byte Status, string? Meta, byte[]? Payload) r)
    {
        foreach (var (k, v) in _keyResponses)
        {
            var sep = k.IndexOf(':');
            if (sep < 0) continue;
            var opCode = (OpCode)Enum.Parse(typeof(OpCode), k[..sep]);
            var prefix = k[(sep + 1)..];
            if (opCode == op && key.StartsWith(prefix, StringComparison.Ordinal))
            {
                r = v;
                return true;
            }
        }
        r = default;
        return false;
    }

    private RpcResponse DefaultResponse(OpCode op, string key, ReadOnlyMemory<byte> payload)
    {
        // ── Engine opcodes (defaults mirror EngineTestRpcClient) ──
        if (op == OpCode.EnginePrefill)
        {
            var payloadStr = Encoding.UTF8.GetString(payload.Span);
            var hasHydraConfig = payloadStr.Contains("hydra_config", StringComparison.Ordinal);

            if (FailMultiEngineAttach && hasHydraConfig)
                return new RpcResponse((byte)StatusCode.Error,
                    JsonSerializer.Serialize(new { mode = "solo", peer_connected = false }), []);
            if (FailMultiEngineAttachFallback && hasHydraConfig)
                return new RpcResponse((byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096, model_fallback = true }),
                    PrefillKvBlob);

            lock (_busyLock)
            {
                if (BusyPrefillAttempts > 0 && _busyFired < BusyPrefillAttempts)
                {
                    _busyFired++;
                    return new RpcResponse((byte)StatusCode.Busy, null, []);
                }
            }

            if (MakeEnginePrefillThrowCancellation)
                throw new OperationCanceledException("simulated caller cancellation during engine prefill");
            if (MakeEnginePrefillNotImplemented)
                return new RpcResponse((byte)StatusCode.NotImplemented, null, []);
            if (MakeEnginePrefillFail)
                return new RpcResponse((byte)StatusCode.Error, null, []);

            return new RpcResponse((byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096 }),
                PrefillKvBlob);
        }

        if (op == OpCode.EngineDecode)
            return new RpcResponse((byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 1050, tokens_generated = 50, stop_reason = "complete" }),
                Encoding.UTF8.GetBytes("Hello from engine decode"));

        if (op == OpCode.StateGet)
            return new RpcResponse((byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 2000 }),
                StateGetBlob);

        if (op == OpCode.StatePut)
        {
            if (MakeStatePutFail)
                return new RpcResponse((byte)StatusCode.Error, null, []);
            if (MakeStatePutMismatch && Interlocked.CompareExchange(ref _statePutMismatchFired, 1, 0) == 0)
                return new RpcResponse((byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new
                    {
                        n_past = 2000, model_match = false, tokenizer = "gpt2", model_name = "other_model",
                        model_quant = "Q4_K", model_capabilities = 0, model_alias = "other_model", model_path = "/wrong/path"
                    }), []);
            if (MakeStatePutHashMismatch && Interlocked.CompareExchange(ref _statePutHashMismatchFired, 1, 0) == 0)
                return new RpcResponse((byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new
                    {
                        n_past = 2000, model_match = true, tokenizer = "llama", model_name = "DifferentModel",
                        model_quant = "Q5_K", model_capabilities = 1, model_alias = "nano", model_path = "/dev/null"
                    }), []);

            return new RpcResponse((byte)StatusCode.Ok,
                JsonSerializer.Serialize(new
                {
                    n_past = 2000, model_match = true, tokenizer = "llama", model_name = "nano",
                    model_quant = "Q4_K", model_capabilities = 0, model_alias = "nano", model_path = "/dev/null"
                }), []);
        }

        if (op == OpCode.StateMeta)
            return new RpcResponse((byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 2000 }), []);

        // ── Store opcodes (default Ok, mirroring FakeStoreClient) ──
        return new RpcResponse((byte)StatusCode.Ok, null, []);
    }

    // ── Merged decode (0x43 framed) ──

    public override Task<MergedDecodeResponse> EngineMergedDecodeAsync(
        string slotKey, int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias,
        string? messagesJson, int nPredict, string? samplingJson, bool stream,
        ReadOnlyMemory<byte> kvBlob,
        string traceId, CancellationToken ct)
    {
        MergedDecodeCalls.Add((slotKey, modelName ?? "", stream));

        try
        {
            if (MakeMergedDecodeThrow && Interlocked.CompareExchange(ref _mergedDecodeThrowFired, 1, 0) == 0)
                throw new System.Net.Http.HttpRequestException("connection reset by engine");

            // The merged decode is a framed EngineDecode on the wire.
            RpcCalls.Add((OpCode.EngineDecode, slotKey, kvBlob.Length, StatusCode.Ok.ToString()));

            if (MakeMergedDecodeReject && Interlocked.CompareExchange(ref _mergedDecodeRejectFired, 1, 0) == 0)
            {
                return Task.FromResult(new MergedDecodeResponse
                {
                    Status = (byte)StatusCode.Ok,
                    Valid = false,
                    Error = "gate_a_model_mismatch",
                    TokenizerMatch = kvTokenizer == modelTokenizer,
                    ModelNameMatch = kvModelName == modelName,
                    ModelQuantMatch = kvModelQuant == modelQuant,
                    ModelCapabilitiesMatch = kvModelCapabilities == modelCapabilities,
                });
            }

            return Task.FromResult(new MergedDecodeResponse
            {
                Status = (byte)StatusCode.Ok,
                Valid = true,
                DecodeRequestId = 42,
                NPastAfterRestore = nPast,
                RestoreSlotMs = 1.0,
                DecodeInitMs = 1.0,
                ModelLoadMs = 0,
                ModelFallback = false,
                TokenizerMatch = kvTokenizer == modelTokenizer,
                ModelNameMatch = kvModelName == modelName,
                ModelCapabilitiesMatch = kvModelCapabilities == modelCapabilities,
            CapabilitiesXor = kvModelCapabilities ^ modelCapabilities,
            ModelQuantMatch = kvModelQuant == modelQuant,
            ModelAliasMatch = true,
        });
        }
        catch (Exception)
        {
            // The framed decode never reached the engine — record the wire
            // call with a Throw status so the trace shows the transport fault.
            RpcCalls.Add((OpCode.EngineDecode, slotKey, kvBlob.Length, "Throw"));
            throw;
        }
    }
}
