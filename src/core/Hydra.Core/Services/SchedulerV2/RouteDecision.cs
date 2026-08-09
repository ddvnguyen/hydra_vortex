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
/// </summary>
public sealed record RouteDecision(
    RequestType RequestType,
    string? PrefillWorker,
    string? DecodeWorker,
    bool ReuseStoreState,
    int Priority)
{
    /// <summary>True when the planner found a worker to start the request on
    /// (a prefill worker for Prefill-type, or a decode worker for Solo).</summary>
    public bool HasCapacity => !string.IsNullOrEmpty(PrefillWorker) || !string.IsNullOrEmpty(DecodeWorker);
}
