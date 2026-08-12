using System.Text;
using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Hydra.Shared;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>Always-healthy health monitor for v2 tests. <see cref="EngineCapabilities"/>
/// advertises merged_decode so tests can enter the framed DECODE 0x43 path.</summary>
internal sealed class FakeHealthMonitor : IHealthMonitorService
{
    public bool IsHealthy(string nodeName) => true;
    public bool IsStoreHealthy => true;
    public int? GetIdleSlot(string nodeName) => null;
    public HashSet<string> EngineCapabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public NodeInfo? GetNodeInfo(string nodeName)
        => EngineCapabilities.Count == 0 ? null : new NodeInfo { NodeName = nodeName, EngineCapabilities = EngineCapabilities };
    public Dictionary<string, object> GetHealthSummary() => new();
    public void UpdateNodeModelIdentity(string nodeName, string tokenizer, string modelName, string modelQuant, uint modelCapabilities) { }
    public event Action? HealthyChanged;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Recording + fault-injecting engine RPC channel (no sockets).</summary>
internal sealed class FakeEngineRpcClient : IEngineRpcClient
{
    public List<(OpCode Op, string Key, int PayloadLen)> Calls { get; } = new();

    /// <summary>When true, EnginePrefill throws (simulates a transient engine fault).</summary>
    public bool FailPrefill { get; set; }

    /// <summary>When true, EnginePrefill throws exactly ONCE, then succeeds —
    /// simulates a single transient fault so a retry re-route can be observed.</summary>
    public bool FailPrefillOnce { get; set; }
    private int _prefillFailuresFired;

    /// <summary>When true, EnginePrefill returns NotImplemented (old binary, #279).</summary>
    public bool MakePrefillNotImplemented { get; set; }

    /// <summary>When true, STATE_PUT returns model_match=false once (cross-model guard).</summary>
    public bool MakeStatePutMismatch { get; set; }
    private int _statePutMismatchFired;

    /// <summary>When true, the merged DECODE (0x43) returns Valid=false (Gate A reject).</summary>
    public bool RejectMergedDecode { get; set; }

    /// <summary>When true, the merged DECODE (0x43) throws (simulated transport fault).</summary>
    public bool MergedDecodeThrows { get; set; }

    /// <summary>When set, EnginePrefill awaits this gate before returning — lets tests
    /// hold a prefill in-flight and then cancel mid-pipeline (review #3/#8).</summary>
    public TaskCompletionSource<bool>? BlockPrefill { get; set; }

    public int MergedDecodeCalls { get; private set; }

