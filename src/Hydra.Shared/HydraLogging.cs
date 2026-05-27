using Serilog;
using Serilog.Context;
using Serilog.Formatting.Json;

namespace Hydra.Shared;

public static class HydraLogging
{
    public static ILogger CreateLogger(string component)
    {
        return new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("component", component)
            .WriteTo.Console(new JsonFormatter())
            .CreateLogger();
    }

    public static IDisposable TraceScope(this ILogger log, string traceId)
    {
        return LogContext.PushProperty("trace_id", traceId);
    }
}
