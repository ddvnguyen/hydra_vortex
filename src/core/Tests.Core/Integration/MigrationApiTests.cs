using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests.Core.Integration;

// ═══════════════════════════════════════════════════════════════════════
// Direct coverage for MigrateSessionAsync (A5, #299 / review finding #318).
//
// Before the fix this method hardcoded slotId=0 and never acquired/released a
// slot on the target — a silent slot leak and a collision with whatever was
// already in slot 0. These tests exercise the standalone API directly (the
// existing StreamingSlotCleanupTests only drive RouteAsync→RestoreKvAsync and
// never call MigrateSessionAsync), pinning the post-fix contract:
//   • a REAL slot is acquired on the target and released again (no leak),
//   • the ledger is re-registered on the target with that concrete slot,
//   • n_past is carried from the StatePut meta,
//   • the guard branches throw before touching any slot.
//
// Reuses StreamingFixture: its TestRpcClient doubles as Store + llama binary
// RPC (returns Ok with n_past=2000), so nothing touches a socket.
// ═══════════════════════════════════════════════════════════════════════

[Collection("StreamingIntegrationTests")]
public sealed class MigrationApiTests
{
    [Fact]
    public async Task Migrate_AcquiresRealSlot_RegistersTarget_AndReleases()
    {
        await using var f = new StreamingFixture();
        f.Ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        f.Ledger.MarkStoreState("sess_mig"); // HasStoreState=true → migratable
        var p100FreeBefore = f.Tracker.FreeSlotCount("p100");
        Assert.True(p100FreeBefore >= 1, "fixture should leave p100 with a free slot");
        f.Rpc.ClearCalls();

        await f.Scheduler.MigrateSessionAsync("sess_mig", "p100", default);

        // Slot released after the StatePut completes → no leak (A5 core guarantee).
        Assert.Equal(p100FreeBefore, f.Tracker.FreeSlotCount("p100"));

        // Ledger now points at the target with a concrete (non-null) slot id,
        // not the old hardcoded 0-with-no-acquire.
        var entry = f.Ledger.Lookup("sess_mig")!;
        Assert.Equal("p100", entry.NodeName);
        Assert.NotNull(entry.SlotId);
        // #617/A2: a successful migrate marks the entry NON-RESIDENT (as if
        // evicted) so the next request re-enters the KV restore path instead
        // of a doomed warm decode on the migrated slot.
        Assert.True(entry.SlotFreed, "migrated entry must be non-resident (SlotFreed=true)");
        Assert.Equal(2000, entry.NPast); // parsed from StatePut meta

        // A Store Get (fetch KV) and a llama StatePut (restore into the slot) happened.
        Assert.True(f.Rpc.HasCall(OpCode.Get), "expected a Store Get for the KV blob");
        Assert.True(f.Rpc.HasCall(OpCode.StatePut), "expected a llama StatePut into the target slot");
    }

    [Fact]
    public async Task Migrate_NoFreeSlotOnTarget_Throws_WithoutTouchingOthersSlots()
    {
        await using var f = new StreamingFixture(p100Slots: 1);
        f.Ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        f.Ledger.MarkStoreState("sess_mig");

        // Occupy the target's only slot.
        Assert.True(f.Tracker.TryAcquireSlot("p100", out var held, "decode"));
        Assert.Equal(0, f.Tracker.FreeSlotCount("p100"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Scheduler.MigrateSessionAsync("sess_mig", "p100", default));

        // The failed migration must not have released the unrelated held slot
        // (the old Release(name) bug would pop an arbitrary one).
        Assert.Equal(0, f.Tracker.FreeSlotCount("p100"));
        f.Tracker.ReleaseSlot("p100", held);
    }

    [Fact]
    public async Task Migrate_NoStoreState_Throws_NotMigratable_WithoutAcquiringSlot()
    {
        await using var f = new StreamingFixture();
        f.Ledger.Register("sess_nostate", "rtx", slotId: 1, nPast: 100); // HasStoreState=false
        var p100FreeBefore = f.Tracker.FreeSlotCount("p100");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Scheduler.MigrateSessionAsync("sess_nostate", "p100", default));
        Assert.Contains("not migratable", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Guard fires before any slot work — target untouched.
        Assert.Equal(p100FreeBefore, f.Tracker.FreeSlotCount("p100"));
    }

    [Fact]
    public async Task Migrate_UnknownTarget_Throws_WithoutAcquiringSlot()
    {
        await using var f = new StreamingFixture();
        f.Ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        f.Ledger.MarkStoreState("sess_mig");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Scheduler.MigrateSessionAsync("sess_mig", "does_not_exist", default));
    }