    public async Task<RpcResponse> RequestAsync(OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct)
    {
        Calls.Add((op, key, payload.Length));

        if (op == OpCode.EnginePrefill && FailPrefill)
            throw new InvalidOperationException("simulated engine prefill fault");

        if (op == OpCode.EnginePrefill && FailPrefillOnce
            && Interlocked.CompareExchange(ref _prefillFailuresFired, 1, 0) == 0)
            throw new InvalidOperationException("simulated engine prefill fault");

        if (op == OpCode.EnginePrefill && MakePrefillNotImplemented)
            return new RpcResponse((byte)StatusCode.NotImplemented, null, []);

        if (op == OpCode.EnginePrefill)
        {
            if (BlockPrefill is { } gate)
                await gate.Task.WaitAsync(CancellationToken.None);
            return new RpcResponse(
                (byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096, model_name = "nano", tokenizer = "llama", model_quant = "Q4_K", model_capabilities = 0 }),
                Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        }

        if (op == OpCode.StatePut)
        {
            if (MakeStatePutMismatch && Interlocked.CompareExchange(ref _statePutMismatchFired, 1, 0) == 0)
                return new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, model_match = false, tokenizer = "gpt2", model_name = "other_model", model_quant = "Q4_K", model_capabilities = 0 }),
                    []);
            return new RpcResponse(
                (byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 2000, model_match = true, tokenizer = "llama", model_name = "nano", model_quant = "Q4_K", model_capabilities = 0 }),
                []);
        }

        if (op == OpCode.StateGet) // BgSave capture: the slot's post-decode KV
            return new RpcResponse(
                (byte)StatusCode.Ok, null,
                Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray());

        return new RpcResponse((byte)StatusCode.Ok, null, []);
    }

    public async Task<Hydra.Core.Services.SchedulerV2.EnginePrefillResult> EnginePrefillAsync(
        string slotKey, string payloadJson, string traceId, CancellationToken ct,
        Dictionary<string, object>? hydraConfig)
    {
        // Route through RequestAsync so every failure switch (FailPrefill /
        // FailPrefillOnce / MakePrefillNotImplemented / BlockPrefill) + the Calls
        // recording behave identically to the adapter's wire path.
        var resp = await RequestAsync(OpCode.EnginePrefill, slotKey, Encoding.UTF8.GetBytes(payloadJson), traceId, ct);
        return EnginePrefillResponseParser.Parse(resp);
    }

    public Task<MergedDecodeResponse> EngineMergedDecodeAsync(
        string slotKey, int nPast,
        string? kvTokenizer, string? kvModelName, string? kvModelQuant, uint kvModelCapabilities,
        string? modelTokenizer, string? modelName, string? modelQuant, uint modelCapabilities,
        string? modelAlias, string? messagesJson, int nPredict, string? samplingJson, bool stream,
        ReadOnlyMemory<byte> kvBlob, string traceId, CancellationToken ct)
    {
        MergedDecodeCalls++;
        Calls.Add((OpCode.EngineDecode, slotKey, kvBlob.Length)); // the framed 0x43 on the wire

        if (MergedDecodeThrows)
            throw new System.Net.Http.HttpRequestException("connection reset by engine");

        if (RejectMergedDecode)
            return Task.FromResult(new MergedDecodeResponse
            {
                Status = (byte)StatusCode.Ok,
                Valid = false,
                Error = "gate_a_model_mismatch",
                TokenizerMatch = kvTokenizer == modelTokenizer,
                ModelNameMatch = kvModelName == modelName,
                ModelCapabilitiesMatch = kvModelCapabilities == modelCapabilities,
            });

        return Task.FromResult(new MergedDecodeResponse
        {
            Status = (byte)StatusCode.Ok,
            Valid = true,
            DecodeRequestId = 42,
            NPastAfterRestore = nPast,
            TokenizerMatch = kvTokenizer == modelTokenizer,
            ModelNameMatch = kvModelName == modelName,
            ModelCapabilitiesMatch = kvModelCapabilities == modelCapabilities,
            CapabilitiesXor = kvModelCapabilities ^ modelCapabilities,
            ModelQuantMatch = kvModelQuant == modelQuant,
            ModelAliasMatch = true,
        });
    }
}

/// <summary>Stub completion proxy: returns a canned non-stream response; streams empty.
/// Records the erase calls so the cross-model abort path is observable.</summary>
internal sealed class FakeCompletionProxy : ICompletionProxyService
{
    /// <summary>URLs the non-streaming proxy was called with, in order (decode-target assertions).</summary>
    public List<string> NonStreamingUrls { get; } = new();

    /// <summary>Bodies of the non-streaming proxy calls (n_predict=0 prefill-fallback assertion).</summary>
    public List<Dictionary<string, object>> NonStreamingBodies { get; } = new();

    /// <summary>(nodeUrl, slotId) erase calls (cross-model abort assertions).</summary>
    public List<(string NodeUrl, int SlotId)> EraseCalls { get; } = new();

    public Task<Dictionary<string, object>> ProxyCompletionAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
    {
        NonStreamingUrls.Add(nodeUrl);
        NonStreamingBodies.Add(new Dictionary<string, object>(body));
        return Task.FromResult(JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":3,"completion_tokens":12,"total_tokens":15}}""")!);
    }

    public IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
        => AsyncEnumerable.Empty<byte[]>();

    public Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct) => Task.FromResult(true);
    public IAsyncEnumerable<byte[]> PollDecodeStreamAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct, WorkItem? item = null)
        => AsyncEnumerable.Empty<byte[]>();
    public Task<Dictionary<string, object>> PollDecodeResultAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
        => Task.FromResult(new Dictionary<string, object>
        {
            ["usage"] = JsonSerializer.SerializeToElement(new { total_tokens = 150 }),
        });
    public Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct) => Task.CompletedTask;
    public Task EraseSlotAsync(string nodeUrl, int slotId, CancellationToken ct)
    {
        EraseCalls.Add((nodeUrl, slotId));
        return Task.CompletedTask;
    }
}

/// <summary>Deterministic warm-slot verifier for v2 tests (review #8). Defaults to
/// "verified" so warm routes proceed; set <see cref="Result"/> false to force a
/// warm-verification failure (evict + re-route cold).</summary>
internal sealed class FakeWarmSlotVerifier : IWarmSlotVerifier
{
    public bool Result { get; set; } = true;
    public Task<bool> VerifyAsync(WorkerConfig worker, SessionEntry? entry, string traceId)
        => Task.FromResult(Result);
}
