using System.Reflection;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Json;

namespace Hydra.Shared;

public static class HydraLogging
{
    public static string ServiceVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";

    public static ILogger CreateLogger(string component)
    {
        return new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("component", component)
            .Enrich.WithProperty("version", ServiceVersion)
            .WriteTo.Console(new JsonFormatter())
            .CreateLogger();
    }

    public static IDisposable TraceScope(this ILogger log, string traceId)
    {
        return LogContext.PushProperty("trace_id", traceId);
    }
}
