using Hydra.Core.Models;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// The routing decision for one request — produced by <see cref="IRoutePlanner"/>.
/// Pure data: no side effects, so it is trivially unit-testable.
///
/// <para><b>Two-phase (Prefill) requests pick ONE worker at a time</b> (GPU
/// utilization rule): <see cref="PrefillWorker"/> is chosen up front; the decode
/// worker is deliberately NOT reserved — it is picked later at decode time via
/// <see cref="IRoutePlanner.PlanDecode"/>, after the prefill slot has been
/// released. Atomic/Solo requests set the decode worker immediately because they
/// occupy a single slot for the whole request.</para>
///
/// <para><b>COMBINED (epic #591):</b> <see cref="PrefillWorker"/> and
/// <see cref="DecodeWorker"/> are BOTH the head (decode stays on the head — the
/// KV is resident), <see cref="PeerWorker"/> names the exclusively-reserved peer
/// GPU, and <see cref="MultiMode"/>/<see cref="MultiEngineConfig"/> carry the
/// two-engine mode + model config whose hydra_config rides the EnginePrefill.</para>
/// </summary>
public sealed record RouteDecision(
    RequestType RequestType,
    string? PrefillWorker,
    string? DecodeWorker,
    bool ReuseStoreState,
    int Priority,
    string? PeerWorker = null,
    MultiEngineMode MultiMode = MultiEngineMode.None,
    EngineConfig? MultiEngineConfig = null)
{
    /// <summary>True when the planner found a worker to start the request on
    /// (a prefill worker for Prefill-type, or a decode worker for Solo).</summary>
    public bool HasCapacity => !string.IsNullOrEmpty(PrefillWorker) || !string.IsNullOrEmpty(DecodeWorker);
}
