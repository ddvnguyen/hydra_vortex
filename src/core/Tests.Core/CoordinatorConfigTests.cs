using Hydra.Core;
using Hydra.Core.Models;

namespace Tests.Core;

public sealed class CoordinatorConfigTests
{
    [Fact]
    public void Validate_NoWorkers_Throws()
    {
        var cfg = new CoordinatorConfig();
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_ValidWorkers_DoesNotThrow()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3 });
        cfg.Validate(); // no throw
    }

    [Fact]
    public void Validate_EmptyWorkerName_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "", Host = "localhost", RpcPort = 9601, LlamaUrl = "http://localhost:8080" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_MissingHost_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", RpcPort = 9601, LlamaUrl = "http://localhost:8080" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_InvalidPort_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", Host = "localhost", RpcPort = 0, LlamaUrl = "http://localhost:8080" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_InvalidLlamaUrl_Throws()
    {
        var cfg = new CoordinatorConfig();
        cfg.Workers.Add(new WorkerConfig { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaUrl = "not-a-url" });
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Defaults_Are_Reasonable()
    {
        var cfg = new CoordinatorConfig();
        Assert.Equal(9000, cfg.Port);
        Assert.Equal("concurrency", cfg.RunMode);
        Assert.False(cfg.MixPrecisionEnabled);
        Assert.Equal(2048, cfg.AtomicThreshold);
        Assert.Equal(5120, cfg.WarmThreshold);
        Assert.Equal(1800, cfg.LlamaRequestTimeoutS);
    }

    [Fact]
    public void AtomicThreshold_NewEnvVar_Wins()
    {
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_THRESHOLD", "777");
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", "111");
        try
        {
            Assert.Equal(777, new CoordinatorConfig().AtomicThreshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_THRESHOLD", null);
            Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", null);
        }
    }

    [Fact]
    public void AtomicThreshold_LegacyEnvVar_FallsBack()
    {
        // Back-compat: the legacy HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD still applies
        // when the new HYDRA_COORD_ATOMIC_THRESHOLD is unset.
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_THRESHOLD", null);
        Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", "333");
        try
        {
            Assert.Equal(333, new CoordinatorConfig().AtomicThreshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYDRA_COORD_ATOMIC_TOKEN_THRESHOLD", null);
        }
    }

    [Fact]
    public void Worker_CanPrefill_Mixed()
    {
        var w = new WorkerConfig { WorkerType = 3 };
        Assert.True(w.CanPrefill);
        Assert.True(w.CanDecode);
    }

    [Fact]
    public void Worker_CanPrefill_OnlyPrefill()
    {
        var w = new WorkerConfig { WorkerType = 1 };
        Assert.True(w.CanPrefill);
        Assert.False(w.CanDecode);
    }

    [Fact]
    public void Worker_CanDecode_OnlyDecode()
    {
        var w = new WorkerConfig { WorkerType = 2 };
        Assert.False(w.CanPrefill);
        Assert.True(w.CanDecode);
    }
}
