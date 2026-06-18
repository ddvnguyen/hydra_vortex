using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core.Integration;

[Collection("StreamingIntegrationTests")]
public sealed class EngineModeTests
{
    internal sealed class EngineTestRpcClient : RpcClient
    {
        public List<(OpCode Op, string Key, byte[] Payload)> Calls { get; } = new();

        public EngineTestRpcClient() : base("test", 0) { }

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct)
        {
            Calls.Add((op, key, payload.ToArray()));

            var response = op switch
            {
                OpCode.EnginePrefill => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096 }),
                    new byte[4096]),

                OpCode.EngineDecode => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 1050, tokens_generated = 50, stop_reason = "complete" }),
                    Encoding.UTF8.GetBytes("Hello from engine decode")),

                OpCode.StateGet => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000 }),
                    new byte[2048]),

                _ => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, stored = true, restored = true, erased = true }),
                    [])
            };
            return Task.FromResult(response);
        }

        public void ClearCalls() => Calls.Clear();
        public bool HasCall(OpCode op) => Calls.Any(c => c.Op == op);
        public bool HasCall(OpCode op, string keyContains)
            => Calls.Any(c => c.Op == op && c.Key.Contains(keyContains));
        public int CountCalls(OpCode op) => Calls.Count(c => c.Op == op);

        public string PayloadAsUtf8(OpCode op)
        {
            var pair = Calls.FirstOrDefault(c => c.Op == op);
            return pair == default ? "" : Encoding.UTF8.GetString(pair.Payload);
        }
    }

    internal sealed class EngineFixture : IAsyncDisposable
    {
        public CoordinatorConfig Cfg { get; }
        public SessionLedger Ledger { get; }
        public WorkerTracker Tracker { get; }
        public ICompletionProxyService Proxy { get; }
        public IHealthMonitorService Health { get; }
        public EngineTestRpcClient Rpc { get; } = new();
        public WorkerSchedulerService Scheduler { get; }
        private readonly CancellationTokenSource _runCts = new();
        private readonly Task _runTask;

        public EngineFixture(
            string runMode = "concurrency",
            int rtxSlots = 2,
            int p100Slots = 1)
        {
            Health = new TestHealthMonitor();
            Proxy = new TestCompletionProxy(totalTokens: 150, slotId: 0);
            Ledger = new SessionLedger();
            Tracker = new WorkerTracker();

            Cfg = new CoordinatorConfig
            {
                RunMode = runMode,
                UseLlamaEngine = true,
                PrefixCheckpointEnabled = false,
                WarmSlotVerificationEnabled = false,
                MixPrecisionEnabled = false,
                AtomicThreshold = 2048,
                Workers = new List<WorkerConfig>
                {
                    new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = rtxSlots,  PrefillPriority = 1, DecodePriority = 2 },
                    new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = p100Slots, PrefillPriority = 100, DecodePriority = 1 },
                }
            };
            foreach (var w in Cfg.Workers)
                Tracker.InitWorker(w.Name, w.Slots);

            var sp = new ServiceCollection().BuildServiceProvider();
            Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, Proxy, Health, Rpc,
                sp, Serilog.Log.Logger);
            Scheduler.AgentClientFactory = (_, _) => Rpc;

            _runTask = Scheduler.RunAsync(_runCts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _runCts.Cancel();
            try { await _runTask; } catch (OperationCanceledException) { }
            _runCts.Dispose();
        }

        public async Task<object?> SubmitAsync(
            string sessionId, int estimatedTokens, int maxTokens = 500,
            bool stream = false, string? prefixHash = null)
        {
            var req = new Dictionary<string, object>
            {
                ["stream"] = stream,
                ["max_tokens"] = maxTokens,
                ["model"] = "nano"
            };
            var msgs = new List<Dictionary<string, object>>
            {
                new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
            };
            return await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
                maxTokens, prefixHash, _runCts.Token);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Cold atomic path
    //
    // Engine mode now uses HTTP proxy for chat completions (issue #273 hotfix),
    // so the assertions on EngineDecode RPC have moved to Proxy.NonStreamingCalls.
    // The engine RPC is still used for prefill (EnginePrefill) and KV state
    // (StateGet/Put).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Atomic_Cold_HttpProxyCalled_NoEngineRpcDecode()
    {
        await using var f = new EngineFixture(runMode: "fast");
        var proxy = (TestCompletionProxy)f.Proxy;

        await f.SubmitAsync("sess_ea1", 500, 100);

        // Issue #273: chat completions must use HTTP proxy to preserve OAI schema
        // (content, reasoning_content, finish_reason, id_slot, timings).
        Assert.Single(proxy.NonStreamingCalls);
        Assert.Equal("http://localhost:8080", proxy.NonStreamingCalls[0].NodeUrl);

        // Engine RPC no longer drives chat completions.
        Assert.False(f.Rpc.HasCall(OpCode.EngineDecode),
            "Engine chat-completion path is disabled (issue #273 hotfix); HTTP proxy owns text responses");
        Assert.False(f.Rpc.HasCall(OpCode.EnginePrefill),
            "Engine atomic should NOT call EnginePrefill separately");
        Assert.False(f.Rpc.HasCall(OpCode.StatePut),
            "Engine atomic should NOT call StatePut for KV restore");

        var e = f.Ledger.Lookup("sess_ea1");
        Assert.NotNull(e);
        Assert.True(e!.NPast > 0, $"NPast should be > 0 after atomic, got {e.NPast}");
    }

    [Fact]
    public async Task Atomic_WarmFollowup_HttpProxyCalled()
    {
        await using var f = new EngineFixture(runMode: "fast");
        var proxy = (TestCompletionProxy)f.Proxy;

        await f.SubmitAsync("sess_ea2", 500, 100);
        int np1 = f.Ledger.Lookup("sess_ea2")!.NPast;
        Assert.True(np1 > 0);

        proxy.NonStreamingCalls.Clear();
        await f.SubmitAsync("sess_ea2", 300, 50);

        // Warm follow-up still goes through HTTP proxy (warm-affinity path),
        // not EngineDecode RPC.
        Assert.Single(proxy.NonStreamingCalls);
        Assert.False(f.Rpc.HasCall(OpCode.EngineDecode),
            "Warm follow-up must not use EngineDecode (issue #273)");

        int np2 = f.Ledger.Lookup("sess_ea2")!.NPast;
        Assert.True(np2 >= np1, $"NPast should grow: {np1} -> {np2}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Cold concurrency (P/D split) path
    //
    // In engine mode, prefill still uses EnginePrefill RPC, KV state moves
    // through StateGet/Put, and the final decode on the P100 uses the HTTP
    // proxy. EnginePrefill remains untouched by the hotfix.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrency_EnginePrefillCalled_HttpProxyDoesDecode()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);
        var proxy = (TestCompletionProxy)f.Proxy;

        await f.SubmitAsync("sess_ec1", 3000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "P/D split should call EnginePrefill");
        Assert.False(f.Rpc.HasCall(OpCode.EngineDecode),
            "Engine chat-completion path is disabled (issue #273 hotfix)");

        // Decode happens via HTTP proxy on the P100 node.
        Assert.Single(proxy.NonStreamingCalls);
        Assert.Equal("http://192.168.122.21:8086", proxy.NonStreamingCalls[0].NodeUrl);

        Assert.True(f.Rpc.HasCall(OpCode.StatePut),
            "P/D split should restore KV via StatePut");

        var e = f.Ledger.Lookup("sess_ec1");
        Assert.NotNull(e);
        Assert.True(e!.HasStoreState, "Should have store state after P/D split");
    }

    [Fact]
    public async Task Concurrency_EnginePrefillPayloadIsJson()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);

        await f.SubmitAsync("sess_ec2", 3000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill));
        var payload = f.Rpc.PayloadAsUtf8(OpCode.EnginePrefill);
        Assert.Contains("\"messages\"", payload);
        Assert.Contains("\"n_predict\"", payload);
    }

    [Fact]
    public async Task Concurrency_SaveKvStoresKvBlob()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);

        await f.SubmitAsync("sess_ec3", 3000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.Put, "sess_ec3"),
            "SaveKv should store KV under session key");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Same-node skip
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SameNode_NoStatePut_HttpProxyCalled()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 0);
        var proxy = (TestCompletionProxy)f.Proxy;

        await f.SubmitAsync("sess_es1", 3000, 100);

        Assert.False(f.Rpc.HasCall(OpCode.StatePut),
            "Same-node skip should NOT call StatePut");
        Assert.False(f.Rpc.HasCall(OpCode.EngineDecode),
            "Engine chat-completion path is disabled (issue #273 hotfix)");

        // Same-node decode still goes through HTTP proxy.
        Assert.Single(proxy.NonStreamingCalls);
        Assert.Equal("http://localhost:8080", proxy.NonStreamingCalls[0].NodeUrl);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Migration path
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_StatePutCalled_HttpProxyDoesDecode()
    {
        await using var f = new EngineFixture(rtxSlots: 1, p100Slots: 1);
        var proxy = (TestCompletionProxy)f.Proxy;

        await f.SubmitAsync("sess_em1", 3000, 100);

        var e = f.Ledger.Lookup("sess_em1");
        Assert.NotNull(e);
        e!.SlotFreed = true;
        e.HasStoreState = true;

        f.Rpc.ClearCalls();
        proxy.NonStreamingCalls.Clear();
        await f.SubmitAsync("sess_em1", 100, 50);

        Assert.True(f.Rpc.HasCall(OpCode.StatePut),
            "Migration should restore KV via StatePut");
        Assert.False(f.Rpc.HasCall(OpCode.EngineDecode),
            "Engine chat-completion path is disabled (issue #273 hotfix)");

        Assert.Single(proxy.NonStreamingCalls);
    }

    // ─────────────────────────────────────────────────────────────────────
    // RPC call sequence
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrency_RpcSequence_EnginePrefillBeforeStatePutBeforeProxy()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);
        var proxy = (TestCompletionProxy)f.Proxy;

        await f.SubmitAsync("sess_er1", 3000, 100);

        var calls = f.Rpc.Calls.Select(c => c.Op).ToList();

        var prefillIdx = calls.IndexOf(OpCode.EnginePrefill);
        var statePutIdx = calls.IndexOf(OpCode.StatePut);

        Assert.True(prefillIdx >= 0, "EnginePrefill must be called");
        Assert.True(statePutIdx >= 0, "StatePut must be called");
        Assert.True(prefillIdx < statePutIdx,
            "EnginePrefill must precede StatePut (P/D ordering)");
        Assert.Single(proxy.NonStreamingCalls);
    }

    [Fact]
    public async Task Atomic_RpcSequence_NoEngineDecodeOrPrefillOrStatePut()
    {
        await using var f = new EngineFixture(runMode: "fast");
        var proxy = (TestCompletionProxy)f.Proxy;

        await f.SubmitAsync("sess_er2", 500, 100);

        Assert.DoesNotContain(OpCode.EngineDecode, f.Rpc.Calls.Select(c => c.Op));
        Assert.DoesNotContain(OpCode.EnginePrefill, f.Rpc.Calls.Select(c => c.Op));
        Assert.DoesNotContain(OpCode.StatePut, f.Rpc.Calls.Select(c => c.Op));
        Assert.Single(proxy.NonStreamingCalls);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Issue #273 regression: response preserves OAI schema (reasoning_content)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Atomic_Response_PreservesReasoningContent()
    {
        // Regression test for #273. The Qwopus3.6-35B-A3B model is run with
        // --reasoning on, so the chat template emits the model's chain-of-thought
        // in `reasoning_content` and leaves `content` empty. The Core must not
        // collapse the two fields. The proxy here simulates llama-server's
        // well-formed OAI response (as if the model returned reasoning).
        await using var f = new EngineFixture(runMode: "fast");
        var proxy = (TestCompletionProxy)f.Proxy;
        proxy.ResponseOverride = new Dictionary<string, object>
        {
            ["id"] = "chatcmpl-test",
            ["model"] = "balanced",
            ["object"] = "chat.completion",
            ["id_slot"] = 0,
            ["choices"] = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    index = 0,
                    finish_reason = "length",
                    message = new
                    {
                        role = "assistant",
                        content = "",
                        reasoning_content = "1.  **Analyze the Request**"
                    }
                }
            }),
            ["usage"] = JsonSerializer.SerializeToElement(new
            {
                prompt_tokens = 17, completion_tokens = 4, total_tokens = 21
            })
        };

        var result = await f.SubmitAsync("sess_reason_1", 500, 100);

        // The Core must pass the response through with both fields intact.
        Assert.NotNull(result);
        var dict = (Dictionary<string, object>)result!;
        Assert.True(dict.ContainsKey("choices"));
        var choices = (System.Text.Json.JsonElement)dict["choices"];
        var msg = choices[0].GetProperty("message");
        Assert.Equal("", msg.GetProperty("content").GetString());
        Assert.Equal("1.  **Analyze the Request**",
            msg.GetProperty("reasoning_content").GetString());
    }
}