    // A5 finally: the StatePut into the target slot is fault-injectable (it goes
    // through the llama RPC double). When it throws, the slot acquired moments
    // earlier MUST still be released in the finally — the core leak the fix closes.
    [Fact]
    public async Task Migrate_StatePutThrows_AcquiredSlotReleasedInFinally()
    {
        var cfg = MakeMigrationConfig();
        var ledger = new SessionLedger();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var sp = new ServiceCollection().BuildServiceProvider();

        // Store Get succeeds; the llama StatePut throws.
        var rpc = new ThrowOnOpRpcClient(throwOn: OpCode.StatePut);
        var scheduler = new WorkerSchedulerService(cfg, ledger, tracker,
            new TestCompletionProxy(), new TestHealthMonitor(), rpc, sp, Serilog.Log.Logger);
        scheduler.AgentClientFactory = (_, _) => rpc;

        ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        ledger.MarkStoreState("sess_mig");
        var p100FreeBefore = tracker.FreeSlotCount("p100");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.MigrateSessionAsync("sess_mig", "p100", default));

        // The slot acquired before the failing StatePut was released in finally.
        Assert.Equal(p100FreeBefore, tracker.FreeSlotCount("p100"));
    }

    // #617/A1: the blind StatePut's response status was never checked. A
    // non-success (engine reports the slot didn't restore) must FAIL the
    // migrate — no ledger register, no migrated=true — so the request never
    // proceeds into a doomed continuation.
    [Fact]
    public async Task Migrate_StatePutNonOkStatus_Fails_NotResident_NoMigratedTrue()
    {
        var cfg = MakeMigrationConfig();
        var ledger = new SessionLedger();
        var tracker = new WorkerTracker();
        foreach (var w in cfg.Workers) tracker.InitWorker(w.Name, w.Slots);
        var sp = new ServiceCollection().BuildServiceProvider();

        // Store Get succeeds; the llama StatePut returns a non-success status.
        var rpc = new NonOkOnOpRpcClient(OpCode.StatePut);
        var scheduler = new WorkerSchedulerService(cfg, ledger, tracker,
            new TestCompletionProxy(), new TestHealthMonitor(), rpc, sp, Serilog.Log.Logger);
        scheduler.AgentClientFactory = (_, _) => rpc;

        ledger.Register("sess_mig", "rtx", slotId: 1, nPast: 100);
        ledger.MarkStoreState("sess_mig");
        var p100FreeBefore = tracker.FreeSlotCount("p100");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.MigrateSessionAsync("sess_mig", "p100", default));

        // The failed migrate must not register the session as resident on the
        // target — the ledger still points at the source node.
        var entry = ledger.Lookup("sess_mig")!;
        Assert.Equal("rtx", entry.NodeName);
        Assert.Equal(1, entry.SlotId);
        Assert.False(entry.SlotFreed, "failed migrate must leave the source residency untouched");
        Assert.True(entry.HasStoreState);

        // The acquired slot was released despite the failure (no leak).
        Assert.Equal(p100FreeBefore, tracker.FreeSlotCount("p100"));
    }

    // #617/A2: on SUCCESS the migrated ledger entry is marked NON-RESIDENT
    // (as if SlotFreed/evicted) so the NEXT request for the session re-enters
    // the restore path (Store Get → llama StatePut) instead of a warm
    // straight-decode on the migrated slot (a doomed continuation).
    [Fact]
    public async Task Migrate_Success_NonResident_NextRequestRestores()
    {
        await using var f = new StreamingFixture(prefillTokens: 2000, decodeTokens: 150);
        // Turn 1 (non-streaming): build a session with store state
        // (prefill → save → decode) so it becomes migratable.
        await f.SubmitAsync("sess_mig2", 2000, 100, stream: false);

        // Migrate to p100 (StatePut via the RPC double returns Ok + n_past=2000).
        f.Rpc.ClearCalls();
        var result = await f.Scheduler.MigrateSessionAsync("sess_mig2", "p100", default);
        Assert.Equal(true, ((dynamic)result).migrated);

        // Migrated entry is non-resident but still points at the target node.
        var e = f.Ledger.Lookup("sess_mig2")!;
        Assert.Equal("p100", e.NodeName);
        Assert.True(e.SlotFreed, "migrated entry must be marked non-resident");
        Assert.True(e.HasStoreState);

        // Turn 2: the continuation must re-enter the restore path — Store Get
        // (KV fetch) + llama StatePut (KV push) — NOT a warm-affinity
        // straight-decode (which would make zero RPC calls).
        f.Rpc.ClearCalls();
        await f.SubmitAsync("sess_mig2", 100, 50);
        Assert.True(f.Rpc.HasCall(OpCode.Get, "sess_mig2"),
            "continuation after migrate must fetch KV from the Store (restore path)");
        Assert.True(f.Rpc.HasCall(OpCode.StatePut),
            "continuation after migrate must push KV into the decode node (restore path)");
    }

    private static CoordinatorConfig MakeMigrationConfig() => new()
    {
        PrefixCheckpointEnabled = false,
        WarmSlotVerificationEnabled = false,
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx",  Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080",      WorkerType = 3, Slots = 2, PrefillPriority = 1,   DecodePriority = 2 },
            new() { Name = "p100", Host = "localhost", RpcPort = 9602, LlamaUrl = "http://192.168.122.21:8086", WorkerType = 2, Slots = 1, PrefillPriority = 100, DecodePriority = 1 },
        }
    };

    // ── #470 canonical identity: migrated turn with a JsonElement model ──

    /// <summary>Loader where the P/D-split routing identity maps prefill to
    /// the mini quant and decode to the balanced quant — the decode alias is
    /// what the DECODE frame must carry, never the raw routing key.</summary>
    private static void UsePdLoader()
    {
        var models = new Dictionary<string, ModelTemplate>
        {
            ["moe-35b-pd"] = new ModelTemplate
            {
                PrefillAlias = "qwen3.6-35B-mini",
                DecodeAlias  = "qwen3.6-35B-balanced",
            },
        };
        var config = new ModelsConfig
        {
            SchemaVersion = 3,
            Models = models,
            ModelFileAliases = new Dictionary<string, string>
            {
                ["qwen3.6-35B-mini"]     = "Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
                ["qwen3.6-35B-balanced"] = "Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf",
            },
        };
        ModelConfigLoader.Reset();
        ModelConfigLoader.SetInstance(ModelConfigLoader.Create(config));
    }

    /// <summary>Build a migrated-continuation item whose request model is a
    /// JsonElement (the shape AutoRouter failure leaves in the body) with the
    /// canonical identity stamped the way SubmitAsync does at ingress.</summary>
    private static WorkItem MakeJsonElementItem(string requestModel, ModelConfigLoader loader)
    {
        var item = new WorkItem(
            new Dictionary<string, object>(),
            new List<Dictionary<string, object>>(),
            "sess", "trace", null, 1, 10);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(requestModel));
        item.Request["model"] = doc.RootElement.Clone();
        item.ModelIdentity = RequestedModelIdentity.Resolve(requestModel, loader);
        return item;
    }

    [Fact]
    public void MigrationContinuation_DecodeFrameModel_IsEngineAlias()
    {
        // #470 canonical identity: a migrated turn (RouteType="migration")
        // whose request model is a JsonElement (AutoRouter failed) must
        // resolve the DECODE frame model to the engine's decode alias
        // (moe-35b-pd → qwen3.6-35B-balanced) — NOT the raw routing key.
        // The last `is string` read at :2552 silently returned null on the
        // JsonElement shape and fell through to the historic chain.
        UsePdLoader();
        try
        {
            var loader = ModelConfigLoader.InstanceOrNull!;
            WorkerConfig worker = new() { Name = "p100", ModelAlias = null };

            // KvModelAlias absent: the frame model is the identity's decode
            // alias, never the routing key.
            var item = MakeJsonElementItem("moe-35b-pd", loader);
            item.RouteType = "migration";
            var alias = WorkerSchedulerService.ResolveMergedDecodeModelAlias(item, worker);
            Assert.Equal("qwen3.6-35B-balanced", alias);
            Assert.DoesNotContain("moe-35b-pd", alias);

            // Legacy item without ModelIdentity (constructed outside
            // SubmitAsync): RequestModelString unwraps the JsonElement and the
            // decode-role translation still fires.
            var legacy = new WorkItem(
                new Dictionary<string, object>(),
                new List<Dictionary<string, object>>(),
                "sess", "trace", null, 1, 10)
            {
                RouteType = "migration",
            };
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize("moe-35b-pd")))
                legacy.Request["model"] = doc.RootElement.Clone();
            Assert.Equal("qwen3.6-35B-balanced",
                WorkerSchedulerService.ResolveMergedDecodeModelAlias(legacy, worker));

            // The source-node KV alias (mini — what actually built the KV on
            // the source node) must NOT win: the migrated branch prefers the
            // request's decode quant — the alias that maps to the target's
            // resident path. Pre-fix the `is string` null fall-through sent
            // the source quant on a cross-quant restore.
            var withKvAlias = MakeJsonElementItem("moe-35b-pd", loader);
            withKvAlias.RouteType = "migration";
            withKvAlias.KvModelAlias = "qwen3.6-35B-mini";
            Assert.Equal("qwen3.6-35B-balanced",
                WorkerSchedulerService.ResolveMergedDecodeModelAlias(withKvAlias, worker));
        }
        finally
        {
            ModelConfigLoader.Reset();
        }
    }
}

