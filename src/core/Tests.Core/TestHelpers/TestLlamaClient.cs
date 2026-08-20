using Hydra.Core;

namespace Tests.Core.Integration;

/// <summary>
/// Hermetic LlamaClient stub for coordinator-logic tests. The scheduler's
/// GetLlamaClient() falls back to a REAL HttpClient + live engine URL
/// (localhost:8080 / 192.168.122.21:8086) unless the fixture sets
/// LlamaClientFactory. That made integration tests dial the production rig
/// and hang at teardown when the engine was busy. These tests verify the
/// coordinator's routing/fallback logic, not the engine boundary — so every
/// state call is stubbed here.
///
/// Real engine-boundary tests belong in Tests.LiveRig (run against the live
/// rig via .github/workflows/test-system.yml), not Tests.Core.
/// </summary>
internal class TestLlamaClient : LlamaClient
{
    private readonly SlotMeta _meta;

    public TestLlamaClient(SlotMeta? meta = null)
        : base("http://mock:0")
        => _meta = meta ?? new SlotMeta { SlotId = 0, NPast = 0, IsProcessing = false };

    public override Task<SlotMeta> GetStateMetaAsync(int slotId, CancellationToken ct)
        => Task.FromResult(_meta);

    public override Task<bool> HealthAsync(CancellationToken ct)
        => Task.FromResult(true);

    public override Task EraseSlotAsync(int slotId, CancellationToken ct)
        => Task.CompletedTask;
}
