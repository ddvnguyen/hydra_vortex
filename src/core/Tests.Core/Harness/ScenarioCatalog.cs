using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Shared;
using Tests.Core.Integration;
using Hydra.Core;
using Hydra.Core.Services;

namespace Tests.Core.Harness;

/// <summary>
/// One named scenario: options to reach a route, a script of steps, and the
/// expected terminal outcome class. Each spec's golden JSON lives under
/// <c>Harness/Goldens/{Id}.json</c>.
/// </summary>
internal sealed class ScenarioSpec
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required ScenarioOptions Options { get; init; }
    public required Func<IScenarioDriver, Task> Run { get; init; }
    /// <summary>Terminal outcome family the scenario is expected to land in.</summary>
    public OutcomeClass ExpectedOutcome { get; init; } = OutcomeClass.Done;

    /// <summary>Scenario depends on legacy-only direct-drive seams (DispatchAsync /
    /// RunItemPipeline / CreateWorkItem); the v2 differential driver skips these.</summary>
    public bool LegacyOnly { get; init; }

    /// <summary>Scenario exercises the LEGACY (non-engine) scheduler mode —
    /// <c>UseLlamaEngine=false</c>. v2's contract is the hydra model (engine mode),
    /// so the differential gate skips these rather than chasing byte-parity with an
    /// obsolete path.</summary>
    public bool LegacyMode { get; init; }

    /// <summary>
    /// Documented legacy defect this scenario pins: the cross-model abort path
    /// (StatePut model_match=false → erase → decode-fallback lease on the
    /// prefill node) orphans that fallback slot when PickDecodeAsync later
    /// re-acquires a fresh decode lease without disposing the old one. The
    /// lease-invariant gate asserts the EXACT leak shape instead of failing;
    /// the differential gate will surface whether v2 reproduces or fixes it.
    /// </summary>
    public bool HasKnownLegacySlotLeak { get; init; }
}

/// <summary>
/// The contract-harness scenario catalog: every behavior the legacy scheduler
/// exhibits end-to-end, driven through the real evaluator loop and captured as
/// a normalized golden trace. The differential gate (epic #591 WP3+) re-runs
/// this same catalog against the v2 scheduler and diffs the goldens.
/// </summary>
internal static class ScenarioCatalog
{
    public static IReadOnlyList<ScenarioSpec> All { get; } = BuildCatalog();

    /// <summary>Directory holding the checked-in golden JSON files.</summary>
    public static string GoldensDirectory { get; } = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Tests.Core", "Harness", "Goldens");

    /// <summary>Env var that flips the golden tests into regenerate mode.</summary>
    public const string RegenerateEnvVar = "HYDRA_HARNESS_REGEN";

    private static List<ScenarioSpec> BuildCatalog() => new()
    {
        // ─────────────────────────────────────────────────────────────────
        // Cold atomic — engine mode (single GPU; EnginePrefill → KV save →
        // same-node HTTP decode). The "engine + HTTP fallback" pillar: the
        // engine owns prefill, the HTTP proxy owns the chat completion.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "cold_atomic_engine",
            Description = "Cold atomic request in engine mode: EnginePrefill builds KV, Store Put saves it, " +
                          "same-node HTTP proxy decodes, BgSave persists the KV blob.",
            Options = new ScenarioOptions { RunMode = "fast", UseLlamaEngine = true },
            Run = r => r.SubmitAsync(r.SessionId, 500, 100),
        },

        // Legacy (non-engine) cold atomic — ModelLoadDecode + HTTP decode.
        new()
        {
            Id = "cold_atomic_http",
            Description = "Cold atomic request in legacy mode (UseLlamaEngine=false): ModelLoadDecode path, " +
                          "HTTP proxy owns prefill-style completion, no engine RPC at all.",
            Options = new ScenarioOptions { RunMode = "fast", UseLlamaEngine = false },
            // Legacy-only mode: v2's contract is the hydra model (engine mode).
            // By contract we do not chase parity with the obsolete non-engine path.
            LegacyMode = true,
            Run = r => r.SubmitAsync(r.SessionId, 500, 100),
        },

