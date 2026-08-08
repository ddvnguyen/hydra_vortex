using Hydra.Core;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core.Integration;

/// <summary>
/// Gap 4 regression tests: the 66ms/30-retry 503 cascade was caused by the
/// Atomic admission gate accepting a decode-only worker (p100, cp=False), which
/// passed the gate but failed routing to None in a tight loop. With the gate
/// fixed (Atomic requires a prefill-capable free worker), a request with no
/// prefill capacity simply WAITS in the queue (no spin, no 503) and is served
/// the moment a prefill worker frees a slot. The followup tests additionally
/// guard the streaming warm path from regressions.
/// </summary>
public sealed class TwoStageQueueDrainTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public CoordinatorConfig Cfg { get; }
        public SessionLedger Ledger { get; }
        public WorkerTracker Tracker { get; }
        public IHealthMonitorService Health { get; }
        public EngineModeTests.EngineTestRpcClient Rpc { get; } = new();
        public WorkerSchedulerService Scheduler { get; }
        private readonly CancellationTokenSource _runCts = new();
        private readonly Task _runTask;

        public Fixture(int rtxSlots = 2, int p100Slots = 1)
        {
            Health = new Tests.Core.TestHealthMonitor();
            Ledger = new SessionLedger();
            Tracker = new WorkerTracker();

            Cfg = new CoordinatorConfig
            {
                RunMode = "concurrency",
                UseLlamaEngine = true,
                PrefixCheckpointEnabled = false,
                WarmSlotVerificationEnabled = false,
                MixPrecisionEnabled = false,
                AtomicThreshold = 2048,
                Workers = new List<WorkerConfig>
                {
                    new() { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080",
                        WorkerType = 3, Slots = rtxSlots, PrefillPriority = 1, DecodePriority = 2 },
                    new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086",
                        WorkerType = 2, Slots = p100Slots, PrefillPriority = 100, DecodePriority = 1 },
                }
            };
            foreach (var w in Cfg.Workers)
                Tracker.InitWorker(w.Name, w.Slots);

            var sp = new ServiceCollection().BuildServiceProvider();
            Scheduler = new WorkerSchedulerService(Cfg, Ledger, Tracker, new TestCompletionProxy(),
                Health, Rpc, sp, Serilog.Log.Logger);
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

        public Task<object?> SubmitAsync(string sessionId, int estimatedTokens, int maxTokens = 100, bool stream = false)
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
            return Scheduler.SubmitAsync(req, msgs, sessionId, estimatedTokens, maxTokens, null, _runCts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _runCts.Cancel();
            try { await _runTask; } catch (OperationCanceledException) { }
            _runCts.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Atomic_NoPrefillWorkerFree_WaitsNot503_ThenServedOnRtxSlotRelease()
    {
        await using var f = new Fixture();

        // Occupy every rtx slot (prefill+decode). p100 (decode-only) is free but
        // cannot satisfy an atomic (prefill-needing) request. The fixed gate must
        // keep the item queued (NOT dispatch → no None → no 30-retry spin → no 503).
        Assert.True(f.Tracker.TryAcquireSlot("rtx", out var slot1, "test"));
        Assert.True(f.Tracker.TryAcquireSlot("rtx", out var slot2, "test"));

        var submit = f.SubmitAsync("sess_park", 500, 50);

        // The item must wait (not fail fast) while no prefill worker is free.
        await WaitUntilAsync(() => submit.IsCompleted, TimeSpan.FromMilliseconds(500));
        Assert.False(submit.IsCompleted, "Atomic with no prefill worker must wait, not 503/spin");
        Assert.False(submit.IsFaulted);

        // Release one rtx slot → capacity release wakes the evaluator → served.
        f.Tracker.ReleaseSlot("rtx", slot1);
        var result = await submit.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Atomic_DecodeOnlySlotRelease_DoesNotServeWaiter_ButPrefillReleaseDoes()
    {
        await using var f = new Fixture();

        Assert.True(f.Tracker.TryAcquireSlot("rtx", out var slot1, "test"));
        Assert.True(f.Tracker.TryAcquireSlot("rtx", out var slot2, "test"));

        var submit = f.SubmitAsync("sess_gate", 500, 50);
        await WaitUntilAsync(() => submit.IsCompleted, TimeSpan.FromMilliseconds(500));
        Assert.False(submit.IsCompleted);

        // A capacity release on the decode-only node (p100) must NOT serve the
        // atomic waiter — it still needs a prefill-capable worker.
        Assert.True(f.Tracker.TryAcquireSlot("p100", out var p100Slot, "test"));
        f.Tracker.ReleaseSlot("p100", p100Slot);
        await Task.Delay(100);
        Assert.False(submit.IsCompleted,
            "decode-only capacity release must not serve a prefill-needing atomic request");

        // Releasing an rtx slot serves it.
        f.Tracker.ReleaseSlot("rtx", slot1);
        var result = await submit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task WarmSoloFollowup_DispatchesAndCompletes()
    {
        await using var f = new Fixture();

        // Turn 1: cold atomic completes, KV saved, slot released.
        var r1 = await f.SubmitAsync("sess_fu", 300, 30);
        Assert.NotNull(r1);

        // Turn 2: warm Solo followup must dispatch on the affinity node and complete.
        var r2 = await f.SubmitAsync("sess_fu", 100, 20);
        Assert.NotNull(r2);
    }

    [Fact]
    public async Task WarmSoloFollowup_Streaming_DispatchesAndCompletes()
    {
        await using var f = new Fixture();

        // Turn 1: streaming cold atomic.
        var r1 = await f.SubmitAsync("sess_fus", 300, 30, stream: true);
        Assert.NotNull(r1);

        // Turn 2: streaming warm Solo followup — must dispatch and complete
        // (guards the E2E regression where a transient routing None parked the
        // item forever).
        var r2 = await f.SubmitAsync("sess_fus", 100, 20, stream: true);
        Assert.NotNull(r2);
    }
}
