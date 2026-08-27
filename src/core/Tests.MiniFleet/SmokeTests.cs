using Xunit;

namespace Tests.MiniFleet;

/// <summary>
/// Mini-fleet smoke tier entry points (brief §Acceptance criteria).
/// All facts carry Trait Tier=MiniFleet so the charter VERIFY filter
///   dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet"
/// selects exactly this tier. Skeletons are Skip'd until implemented.
/// </summary>
[Trait("Tier", "MiniFleet")]
public sealed class SmokeTests
{
    /// <summary>AC1: cpu-2node preset green end-to-end on host WITHOUT GPU
    /// (real engines, ngl=0, threads 3+3, ctx 4096).</summary>
    [Fact(Skip = "skeleton — implement per task brief §Components 1")]
    public void CpuTwoNode_EndToEnd_Passes()
    {
        // TODO(minifleet): start MiniFleetAppHost(Presets.Cpu2Node) → run catalog specs
        // via RealEngineScenarioRunner → assert completion OK / finish_reason / usage>0.
    }

    /// <summary>AC2: gpu-gpu-shared preset green against live P100 VM through the
    /// ssh shim (validated topology in Presets.GpuGpuShared); evidence committed.</summary>
    [Fact(Skip = "skeleton — requires live P100 VM lane + staged binaries")]
    public void GpuGpuShared_VmSmoke_Passes()
    {
        // TODO(minifleet): scripts/minifleet/vm-run.sh start → run scenarios over ssh-shimmed
        // engines → Artifacts.WriteTracePairAsync + evidence under docs/minifleet/evidence/.
        // VM hygiene (AC3): only our own pids; never touch resident :8086/:8090.
    }
}
