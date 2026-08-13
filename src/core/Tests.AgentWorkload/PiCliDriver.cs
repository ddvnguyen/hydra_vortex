using System.Diagnostics;
using System.Text.Json;

namespace Tests.AgentWorkload;

/// <summary>
/// Drives the <c>pi</c> CLI non-interactively for agent workload testing.
/// Uses <c>--print</c>/<c>-p</c> for single-turn execution, <c>--provider</c>/<c>--model</c>
/// for model selection, and <c>--session-id</c> to keep context accumulating across turns.
/// Output mode is <c>--mode json</c> which emits NDJSON (one JSON object per line).
/// </summary>
public sealed class PiCliDriver : IAgentCliDriver
{
    private readonly string _provider;
    private readonly string _model;
    private readonly string _binPath;

    public PiCliDriver(
        string provider = "hydra",
        string? model = null,
        string binPath = "pi")
    {
        _provider = provider;
        // #470: AGENT_WORKLOAD_MODEL env override (e.g. dense-27b-combined)
        // lets CI target the combined 27B rig session instead of the default.
        _model = model ?? Environment.GetEnvironmentVariable("AGENT_WORKLOAD_MODEL") ?? "moe-35b-solo";
        _binPath = binPath;
    }

    public string Name => "pi";

    public bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo(_binPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(TimeSpan.FromSeconds(5));
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        string sessionId, string prompt, CancellationToken ct = default)
    {
        // -p = non-interactive print mode (flag, no value)
        // --provider / --model = explicit model selection
        // --session-id = session continuity across turns
        // --mode json = NDJSON output (one JSON object per line)
        var args = $"--provider {_provider} --model {_model} --session-id {sessionId} --mode json -p";
        var psi = new ProcessStartInfo(_binPath, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {_binPath}");

        await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        await proc.StandardInput.WriteAsync('\n');
        await proc.StandardInput.FlushAsync(ct);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        sw.Stop();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var completedAt = DateTimeOffset.UtcNow;
        var output = stdout + stderr;

        return PiCliDriver.ParseOutput(
            output, proc.ExitCode, sw.Elapsed, startedAt, completedAt);
    }

    /// <summary>
    /// Parse NDJSON output from <c>pi --mode json</c>. Each line is a separate
    /// JSON object of pi's internal session event stream (event types such as
    /// <c>session</c>, <c>agent_start</c>, <c>turn_start</c>, <c>message_start</c>,
    /// <c>message_end</c>, <c>turn_end</c>, <c>agent_end</c>, <c>auto_retry_start</c>).
    ///
    /// The finalized assistant message is carried in the <c>message</c> property of
    /// message_start/message_end (and the finalized turn in turn_end/agent_end):
    /// <c>message.content</c> is an ARRAY of typed blocks
    /// (<c>{"type":"text","text":...}</c> for output, <c>{"type":"thinking","thinking":...}</c>
    /// for reasoning) and <c>message.usage</c> uses pi's own counters
    /// (<c>input</c>, <c>output</c>, <c>cacheRead</c>, <c>cacheWrite</c>, <c>totalTokens</c>),
    /// NOT OpenAI's <c>prompt_tokens</c>/<c>prompt_tokens_details.cached_tokens</c>.
    ///
    /// We prefer the LAST assistant message whose <c>stopReason</c> is not
    /// <c>"error"</c> (the final response). As a fallback for legacy OpenAI-shaped
    /// single-doc fixtures, top-level <c>content</c>/<c>reasoning_content</c> and
    /// <c>usage.prompt_tokens</c> are still honored when present.
    /// </summary>
    internal static AgentTurnResult ParseOutput(
        string output,
        int exitCode,
        TimeSpan duration,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var builder = new AgentTurnResultBuilder
        {
            ExitCode = exitCode,
            RawOutput = output,
            WallClockDuration = duration,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };

        if (string.IsNullOrWhiteSpace(output))
        {
            return builder.Build();
        }

        // pi --mode json emits NDJSON: one JSON object per line.
        // Parse each line independently; accumulate the most complete result.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var bestContent = (string?)null;
        var bestPromptTokens = 0;
        var bestCompletionTokens = 0;
        var bestCachedTokens = 0;
        var bestReasoningPresent = false;
        var bestReasoning = (string?)null;
        var bestToolCallsPresent = false;
        var bestToolCallName = (string?)null;
        var bestToolCallArgs = (string?)null;
        var anyValidJson = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                anyValidJson = true;

                // PRIMARY: pi's internal assistant-message schema.
                if (IsAssistantMessage(root, out var messageEl))
                {
                    // Skip errored messages so an earlier good response is not
                    // overwritten by a failed final attempt.
                    if (messageEl.TryGetProperty("stopReason", out var stopReasonEl)
                        && stopReasonEl.ValueKind == JsonValueKind.String
                        && stopReasonEl.GetString() == "error")
                    {
                        continue;
                    }

                    if (messageEl.TryGetProperty("content", out var contentEl)
                        && contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var textParts = new List<string>();
                        foreach (var block in contentEl.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object
                                || !block.TryGetProperty("type", out var blockType)
                                || blockType.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var blockTypeName = blockType.GetString();
                            if (blockTypeName == "text"
                                && block.TryGetProperty("text", out var textEl)
                                && textEl.ValueKind == JsonValueKind.String)
                            {
                                var text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    textParts.Add(text!);
                            }
                            else if (blockTypeName == "thinking"
                                && block.TryGetProperty("thinking", out var thinkingEl)
                                && thinkingEl.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(thinkingEl.GetString()))
                            {
                                bestReasoningPresent = true;
                                bestReasoning = thinkingEl.GetString();
                            }
                            else if (blockTypeName == "tool_call"
                                && block.ValueKind == JsonValueKind.Object)
                            {
                                bestToolCallsPresent = true;
                                if (bestToolCallName is null
                                    && block.TryGetProperty("name", out var nameEl)
                                    && nameEl.ValueKind == JsonValueKind.String)
                                {
                                    bestToolCallName = nameEl.GetString();
                                }
                                bestToolCallArgs ??= ExtractToolCallArgs(block);
                            }
                        }

                        if (textParts.Count > 0)
                            bestContent = string.Join("", textParts);
                    }

                    if (messageEl.TryGetProperty("usage", out var usageEl)
                        && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (usageEl.TryGetProperty("input", out var inputEl))
                            bestPromptTokens = JsonElementExtensions.GetInt32OrDefault(inputEl);
                        if (usageEl.TryGetProperty("output", out var outputEl))
                            bestCompletionTokens = JsonElementExtensions.GetInt32OrDefault(outputEl);
                        if (usageEl.TryGetProperty("cacheRead", out var cacheReadEl))
                            bestCachedTokens = JsonElementExtensions.GetInt32OrDefault(cacheReadEl);
                    }

                    continue;
                }

                // FALLBACK: legacy / OpenAI-shaped single-doc fixtures that have
                // no `message` wrapper (kept so older fixtures remain valid).
                if (root.TryGetProperty("content", out var legacyContentEl))
                {
                    var content = legacyContentEl.ValueKind == JsonValueKind.String
                        ? legacyContentEl.GetString()
                        : legacyContentEl.GetRawText();
                    // Prefer the last non-empty content (final response)
                    if (!string.IsNullOrEmpty(content))
                        bestContent = content;
                }

                // Legacy OpenAI-shaped tool_calls array:
                // [{ "id": "...", "type": "function", "function": { "name", "arguments" } }]
                if (root.TryGetProperty("tool_calls", out var legacyToolCalls)
                    && legacyToolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in legacyToolCalls.EnumerateArray())
                    {
                        if (call.ValueKind != JsonValueKind.Object) continue;
                        bestToolCallsPresent = true;

                        if (bestToolCallName is null || bestToolCallArgs is null)
                        {
                            string? name = null;
                            string? args = null;
                            if (call.TryGetProperty("function", out var fn)
                                && fn.ValueKind == JsonValueKind.Object)
                            {
                                if (fn.TryGetProperty("name", out var fnName)
                                    && fnName.ValueKind == JsonValueKind.String)
                                {
                                    name = fnName.GetString();
                                }
                                if (fn.TryGetProperty("arguments", out var fnArgs))
                                {
                                    args = fnArgs.ValueKind == JsonValueKind.String
                                        ? fnArgs.GetString()
                                        : fnArgs.GetRawText();
                                }
                            }
                            bestToolCallName ??= name;
                            bestToolCallArgs ??= args;
                        }
                    }
                }

                if (root.TryGetProperty("reasoning_content", out _))
                    bestReasoningPresent = true;

                if (root.TryGetProperty("usage", out var legacyUsage)
                    && legacyUsage.ValueKind == JsonValueKind.Object)
                {
                    if (legacyUsage.TryGetProperty("prompt_tokens", out var pt))
                        bestPromptTokens = JsonElementExtensions.GetInt32OrDefault(pt);
                    if (legacyUsage.TryGetProperty("completion_tokens", out var completionTokens))
                        bestCompletionTokens = JsonElementExtensions.GetInt32OrDefault(completionTokens);
                    if (legacyUsage.TryGetProperty("prompt_tokens_details", out var details)
                        && details.ValueKind == JsonValueKind.Object
                        && details.TryGetProperty("cached_tokens", out var cached))
                    {
                        bestCachedTokens = JsonElementExtensions.GetInt32OrDefault(cached);
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON line (e.g. progress indicator) — skip
            }
        }

        builder.ResponseContent = bestContent;
        builder.ReasoningContentPresent = bestReasoningPresent;
        builder.ReasoningContent = bestReasoning;
        builder.ToolCallsPresent = bestToolCallsPresent;
        builder.ToolCallName = bestToolCallName;
        builder.ToolCallArgs = bestToolCallArgs;
        builder.PromptTokens = bestPromptTokens;
        builder.CompletionTokens = bestCompletionTokens;
        builder.CachedTokens = bestCachedTokens;
        builder.IsValidJson = anyValidJson;

        return builder.Build();
    }

