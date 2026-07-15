using Xunit;

namespace Tests.Core.Services;

public class AutoRouterTests
{
    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step0_WarmSession_StayOnBoundModel() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step0_WarmSession_SlotStale_Reroute() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step1_SmallPrompt_MatchesMoESolo() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step1_LargePrompt_MatchesMoEPD() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step1_ContextOverflow_DenseExcluded() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step1_P100Down_MoEPDExcluded() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step2_RTX_MeetsMoERequirements() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step2_Dense_RequiresCombinedCapability() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step3_KeepResidentHigherQuality() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step3_SwapWhenJustified() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step4_Solo_PicksAtomicWorker() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step4_PD_PicksPrefillAndDecode() { }

    [Fact(Skip = "Requires AutoRouter implementation - Batch 3")]
    public void Step4_Combined_PicksHeadAndPeer() { }
}
