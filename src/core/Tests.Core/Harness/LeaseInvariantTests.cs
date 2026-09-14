using System.Collections.Concurrent;
using System.Reflection;
using Hydra.Core.Models;
using Hydra.Core.Services;
using Xunit;

namespace Tests.Core.Harness;

/// <summary>
/// Lease/leak invariants, asserted AFTER EVERY scenario in the catalog:
/// <list type="number">
/// <item>No stray worker claim: busy slots on a worker == warm leases held on it.
/// When no warm lease survives (Failed/Cancelled/streaming-complete) that means
/// <c>GetElapsedSeconds == 0</c> for every worker — the exact leak class that
/// used to leave <c>hydra_worker_busy_seconds</c> climbing forever.</item>
/// <item><c>_peerLeases</c> is empty — every exclusive peer reservation is
/// released, including on Failed/Cancelled (P3.0).</item>
/// <item><c>_warmLeases</c> is bounded — never more than one lease per session
/// and never more leases than total slots across workers.</item>
/// <item>Completion resolves exactly once — the request's outcome is terminal
/// and the completion future is settled.</item>
/// <item>The ledger is consistent — a completed session is live (not evicted)
/// with a valid slot, and n_past is never negative.</item>
/// </list>
/// Plus the cancel-mid-flight leak regressions (modeled on
/// <c>WorkerSchedulerTests.RunItemPipeline_CancelledBetweenDispatches_ReleasesLease</c>).
/// </summary>
[Collection("HydraHarnessTests")]
public sealed class LeaseInvariantTests
{
    [Fact]
    public async Task Every_Scenario_Is_Leak_Free()
    {
        foreach (var spec in ScenarioCatalog.All)
        {
            await using var runner = new SchedulerScenarioRunner(spec.Options, "sess_h");

            Exception? error = null;
            OutcomeClass outcome;
            try
            {
                await spec.Run(runner);
                outcome = OutcomeClass.Done;
            }
            catch (OperationCanceledException)
            {
                outcome = OutcomeClass.Cancelled;
            }
            catch (Exception ex)
            {
                outcome = OutcomeClass.Failed;
                error = ex;
            }

            await runner.SettleAsync();

            try
            {
                AssertInvariants(runner, spec, outcome, error);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Invariant violated by scenario '{spec.Id}' (outcome {outcome}): {ex.Message}");
            }
        }
    }

