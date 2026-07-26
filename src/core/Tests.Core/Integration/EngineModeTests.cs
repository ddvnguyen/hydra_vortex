using System.Text;
using System.Text.Json;
using System.Threading;
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

        // Regression hooks for #279: when set, the matching opcode returns
        // non-OK status with empty meta, simulating an out-of-date llama-server
        // binary (or any future engine RPC regression).
        public bool MakeEnginePrefillFail { get; set; } = false;

        // When set, the engine returns NotImplemented — the old binary path
        // (#279) that should still trigger HTTP fallback.
        public bool MakeEnginePrefillNotImplemented { get; set; } = false;

        // When set, the engine prefill throws OperationCanceledException as if
        // the caller's CancellationToken was cancelled. Review note: the catch
        // in PrefillAsync must filter this out so it doesn't masquerade as a
        // binary-mismatch RPC error and pollute the fallback counter.
        public bool MakeEnginePrefillThrowCancellation { get; set; } = false;

        /// <summary>When set, StatePut returns Error status (simulates restore failure).</summary>
        public bool MakeStatePutFail { get; set; } = false;

        /// <summary>When set, StatePut returns model_match=false (simulates cross-model mismatch).
        /// Only fires once — subsequent StatePut calls return match=true so re-prefill succeeds.</summary>
        public bool MakeStatePutMismatch { get; set; } = false;

        /// <summary>When set, StatePut returns model_match=true but a differing model identity.
        /// Triggers CrossModelGuard.Decide identity comparison. Only fires once.</summary>
        public bool MakeStatePutHashMismatch { get; set; } = false;

        private int _statePutMismatchFired;
        private int _statePutHashMismatchFired;

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

            // Phase 2b (#481): COMBINED mode now sends hydra_config via PREFILL
            // instead of SET_EXPERT_MODE. When FailMultiEngineAttach is set and
            // this is a PREFILL with hydra_config (combined mode activation),
            // simulate the engine accepting the PREFILL but being unable to
            // activate combined mode (peer down) by returning model_fallback.
            // This makes HydraConfigDeliveredSucceeded=false, so
            // ApplyMultiEngineAsync at decode time records the fallback and
            // the request continues as solo. Returning Error here would cause
            // PrefillAsync to treat it as BUSY and retry with hydra_config
            // still set, creating an infinite loop.
            if (op == OpCode.EnginePrefill && FailMultiEngineAttach && payload.Length > 0)
            {
                var payloadStr = Encoding.UTF8.GetString(payload.Span);
                if (payloadStr.Contains("hydra_config"))
                    return Task.FromResult(new RpcResponse(
                        (byte)StatusCode.Ok,
                        JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096, model_fallback = true }),
                        new byte[4096]));
            }

            var response = op switch
            {
                OpCode.EnginePrefill when MakeEnginePrefillThrowCancellation => throw
                    new OperationCanceledException("simulated caller cancellation during engine prefill"),

                OpCode.EnginePrefill when MakeEnginePrefillNotImplemented => new RpcResponse(
                    (byte)StatusCode.NotImplemented,
                    Meta: null,
                    Payload: Array.Empty<byte>()),

                OpCode.EnginePrefill when MakeEnginePrefillFail => new RpcResponse(
                    (byte)StatusCode.Error, // real error — should NOT fall back
                    Meta: null,
                    Payload: Array.Empty<byte>()),

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

                OpCode.StatePut when MakeStatePutFail => new RpcResponse(
                    (byte)StatusCode.Error,
                    Meta: null,
                    Payload: Array.Empty<byte>()),

                OpCode.StatePut when MakeStatePutMismatch && Interlocked.CompareExchange(ref _statePutMismatchFired, 1, 0) == 0 => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, model_match = false, tokenizer = "gpt2", model_name = "other_model", model_quant = "Q4_K", model_capabilities = 0, model_alias = "other_model", model_path = "/wrong/path" }),
                    Array.Empty<byte>()),

                // #470: model_match=true but hash differs — triggers CrossModelGuard.Decide
                OpCode.StatePut when MakeStatePutHashMismatch && Interlocked.CompareExchange(ref _statePutHashMismatchFired, 1, 0) == 0 => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, model_match = true, tokenizer = "llama", model_name = "DifferentModel", model_quant = "Q5_K", model_capabilities = 1, model_alias = "nano", model_path = "/dev/null" }),
                    Array.Empty<byte>()),

                OpCode.StatePut => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, model_match = true, tokenizer = "llama", model_name = "nano", model_quant = "Q4_K", model_capabilities = 0, model_alias = "nano", model_path = "/dev/null" }),
                    Array.Empty<byte>()),

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
        /// <summary>Count EnginePrefill calls whose payload contains the given substring.</summary>
        public int CountPrefillCallsWithPayload(string contains)
            => Calls.Count(c => c.Op == OpCode.EnginePrefill && Encoding.UTF8.GetString(c.Payload).Contains(contains));

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
                        ModelAlias = multiEngine ? "moe-35b-solo" : null },
                    new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = p100Slots, PrefillPriority = 100, DecodePriority = 1, Role = multiEngine ? "worker" : "standalone" },
                }
            };
            foreach (var w in Cfg.Workers)
                Tracker.InitWorker(w.Name, w.Slots);

            var sp = new ServiceCollection().BuildServiceProvider();
            Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, Proxy, Health, Rpc,
                sp, Serilog.Log.Logger);
            Scheduler.AgentClientFactory = (_, _) => Rpc;

            // Register test model so the "nano" alias used by these tests
            // passes the unknown-model validation in SubmitAsync.
            ModelRegistry.ClearForTest();
            ModelRegistry.RegisterForTest(new EngineConfig(
                ModelAlias: "nano",
                ModelPath: "/dev/null",
                NGpuLayers: 0, NCtx: 2048,
                ContBatching: true, Fit: false, UbatchSize: 512,
                SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));
            // Also register "moe-35b-solo" which is used as ModelAlias on the head worker.
            ModelRegistry.RegisterForTest(new EngineConfig(
                ModelAlias: "moe-35b-solo",
                ModelPath: "/dev/null",
                NGpuLayers: 99, NCpuMoe: 8, NCtx: 320000,
                OverrideTensors: new[] { "blk.*.ffn_*_exps.weight=CPU" },
                ContBatching: true, Fit: false, UbatchSize: 512,
                SpecType: "draft-mtp", SpecDraftNMax: 3, SpecDraftPMin: 0.75f, SpecDraftNgl: 0));

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
            bool stream = false, string? prefixHash = null, string? forceMode = null)
        {
            var req = new Dictionary<string, object>
            {
                ["stream"] = stream,
                ["max_tokens"] = maxTokens,
                ["model"] = "nano"
            };
            if (forceMode is not null)
                req["force_mode"] = forceMode;
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
    // Two-engine "work together" (PIPELINE / COMBINED)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiEngine_Pipeline_AttachesPeerAndDecodes()
    {
        await using var f = new EngineFixture(pipeline: true);
        var proxy = (TestCompletionProxy)f.Proxy;

        var result = await f.SubmitAsync("sess_me1", 20000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EnginePipelineAttach),
            "Large request should attach the pipeline peer");
        var attach = f.Rpc.PayloadAsUtf8(OpCode.EnginePipelineAttach);
        Assert.Contains("ot_split", attach);
        Assert.Contains("peer", attach);
        // Decode itself always goes through the HTTP proxy (issue #273 hotfix);
        // PIPELINE only changes which tensors the head/peer own underneath.
        Assert.False(f.Rpc.HasCall(OpCode.EngineDecode),
            "Engine chat-completion path is disabled (issue #273 hotfix)");
        Assert.Single(proxy.NonStreamingCalls);
        Assert.Equal("http://localhost:8080", proxy.NonStreamingCalls[0].NodeUrl);

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

        // Phase 2b (#481): COMBINED mode now sends hydra_config via PREFILL
        // instead of SET_EXPERT_MODE. The engine reads split_mode, tensor_split,
        // rpc_servers from the hydra_config dict to configure the COMBINED path.
        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "COMBINED should call EnginePrefill with hydra_config");
        var payload = f.Rpc.PayloadAsUtf8(OpCode.EnginePrefill);
        Assert.Contains("hydra_config", payload);

        // Exactly ONE EnginePrefill with hydra_config — the auto-multiengine path
        // (MultiEngineRouter.Select → Decode directly) must send it once here,
        // not redundantly in both PrefillAsync and ApplyMultiEngineAsync.
        Assert.Equal(1, f.Rpc.CountPrefillCallsWithPayload("hydra_config"));

        var hydra = Assert.IsType<Dictionary<string, object>>(
            ((Dictionary<string, object>)result!)["hydra"]);
        Assert.Equal("combined", hydra["engine_mode"]);
    }

    [Fact]
    public async Task MultiEngine_Combined_ForceMode_PrefillPath_SendsHydraConfigExactlyOnce()
    {
        // Force the combined path through Prefill (ForceMultiEnginePlan → PrefixRestore → Prefill → Decode).
        // PrefillAsync delivers hydra_config with real content; ApplyMultiEngineAsync must NOT send it again.
        await using var f = new EngineFixture(combined: true, multiPolicy: "combined");

        var result = await f.SubmitAsync("sess_me2_force", 20000, 100, forceMode: "combined");

        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "COMBINED force-mode should call EnginePrefill with hydra_config");
        var payload = f.Rpc.PayloadAsUtf8(OpCode.EnginePrefill);
        Assert.Contains("hydra_config", payload);

        // Exactly ONE EnginePrefill with hydra_config — PrefillAsync sent it with
        // real content, ApplyMultiEngineAsync must skip the redundant empty-body one.
        Assert.Equal(1, f.Rpc.CountPrefillCallsWithPayload("hydra_config"));

        var hydra = Assert.IsType<Dictionary<string, object>>(
            ((Dictionary<string, object>)result!)["hydra"]);
        Assert.Equal("combined", hydra["engine_mode"]);
    }

    [Fact]
    public async Task MultiEngine_FallsBackToSolo_WhenPeerDeclines()
    {
        await using var f = new EngineFixture(pipeline: true);
        f.Rpc.FailMultiEngineAttach = true;
        var proxy = (TestCompletionProxy)f.Proxy;

        var result = await f.SubmitAsync("sess_me3", 20000, 100);

        Assert.True(f.Rpc.HasCall(OpCode.EnginePipelineAttach), "Attach is attempted");
        // Decode still runs (solo), via the HTTP proxy like every other decode.
        Assert.Single(proxy.NonStreamingCalls);

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

    // ── P3.0 (#366): per-GPU exclusive peer reservation ──

    [Fact]
    public async Task Combined_Exclusive_Reserve_Is_Released_On_Completion()
    {
        // After a COMBINED request completes, the peer's exclusive reservation
        // must be released so the peer returns to the pool of routable workers.
        // p100 is configured with 2 slots to make the multi-slot invariant
        // visible: a peer's "free" status must NOT collapse when one slot
        // is busy, but it MUST collapse when exclusively reserved.
        await using var f = new EngineFixture(combined: true, multiPolicy: "combined", p100Slots: 2);

        var result = await f.SubmitAsync("sess_p30_1", 20000, 100);

        // Phase 2b (#481): COMBINED mode now sends hydra_config via PREFILL
        // instead of SET_EXPERT_MODE.
        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "COMBINED should call EnginePrefill with hydra_config");

        // Exactly ONE EnginePrefill with hydra_config — no redundant activation.
        Assert.Equal(1, f.Rpc.CountPrefillCallsWithPayload("hydra_config"));

        var hydra = Assert.IsType<Dictionary<string, object>>(
            ((Dictionary<string, object>)result!)["hydra"]);
        Assert.Equal("combined", hydra["engine_mode"]);

        // After completion, the peer must be back to the routable pool —
        // the exclusive reservation has been released.
        Assert.False(f.Tracker.IsExclusiveReserved("p100"));
        Assert.True(f.Tracker.HasFreeSlot("p100"));
        Assert.True(f.Tracker.IsFree("p100"));
        Assert.Contains("p100", f.Tracker.FreeWorkers());
    }

    [Fact]
    public async Task Combined_Solo_Fallback_Still_Releases_Exclusive_Reserve()
    {
        // P3.0 §"Failure handling": when the activate-degrade path
        // (ReportsSolo = true) fires — the engine could not flip expert mode
        // because the peer wasn't really there — the exclusive reservation
        // must STILL be released (we don't want to strand the peer just
        // because the head went solo).
        await using var f = new EngineFixture(combined: true, multiPolicy: "combined", p100Slots: 2);
        f.Rpc.FailMultiEngineAttach = true;

        var result = await f.SubmitAsync("sess_p30_2", 20000, 100);

        var hydra = Assert.IsType<Dictionary<string, object>>(
            ((Dictionary<string, object>)result!)["hydra"]);
        Assert.True((bool)hydra["fell_back"]);
        Assert.Equal("solo", hydra["engine_mode"]);

        Assert.False(f.Tracker.IsExclusiveReserved("p100"),
            "Solo-fallback must release the exclusive reservation");
        Assert.True(f.Tracker.HasFreeSlot("p100"));
    }

    [Fact]
    public void Peer_Exclusive_Reserve_Blocks_Concurrent_Solo_Acquisition()
    {
        // The core #21 invariant: while the peer is exclusively reserved for
        // COMBINED, no concurrent actor (SOLO, another COMBINED, or any
        // other routing path) may acquire a slot on it. All the existing
        // routing entry points consult IWorkerTracker, which gates on
        // ExclusiveReserved — verified here at the tracker level (the
        // scheduler routes through this same gate).
        var tracker = new WorkerTracker();
        tracker.InitWorker("p100", 2);

        // Pre-reserve the peer as if COMBINED were driving it.
        Assert.True(tracker.TryReserveWorkerExclusive("p100"));

        // Every "I want a slot on p100" entry point must be closed.
        Assert.False(tracker.TryAcquireSlot("p100", out _, "decode"),
            "TryAcquireSlot must fail when peer is exclusively reserved");
        Assert.False(tracker.TryAcquireSlot("p100", out _, "prefill"),
            "TryAcquireSlot must fail regardless of role when peer is exclusively reserved");
        Assert.False(tracker.TryAcquireSlot("p100", out _, "migrate"),
            "TryAcquireSlot must fail for migrate too");
        Assert.False(tracker.Acquire("p100", "decode"),
            "Legacy Acquire must also fail");

        // And the peer is not in the candidate list for new requests.
        Assert.False(tracker.IsFree("p100"));
        Assert.False(tracker.HasFreeSlot("p100"));
        Assert.Equal(0, tracker.FreeSlotCount("p100"));
        Assert.DoesNotContain("p100", tracker.FreeWorkers());

        // Release restores all of the above.
        tracker.ReleaseWorkerExclusive("p100");
        Assert.True(tracker.TryAcquireSlot("p100", out _, "decode"));
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
    public async Task Concurrency_RestoreFails_NoFallback_ReRoutesWithoutCrash()
    {
        // Regression: RestoreKvAsync catch block must set item.State=RouteDecision
        // before returning None, otherwise RunItemPipeline re-dispatches with
        // State=RestoreKv and hits NullReferenceException on item.DecodeWorker.
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);
        ((EngineTestRpcClient)f.Rpc).MakeStatePutFail = true;

        // Submit should NOT throw NullReferenceException — it must re-route
        // or exhaust retries gracefully.
        var ex = await Record.ExceptionAsync(async () =>
            await f.SubmitAsync("sess_restore_fail", 3000, 100));

        // The request may fail (MaxRetries) but must NOT crash with NRE.
        Assert.Null(ex);

        // StatePut was attempted (restore tried and failed)
        Assert.True(f.Rpc.HasCall(OpCode.StatePut),
            "StatePut should have been attempted before the failure");
    }

    [Fact]
    public async Task Concurrency_StatePutModelMismatch_AbortsAndReRoutes()
    {
        // Issue #470: When STATE_PUT returns model_match=false, the coordinator
        // must abort the restore, erase the slot, and re-route the request.
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);
        ((EngineTestRpcClient)f.Rpc).MakeStatePutMismatch = true;

        // Submit should NOT throw — it must re-route gracefully after the mismatch.
        var ex = await Record.ExceptionAsync(async () =>
            await f.SubmitAsync("sess_mismatch", 3000, 100));

        Assert.Null(ex);

        // StatePut was attempted (restore tried and detected mismatch)
        Assert.True(f.Rpc.HasCall(OpCode.StatePut),
            "StatePut should have been attempted before the mismatch detection");
    }

    [Fact]
    public async Task Concurrency_StatePutHashMismatch_CrossModelGuardCatches()
    {
        // Issue #470: STATE_PUT returns model_match=true but a differing model identity.
        // CrossModelGuard.Decide must detect the identity mismatch and abort the restore.
        await using var f = new EngineFixture(rtxSlots: 2, p100Slots: 1);
        ((EngineTestRpcClient)f.Rpc).MakeStatePutHashMismatch = true;

        // Submit should NOT throw — CrossModelGuard catches the hash mismatch
        // and re-routes the request.
        var ex = await Record.ExceptionAsync(async () =>
            await f.SubmitAsync("sess_hash_mismatch", 3000, 100));

        Assert.Null(ex);

        // StatePut was attempted (restore tried, CrossModelGuard caught hash mismatch)
        Assert.True(f.Rpc.HasCall(OpCode.StatePut),
            "StatePut should have been attempted before the hash mismatch detection");
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

    // ─────────────────────────────────────────────────────────────────────
    // Issue #277 regression: bg_save vs next-decode slot race
    //
    // Before the fix, the bg_save ran in fire-and-forget `Task.Run` after the
    // lease was disposed. A new decode on the same slot would TryAcquireSlot
    // and start its chat completion before the bg_save's StateGet RPC had
    // returned, racing on llama-server's per-slot serialization and hanging
    // for the full 30s HTTP timeout. The fix makes the bg_save await-synchronous
    // in BgSaveAsync (and the streaming equivalent in NotifyStreamComplete) so
    // the slot isn't returned to the pool until the StateGet completes.
    //
    // We assert that two consecutive turns on the same session both complete
    // well under the 30s timeout.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiTurn_ConsecutiveTurns_NoSlotRaceHang()
    {
        // Cold-atomic mode so each turn gets the same RTX slot 0 (warm affinity
        // on a non-existent entry would force migration). Two turns back-to-back
        // exercise the bg_save → new-decode path that hung in the live system.
        await using var f = new EngineFixture(runMode: "fast");
        var proxy = (TestCompletionProxy)f.Proxy;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Turn 1 — establishes a warm session entry in the ledger on RTX slot 0.
        await f.SubmitAsync("sess_race_1", 500, 100);
        var t1 = sw.ElapsedMilliseconds;
        sw.Restart();

        // Turn 2 — comes in quickly. The previous turn's bg_save must complete
        // (synchronous BgSaveAsync) before the slot is returned to the pool, so
        // the new decode gets a clean slot. Pre-fix, this hung for ~30s.
        var turn2 = f.SubmitAsync("sess_race_1", 300, 50);
        if (await Task.WhenAny(turn2, Task.Delay(5000)) != turn2)
        {
            Assert.Fail("Turn 2 hung for >5s — slot race regression (issue #277)");
        }
        await turn2;
        var t2 = sw.ElapsedMilliseconds;

        Assert.True(t1 < 3000, $"Turn 1 unexpectedly slow: {t1}ms");
        Assert.True(t2 < 3000, $"Turn 2 unexpectedly slow: {t2}ms — slot race?");
        Assert.Equal(2, proxy.NonStreamingCalls.Count);
    }

    [Fact]
    public async Task MultiTurn_StreamingConsecutiveTurns_NoSlotRaceHang()
    {
        // Same regression for the streaming path. NotifyStreamComplete's deferred
        // bg_save was also fire-and-forget; the fix awaits it before disposing
        // the warm lease. We exercise the streaming path with stream:true and
        // ensure a follow-up turn doesn't hang.
        await using var f = new EngineFixture(runMode: "fast");
        var proxy = (TestCompletionProxy)f.Proxy;

        var turn1 = f.SubmitAsync("sess_race_stream", 500, 100, stream: true);
        if (await Task.WhenAny(turn1, Task.Delay(5000)) != turn1)
        {
            Assert.Fail("Turn 1 (streaming) hung for >5s — slot race regression (issue #277)");
        }
        await turn1;

        var turn2 = f.SubmitAsync("sess_race_stream", 300, 50, stream: true);
        if (await Task.WhenAny(turn2, Task.Delay(5000)) != turn2)
        {
            Assert.Fail("Turn 2 (streaming) hung for >5s — slot race regression (issue #277)");
        }
        await turn2;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Issue #279 regression: EnginePrefill RPC fallback to HTTP
    //
    // After the fix, only NotImplemented (old binary) triggers HTTP fallback.
    // Real errors (BUSY, NotFound, Error) propagate so the routing layer can
    // retry/evict instead of silently masking the issue.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ColdConcurrency_EnginePrefillFails_RetriesThenFails()
    {
        await using var f = new EngineFixture(runMode: "concurrency");
        f.Rpc.MakeEnginePrefillFail = true;   // simulates real engine error (BUSY/Error)
        var proxy = (TestCompletionProxy)f.Proxy;

        // > 2048 estimated tokens → routes as cold_concurrency → triggers EnginePrefill
        // Real error should retry, then fail after MaxRetries with clear error message.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.SubmitAsync("sess_real_error", 5000, 100));

        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "Test setup failure: engine RPC was never called for cold_concurrency");

        // No HTTP fallback — errors throw directly.
        Assert.Empty(proxy.NonStreamingCalls);
    }

    [Fact]
    public async Task ColdConcurrency_EnginePrefillNotImplemented_FallsBackToHttp()
    {
        await using var f = new EngineFixture(runMode: "concurrency");
        f.Rpc.MakeEnginePrefillNotImplemented = true;   // simulates old binary (#279)
        var proxy = (TestCompletionProxy)f.Proxy;

        // > 2048 estimated tokens → routes as cold_concurrency → triggers EnginePrefill
        var result = await f.SubmitAsync("sess_279_fallback", 5000, 100);

        Assert.NotNull(result);
        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "Test setup failure: engine RPC was never called for cold_concurrency");

        // NotImplemented should trigger HTTP fallback.
        Assert.True(proxy.NonStreamingCalls.Count >= 1,
            "HTTP fallback did not fire — NotImplemented should trigger fallback");

        var prefillFallback = proxy.NonStreamingCalls.FirstOrDefault(c =>
            c.NodeUrl == "http://localhost:8080" &&
            c.Body.ContainsKey("n_predict"));
        Assert.NotNull(prefillFallback);
        Assert.Equal(0, Convert.ToInt32(prefillFallback.Body["n_predict"]));
    }

    [Fact]
    public async Task ColdConcurrency_EnginePrefillWorks_PrefillUsesEngineRpc()
    {
        // Counterpart to the fallback test: when the engine RPC WORKS, the
        // prefill uses the engine RPC (not HTTP). The decode still uses HTTP
        // (#273 hotfix). Verifies the fallback is conditional, not unconditional.
        await using var f = new EngineFixture(runMode: "concurrency");
        // (MakeEnginePrefillFail defaults to false)
        var proxy = (TestCompletionProxy)f.Proxy;

        var result = await f.SubmitAsync("sess_279_no_fallback", 5000, 100);

        Assert.NotNull(result);

        // Prefill: engine RPC was called
        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "Engine RPC prefill was not called — engine path is the expected prefill route");

        // Decode: HTTP was called (to the decode worker P100), exactly once.
        // (No HTTP prefill — the engine handled the prefill.)
        Assert.Single(proxy.NonStreamingCalls);
        Assert.Equal("http://192.168.122.21:8086", proxy.NonStreamingCalls[0].NodeUrl);
    }

    [Fact]
    public async Task ColdConcurrency_EnginePrefillCancelled_DoesNotFallBackToHttp()
    {
        // Review note (PR #280): the engine prefill try/catch must filter out
        // OperationCanceledException, otherwise normal caller cancellations
        // (client disconnect, server shutdown) would (a) increment
        // hydra_engine_prefill_fallbacks_total — polluting the operator's
        // "binary out of date" signal, (b) log a misleading
        // engine_prefill_fell_back_to_http warning, and (c) re-enter the HTTP
        // path with the already-cancelled token (which throws again).
        //
        // This test simulates the cancellation by making the test RPC client
        // throw OperationCanceledException on EnginePrefill, then asserts the
        // HTTP proxy was NOT called as a fallback. (The fallback would have
        // caused the proxy to fire — so an empty proxy is sufficient evidence
        // that the fallback was skipped, including the counter increment and
        // the misleading warning log.)
        await using var f = new EngineFixture(runMode: "concurrency");
        f.Rpc.MakeEnginePrefillThrowCancellation = true; // simulate caller cancellation
        var proxy = (TestCompletionProxy)f.Proxy;

        // The OCE propagates from the work item's processing back through
        // SubmitAsync. The test asserts on the side-effects (no fallback fire),
        // not on the return value.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.SubmitAsync("sess_279_cancel", 5000, 100));

        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "Test setup failure: engine RPC was never called for cold_concurrency");

        Assert.Empty(proxy.NonStreamingCalls);
    }
}
