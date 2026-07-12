using System.Text.Json;

namespace Hydra.Core.Models;

/// <summary>
/// Typed result for 0x40 CONFIGURE responses from the engine. Matches
/// the wire schema in ddvnguyen/hydra_vortex#406 specs/rpc-protocol.md
/// (the engine emits <c>{success, tier, params_applied, deferred_keys, error}</c>).
///
/// <see cref="StateChunkSizeApplied"/> is the legacy echo (per
/// hydra#334) for the existing
/// <c>WorkerSchedulerService.cs:2842</c> startup <c>state_chunk_size</c>
/// call site; it stays populated identically to
/// <see cref="ParamsApplied"/>["state_chunk_size"] for single-source-of-truth.
/// </summary>
public sealed record EngineConfigureResult(
    bool Success,
    string Tier,
    IReadOnlyDictionary<string, JsonElement> ParamsApplied,
    IReadOnlyList<string> DeferredKeys,
    string? Error,
    long StateChunkSizeApplied = 0
)
{
    /// <summary>True when the response indicates any T2/T3 deferred work.</summary>
    public bool HasDeferredChanges => DeferredKeys.Count > 0;

    /// <summary>True when the response indicates a T1-only apply (no deferred work).</summary>
    public bool IsT1 => string.Equals(Tier, "T1", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the response indicates T2 work was deferred.</summary>
    public bool IsT2 => string.Equals(Tier, "T2", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the response indicates T3 work was deferred.</summary>
    public bool IsT3 => string.Equals(Tier, "T3", StringComparison.OrdinalIgnoreCase);
}
