using Prometheus;

namespace Hydra.Agent;

internal static class AgentMetrics
{
    public static readonly Counter SaveOpsTotal = Metrics.CreateCounter(
        "hydra_agent_save_ops_total", "Save operations count");

    public static readonly Counter RestoreOpsTotal = Metrics.CreateCounter(
        "hydra_agent_restore_ops_total", "Restore operations count");

    public static readonly Histogram SaveDuration = Metrics.CreateHistogram(
        "hydra_agent_save_duration_seconds", "Save duration in seconds");

    public static readonly Histogram RestoreDuration = Metrics.CreateHistogram(
        "hydra_agent_restore_duration_seconds", "Restore duration in seconds");

    public static readonly Gauge SlotsIdle = Metrics.CreateGauge(
        "hydra_agent_slots_idle", "Number of idle slots", "node");

    public static readonly Gauge LlamaHealthy = Metrics.CreateGauge(
        "hydra_agent_llama_healthy", "Whether llama-server is healthy", "node");
}
