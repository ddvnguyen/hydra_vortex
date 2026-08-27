namespace Tests.MiniFleet;

/// <summary>
/// Driver adapter: executes scenario specs (Tests.Core harness catalog — reuse, NOT
/// fork, per brief §Components 1; restore the Tests.Core.Harness using once the
/// catalog lands) against REAL HTTP llama-engine endpoints instead
/// of the fake RPC client. Asserts, per brief:
///   - completion status OK,
///   - finish_reason present,
///   - usage tokens &gt; 0;
/// store side-effects are ignored (out of scope this PR).
///
/// Reasoning-model quirk (#4): Qwen3.5-9B-Q4_K_M is a REASONING model — reserve
/// ≥120 completion tokens or content comes back "" while thinking fills
/// reasoning_content. Treat that as PASS for smoke purposes.
/// </summary>
public sealed class RealEngineScenarioRunner
{
    // TODO(minifleet): map each ScenarioSpec to a real HTTP conversation
    //   (POST {engine}/v1/chat/completions), apply the assertions above,
    //   and return a normalized result comparable across HYDRA_SCHEDULER_IMPL passes.
}
