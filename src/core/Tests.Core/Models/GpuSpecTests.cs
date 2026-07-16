using Hydra.Core.Models;
using Xunit;

namespace Tests.Core.Models;

public class GpuSpecTests
{
    [Fact]
    public void FlashAttn_HasCapability_ReturnsTrue()
    {
        var gpu = new GpuSpec { Capabilities = GpuCapabilities.FlashAttn };
        Assert.True(gpu.HasCapability(GpuCapabilities.FlashAttn));
    }

    [Fact]
    public void FlashAttn_LacksCombined_ReturnsFalse()
    {
        var gpu = new GpuSpec { Capabilities = GpuCapabilities.FlashAttn };
        Assert.False(gpu.HasCapability(GpuCapabilities.Combined));
    }

    [Fact]
    public void RtxCapable_HasAll_ReturnsTrue()
    {
        var gpu = new GpuSpec { Capabilities = 7 };
        Assert.True(gpu.HasCapability(GpuCapabilities.FlashAttn));
        Assert.True(gpu.HasCapability(GpuCapabilities.Rpc));
        Assert.True(gpu.HasCapability(GpuCapabilities.Combined));
        Assert.True(gpu.HasCapability(7));
    }

    [Fact]
    public void CombinedRequiresAllBits()
    {
        var gpu = new GpuSpec { Capabilities = GpuCapabilities.FlashAttn | GpuCapabilities.Rpc };
        Assert.False(gpu.HasCapability(GpuCapabilities.Combined));
        Assert.True(gpu.HasCapability(GpuCapabilities.FlashAttn | GpuCapabilities.Rpc));
    }

    [Fact]
    public void Constants_HaveCorrectValues()
    {
        Assert.Equal(1, GpuCapabilities.FlashAttn);
        Assert.Equal(2, GpuCapabilities.Rpc);
        Assert.Equal(4, GpuCapabilities.Combined);
    }
}