    /// <summary>
    /// Extract the arguments payload of a pi tool_call content block.
    /// Accepts the common variants: <c>arguments</c> (OpenAI style), <c>input</c>
    /// (Anthropic style) and <c>args</c>; the value may be a JSON string or an
    /// already-parsed JSON value (returned as raw text).
    /// </summary>
    private static string? ExtractToolCallArgs(JsonElement toolCall)
    {
        foreach (var key in new[] { "arguments", "input", "args" })
        {
            if (!toolCall.TryGetProperty(key, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => null,
            };
        }

        return null;
    }

    private static bool IsAssistantMessage(JsonElement root, out JsonElement messageEl)
    {
        messageEl = default;
        if (!root.TryGetProperty("message", out var candidate)
            || candidate.ValueKind != JsonValueKind.Object
            || !candidate.TryGetProperty("role", out var roleEl)
            || roleEl.ValueKind != JsonValueKind.String
            || roleEl.GetString() != "assistant")
        {
            return false;
        }

        messageEl = candidate;
        return true;
    }
}

internal sealed class AgentTurnResultBuilder
{
    public int ExitCode;
    public string RawOutput = string.Empty;
    public TimeSpan WallClockDuration;
    public DateTimeOffset StartedAt;
    public DateTimeOffset CompletedAt;
    public string? ResponseContent;
    public bool ReasoningContentPresent;
    public string? ReasoningContent;
    public bool ToolCallsPresent;
    public string? ToolCallName;
    public string? ToolCallArgs;
    public int PromptTokens;
    public int CompletionTokens;
    public int CachedTokens;
    public bool IsValidJson;

    public AgentTurnResult Build() => new()
    {
        ExitCode = ExitCode,
        RawOutput = RawOutput,
        WallClockDuration = WallClockDuration,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        ResponseContent = ResponseContent,
        ReasoningContentPresent = ReasoningContentPresent,
        ReasoningContent = ReasoningContent,
        ToolCallsPresent = ToolCallsPresent,
        ToolCallName = ToolCallName,
        ToolCallArgs = ToolCallArgs,
        PromptTokens = PromptTokens,
        CompletionTokens = CompletionTokens,
        CachedTokens = CachedTokens,
        IsValidJson = IsValidJson,
    };
}
