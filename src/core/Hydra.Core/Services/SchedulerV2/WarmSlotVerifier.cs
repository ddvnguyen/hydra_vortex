using Hydra.Core.Models;
using Hydra.Core.Repositories;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Verifies that a warm slot still genuinely holds the session's KV before a warm
/// (Solo) route decodes on it (review #8 / golden warm_affinity_verify_on). The
/// default HTTP implementation wraps <see cref="Router.VerifyWarmSlotAsync"/>:
/// GET the worker's /slots, find the slot, and check it is not stuck and its
/// n_past covers the session's NPast. A failure means the warm route must be
/// abandoned (evict + re-route cold) — never decode over a dead/empty slot (#469).
/// </summary>
public interface IWarmSlotVerifier
{
    Task<bool> VerifyAsync(WorkerConfig worker, SessionEntry? entry, string traceId);
}

/// <summary>HTTP-backed warm-slot verification (the legacy Router check).</summary>
public sealed class HttpWarmSlotVerifier : IWarmSlotVerifier
{
    public Task<bool> VerifyAsync(WorkerConfig worker, SessionEntry? entry, string traceId)
        => entry is null
            ? Task.FromResult(false)
            : Router.VerifyWarmSlotAsync(worker, entry, traceId);
}
