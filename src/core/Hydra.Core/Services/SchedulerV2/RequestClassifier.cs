using Hydra.Core.Models;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Classifies a request into a <see cref="RequestType"/> and its queue priority.
/// Single responsibility: turn a parsed request + system state into a type/priority.
/// </summary>
public interface IRequestClassifier
{
    RequestType Classify(ChatRequest chat, CoordinatorConfig config, bool hasWarmSession);
    int ComputePriority(RequestType type);
}

public sealed class RequestClassifier : IRequestClassifier
{
    public RequestType Classify(ChatRequest chat, CoordinatorConfig config, bool hasWarmSession)
    {
        // Explicit multi-engine request (hydra model rule): COMBINED/PIPELINE
        // mode is only valid when the coordinator has it enabled.
        if (chat.ForceMode is "combined" && config.CombinedEnabled)
            return RequestType.Combined;

        // Warm affinity (session KV resident) is always decode-only follow-up work.
        if (hasWarmSession)
            return RequestType.Solo;

        // Large prompts go through the two-tier (prefill-then-decode) pipeline;
        // small prompts run prefill+decode on one slot.
        return chat.EstimatedTokens >= config.AtomicThreshold
            ? RequestType.Prefill
            : RequestType.Atomic;
    }

    /// <summary>Lower = higher priority. Mirrors the legacy priority ladder so
    /// A/B queue behaviour stays comparable (decode handoff beats fresh work).</summary>
    public int ComputePriority(RequestType type) => type switch
    {
        RequestType.Decode => 0,
        RequestType.Solo => 10,
        RequestType.Combined => 20,
        RequestType.Atomic => 30,
        _ => 40, // Prefill
    };
}
