using Hydra.Store.Controllers;
using Hydra.Store.Models;
using Hydra.Store.Repositories;
using Hydra.Store.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hydra.Store.Extensions;

public static class CoordinatorServiceExtensions
{
    public static IServiceCollection AddCoordinator(this IServiceCollection services, CoordinatorConfig config)
    {
        // Config
        services.AddSingleton(config);

        // Repositories
        services.AddSingleton<ISessionLedger, SessionLedger>();
        services.AddSingleton<IWorkerTracker>(sp => new WorkerTracker(config.WorkerErrorThreshold));

        // Services
        services.AddSingleton<ICompletionProxyService>(sp => new CompletionProxyService(config.LlamaRequestTimeoutS));
        services.AddSingleton<IWorkerScheduler, WorkerSchedulerService>(sp =>
        {
            var cfg = sp.GetRequiredService<CoordinatorConfig>();
            var ledger = sp.GetRequiredService<ISessionLedger>();
            var tracker = sp.GetRequiredService<IWorkerTracker>();
            var proxy = sp.GetRequiredService<ICompletionProxyService>();
            var storeClient = new Hydra.Shared.RpcClient(cfg.StoreHost, cfg.StorePort);
            var log = Serilog.Log.ForContext("component", "coordinator");
            return new WorkerSchedulerService(cfg, ledger, tracker, proxy, storeClient, log);
        });

        services.AddSingleton<IHealthMonitorService, HealthMonitorService>(sp =>
        {
            var cfg = sp.GetRequiredService<CoordinatorConfig>();
            var tracker = sp.GetRequiredService<IWorkerTracker>();
            var storeClient = new Hydra.Shared.RpcClient(cfg.StoreHost, cfg.StorePort);
            var log = Serilog.Log.ForContext("component", "health");
            return new HealthMonitorService(cfg, cfg.Workers, tracker, storeClient, log);
        });
        services.AddHostedService(sp => (HealthMonitorService)sp.GetRequiredService<IHealthMonitorService>());

        // Scheduler background runner
        services.AddHostedService<SchedulerBackgroundRunner>();

        // Controllers
        services.AddControllers()
            .AddApplicationPart(typeof(CompletionsController).Assembly)
            .AddControllersAsServices();

        return services;
    }
}

/// <summary>
/// Background-service wrapper that starts the scheduler's RunAsync loop.
/// </summary>
internal sealed class SchedulerBackgroundRunner : BackgroundService
{
    private readonly IWorkerScheduler _scheduler;
    public SchedulerBackgroundRunner(IWorkerScheduler s) => _scheduler = s;
    protected override Task ExecuteAsync(CancellationToken ct) => _scheduler.RunAsync(ct);
}
