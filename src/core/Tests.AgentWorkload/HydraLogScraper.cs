using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tests.AgentWorkload;

/// <summary>
/// Scrapes structured events from Hydra podman container logs, porting the
/// 3 grep patterns from docs/paseo-hydra-agent-test.md §2 into typed events.
/// Bounded by a test's own start/end timestamps.
/// </summary>
public sealed partial class HydraLogScraper
{
    /// <summary>
    /// Pattern 1: event=request_timeline — per-request signal with
    /// tokens_out, decode_ms, queue_wait_ms, route_type, node, status.
    /// </summary>
    [GeneratedRegex(@"event=request_timeline\b.*", RegexOptions.Compiled)]
    private static partial Regex RequestTimelineRegex();

    /// <summary>
    /// Pattern 2: KV reuse decisions — N_COMMON, restored logits, slot released.
    /// </summary>
    [GeneratedRegex(@"(?:N_COMMON|restored logits|slot .* released)", RegexOptions.Compiled)]
    private static partial Regex KvReuseRegex();

    /// <summary>
    /// Pattern 3: crash/restart watch — GGML_ASSERT, exit_code, attempting restart.
    /// </summary>
    [GeneratedRegex(@"(?:GGML_ASSERT|exit_code|attempting restart)", RegexOptions.Compiled)]
    private static partial Regex CrashRestartRegex();

    /// <summary>
    /// Field parser for key=value pairs in request_timeline log lines.
    /// Matches patterns like: tokens_out=150 decode_ms=750.0 queue_wait_ms=0 route_type=affinity node=rtx
    /// </summary>
    [GeneratedRegex(@"([a-z_]+)=([^\s]*)", RegexOptions.Compiled)]
    private static partial Regex KeyValueRegex();

