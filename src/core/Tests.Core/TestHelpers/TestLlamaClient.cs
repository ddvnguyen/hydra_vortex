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

    /// <summary>STATE_META call count (warm-check observability for #720 tests).</summary>
    public int MetaCallCount;

    /// <summary>Every PutStateAsync call: (slot, body bytes drained from the stream, declared content length).</summary>
    public List<(int SlotId, byte[] Body, long DeclaredLen)> PutStateCalls { get; } = [];

    /// <summary>Responder for PutStateAsync; defaults to a successful restore of the
    /// drained body size.</summary>
    public Func<int, byte[], Task<RestoreResult>> PutStateResponder =
        (slotId, body) => Task.FromResult(new RestoreResult
        {
            Restored = true,
            NPast = 0,
            Bytes = body.Length,
        });

    public override Task<SlotMeta> GetStateMetaAsync(int slotId, CancellationToken ct)
    {
        Interlocked.Increment(ref MetaCallCount);
        return Task.FromResult(_meta);
    }

    public override Task<RestoreResult> PutStateAsync(int slotId, Stream data, long contentLength, CancellationToken ct)
    {
        var drained = new byte[contentLength > 0 ? (int)Math.Min(contentLength, int.MaxValue) : 0];
        var read = 0;
        while (read < drained.Length)
        {
            var n = data.Read(drained, read, drained.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var body = drained[..read];
        PutStateCalls.Add((slotId, body, contentLength));
        return PutStateResponder(slotId, body);
    }


    public override Task<bool> HealthAsync(CancellationToken ct)
        => Task.FromResult(true);
    public override Task EraseSlotAsync(int slotId, CancellationToken ct)
        => Task.CompletedTask;
}
