using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Hydra.Shared;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>Always-healthy health monitor for v2 tests.</summary>
internal sealed class FakeHealthMonitor : IHealthMonitorService
{
    public bool IsHealthy(string nodeName) => true;
    public bool IsStoreHealthy => true;
    public int? GetIdleSlot(string nodeName) => null;
    public NodeInfo? GetNodeInfo(string nodeName) => null;
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

    public Task<RpcResponse> RequestAsync(OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct)
    {
        Calls.Add((op, key, payload.Length));

        if (op == OpCode.EnginePrefill && FailPrefill)
            throw new InvalidOperationException("simulated engine prefill fault");

        if (op == OpCode.EnginePrefill)
            return Task.FromResult(new RpcResponse(
                (byte)StatusCode.Ok,
                JsonSerializer.Serialize(new { n_past = 7, state_size = 4096 }),
                Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray()));

        if (op == OpCode.StateGet) // BgSave capture: the slot's post-decode KV
            return Task.FromResult(new RpcResponse(
                (byte)StatusCode.Ok, null,
                Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray()));

        return Task.FromResult(new RpcResponse((byte)StatusCode.Ok, null, []));
    }
}

/// <summary>Stub completion proxy: returns a canned non-stream response; streams empty.</summary>
internal sealed class FakeCompletionProxy : ICompletionProxyService
{
    /// <summary>URLs the non-streaming proxy was called with, in order (decode-target assertions).</summary>
    public List<string> NonStreamingUrls { get; } = new();

    public Task<Dictionary<string, object>> ProxyCompletionAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
    {
        NonStreamingUrls.Add(nodeUrl);
        return Task.FromResult(JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":3,"completion_tokens":12,"total_tokens":15}}""")!);
    }

    public IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
        => AsyncEnumerable.Empty<byte[]>();

    public Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct) => Task.FromResult(true);
    public IAsyncEnumerable<byte[]> PollDecodeStreamAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct, WorkItem? item = null)
        => AsyncEnumerable.Empty<byte[]>();
    public Task<Dictionary<string, object>> PollDecodeResultAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
        => Task.FromResult(new Dictionary<string, object>());
    public Task CancelDecodeAsync(string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct) => Task.CompletedTask;
}
