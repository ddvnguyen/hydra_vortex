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

        // Regression hooks for #279: when set, the matching opcode returns
        // non-OK status with empty meta, simulating an out-of-date llama-server
        // binary (or any future engine RPC regression).
        public bool MakeEnginePrefillFail { get; set; } = false;
        public bool MakeEngineDecodeFail { get; set; } = false;

        // When set, the engine prefill throws OperationCanceledException as if
        // the caller's CancellationToken was cancelled. Review note: the catch
        // in PrefillAsync must filter this out so it doesn't masquerade as a
        // binary-mismatch RPC error and pollute the fallback counter.
        public bool MakeEnginePrefillThrowCancellation { get; set; } = false;

        public EngineTestRpcClient() : base("test", 0) { }

        public override Task<RpcResponse> RequestAsync(
            OpCode op, string key, ReadOnlyMemory<byte> payload,
            string traceId, CancellationToken ct)
        {
            Calls.Add((op, key, payload.ToArray()));

            var response = op switch
            {
                OpCode.EnginePrefill when MakeEnginePrefillThrowCancellation => throw
                    new OperationCanceledException("simulated caller cancellation during engine prefill"),

                OpCode.EnginePrefill when MakeEnginePrefillFail => new RpcResponse(
                    (byte)StatusCode.Error, // non-OK → triggers #279 fallback
                    Meta: null,
                    Payload: Array.Empty<byte>()),

                OpCode.EnginePrefill => new RpcResponse(
                    (byte)StatusCode.Ok,
                    JsonSerializer.Serialize(new { n_past = 2000, state_size = 4096 }),
                    new byte[4096]),

                OpCode.EngineDecode when MakeEngineDecodeFail => new RpcResponse(
                    (byte)StatusCode.Error, Meta: null, Payload: Array.Empty<byte>()),

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
    // Background: the deployed llama-server binary may be older than the
    // source it was built from. In that case, the RPC dispatch at the C++
    // side doesn't recognize opcode 0x42 (HYDRA_OP_PREFILL) and returns
    // HYDRA_STATUS_ERROR with no meta. The C# side throws an
    // InvalidOperationException and the controller returns 503 to the
    // client. To avoid a full 503 on every prompt > 2048 tokens, the
    // PrefillAsync worker falls back to the HTTP chat-completion path
    // when the engine RPC throws. The HTTP path uses the same slot and
    // the same OAI body, so the user-visible behaviour is identical
    // except for slightly higher prefill latency.
    //
    // This test simulates the regression by making the test RPC client
    // return non-OK for EnginePrefill, then asserts that:
    //   1. The engine RPC was called (proves the test setup worked)
    //   2. The HTTP proxy was called as the fallback
    //   3. The request completed successfully (no 503)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ColdConcurrency_EnginePrefillFails_FallsBackToHttp()
    {
        await using var f = new EngineFixture(runMode: "concurrency");
        f.Rpc.MakeEnginePrefillFail = true;   // simulate #279 (out-of-date binary)
        var proxy = (TestCompletionProxy)f.Proxy;

        // > 2048 estimated tokens → routes as cold_concurrency → triggers EnginePrefill
        var result = await f.SubmitAsync("sess_279_fallback", 5000, 100);

        Assert.NotNull(result);

        // 1. The engine RPC was called (and failed, recording the call). Without
        //    this, the test setup is wrong (routing didn't reach the engine path).
        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "Test setup failure: engine RPC was never called for cold_concurrency");

        // 2. The HTTP proxy was called as the fallback. The prefill HTTP body
        //    uses n_predict=0 (target localhost:8080 = RTX, the prefill worker);
        //    the decode HTTP body uses the original max_tokens (target
        //    192.168.122.21:8086 = P100, the decode worker). At least one of
        //    them is the prefill fallback, and the total is >= 1.
        Assert.True(proxy.NonStreamingCalls.Count >= 1,
            "HTTP fallback did not fire — engine prefill failed and no HTTP request was made");

        // 3. Verify the prefill fallback was made. The fallback body is built
        //    in PrefillAsync as a Dictionary<string, object> with n_predict=0
        //    (an int), then passed to ProxyCompletionAsync. The test proxy
        //    stores the body as-is. Check that the call to the prefill
        //    worker (RTX = localhost:8080) has n_predict=0.
        var prefillFallback = proxy.NonStreamingCalls.FirstOrDefault(c =>
            c.NodeUrl == "http://localhost:8080" &&
            c.Body.ContainsKey("n_predict"));
        Assert.NotNull(prefillFallback);
        Assert.Equal(0, Convert.ToInt32(prefillFallback.Body["n_predict"]));

        // 4. The request completed without throwing (no 503 from the
        //    engine RPC failure). The test proxy returns a minimal response
        //    (id_slot, usage) — full OAI shape validation is done by the
        //    other tests. The key invariant here is "no exception thrown".
        Assert.NotNull(result);
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