// RPC double that returns Ok for every op except the one it is told to fail on,
// where it throws — lets a test fault-inject a specific stage of a flow.
internal sealed class ThrowOnOpRpcClient : RpcClient
{
    private readonly OpCode _throwOn;
    public ThrowOnOpRpcClient(OpCode throwOn) : base("test", 0) => _throwOn = throwOn;

    public override Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
    {
        if (op == _throwOn)
            throw new InvalidOperationException($"injected RPC failure on {op}");
        var meta = JsonSerializer.Serialize(new { n_past = 2000, restored = true, stored = true, model_match = true, tokenizer = "llama", model_name = "nano", model_quant = "Q4_K", model_capabilities = 0, model_alias = "nano", model_path = "/dev/null" });
        return Task.FromResult(new RpcResponse((byte)StatusCode.Ok, meta, Array.Empty<byte>()));
    }
}

// RPC double that returns Ok for every op except the one it is told to fail
// on, where it returns a non-success status (no throw) — lets a test verify
// the caller checks response STATUS, not just exceptions.
internal sealed class NonOkOnOpRpcClient : RpcClient
{
    private readonly OpCode _nonOkOn;
    public NonOkOnOpRpcClient(OpCode nonOkOn) : base("test", 0) => _nonOkOn = nonOkOn;

    public override Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
    {
        if (op == _nonOkOn)
            return Task.FromResult(new RpcResponse((byte)StatusCode.Error, "slot restore failed", Array.Empty<byte>()));
        var meta = JsonSerializer.Serialize(new { n_past = 2000, restored = true, stored = true, model_match = true, tokenizer = "llama", model_name = "nano", model_quant = "Q4_K", model_capabilities = 0, model_alias = "nano", model_path = "/dev/null" });
        return Task.FromResult(new RpcResponse((byte)StatusCode.Ok, meta, Array.Empty<byte>()));
    }
}
