using System.Reflection;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.OpenTelemetry;

namespace Hydra.Shared;

public static class HydraLogging
{
    public static string ServiceVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";

    private static string OtelEndpoint =>
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT")
        ?? "http://localhost:4318";

    private static string ServiceName =>
        Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
        ?? "hydra";

    private static string ServiceNamespace =>
        Environment.GetEnvironmentVariable("OTEL_SERVICE_NAMESPACE")
        ?? "hydra-core";

    private static string ServiceInstance =>
        Environment.GetEnvironmentVariable("HYDRA_LOG_NODE")
        ?? Environment.GetEnvironmentVariable("OTEL_SERVICE_INSTANCE_ID")
        ?? "rtx";

    /// <summary>
    /// Build a Serilog logger that pushes every record to the OTel
    /// Collector gateway via OTLP/HTTP. The previous design
    /// (Serilog.Sinks.Grafana.Loki) double-logged to both the
    /// console and Loki under {component=&quot;hydra&quot;}; the
    /// OTel pipeline is the new primary path and the console
    /// sink is dropped entirely.
    ///
    /// Resource attributes set on the OTel exporter (consumed by
    /// the collector's transform processor to populate Loki
    /// stream labels):
    ///   service.name              -> Loki label &quot;component&quot;
    ///   service.instance.id       -> Loki label &quot;node&quot;
    ///   service.namespace         -> not labeled (low-cardinality
    ///                                namespace discriminator)
    ///   deployment.environment    -> not labeled
    ///
    /// The Serilog Level is mapped to the OTel severity_text
    /// (Info/Warn/Error/Debug); the collector's transform
    /// processor copies severity_text to the Loki &quot;level&quot;
    /// stream label uniformly.
    /// </summary>
    public static ILogger CreateLogger(string component)
    {
        var cfg = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("component", component)
            .Enrich.WithProperty("version", ServiceVersion)
            // OpenTelemetry sink — pushes to the OTel Collector
            // gateway. The endpoint defaults to
            // http://localhost:4318 (RTX collector) and is
            // overridden by OTEL_EXPORTER_OTLP_LOGS_ENDPOINT
            // (e.g., on P100 via node-p100.yaml).
            //
            // The sink uses the OpenTelemetry SDK's internal
            // batch processor (configurable via the OTel SDK
            // defaults; the SDK's own queue is bounded and
            // drop-oldest on overflow). We do NOT use the
            // BatchedOpenTelemetrySinkOptions here because
            // BatchingOptions is a read-only property on
            // BatchedOpenTelemetrySinkOptions in v4.0.0; the
            // default OpenTelemetrySinkOptions path delegates
            // batching to the OTel SDK which is what we want
            // anyway.
            .WriteTo.OpenTelemetry(opts =>
            {
                opts.Endpoint = OtelEndpoint;
                opts.Protocol = OtlpProtocol.HttpProtobuf;

                // Resource attributes — consumed by the OTel
                // Collector's transform processor to map to
                // Loki stream labels. service.name carries the
                // component (hydra-core passes the same
                // component string as a resource attribute,
                // so dashboard queries like
                // {component="hydra"} continue to work).
                opts.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = ServiceName,
                    ["service.namespace"] = ServiceNamespace,
                    ["service.instance.id"] = ServiceInstance,
                    ["deployment.environment.name"] = "dev",
                };
            });

        return cfg.CreateLogger();
    }

    public static IDisposable TraceScope(this ILogger log, string traceId)
    {
        return LogContext.PushProperty("trace_id", traceId);
    }
}
