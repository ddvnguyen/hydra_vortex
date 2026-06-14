using System.Reflection;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Json;
using Serilog.Sinks.Grafana.Loki;

namespace Hydra.Shared;

public static class HydraLogging
{
    public static string ServiceVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";

    private static string? LokiUrl =>
        Environment.GetEnvironmentVariable("HYDRA_LOG_LOKI_URL");

    public static ILogger CreateLogger(string component)
    {
        var cfg = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("component", component)
            .Enrich.WithProperty("version", ServiceVersion)
            .WriteTo.Console(new JsonFormatter());

        var lokiUrl = LokiUrl;
        if (lokiUrl != null)
        {
            cfg = cfg.WriteTo.GrafanaLoki(
                uri: lokiUrl,
                labels: [new LokiLabel { Key = "component", Value = component }]);
        }

        return cfg.CreateLogger();
    }

    public static IDisposable TraceScope(this ILogger log, string traceId)
    {
        return LogContext.PushProperty("trace_id", traceId);
    }
}
