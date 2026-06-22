namespace Hydra.Core.Models;

/// <summary>
/// Two-engine "work together" mode for a single request.
///   None     — solo (one engine).
///   Pipeline — prima.cpp-style layer-window split: head computes blk[0..k), the peer
///              loads blk[k..N) from its OWN local model and computes them; only
///              boundary activations cross the link (Hydra HY RPC).
///   Combined — ggml expert-split: expert tensors routed to the peer's RPC backend,
///              flipped per request via EngineSetExpertMode.
/// </summary>
public enum MultiEngineMode
{
    None = 0,
    Pipeline = 1,
    Combined = 2
}
