using Hydra.Core.Models;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// The routing decision for one request — produced by <see cref="IRoutePlanner"/>,
/// consumed by the evaluator (capacity gate) and the route phase handler. Pure
/// data: no side effects, so it is trivially unit-testable.
/// </summary>
public sealed record RouteDecision(
    RequestType RequestType,
    string PrefillWorker,
    string? DecodeWorker,
    bool ReuseStoreState,
    int Priority)
{
    /// <summary>True when no viable worker was found (the request must wait).</summary>
    public bool HasCapacity => !string.IsNullOrEmpty(PrefillWorker);
}
