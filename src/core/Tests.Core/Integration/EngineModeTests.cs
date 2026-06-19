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
        /// <summary>When set, EnginePipelineAttach/EngineSetExpertMode report failure (peer down).</summary>
        public bool FailMultiEngineAttach { get; set; }

        public EngineTestRpcClient() : base("test", 0) { }

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct)
        {
            Calls.Add((op, key, payload.ToArray()));

            if ((op == OpCode.EnginePipelineAttach || op == OpCode.EngineSetExpertMode) && FailMultiEngineAttach)
                return Task.FromResult(new RpcResponse(
                    (byte)StatusCode.Error,
                    JsonSerializer.Serialize(new { mode = "solo", peer_connected = false }),
                    []));

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
            int p100Slots = 1,
            bool pipeline = false,
            bool combined = false,
            string multiPolicy = "pipeline")
        {
            Health = new TestHealthMonitor();
            Proxy = new TestCompletionProxy(totalTokens: 150, slotId: 0);
            Ledger = new SessionLedger();
            Tracker = new WorkerTracker();

            var multiEngine = pipeline || combined;
            Cfg = new CoordinatorConfig
            {
                RunMode = runMode,
                UseLlamaEngine = true,
                PrefixCheckpointEnabled = false,
                WarmSlotVerificationEnabled = false,
                MixPrecisionEnabled = false,
                AtomicThreshold = 2048,
                PipelineEnabled = pipeline,
                CombinedEnabled = combined,
                MultiEnginePolicy = multiPolicy,
                MultiEngineThreshold = 10,
                Workers = new List<WorkerConfig>
                {
                    new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = rtxSlots,  PrefillPriority = 1, DecodePriority = 2,
                        Role = multiEngine ? "head" : "standalone", PeerWorker = multiEngine ? "p100" : null,
                        PeerHost = "192.168.122.21", PeerPort = 9700,
                        PipelineCapable = multiEngine, CombinedCapable = multiEngine,
                        PipelineOtSplit = "blk\\.(2[0-9]|3[0-9])\\..*=PEER", CombinedOtSplit = "ffn_.*_exps=PEER" },
                    new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = p100Slots, PrefillPriority = 100, DecodePriority = 1, Role = multiEngine ? "worker" : "standalone" },
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
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Atomic_Cold_EngineDecodeCalledWithMessages()
    {
        await using var f = new EngineFixture(runMode: "fast");

        await f.SubmitAsync("sess_ea1", 500, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EngineDecode),
            "Engine atomic should call EngineDecode RPC");

        var payload = f.Rpc.PayloadAsUtf8(OpCode.EngineDecode);
        Assert.Contains("\"messages\"", payload);
        Assert.Contains("\"n_predict\"", payload);

        Assert.False(f.Rpc.HasCall(OpCode.EnginePrefill),
            "Engine atomic should NOT call EnginePrefill separately");
        Assert.False(f.Rpc.HasCall(OpCode.StatePut),
            "Engine atomic should NOT call StatePut for KV restore");

        var e = f.Ledger.Lookup("sess_ea1");
        Assert.NotNull(e);
        Assert.True(e!.NPast > 0, $"NPast should be > 0 after engine atomic, got {e.NPast}");
    }

    [Fact]
    public async Task Atomic_WarmFollowup_NoMessages()
    {
        await using var f = new EngineFixture(runMode: "fast");

        await f.SubmitAsync("sess_ea2", 500, 100);
        int np1 = f.Ledger.Lookup("sess_ea2")!.NPast;
        Assert.True(np1 > 0);

        f.Rpc.ClearCalls();
        await f.SubmitAsync("sess_ea2", 300, 50);

        var payload = f.Rpc.PayloadAsUtf8(OpCode.EngineDecode);
        Assert.Contains("\"messages\":null", payload);

        int np2 = f.Ledger.Lookup("sess_ea2")!.NPast;
        Assert.True(np2 >= np1, $"NPast should grow: {np1} -> {np2}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Two-engine "work together" (PIPELINE / COMBINED)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiEngine_Pipeline_AttachesPeerAndDecodes()
    {
        await using var f = new EngineFixture(pipeline: true);

        var result = await f.SubmitAsync("sess_me1", 20000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EnginePipelineAttach),
            "Large request should attach the pipeline peer");
        var attach = f.Rpc.PayloadAsUtf8(OpCode.EnginePipelineAttach);
        Assert.Contains("ot_split", attach);
        Assert.Contains("PEER", attach);
        Assert.True(f.Rpc.HasCall(OpCode.EngineDecode), "Decode runs on the head");

        var dict = Assert.IsType<Dictionary<string, object>>(result);
        var hydra = Assert.IsType<Dictionary<string, object>>(dict["hydra"]);
        Assert.Equal("pipeline", hydra["engine_mode"]);
        Assert.Equal("p100", hydra["peer"]);
        Assert.False((bool)hydra["fell_back"]);
    }

    [Fact]
    public async Task MultiEngine_Combined_SetsExpertMode()
    {
        await using var f = new EngineFixture(combined: true, multiPolicy: "combined");

        var result = await f.SubmitAsync("sess_me2", 20000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EngineSetExpertMode),
            "COMBINED should flip expert mode on the head");
        Assert.Equal("combined", Encoding.UTF8.GetString(
            f.Rpc.Calls.First(c => c.Op == OpCode.EngineSetExpertMode).Payload));

        var hydra = Assert.IsType<Dictionary<string, object>>(
            ((Dictionary<string, object>)result!)["hydra"]);
        Assert.Equal("combined", hydra["engine_mode"]);
    }

    [Fact]
    public async Task MultiEngine_FallsBackToSolo_WhenPeerDeclines()
    {
        await using var f = new EngineFixture(pipeline: true);
        f.Rpc.FailMultiEngineAttach = true;

        var result = await f.SubmitAsync("sess_me3", 20000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EnginePipelineAttach), "Attach is attempted");
        Assert.True(f.Rpc.HasCall(OpCode.EngineDecode), "Decode still runs (solo)");

        var hydra = Assert.IsType<Dictionary<string, object>>(
            ((Dictionary<string, object>)result!)["hydra"]);
        Assert.True((bool)hydra["fell_back"]);
        Assert.Equal("solo", hydra["engine_mode"]);
        Assert.Equal("pipeline", hydra["requested_mode"]);
    }

    [Fact]
    public async Task MultiEngine_Disabled_NoAttach()
    {
        await using var f = new EngineFixture(); // pipeline/combined both off

        await f.SubmitAsync("sess_me4", 20000, 100);

        Assert.False(f.Rpc.HasCall(OpCode.EnginePipelineAttach),
            "No peer attach when multi-engine is disabled");
        Assert.False(f.Rpc.HasCall(OpCode.EngineSetExpertMode));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Cold concurrency (P/D split) path
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrency_EnginePrefillAndDecodeCalled()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);

        await f.SubmitAsync("sess_ec1", 3000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "P/D split should call EnginePrefill");
        Assert.True(f.Rpc.HasCall(OpCode.EngineDecode),
            "P/D split should call EngineDecode");

        var decodePayload = f.Rpc.PayloadAsUtf8(OpCode.EngineDecode);
        Assert.Contains("\"messages\":null", decodePayload);

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
    public async Task SameNode_NoStatePut_EngineDecodeNoMessages()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 0);

        await f.SubmitAsync("sess_es1", 3000, 100);

        Assert.False(f.Rpc.HasCall(OpCode.StatePut),
            "Same-node skip should NOT call StatePut");
        Assert.True(f.Rpc.HasCall(OpCode.EngineDecode));

        var payload = f.Rpc.PayloadAsUtf8(OpCode.EngineDecode);
        Assert.Contains("\"messages\":null", payload);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Migration path
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_EngineDecodeWithoutMessages()
    {
        await using var f = new EngineFixture(rtxSlots: 1, p100Slots: 1);

        await f.SubmitAsync("sess_em1", 3000, 100);

        var e = f.Ledger.Lookup("sess_em1");
        Assert.NotNull(e);
        e!.SlotFreed = true;
        e.HasStoreState = true;

        f.Rpc.ClearCalls();
        await f.SubmitAsync("sess_em1", 100, 50);

        Assert.True(f.Rpc.HasCall(OpCode.StatePut),
            "Migration should restore KV via StatePut");

        var payload = f.Rpc.PayloadAsUtf8(OpCode.EngineDecode);
        Assert.Contains("\"messages\":null", payload);
    }

    // ─────────────────────────────────────────────────────────────────────
    // RPC call sequence
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrency_RpcSequence_EnginePrefillBeforeDecode()
    {
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);

        await f.SubmitAsync("sess_er1", 3000, 100);

        var calls = f.Rpc.Calls.Select(c => c.Op).ToList();

        var prefillIdx = calls.IndexOf(OpCode.EnginePrefill);
        var decodeIdx = calls.IndexOf(OpCode.EngineDecode);
        var statePutIdx = calls.IndexOf(OpCode.StatePut);

        Assert.True(prefillIdx >= 0, "EnginePrefill must be called");
        Assert.True(decodeIdx >= 0, "EngineDecode must be called");
        Assert.True(statePutIdx >= 0, "StatePut must be called");
        Assert.True(prefillIdx < decodeIdx,
            "EnginePrefill must precede EngineDecode");
        Assert.True(statePutIdx < decodeIdx,
            "StatePut must precede EngineDecode");
    }

    [Fact]
    public async Task Atomic_RpcSequence_SingleDecodeOnly()
    {
        await using var f = new EngineFixture(runMode: "fast");

        await f.SubmitAsync("sess_er2", 500, 100);

        Assert.Contains(OpCode.EngineDecode, f.Rpc.Calls.Select(c => c.Op));
        Assert.DoesNotContain(OpCode.EnginePrefill, f.Rpc.Calls.Select(c => c.Op));
        Assert.DoesNotContain(OpCode.StatePut, f.Rpc.Calls.Select(c => c.Op));
    }
}