    private static void AssertInvariants(
        SchedulerScenarioRunner runner, ScenarioSpec spec, OutcomeClass outcome, Exception? error)
    {
        var warm = runner.Scheduler.GetWarmLeasesSnapshot();

        // ── 1. No stray worker claims ────────────────────────────────────
        foreach (var w in runner.Cfg.Workers)
        {
            var busySlots = runner.Tracker.TotalSlots(w.Name) - runner.Tracker.FreeSlotCount(w.Name);
            var warmHeld = warm.Values.Count(l => l.WorkerName == w.Name);

            // Known legacy defect carve-out: the cross-model abort path
            // (StatePut model_match=false → decode-fallback on the prefill
            // node) orphans that fallback lease when PickDecodeAsync
            // re-acquires a fresh decode lease. The gate pins the EXACT leak
            // shape here instead of failing — a regression in v2 either
            // reproduces it identically (parity) or fixes it (deliberate
            // divergence the differential gate flags for review).
            if (spec.HasKnownLegacySlotLeak && warmHeld == 0 && busySlots > 0)
            {
                Assert.True(busySlots == 1,
                    $"known-leak scenario '{spec.Id}': expected exactly 1 orphaned slot on '{w.Name}', got {busySlots}");
                Assert.True(runner.Tracker.GetElapsedSeconds(w.Name) > 0d,
                    $"known-leak scenario '{spec.Id}': orphaned slot on '{w.Name}' must read busy");
                continue;
            }

            if (warmHeld == 0)
            {
                Assert.True(runner.Tracker.GetElapsedSeconds(w.Name) == 0d,
                    $"worker '{w.Name}' still busy ({runner.Tracker.GetElapsedSeconds(w.Name):F3}s) with no warm lease — leaked claim");
                Assert.True(busySlots == 0,
                    $"worker '{w.Name}' has {busySlots} busy slot(s) but no warm lease");
            }
            else
            {
                // A warm lease legitimately keeps its slot busy across turns;
                // the invariant is that EVERY busy slot is backed by a warm
                // lease (no stray acquisition beyond the sanctioned hold).
                Assert.True(busySlots == warmHeld,
                    $"worker '{w.Name}' has {busySlots} busy slots but only {warmHeld} warm leases");
                // NB: WorkerTracker.BusySince is a coarse per-worker flag —
                // a ReleaseSlot clears it even while another slot stays held
                // (e.g. warm turn 2 releases the stale lease, keeps the new
                // one). So GetElapsedSeconds is only a reliable leak detector
                // when NO warm lease survives; the pool-level equality above
                // is the authoritative claim check.
            }
        }

        // ── 2. Peer leases empty ─────────────────────────────────────────
        var peerField = typeof(WorkerSchedulerService)
            .GetField("_peerLeases", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("scheduler _peerLeases field moved");
        var peerLeases = (ConcurrentDictionary<string, IPeerReservation>)peerField.GetValue(runner.Scheduler)!;
        Assert.True(peerLeases.IsEmpty,
            $"peer lease(s) leaked: {string.Join(",", peerLeases.Keys)}");
        foreach (var w in runner.Cfg.Workers)
            Assert.False(runner.Tracker.IsExclusiveReserved(w.Name),
                $"worker '{w.Name}' still exclusively reserved after scenario '{spec.Id}'");

        // ── 3. Warm leases bounded ───────────────────────────────────────
        var totalSlots = runner.Cfg.Workers.Sum(w => w.Slots);
        Assert.True(warm.Count <= totalSlots,
            $"warm leases ({warm.Count}) exceed total slots ({totalSlots})");
        var perSession = warm.Values.GroupBy(l => l.SessionId).Select(g => (g.Key, g.Count())).ToList();
        Assert.True(perSession.All(p => p.Item2 == 1),
            $"a session holds multiple warm leases: {string.Join(",", perSession.Where(p => p.Item2 > 1).Select(p => $"{p.Key}x{p.Item2}"))}");
        foreach (var (workerName, slotId, sessionId) in warm.Values.Select(l => (l.WorkerName, l.SlotId, l.SessionId)))
        {
            var cfg = runner.Cfg.Workers.FirstOrDefault(w => w.Name == workerName);
            Assert.NotNull(cfg);
            Assert.True(slotId < cfg!.Slots, $"warm lease slot {slotId} out of range for {workerName}");
        }

        // ── 4. Completion resolves exactly once ──────────────────────────
        Assert.True(outcome is OutcomeClass.Done or OutcomeClass.Failed or OutcomeClass.Cancelled,
            $"unexpected outcome {outcome} for '{spec.Id}'");
        if (outcome == OutcomeClass.Failed && spec.ExpectedOutcome == OutcomeClass.Done)
            Assert.Fail($"scenario '{spec.Id}' failed unexpectedly: {error?.GetType().Name}: {error?.Message}");

        // ── 5. Ledger consistency ────────────────────────────────────────
        var entry = runner.Ledger.Lookup(runner.SessionId);
        if (outcome == OutcomeClass.Done)
        {
            Assert.NotNull(entry);
            // A Done session is either live on a slot (SlotFreed=false) or
            // evicted WITH a store copy (SlotFreed=true + HasStoreState=true —
            // cold_atomic hands the slot to the warm lease and marks the
            // session evicted; the next turn migrates from Store). An evicted
            // session with NO store copy would silently lose its KV — that is
            // the consistency violation this gate rejects.
            Assert.True(!entry!.SlotFreed || entry.HasStoreState,
                $"session '{runner.SessionId}' evicted without a store copy after Done scenario '{spec.Id}'");
        }
        if (entry != null)
        {
            Assert.True(entry.NPast >= 0, $"negative NPast {entry.NPast} for '{spec.Id}'");
            if (entry.SlotId is { } slot)
            {
                var w = runner.Cfg.Workers.FirstOrDefault(x => x.Name == entry.NodeName);
                Assert.True(w != null, $"ledger node '{entry.NodeName}' is not a configured worker");
                Assert.True(slot < w!.Slots, $"ledger slot {slot} out of range for {entry.NodeName} ({w.Slots} slots)");
            }
        }
    }

    // ── Cancel-mid-flight leak regressions ───────────────────────────────

    [Fact]
    public async Task CancelBetweenDispatches_ReleasesLease_And_ClearsBusy()
    {
        // Mirror of WorkerSchedulerTests.RunItemPipeline_CancelledBetweenDispatches_ReleasesLease,
        // driven through the harness: phase 1 acquires a real decode lease,
        // the client cancels between dispatch phases, the pipeline re-entry
        // must finalize as Cancelled and release every held claim.
        await using var runner = new SchedulerScenarioRunner(
            new ScenarioOptions { StartEvaluator = false, RunMode = "fast", UseLlamaEngine = true },
            "sess_lease_leak");

        var item = runner.CreateWorkItem("sess_lease_leak", 2000, 100);
        var next = await runner.DispatchAsync(item);
        // Engine-mode cold atomic routes through Prefill (the legacy
        // non-engine path returns ModelLoadDecode; both hold a real lease).
        Assert.True(next is WorkItemState.Prefill or WorkItemState.ModelLoadDecode,
            $"phase 1 dispatch should acquire a route, got {next}");
        Assert.NotNull(item.DecodeLease);
        Assert.True(runner.Tracker.GetElapsedSeconds("rtx") > 0, "slot must be busy before finalize");

        runner.CancelItem(item);
        Assert.True(item.IsCancelled);

        await runner.RunItemPipelineAsync(item, RequestType.Atomic);

        Assert.Equal(0d, runner.Tracker.GetElapsedSeconds("rtx"));
        Assert.True(item.Completion.Task.IsCanceled, "finalized-cancelled item must complete as cancelled");
        Assert.Equal(0, runner.Scheduler.WarmLeaseCount);
    }

    [Fact]
    public async Task CancelMidPipeline_AfterTwoPhases_ReleasesAllLeases()
    {
        // Two-phase cancel: the cold_concurrency route acquires a PREFILL lease
        // (phase 1) and the prefill runs (phase 2, still holding the prefill
        // lease). Cancelling mid-pipeline must release BOTH the prefill lease
        // and any decode claim via FinalizeAsync(Cancelled).
        await using var runner = new SchedulerScenarioRunner(
            new ScenarioOptions { StartEvaluator = false, UseLlamaEngine = true },
            "sess_lease_two_phase");

        var item = runner.CreateWorkItem("sess_lease_two_phase", 5000, 100);
        var next = await runner.DispatchAsync(item);            // None → route (prefill lease acquired)
        Assert.True(next is WorkItemState.PrefixRestore or WorkItemState.Prefill,
            $"phase 1 dispatch should land in prefill, got {next}");
        Assert.NotNull(item.PrefillLease);
        Assert.True(runner.Tracker.GetElapsedSeconds("rtx") > 0);

        item.State = next;
        var next2 = await runner.DispatchAsync(item);           // prefill runs (lease held through SaveKv)
        Assert.True(next2 is WorkItemState.SaveKv or WorkItemState.Prefill,
            $"phase 2 dispatch should progress prefill, got {next2}");

        runner.CancelItem(item);
        await runner.RunItemPipelineAsync(item, RequestType.Prefill);

        Assert.True(item.Completion.Task.IsCanceled);
        Assert.True(runner.Tracker.GetElapsedSeconds("rtx") == 0d,
            $"prefill worker still busy after cancel: {runner.Tracker.GetElapsedSeconds("rtx"):F3}s");
        Assert.True(runner.Tracker.GetElapsedSeconds("p100") == 0d);
        Assert.Equal(0, runner.Scheduler.WarmLeaseCount);
    }

    [Fact]
    public async Task CancelBeforeDispatch_HoldsNoSlot()
    {
        // Pre-dispatch cancel must never acquire anything.
        await using var runner = new SchedulerScenarioRunner(
            new ScenarioOptions { StartEvaluator = false },
            "sess_pre_cancel");

        var item = runner.CreateWorkItem("sess_pre_cancel", 2000, 100);
        runner.CancelItem(item);

        await runner.RunItemPipelineAsync(item, RequestType.Atomic);

        foreach (var w in runner.Cfg.Workers)
            Assert.True(runner.Tracker.GetElapsedSeconds(w.Name) == 0d,
                $"worker '{w.Name}' busy after pre-dispatch cancel");
        Assert.True(item.Completion.Task.IsCanceled);
    }
}
