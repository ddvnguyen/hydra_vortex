using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// #709 repro: concurrent cold_atomic self-lease decode deadlock.
//
// Live repro (2026-08-28, P100 T2 rig, PR #709 comment 5452376844):
//
//   1. ColdRouteAsync acquires a decode slot up-front for cold_atomic:
//      item.DecodeLease = new SlotLease(...) with reason "decode".
//   2. After Prefill→SaveKv, the pipeline transitions SaveDone→PickDecode,
//      sets RequestType=Decode, then re-enqueues via EnqueueRequest(... Decode).
//   3. The evaluator's CanServeRequest gates Decode on:
//      _cfg.Workers.Any(w => w.CanDecode && _tracker.IsFree(w.Name) && ...)
//   4. WorkerTracker.IsFree is WORKER-level: with two concurrent cold_atomic
//      requests, both slots are held by their own in-flight DecodeLeases,
//      so IsFree=false for both -> neither request re-dispatches -> stall.
//
// Safe concurrency = slots - 1 for same-worker cold_atomic.
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class ColdAtomicSelfLeaseTests
{
    /// <summary>
    /// Fixture with ONE worker (worker_type=3 → CanPrefill+CanDecode, slots=2)
    /// and a stubbed engine + proxy.  The model alias "nano" is registered so
    /// SubmitAsync passes unknown-model validation.
    /// </summary>
    private sealed class SingleWorkerFixture : IAsyncDisposable
    {
        public CoordinatorConfig Cfg { get; }
        public SessionLedger Ledger { get; }
        public WorkerTracker Tracker { get; }
        public TestCompletionProxy Proxy { get; }
        public TestHealthMonitor Health { get; }
        public TestRpcClient Rpc { get; } = new();
        public WorkerSchedulerService Scheduler { get; }
        private readonly CancellationTokenSource _runCts = new();
        private readonly Task _runTask;

        public SingleWorkerFixture()
        {
            Health = new TestHealthMonitor();
            Proxy = new TestCompletionProxy(totalTokens: 150, slotId: 0);
            Ledger = new SessionLedger();
            Tracker = new WorkerTracker();

            Cfg = new CoordinatorConfig
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                PrefixCheckpointEnabled = false,
                WarmSlotVerificationEnabled = false,
                MixPrecisionEnabled = false,
                AtomicThreshold = 2048,
                Workers = new List<WorkerConfig>
                {
                    // worker_type=3 (Mixed): CanPrefill + CanDecode, 2 slots
                    new()
                    {
                        Name = "rtx",
                        Host = "localhost",
                        RpcPort = 9601,
                        LlamaUrl = "http://localhost:8080",
                        WorkerType = 3,
                        Slots = 2,
                        PrefillPriority = 1,
                        DecodePriority = 2,
                    },
                    // worker_type=1 (DecodeOnly): Decode priority 100, 0 slots
                    // Satisfies Workers.Count >= 2 for the pipeline semaphore,
                    // but never routes cold_atomic (CanPrefill = false).
                    new()
                    {
                        Name = "p100",
                        Host = "localhost",
                        RpcPort = 9602,
                        LlamaUrl = "http://192.168.122.21:8086",
                        WorkerType = 1,
                        Slots = 0,
                        PrefillPriority = 100,
                        DecodePriority = 1,
                    }
                }
            };
            foreach (var w in Cfg.Workers)
                Tracker.InitWorker(w.Name, w.Slots);

            var sp = new ServiceCollection().BuildServiceProvider();
            Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker,
                Proxy, Health, Rpc, sp, Serilog.Log.Logger);
            Scheduler.AgentClientFactory = (_, _) => Rpc;
            Scheduler.LlamaClientFactory = _ => new TestLlamaClient();
            Scheduler.BusyTimeoutOverride = (_, _) => (stuckMs: 100, slowMs: 200);

            ModelRegistry.ClearForTest();
            ModelRegistry.RegisterForTest(new EngineConfig(
                ModelAlias: "nano",
                ModelPath: "/dev/null",
                NGpuLayers: 0, NCtx: 2048,
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

        public async Task SubmitAsync(string sessionId, int estimatedTokens, int maxTokens = 100)
        {
            var req = new Dictionary<string, object>
            {
                ["stream"] = false,
                ["max_tokens"] = maxTokens,
                ["model"] = "nano"
            };
            var msgs = new List<Dictionary<string, object>>
            {
                new() { ["role"] = "user", ["content"] = new string('x', estimatedTokens) }
            };
            await Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens,
                maxTokens, null, _runCts.Token);
        }
    }

    // ── FAILING repro ───────────────────────────────────────────────────
    //
    // Two concurrent cold_atomic requests on a 2-slot worker should BOTH
    // progress to Decode.  Today the second request stalls because
    // CanServeRequest(Decode) requires _tracker.IsFree(worker), which is
    // false when both slots are occupied by the requests' own DecodeLeases.
    //
    // xUnit convention: the repo does not use [Trait("Failing")] or
    // Skip=... for known-failing tests, so this test is left as-is.
    // The PR body documents this as the intentional red test.

    [Fact(Timeout = 30_000)]
    public async Task TwoColdAtomicRequests_BothReachDecode_ExpectFailsDueToSelfLeaseDeadlock()
    {
        await using var f = new SingleWorkerFixture();

        var t1 = f.SubmitAsync("sess_deadlock_1", 500);
        var t2 = f.SubmitAsync("sess_deadlock_2", 500);

        // Wait up to 6s for both to complete.
        var done1 = await Task.WhenAny(t1, Task.Delay(TimeSpan.FromSeconds(6)));
        var done2 = await Task.WhenAny(t2, Task.Delay(TimeSpan.FromSeconds(6)));

        var t1Completed = done1 == t1;
        var t2Completed = done2 == t2;

        // CORRECT behavior (after fix): both complete
        // ACTUAL behavior (today): neither completes → assertion fails
        Assert.True(t1Completed && t2Completed,
            $"KNOWN BUG (PR #709): two concurrent cold_atomic requests stall in PickDecode " +
            $"because CanServeRequest gates RequestType.Decode on _tracker.IsFree " +
            $"(worker-level, all slots occupied) while both DecodeLeases are held by " +
            $"the items themselves (self-lease deadlock). Safe concurrency = slots-1. " +
            $"[diag: rtxFree={f.Tracker.FreeSlotCount("rtx")} t1={t1Completed} t2={t2Completed}]");
    }

    // ── Safe-path test (must PASS) ──────────────────────────────────────
    //
    // One cold_atomic request with slots=2: the decode slot is held by
    // its own DecodeLease, but IsFree still reports true because slot Y
    // is free → CanServeRequest(Decode) passes → dispatch → PickDecodeAsync
    // reuse-lease branch → Decode.

    [Fact]
    public async Task SingleColdAtomicRequest_ReachesDecode_WithSlotsTwo()
    {
        await using var f = new SingleWorkerFixture();

        await f.SubmitAsync("sess_safe_1", 500);

        // The request must have completed (reached Decode + FinalizeAsync).
        Assert.Single(f.Proxy.NonStreamingCalls);
        Assert.True(f.Rpc.HasCall(OpCode.EnginePrefill),
            "cold_atomic must PREFILL first (engine mode, #470 merged-decode)");
    }
}
