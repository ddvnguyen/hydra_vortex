namespace Tests.MiniFleet;

/// <summary>
/// Mini-fleet smoke tier — real-engine multi-node scenario runner
/// (spec of record: orchestration/state/tasks/2026-08-27-minifleet.md §Components 1).
///
/// Aspire DistributedApplication host: boots a sandbox Hydra.Core + hydra-head(s)
/// plus REAL llama-engine processes as ExecutableResources. No FakeLlamaEngine in
/// this tier — the point is validating implementation changes against real wire
/// behavior before the expensive rigs are touched.
/// </summary>
public static class MiniFleetAppHost
{
    // TODO(minifleet): build + start DistributedApplication for a preset:
    //   - register ExecutableResource per engine node from Presets.PresetSpec
    //     (binary = MINIFLEET_ENGINE_BIN or staged path; env LD_LIBRARY_PATH per quirk #2)
    //   - sandbox Hydra.Core (+ heads) pointed at those engines, mirroring Hydra.AppHost wiring
    //   - expose engine /health ({"status":"ok"}, quirk #3) readiness gating before scenarios run
    //   - HYDRA_SCHEDULER_IMPL=legacy|v2 plumb-through for A/B passes (brief §Components 1 hooks)
}