    /// <summary>
    /// Extracts the timestamp prefix from podman log lines. Format: [2026-08-03T12:34:56.789Z]
    /// </summary>
    [GeneratedRegex(@"^\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[^\]]*)\]\s*", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    private readonly string _coreContainer;
    private readonly string _headContainer;

    public HydraLogScraper(
        string coreContainer = "hydra-system_core_1",
        string headContainer = "hydra-system_head-rtx5060ti_1")
    {
        _coreContainer = coreContainer;
        _headContainer = headContainer;
    }

    /// <summary>
    /// Scrape request_timeline events from the core container logs.
    /// Deduplicates by (trace_id, timestamp_ms) — the coordinator emits
    /// each event via both Console.Error and Serilog, which can produce
    /// duplicate lines in the podman log stream.
    /// </summary>
    public IReadOnlyList<RequestTimelineEvent> ScrapeRequestTimeline(
        DateTimeOffset since, DateTimeOffset? until = null)
    {
        var lines = FetchLogs(_coreContainer, since, until);
        var seen = new HashSet<string>();
        var events = new List<RequestTimelineEvent>();

        foreach (var line in lines)
        {
            if (!RequestTimelineRegex().IsMatch(line)) continue;
            var parsed = ParseRequestTimeline(line);
            if (parsed is null) continue;

            // Dedup key: trace_id + timestamp_ms (stable across Serilog vs Console.Error dups)
            var fields = ExtractKeyValuePairs(line);
            var traceId = fields.GetValueOrDefault("trace_id", "");
            var timestampMs = fields.GetValueOrDefault("timestamp_ms", "");
            var dedupKey = $"{traceId}|{timestampMs}";
            if (!seen.Add(dedupKey)) continue;

            events.Add(parsed);
        }

        return events;
    }

    /// <summary>
    /// Scrape KV reuse events from the head container logs.
    /// </summary>
    public IReadOnlyList<KvReuseEvent> ScrapeKvReuse(
        DateTimeOffset since, DateTimeOffset? until = null)
    {
        var lines = FetchLogs(_headContainer, since, until);
        var events = new List<KvReuseEvent>();

        foreach (var line in lines)
        {
            if (!KvReuseRegex().IsMatch(line)) continue;
            var parsed = ParseKvReuse(line);
            if (parsed is not null) events.Add(parsed);
        }

        return events;
    }

    /// <summary>
    /// Scrape crash/restart events from the head container logs.
    /// </summary>
    public IReadOnlyList<CrashRestartEvent> ScrapeCrashRestart(
        DateTimeOffset since, DateTimeOffset? until = null)
    {
        var lines = FetchLogs(_headContainer, since, until);
        var events = new List<CrashRestartEvent>();

        foreach (var line in lines)
        {
            if (!CrashRestartRegex().IsMatch(line)) continue;
            var parsed = ParseCrashRestart(line);
            if (parsed is not null) events.Add(parsed);
        }

        return events;
    }

    /// <summary>
    /// Parse a request_timeline log line into a structured event.
    /// Handles the format: [timestamp] event=request_timeline tokens_out=X decode_ms=Y ... status=done
    /// </summary>
    public static RequestTimelineEvent? ParseRequestTimeline(string line)
    {
        var timestamp = ExtractTimestamp(line);
        var fields = ExtractKeyValuePairs(line);

        if (!fields.TryGetValue("tokens_out", out var tokensOutStr) ||
            !int.TryParse(tokensOutStr, out var tokensOut))
        {
            return null;
        }

        float.TryParse(fields.GetValueOrDefault("decode_ms", "0"), out var decodeMs);
        float.TryParse(fields.GetValueOrDefault("queue_wait_ms", "0"), out var queueWaitMs);
        float.TryParse(fields.GetValueOrDefault("restore_kv_ms", "0"), out var restoreKvMs);

        return new RequestTimelineEvent
        {
            Node = fields.GetValueOrDefault("node", "unknown"),
            RouteType = fields.GetValueOrDefault("route_type", "unknown"),
            TokensOut = tokensOut,
            DecodeMs = decodeMs,
            QueueWaitMs = queueWaitMs,
            RestoreKvMs = restoreKvMs,
            Status = fields.GetValueOrDefault("status"),
            Slot = fields.GetValueOrDefault("slot"),
            Model = fields.GetValueOrDefault("model"),
            RawLine = line,
        };
    }

    /// <summary>
    /// Parse a KV reuse log line (N_COMMON, restored logits, slot released).
    /// </summary>
    public static KvReuseEvent? ParseKvReuse(string line)
    {
        var timestamp = ExtractTimestamp(line);
        string eventType;
        string? slot = null;
        int? nCommon = null;

        if (line.Contains("N_COMMON"))
        {
            eventType = "N_COMMON";
            // Try to extract N_COMMON value: e.g. "#PD-TRACE N_COMMON=42" or "N_COMMON 42"
            var match = Regex.Match(line, @"N_COMMON[=:\s]+(\d+)");
            if (match.Success) nCommon = int.Parse(match.Groups[1].Value);
        }
        else if (line.Contains("restored logits"))
        {
            eventType = "restored_logits";
        }
        else if (line.Contains("slot") && line.Contains("released"))
        {
            eventType = "slot_released";
            var slotMatch = Regex.Match(line, @"slot\s+(\S+)\s+released");
            if (slotMatch.Success) slot = slotMatch.Groups[1].Value;
        }
        else
        {
            return null;
        }

        return new KvReuseEvent
        {
            EventType = eventType,
            Slot = slot,
            NCommon = nCommon,
            RawLine = line,
            Timestamp = timestamp,
        };
    }

    /// <summary>
    /// Parse a crash/restart log line.
    /// </summary>
    public static CrashRestartEvent? ParseCrashRestart(string line)
    {
        var timestamp = ExtractTimestamp(line);
        string eventType;

        if (line.Contains("GGML_ASSERT"))
        {
            eventType = "GGML_ASSERT";
        }
        else if (line.Contains("exit_code"))
        {
            eventType = "exit_code";
        }
        else if (line.Contains("attempting restart"))
        {
            eventType = "attempting_restart";
        }
        else
        {
            return null;
        }

        return new CrashRestartEvent
        {
            EventType = eventType,
            Details = line,
            RawLine = line,
            Timestamp = timestamp,
        };
    }

    /// <summary>
    /// Compute throughput (tok/s) from a timeline event, matching the doc's snippet:
    /// tokens_out / (decode_ms / 1000)
    /// </summary>
    public static float ComputeThroughput(RequestTimelineEvent evt)
    {
        if (evt.DecodeMs <= 0) return 0f;
        return evt.TokensOut / (evt.DecodeMs / 1000f);
    }

    internal static Dictionary<string, string> ExtractKeyValuePairs(string line)
    {
        var dict = new Dictionary<string, string>();
        foreach (Match m in KeyValueRegex().Matches(line))
        {
            dict[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return dict;
    }

    internal static DateTimeOffset ExtractTimestamp(string line)
    {
        var match = TimestampRegex().Match(line);
        if (match.Success &&
            DateTimeOffset.TryParse(match.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var ts))
        {
            return ts;
        }
        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Fetch log lines from a podman container within the given time window.
    /// Uses real enforced timeouts: WaitForExit(30s) + kill on timeout,
    /// and drains both stdout and stderr concurrently to prevent pipe deadlock.
    /// </summary>
    internal static List<string> FetchLogs(
        string container, DateTimeOffset since, DateTimeOffset? until)
    {
        var sinceStr = since.ToString("yyyy-MM-ddTHH:mm:ss");
        var args = $"logs --since {sinceStr}";
        if (until.HasValue)
        {
            args += $" --until {until.Value:yyyy-MM-ddTHH:mm:ss}";
        }
        args += $" {container}";

        const int timeoutMs = 30_000;

        try
        {
            var psi = new ProcessStartInfo("podman", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return [];

            // Drain stdout and stderr concurrently to prevent pipe deadlock
            // when stderr is chatty (e.g. "Following log output from ...").
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); }
                catch { /* best-effort kill */ }
                return [];
            }

            // Process has exited — tasks must complete promptly.
            // Use a short grace period for cleanup; already-exited process
            // means the readers will finish near-instantly.
            stdoutTask.Wait(TimeSpan.FromSeconds(5));
            stderrTask.Wait(TimeSpan.FromSeconds(5));

            var output = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty;
            return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        }
        catch
        {
            // podman not available — return empty, tests will skip
            return [];
        }
    }
}
