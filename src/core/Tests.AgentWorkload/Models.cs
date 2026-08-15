namespace Tests.AgentWorkload;

/// <summary>
/// Result of a single turn executed through an agent CLI.
/// </summary>
public sealed class AgentTurnResult
{
    public required int ExitCode { get; init; }
    public required string RawOutput { get; init; }
    public required TimeSpan WallClockDuration { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Parsed content from the model response (null if JSON parse failed).</summary>
    public string? ResponseContent { get; init; }

    /// <summary>Presence of reasoning_content in the response JSON.</summary>
    public bool ReasoningContentPresent { get; init; }

    /// <summary>
    /// Text of the last reasoning (thinking) block in the response.
    /// Null when the model produced no reasoning. Used for relevance checks:
    /// reasoning must engage with the question's topic.
    /// </summary>
    public string? ReasoningContent { get; init; }

    /// <summary>Whether the response contained at least one tool_call block.</summary>
    public bool ToolCallsPresent { get; init; }

    /// <summary>Name of the first tool_call in the response (null when none).</summary>
    public string? ToolCallName { get; init; }

    /// <summary>
    /// Raw arguments (JSON text) of the first tool_call in the response
    /// (null when none). Used for relevance checks: args must match the
    /// question's operands.
    /// </summary>
    public string? ToolCallArgs { get; init; }

    /// <summary>Prompt tokens from usage.input (pi) / part.tokens.input (opencode).</summary>
    public int PromptTokens { get; init; }

    /// <summary>Completion tokens from usage.output (pi) / part.tokens.output (opencode).</summary>
    public int CompletionTokens { get; init; }

    /// <summary>Cached tokens from usage.cacheRead (pi) / part.tokens.cache.read (opencode).</summary>
    public int CachedTokens { get; init; }

    /// <summary>Whether the raw output was valid JSON.</summary>
    public bool IsValidJson { get; init; }
}

/// <summary>
/// Parsed event from the request_timeline log pattern.
/// </summary>
public sealed record RequestTimelineEvent
{
    public required string Node { get; init; }
    public required string RouteType { get; init; }
    public required int TokensOut { get; init; }
    public required float DecodeMs { get; init; }
    public required float QueueWaitMs { get; init; }
    public float RestoreKvMs { get; init; }
    public string? Status { get; init; }
    public string? Slot { get; init; }
    public string? Model { get; init; }
    public string RawLine { get; init; } = string.Empty;

    public float ThroughputTokPerSec => DecodeMs > 0
        ? TokensOut / (DecodeMs / 1000f)
        : 0f;
}

/// <summary>
/// Parsed event from KV reuse log pattern (N_COMMON / restored logits / slot released).
/// </summary>
public sealed record KvReuseEvent
{
    public required string EventType { get; init; }
    public string? Slot { get; init; }
    public int? NCommon { get; init; }
    public string RawLine { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Parsed event from crash/restart watch log pattern.
/// </summary>
public sealed record CrashRestartEvent
{
    public required string EventType { get; init; }
    public string Details { get; init; } = string.Empty;
    public string RawLine { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Parsed event from the autoroute_resolved log pattern emitted by the
/// coordinator when a hydra-auto request is resolved to a concrete plan.
/// </summary>
public sealed record AutoRouteEvent
{
    public required string Sid { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Head { get; init; } = string.Empty;
    public string Peer { get; init; } = string.Empty;
    public string Decode { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string RawLine { get; init; } = string.Empty;
}

/// <summary>
/// Baseline throughput values per GPU node (tok/s).
/// </summary>
public static class ThroughputBaselines
{
    public const float Rtx5060Ti = 200f;
    public const float Rtx3060 = 60f;
    public const float P100 = 28f;
    public const float ToleranceMultiplier = 2f;
}