        // ─────────────────────────────────────────────────────────────────
        // Cold concurrency — P/D split across the two GPUs.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "cold_concurrency_pd",
            Description = "Cold P/D split: EnginePrefill on rtx, KV saved to Store, RestoreKv loads it onto " +
                          "p100 via StatePut, p100 decodes via HTTP proxy. Non-chunked save (plain Put).",
            Options = new ScenarioOptions { UseLlamaEngine = true },
            Run = r => r.SubmitAsync(r.SessionId, 5000, 100),
        },

        // ─────────────────────────────────────────────────────────────────
        // Warm affinity — slot reuse across turns.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "warm_affinity_on",
            Description = "Turn 1 cold P/D split re-registers the session on p100 (SlotFreed=false); turn 2 " +
                          "reuses the warm slot (RouteType=affinity) with zero Store restore — decode + BgSave only. " +
                          "p100 gets 2 slots so the warm lease does not starve the follow-up turn.",
            Options = new ScenarioOptions { UseLlamaEngine = true, P100Slots = 2 },
            Run = async r =>
            {
                await r.SubmitAsync(r.SessionId, 5000, 100);
                await r.SubmitAsync(r.SessionId, 300, 100);
            },
        },

        // Warm affinity WITH slot verification enabled — the verification
        // RPC fails (dead port) → slot saved + evicted → re-routed cold.
        new()
        {
            Id = "warm_affinity_verify_on",
            Description = "WarmSlotVerificationEnabled=true with an unreachable engine URL: turn 2 fails " +
                          "warm-slot verification, saves the slot state, evicts, and re-routes cold.",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                P100Slots = 2,
                WarmSlotVerificationEnabled = true,
                P100LlamaUrl = "http://127.0.0.1:1", // dead port → verify deterministically fails
            },
            Run = async r =>
            {
                await r.SubmitAsync(r.SessionId, 5000, 100);
                await r.SubmitAsync(r.SessionId, 300, 100);
            },
        },

        // ─────────────────────────────────────────────────────────────────
        // Cross-node fallback — affinity node busy, decode wanders to the peer.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "cross_node_fallback",
            Description = "Turn 1 P/D split leaves the affinity session on p100 with its only slot warm-held; " +
                          "turn 2 cannot acquire the affinity node and falls back to rtx (cross_node): KV is " +
                          "restored from Store (Get + StatePut) and rtx decodes.",
            Options = new ScenarioOptions { UseLlamaEngine = true, P100Slots = 1 },
            Run = async r =>
            {
                await r.SubmitAsync(r.SessionId, 5000, 100);
                await r.SubmitAsync(r.SessionId, 300, 100);
            },
        },

        // ─────────────────────────────────────────────────────────────────
        // Migration — store-backed session moves to a new node.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "migration",
            Description = "Slot freed after turn 1 (store state intact): turn 2 restores KV from Store " +
                          "(Get + StatePut onto p100) and decodes on p100.",
            Options = new ScenarioOptions { RunMode = "fast", UseLlamaEngine = true },
            Run = async r =>
            {
                await r.SubmitAsync(r.SessionId, 500, 100);
                var e = r.Ledger.Lookup(r.SessionId);
                Assert.NotNull(e);
                lock (e!)
                {
                    e.SlotFreed = true;
                    e.HasStoreState = true;
                }
                await r.SubmitAsync(r.SessionId, 300, 100);
            },
        },

        // ─────────────────────────────────────────────────────────────────
        // COMBINED two-engine mode.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "combined",
            Description = "COMBINED mode forced: EnginePrefill carries hydra_config once, the peer is " +
                          "exclusively reserved during the request and released after, decode stays on rtx.",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                CombinedEnabled = true,
                MultiEnginePolicy = "combined",
                MultiEngineTopology = true,
            },
            Run = r => r.SubmitAsync(r.SessionId, 20000, 100, forceMode: "combined"),
        },

        // ─────────────────────────────────────────────────────────────────
        // Merged decode (0x43 framed) — Gate A accept and reject.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "merged_decode_accept",
            Description = "Engine advertises merged_decode: the framed DECODE 0x43 carries kv metadata + " +
                          "KV blob, Gate A accepts, the result is polled — the HTTP proxy is NOT called.",
            Options = new ScenarioOptions
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                HealthFactory = () => new EngineModeTests.GateATestHealthMonitor(),
            },
            Run = r => r.SubmitAsync(r.SessionId, 500, 100),
        },

        new()
        {
            Id = "merged_decode_gate_a_reject",
            Description = "Gate A rejects the merged DECODE (Valid=false): the request aborts with " +
                          "InvalidOperationException — no HTTP proxy fallback over an empty KV slot (#470).",
            Options = new ScenarioOptions
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                HealthFactory = () => new EngineModeTests.GateATestHealthMonitor(),
                ConfigureRpc = rpc => rpc.MakeMergedDecodeReject = true,
            },
            Run = r => r.SubmitAsync(r.SessionId, 500, 100),
            ExpectedOutcome = OutcomeClass.Failed,
        },

        // ─────────────────────────────────────────────────────────────────
        // Streaming vs non-streaming decode paths.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "streaming_cold_atomic",
            Description = "Streaming cold atomic: SSE chunks flow through the HTTP proxy, NotifyStreamComplete " +
                          "captures StateGet, releases the warm slot, and persists KV via fire-and-forget Put.",
            Options = new ScenarioOptions { RunMode = "fast", UseLlamaEngine = true },
            Run = r => r.SubmitAsync(r.SessionId, 500, 100, stream: true),
        },

        // ─────────────────────────────────────────────────────────────────
        // Prefix checkpoint — restore hit and miss.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "prefix_hit",
            Description = "Prefix checkpoint present in Store: PrefixRestore gets the blob + manifest, " +
                          "StatePuts it into the prefill slot (PrefixCacheHit), then the request prefills.",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                PrefixCheckpointEnabled = true,
                // #716: Store Get must return a non-empty payload so the empty-payload
                // guard does not suppress the hit path (GetManifest + StatePut).
                ConfigureRpc = rpc => rpc.SetKeyResponse("prefix/", OpCode.Get,
                    (byte)StatusCode.Ok, payload: new byte[4096]),
            },
            Run = r => r.SubmitAsync(r.SessionId, 5000, 100, prefixHash: "abc123"),
        },

        new()
        {
            Id = "prefix_miss",
            Description = "Prefix checkpoint absent from Store (Get → NotFound): PrefixRestore reports a " +
                          "cache miss and falls straight through to a full prefill.",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                PrefixCheckpointEnabled = true,
                ConfigureRpc = rpc => rpc.SetKeyResponse("prefix/", OpCode.Get, (byte)StatusCode.NotFound),
            },
            Run = r => r.SubmitAsync(r.SessionId, 5000, 100, prefixHash: "abc123"),
        },

        // ─────────────────────────────────────────────────────────────────
        // Chunked vs non-chunked KV save (cold_atomic non-chunked save is
        // already covered by cold_atomic_engine; these pin the chunked side).
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "chunked_save",
            Description = "EnableChunks with a tiny chunk size: SaveKv SyncMISSINGs the chunk hashes (all " +
                          "present → no pushes) then writes the manifest. Non-chunked counterpart is " +
                          "cold_atomic_engine / cold_concurrency_pd.",
            Options = new ScenarioOptions
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                EnableChunks = true,
                ChunkSize = 1024,
            },
            Run = r => r.SubmitAsync(r.SessionId, 500, 100),
        },

        new()
        {
            Id = "chunked_save_with_pushes",
            Description = "Chunked save where SyncMissing reports one missing hash: the chunk is pushed " +
                          "(PushChunks) before the manifest is written.",
            Options = new ScenarioOptions
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                EnableChunks = true,
                ChunkSize = 1024,
                ConfigureRpc = rpc =>
                {
                    // PrefillKvBlob (4096 bytes @ 1024 chunk size → 4 chunks) is
                    // deterministic, so the missing-hash response is reproducible.
                    var chunks = ChunkEngine.ChunkAndHash(rpc.PrefillKvBlob);
                    var missing = JsonSerializer.SerializeToUtf8Bytes(new { missing_hashes = new[] { chunks[0].Hash } });
                    rpc.SetKeyResponse("sess_h.kv", OpCode.SyncMissing, (byte)StatusCode.Ok, null, missing);
                },
            },
            Run = r => r.SubmitAsync(r.SessionId, 500, 100),
        },

        // ─────────────────────────────────────────────────────────────────
        // Cancellation mid-flight (lease-leak regression family).
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "cancel_mid_flight",
            Description = "Client cancels between dispatch phases: the pipeline finalizes as Cancelled and " +
                          "every held lease is released (WorkerTracker busy-seconds back to 0).",
            Options = new ScenarioOptions { StartEvaluator = false },
            LegacyOnly = true, // direct-drive seams (DispatchAsync/RunItemPipeline) are legacy-only
            Run = async r =>
            {
                if (r is not SchedulerScenarioRunner sr)
                    throw new NotSupportedException("cancel_mid_flight is legacy-only");
                var item = sr.CreateWorkItem(r.SessionId, 2000, 100);
                var next = await sr.DispatchAsync(item);
                Assert.True(next != WorkItemState.Failed, "phase-1 dispatch should acquire a route");
                sr.CancelItem(item);
                await sr.RunItemPipelineAsync(item, RequestType.Atomic);
                throw new OperationCanceledException("scenario cancelled by design");
            },
            ExpectedOutcome = OutcomeClass.Cancelled,
        },

        // ─────────────────────────────────────────────────────────────────
        // BUSY prefill — retry then success, and retry exhaustion.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "busy_retry_then_success",
            Description = "EnginePrefill returns BUSY twice, then Ok: the pipeline re-enqueues (Retry), " +
                          "re-dispatches, and completes — RetriedThenDone.",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                ConfigureRpc = rpc => rpc.BusyPrefillAttempts = 2,
            },
            Run = r => r.SubmitAsync(r.SessionId, 5000, 100),
            ExpectedOutcome = OutcomeClass.RetriedThenDone,
        },

        new()
        {
            Id = "busy_exhausted",
            Description = "EnginePrefill returns BUSY on every attempt: after WorkItem.MaxRetries the " +
                          "request fails with a clear BUSY-exhausted error (deterministic: 60s busy timeout).",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                ConfigureRpc = rpc => rpc.BusyPrefillAttempts = 100,
            },
            Run = r => r.SubmitAsync(r.SessionId, 5000, 100),
            ExpectedOutcome = OutcomeClass.Failed,
        },

        // ─────────────────────────────────────────────────────────────────
        // #279 — NotImplemented engine prefill falls back to HTTP.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "not_implemented_279",
            Description = "EnginePrefill returns NotImplemented (old binary): the prefill falls back to the " +
                          "HTTP proxy (n_predict=0) and the request completes normally (#279).",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                ConfigureRpc = rpc => rpc.MakeEnginePrefillNotImplemented = true,
            },
            Run = r => r.SubmitAsync(r.SessionId, 5000, 100),
        },

        // ─────────────────────────────────────────────────────────────────
        // Cross-model guard — StatePut mismatch abort + re-prefill.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "state_put_mismatch",
            Description = "StatePut returns model_match=false: the restore is aborted, the slot erased, and " +
                          "the request re-prefills on the correct model before completing (#470).",
            Options = new ScenarioOptions
            {
                UseLlamaEngine = true,
                ConfigureRpc = rpc => rpc.MakeStatePutMismatch = true,
            },
            Run = r => r.SubmitAsync(r.SessionId, 5000, 100),
            // KNOWN LEGACY DEFECT: the abort→decode-fallback lease on rtx is
            // orphaned by the subsequent PickDecode re-acquire (see
            // ScenarioSpec.HasKnownLegacySlotLeak). The invariant gate pins
            // the exact leak shape; the differential gate tracks it.
            HasKnownLegacySlotLeak = true,
        },

        // ─────────────────────────────────────────────────────────────────
        // Store failure — SaveKv store write throws → same-node decode fallback.
        // ─────────────────────────────────────────────────────────────────
        new()
        {
            Id = "store_exception",
            Description = "Store Put throws during SaveKv: the save falls back to same-node decode (the KV " +
                          "stays in the prefill slot) and the request completes; the BgSave Put failure is " +
                          "swallowed (logged) without failing the request.",
            Options = new ScenarioOptions
            {
                RunMode = "fast",
                UseLlamaEngine = true,
                ConfigureRpc = rpc => rpc.SetException(OpCode.Put, new IOException("store: tmpfs write failed")),
            },
            Run = r => r.SubmitAsync(r.SessionId, 500, 100),
        },
    };
}
