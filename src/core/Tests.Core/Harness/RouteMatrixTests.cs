using Hydra.Core.Models;
using Hydra.Shared;
using Tests.Core.Integration;
using Hydra.Core;
using Hydra.Core.Services;
using Xunit;

namespace Tests.Core.Harness;

/// <summary>
/// Route × request-shape × failure-injection matrix: each row pins the terminal
/// outcome class (Done / Failed / Cancelled / RetriedThenDone) and the state
/// path markers (which RPC opcodes fired, which didn't) for one combination.
/// These are the contracts the v2 scheduler must reproduce exactly.
/// </summary>
[Collection("HydraHarnessTests")]
public sealed class RouteMatrixTests
{
    private sealed record MatrixCase(
        string Name,
        ScenarioOptions Options,
        int EstimatedTokens,
        int MaxTokens,
        bool Stream,
        string? PrefixHash,
        string? ForceMode,
        OutcomeClass Expected,
        Action<ScenarioRunResult>? Verify = null);

    private static readonly MatrixCase[] Cases =
    {
        // ── cold atomic ──
        new("atomic_engine",
            new() { RunMode = "fast", UseLlamaEngine = true }, 500, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Rpc.Any(c => c.Op == "EnginePrefill")); Assert.DoesNotContain("StatePut", r.Trace.Rpc.Select(c => c.Op)); Assert.Single(r.Trace.Proxy); }),
        new("atomic_http",
            new() { RunMode = "fast", UseLlamaEngine = false }, 500, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.Empty(r.Trace.Rpc.Where(c => c.Op.StartsWith("Engine"))); Assert.Single(r.Trace.Proxy); }),

        // ── cold concurrency (P/D) ──
        new("concurrency_pd",
            new() { UseLlamaEngine = true }, 5000, 100, false, null, null,
            OutcomeClass.Done,
            r =>
            {
                var ops = r.Trace.Rpc.Select(c => c.Op).ToList();
                Assert.Contains("EnginePrefill", ops);
                Assert.Contains("StatePut", ops);          // KV restored onto p100
                Assert.Contains("Put", ops);               // KV saved to Store
                Assert.Single(r.Trace.Proxy);
                Assert.Equal("p100", r.Trace.Ledger?.NodeName);
            }),

        // ── warm affinity ──
        new("warm_affinity_turn2",
            new() { UseLlamaEngine = true, P100Slots = 2 }, 500, 100, false, null, null,
            OutcomeClass.Done,
            r =>
            {
                // Two turns both decode on p100; turn 2 must NOT restore KV.
                Assert.Equal(2, r.Trace.Proxy.Count);
                Assert.All(r.Trace.Proxy, p => Assert.Contains("8086", p.Url));
                // Turn 1 restores (1 StatePut); turn 2 is pure warm decode (no extra StatePut/Get).
                Assert.Equal(1, r.Trace.Rpc.Count(c => c.Op == "StatePut"));
                Assert.Equal(1, r.Trace.Rpc.Count(c => c.Op == "Get"));
            }),

        // ── migration ──
        new("migration_restore",
            new() { RunMode = "fast", UseLlamaEngine = true }, 500, 100, false, null, null,
            OutcomeClass.Done,
            r =>
            {
                Assert.True(r.Trace.Rpc.Any(c => c.Op == "Get" && c.Key == "sess_h.kv"));
                Assert.True(r.Trace.Rpc.Any(c => c.Op == "StatePut"));
                Assert.True(r.Trace.Ledger?.HasStoreState == true);
            }),

        // ── COMBINED ──
        new("combined_force",
            new() { UseLlamaEngine = true, CombinedEnabled = true, MultiEnginePolicy = "combined", MultiEngineTopology = true },
            20000, 100, false, null, "combined",
            OutcomeClass.Done,
            r => { Assert.Single(r.Trace.Rpc.Where(c => c.Op == "EnginePrefill")); Assert.Single(r.Trace.Proxy); }),

        // ── merged decode ──
        new("merged_decode_accept",
            new() { RunMode = "fast", UseLlamaEngine = true, HealthFactory = () => new EngineModeTests.GateATestHealthMonitor() },
            500, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.Single(r.Trace.MergedDecode); Assert.Empty(r.Trace.Proxy); }),
        new("merged_decode_reject",
            new() { RunMode = "fast", UseLlamaEngine = true, HealthFactory = () => new EngineModeTests.GateATestHealthMonitor(), ConfigureRpc = rpc => rpc.MakeMergedDecodeReject = true },
            500, 100, false, null, null,
            OutcomeClass.Failed,
            r => { Assert.Single(r.Trace.MergedDecode); Assert.Empty(r.Trace.Proxy); }),
        new("merged_decode_transport_fault",
            new() { RunMode = "fast", UseLlamaEngine = true, HealthFactory = () => new EngineModeTests.GateATestHealthMonitor(), ConfigureRpc = rpc => rpc.MakeMergedDecodeThrow = true },
            500, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.Single(r.Trace.MergedDecode); Assert.NotEmpty(r.Trace.Proxy); }), // transport fault → HTTP fallback

        // ── prefill failure injection ──
        new("prefill_terminal_error",
            new() { UseLlamaEngine = true, ConfigureRpc = rpc => rpc.MakeEnginePrefillFail = true },
            5000, 100, false, null, null,
            OutcomeClass.Failed,
            r => { Assert.Empty(r.Trace.Proxy); }), // real engine errors never fall back to HTTP
        new("prefill_not_implemented",
            new() { UseLlamaEngine = true, ConfigureRpc = rpc => rpc.MakeEnginePrefillNotImplemented = true },
            5000, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Proxy.Any(p => p.NPredict == 0)); }), // #279: HTTP prefill fallback
        new("prefill_busy_retry_then_success",
            new() { UseLlamaEngine = true, ConfigureRpc = rpc => rpc.BusyPrefillAttempts = 2 },
            5000, 100, false, null, null,
            OutcomeClass.RetriedThenDone,
            r => { Assert.True(r.Trace.Rpc.Count(c => c.Op == "EnginePrefill") >= 3, "busy retries must re-issue prefill"); }),
        new("prefill_busy_exhausted",
            new() { UseLlamaEngine = true, ConfigureRpc = rpc => rpc.BusyPrefillAttempts = 100 },
            5000, 100, false, null, null,
            OutcomeClass.Failed,
            r => { Assert.True(r.Trace.Rpc.Count(c => c.Op == "EnginePrefill") >= 3); }),

        // ── StatePut failure injection ──
        new("stateput_mismatch_reroutes",
            new() { UseLlamaEngine = true, ConfigureRpc = rpc => rpc.MakeStatePutMismatch = true },
            5000, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Rpc.Count(c => c.Op == "StatePut") >= 2, "mismatch must abort + re-prefill + restore again"); }),
        new("stateput_fail_reroutes",
            new() { UseLlamaEngine = true, ConfigureRpc = rpc => rpc.MakeStatePutFail = true },
            5000, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Rpc.Any(c => c.Op == "StatePut")); }),
        new("stateput_hash_mismatch_crossmodel",
            new() { UseLlamaEngine = true, ConfigureRpc = rpc => rpc.MakeStatePutHashMismatch = true },
            5000, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Rpc.Any(c => c.Op == "StatePut")); }),

        // ── store failure ──
        new("store_put_throws_save_falls_back",
            new() { RunMode = "fast", UseLlamaEngine = true, ConfigureRpc = rpc => rpc.SetException(OpCode.Put, new IOException("tmpfs full")) },
            500, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.Single(r.Trace.Proxy); }),

        // ── streaming ──
        new("streaming_atomic",
            new() { RunMode = "fast", UseLlamaEngine = true }, 500, 100, true, null, null,
            OutcomeClass.Done,
            r => { Assert.Single(r.Trace.Proxy); Assert.True(r.Trace.Proxy[0].Stream); }),
        new("streaming_concurrency",
            new() { UseLlamaEngine = true }, 5000, 100, true, null, null,
            OutcomeClass.Done,
            r => { Assert.Contains("StatePut", r.Trace.Rpc.Select(c => c.Op)); }),

        // ── prefix ──
        new("prefix_hit",
            new() { UseLlamaEngine = true, PrefixCheckpointEnabled = true }, 5000, 100, false, "abc123", null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Rpc.Any(c => c.Op == "Get" && c.Key.StartsWith("prefix/"))); }),
        new("prefix_miss",
            new() { UseLlamaEngine = true, PrefixCheckpointEnabled = true, ConfigureRpc = rpc => rpc.SetKeyResponse("prefix/", OpCode.Get, (byte)StatusCode.NotFound) },
            5000, 100, false, "abc123", null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Rpc.Any(c => c.Op == "Get" && c.Key.StartsWith("prefix/"))); }),

        // ── chunked save ──
        new("chunked_save_all_deduped",
            new() { RunMode = "fast", UseLlamaEngine = true, EnableChunks = true, ChunkSize = 1024 },
            500, 100, false, null, null,
            OutcomeClass.Done,
            r =>
            {
                Assert.True(r.Trace.Rpc.Any(c => c.Op == "SyncMissing"));
                Assert.True(r.Trace.Rpc.Any(c => c.Op == "PutManifest"));
                Assert.DoesNotContain("PushChunks", r.Trace.Rpc.Select(c => c.Op));
            }),
        new("chunked_save_with_pushes",
            new()
            {
                RunMode = "fast", UseLlamaEngine = true, EnableChunks = true, ChunkSize = 1024,
                ConfigureRpc = rpc =>
                {
                    var chunks = ChunkEngine.ChunkAndHash(rpc.PrefillKvBlob);
                    var missing = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { missing_hashes = new[] { chunks[0].Hash } });
                    rpc.SetKeyResponse("sess_h.kv", OpCode.SyncMissing, (byte)StatusCode.Ok, null, missing);
                },
            },
            500, 100, false, null, null,
            OutcomeClass.Done,
            r => { Assert.True(r.Trace.Rpc.Any(c => c.Op == "PushChunks")); }),
    };

    [Fact]
    public async Task RouteMatrix_All_Rows_Hit_Expected_Outcome_And_State_Path()
    {
        var failures = new List<string>();
        foreach (var c in Cases)
        {
            try
            {
                var spec = new ScenarioSpec
                {
                    Id = c.Name,
                    Description = c.Name,
                    Options = c.Options,
                    Run = r => RunMatrixRequest(r, c),
                    ExpectedOutcome = c.Expected,
                };
                var result = await SchedulerScenarioRunner.ExecuteAsync(spec);
                Assert.Equal(c.Expected, result.Outcome);
                c.Verify?.Invoke(result);
            }
            catch (Exception ex)
            {
                failures.Add($"{c.Name}: {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0, "Route-matrix failures:\n" + string.Join("\n", failures));
    }

    private static async Task RunMatrixRequest(IScenarioDriver r, MatrixCase c)
    {
        if (c.Name == "warm_affinity_turn2")
        {
            await r.SubmitAsync(r.SessionId, 5000, 100);
            await r.SubmitAsync(r.SessionId, 300, 100);
            return;
        }
        if (c.Name == "migration_restore")
        {
            await r.SubmitAsync(r.SessionId, 500, 100);
            var e = r.Ledger.Lookup(r.SessionId);
            lock (e!)
            {
                e.SlotFreed = true;
                e.HasStoreState = true;
            }
            await r.SubmitAsync(r.SessionId, 300, 100);
            return;
        }
        await r.SubmitAsync(r.SessionId, c.EstimatedTokens, c.MaxTokens, c.Stream, c.PrefixHash, c.ForceMode);
    }

    // ═════════════════════════════════════════════════════════════════════
    // CoverageReporter — every WorkItemState must be exercised.
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records every WorkItemState observed across the coverage sweep (both as
    /// a <c>DispatchAsync</c> return value and as the item's <c>State</c>). The
    /// v2 rewrite must route through the SAME state set; this reporter fails the
    /// gate the moment any enum value goes unexercised (i.e. the legacy harness
    /// never proves that state's contract, so v2 could break it silently).
    /// </summary>
    public static class CoverageReporter
    {
        private static readonly HashSet<WorkItemState> Observed = new();
        private static readonly object Gate = new();

        public static void Record(WorkItemState state)
        {
            lock (Gate) Observed.Add(state);
        }

        public static List<WorkItemState> Missing()
        {
            lock (Gate)
            {
                return Enum.GetValues<WorkItemState>()
                    .Where(s => !Observed.Contains(s))
                    .ToList();
            }
        }

        public static void Reset() { lock (Gate) Observed.Clear(); }
    }

    [Fact]
    public async Task CoverageReporter_Every_WorkItemState_Is_Exercised()
    {
        CoverageReporter.Reset();

        // Sweep 1 — legacy cold atomic: None → RouteDecision → ModelLoadDecode
        //          → RestoreKv → Decode → BgSave → Done
        await SweepPipelineAsync(
            new ScenarioOptions { RunMode = "fast", UseLlamaEngine = false, StartEvaluator = false },
            "sweep_atomic", 500, prefixHash: null);

        // Sweep 2 — legacy cold concurrency: ModelLoadPrefill → PrefixRestore
        //          (short-circuit) → Prefill → SaveKv → SaveDone → PickDecode
        //          → RestoreKv → Decode → BgSave → Done
        await SweepPipelineAsync(
            new ScenarioOptions { UseLlamaEngine = false, StartEvaluator = false },
            "sweep_pd", 5000, prefixHash: null);

        // Sweep 3 — engine cold concurrency with prefix restore (PrefixRestore
        //          actually issues Store Get + StatePut) and engine prefill.
        await SweepPipelineAsync(
            new ScenarioOptions { UseLlamaEngine = true, PrefixCheckpointEnabled = true, StartEvaluator = false },
            "sweep_engine_prefix", 5000, prefixHash: "abc123");

        // Sweep 4 — BUSY prefill → Retry return value.
        await SweepBusyAsync();

        // Sweep 5 — MarkEvicted as a dispatch input, Failed terminal, Cancelled.
        await SweepTerminalStatesAsync();

        var missing = CoverageReporter.Missing();
        Assert.True(missing.Count == 0,
            "WorkItemStates never exercised by the harness coverage sweep: " +
            string.Join(", ", missing));
    }

    private static async Task SweepPipelineAsync(ScenarioOptions options, string sid, int tokens, string? prefixHash)
    {
        await using var runner = new SchedulerScenarioRunner(options, sid);
        var item = runner.CreateWorkItem(sid, tokens, 100, prefixHash: prefixHash);
        CoverageReporter.Record(item.State); // None

        var guard = 0;
        while (guard++ < 40)
        {
            var next = await runner.DispatchAsync(item);
            CoverageReporter.Record(next);
            CoverageReporter.Record(item.State);

            if (next is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
            {
                await runner.FinalizeAsync(item, next);
                break;
            }
            item.State = next;
        }
    }

    private static async Task SweepBusyAsync()
    {
        await using var runner = new SchedulerScenarioRunner(
            new ScenarioOptions
            {
                UseLlamaEngine = true,
                StartEvaluator = false,
                ConfigureRpc = rpc => rpc.BusyPrefillAttempts = 1,
            }, "sweep_busy");

        var item = runner.CreateWorkItem("sweep_busy", 5000, 100);
        var sawRetry = false;
        var guard = 0;
        while (guard++ < 40 && !sawRetry)
        {
            var next = await runner.DispatchAsync(item);
            CoverageReporter.Record(next);
            CoverageReporter.Record(item.State);
            if (next == WorkItemState.Retry)
            {
                // PrefillAsync resets State to None on BUSY; record that too.
                sawRetry = true;
                CoverageReporter.Record(item.State);
                item.State = WorkItemState.None;
                break;
            }
            if (next is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
            {
                await runner.FinalizeAsync(item, next);
                break;
            }
            item.State = next;
        }
        Assert.True(sawRetry, "busy sweep never observed the Retry state");

        // Continue through a successful re-dispatch to a terminal state so the
        // sweep leaves no leases behind.
        var guard2 = 0;
        while (guard2++ < 40)
        {
            var n = await runner.DispatchAsync(item);
            CoverageReporter.Record(n);
            if (n is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
            {
                await runner.FinalizeAsync(item, n);
                break;
            }
            item.State = n;
        }
    }

    private static async Task SweepTerminalStatesAsync()
    {
        await using var runner = new SchedulerScenarioRunner(
            new ScenarioOptions { UseLlamaEngine = true, StartEvaluator = false, ConfigureRpc = rpc => rpc.MakeEnginePrefillFail = true },
            "sweep_terminal");

        // MarkEvicted as a dispatch input (dead-but-legal state in the enum).
        // Legacy quirk: MarkEvictedStateAsync returns PickDecode only when the
        // item's state is SaveDone — a bare MarkEvicted dispatch returns Done.
        var item = runner.CreateWorkItem("sweep_evicted", 5000, 100);
        item.State = WorkItemState.MarkEvicted;
        var next = await runner.DispatchAsync(item);
        CoverageReporter.Record(WorkItemState.MarkEvicted);
        CoverageReporter.Record(next);
        Assert.Equal(WorkItemState.Done, next);
        await runner.FinalizeAsync(item, WorkItemState.Done);

        // Failed terminal: prefill returns a terminal engine error.
        var failItem = runner.CreateWorkItem("sweep_fail", 5000, 100);
        var fguard = 0;
        while (fguard++ < 40)
        {
            var nf = await runner.DispatchAsync(failItem);
            CoverageReporter.Record(nf);
            CoverageReporter.Record(failItem.State);
            if (nf == WorkItemState.Failed)
            {
                failItem.State = nf;
                break;
            }
            if (nf is WorkItemState.Done or WorkItemState.Cancelled)
            {
                await runner.FinalizeAsync(failItem, nf);
                break;
            }
            failItem.State = nf;
        }
        Assert.Equal(WorkItemState.Failed, failItem.State);

        // Cancelled terminal: cancel after the route dispatch; the first
        // PrefillAsync dispatch after the flag flips returns Cancelled
        // (PrefixRestoreAsync itself has no cancellation guard, so the route
        // completes first — a documented legacy nuance).
        var cancelItem = runner.CreateWorkItem("sweep_cancel", 5000, 100);
        var nc1 = await runner.DispatchAsync(cancelItem);
        CoverageReporter.Record(nc1);
        cancelItem.State = nc1;
        cancelItem.Cancel();
        var ncguard = 0;
        var nc = WorkItemState.None;
        while (ncguard++ < 10)
        {
            nc = await runner.DispatchAsync(cancelItem);
            CoverageReporter.Record(nc);
            CoverageReporter.Record(cancelItem.State);
            if (nc is WorkItemState.Cancelled or WorkItemState.Done or WorkItemState.Failed)
                break;
            cancelItem.State = nc;
        }
        Assert.Equal(WorkItemState.Cancelled, nc);
        await runner.FinalizeAsync(cancelItem, nc);
    }
}
