using Hydra.Core.Extensions;
using Hydra.Core.Models;
using Hydra.Core.Services;
using Hydra.Core.Services.SchedulerV2;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Core.SchedulerV2Tests;

/// <summary>
/// A/B test: legacy <see cref="WorkerSchedulerService"/> and v2
/// <see cref="WorkerSchedulerV2"/> both implement <see cref="IWorkerScheduler"/>,
/// and the DI toggle (HYDRA_SCHEDULER_IMPL) selects which one backs the contract.
/// </summary>
public sealed class SchedulerToggleTests
{
    private static CoordinatorConfig MakeConfig(string impl) => new()
    {
        SchedulerImplementation = impl,
        Workers = new List<WorkerConfig>
        {
            new() { Name = "rtx", Host = "localhost", RpcPort = 9601, LlamaRpcPort = 9601, LlamaUrl = "http://localhost:8080", WorkerType = 3, Slots = 2 },
        },
    };

    [Fact]
    public void Default_Is_Legacy_Scheduler()
    {
        using var sp = new ServiceCollection().AddCoordinator(MakeConfig("legacy")).BuildServiceProvider();
        var scheduler = sp.GetRequiredService<IWorkerScheduler>();
        Assert.IsType<WorkerSchedulerService>(scheduler);
    }

    [Fact]
    public void Toggle_V2_Resolves_V2_Scheduler()
    {
        using var sp = new ServiceCollection().AddCoordinator(MakeConfig("v2")).BuildServiceProvider();
        var scheduler = sp.GetRequiredService<IWorkerScheduler>();
        Assert.IsType<WorkerSchedulerV2>(scheduler);
    }

    [Fact]
    public void Both_Implementations_Are_Independently_Resolvable()
    {
        using var sp = new ServiceCollection().AddCoordinator(MakeConfig("v2")).BuildServiceProvider();
        var legacy = sp.GetRequiredService<WorkerSchedulerService>();
        var v2 = sp.GetRequiredService<WorkerSchedulerV2>();
        var resolved = sp.GetRequiredService<IWorkerScheduler>();
        Assert.IsType<WorkerSchedulerService>(legacy);
        Assert.IsType<WorkerSchedulerV2>(v2);
        Assert.Same(v2, resolved);
    }
}
