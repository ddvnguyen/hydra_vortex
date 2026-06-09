using Hydra.Core.Controllers;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hydra.Core.Extensions;

public static class CoordinatorServiceExtensions
{
    public static IServiceCollection AddCoordinator(this IServiceCollection services, CoordinatorConfig config)
    {
        // Config
        services.AddSingleton(config);

        // Gap 2-A: Restore sessions from Store on startup
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<CoordinatorConfig>();
            var ledger = sp.GetRequiredService<ISessionLedger>();
            var log = Serilog.Log.ForContext("component", "startup");
            _ = Task.Run(async () =>
            {
                try
                {
                    await ledger.RestoreFromStoreAsync(cfg.StoreHost, cfg.StorePort, CancellationToken.None);
                    log.Information("session_table_restored Count={Count}", ledger.ActiveCount);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "session_table_restore_failed");
                }
            });
            return ledger;
        });

        // Repositories
        services.AddSingleton<ISessionLedger, SessionLedger>();
        services.AddSingleton<IWorkerTracker>(sp =>
        {
            var cfg = sp.GetRequiredService<CoordinatorConfig>();
            var tracker = new WorkerTracker(cfg.WorkerErrorThreshold);
            foreach (var w in cfg.Workers)
                tracker.InitWorker(w.Name, w.Slots);
            return tracker;
        });

        // Services
        services.AddSingleton<ICompletionProxyService>(sp => new CompletionProxyService(config.LlamaRequestTimeoutS));
        services.AddSingleton<IWorkerScheduler, WorkerSchedulerService>(sp =>
        {
            var cfg = sp.GetRequiredService<CoordinatorConfig>();
            var ledger = sp.GetRequiredService<ISessionLedger>();
            var tracker = sp.GetRequiredService<IWorkerTracker>();
            var proxy = sp.GetRequiredService<ICompletionProxyService>();
            var health = sp.GetRequiredService<IHealthMonitorService>();
            var storeClient = new Hydra.Shared.RpcClient(cfg.StoreHost, cfg.StorePort);
            var log = Serilog.Log.ForContext("component", "coordinator");
            return new WorkerSchedulerService(cfg, ledger, tracker, proxy, health, storeClient, sp, log);
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

        // Session eviction background loop (Gap 5)
        services.AddSingleton<SessionEvictionService>(sp =>
        {
            var cfg = sp.GetRequiredService<CoordinatorConfig>();
            var ledger = sp.GetRequiredService<ISessionLedger>();
            var tracker = sp.GetRequiredService<IWorkerTracker>();
            var scheduler = sp.GetRequiredService<IWorkerScheduler>();
            var log = Serilog.Log.ForContext("component", "eviction");
            return new SessionEvictionService(cfg, ledger, tracker, scheduler, log);
        });
        services.AddHostedService(sp => sp.GetRequiredService<SessionEvictionService>());

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
