using System.Diagnostics;
using System.Text.Json;

namespace Tests.AgentWorkload;

/// <summary>
/// Drives the <c>opencode</c> CLI non-interactively for agent workload testing.
/// Uses <c>opencode run [message..]</c> with <c>--model</c> and session continuation.
/// </summary>
public sealed class OpenCodeCliDriver : IAgentCliDriver
{
    private readonly string _model;
    private readonly string _binPath;

    /// <summary>
    /// The opencode-created session id captured from the first run's output.
    /// <c>--session</c> only accepts ids that already exist, so turn 1 must let
    /// opencode create the session (no <c>--session</c> flag) and every later
    /// turn must pass the resolved id. <c>-c</c> (continue LAST session) is
    /// deliberately not used — it would resume a different session.
    /// </summary>
    private string? _resolvedSessionId;

    public OpenCodeCliDriver(
        string model = "hydra/moe-35b-solo",
        string binPath = "opencode")
    {
        // #470: AGENT_WORKLOAD_MODEL env override (e.g. dense-27b-combined)
        // lets CI target the combined 27B rig session instead of the default.
        var envModel = Environment.GetEnvironmentVariable("AGENT_WORKLOAD_MODEL");
        _model = !string.IsNullOrEmpty(envModel) ? $"hydra/{envModel}" : model;
        _binPath = binPath;
    }

    public string Name => "opencode";

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
        // opencode run <prompt> --model <model> [--session <id>] --format json
        // opencode generates its own session id and emits it in every event
        // line's `sessionID` field. The caller-supplied `sessionId` is logical
        // only; the real id is resolved from the first run's output below.
        var psi = new ProcessStartInfo(_binPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_model);
        if (_resolvedSessionId is not null)
        {
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add(_resolvedSessionId);
        }
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("json");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {_binPath}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        sw.Stop();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var completedAt = DateTimeOffset.UtcNow;
        var output = stdout + stderr;

        // Capture the opencode-created session id for subsequent turns. Only
        // store it when a real id is found — passing the caller's arbitrary id
        // to --session later would error with "Session not found".
        if (_resolvedSessionId is null)
        {
            var foundId = ExtractSessionId(output);
            if (foundId is not null)
                _resolvedSessionId = foundId;
        }

        return ParseOutput(output, proc.ExitCode, sw.Elapsed, startedAt, completedAt);
    }

    /// <summary>
    /// Parse NDJSON output from <c>opencode run --format json</c>. Each line is a
    /// separate event: <c>step_start</c>, <c>text</c>, <c>step_finish</c>,
    /// <c>tool</c>/<c>error</c>, etc.
    ///
    /// Content is carried in <c>part.text</c> of <c>type=="text"</c> events and
    /// token usage in <c>part.tokens</c> of <c>type=="step_finish"</c> events:
    /// <c>{total, input, output, reasoning, cache:{write, read}}</c>. Reasoning is
    /// detected via <c>part.tokens.reasoning &gt; 0</c>. As a fallback for legacy
    /// OpenAI-shaped single-doc fixtures, top-level <c>content</c> and
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

        // opencode --format json emits NDJSON: one JSON object per line.
        // Parse each line independently; accumulate the most complete result.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var bestContent = (string?)null;
        var bestPromptTokens = 0;
        var bestCompletionTokens = 0;
        var bestCachedTokens = 0;
        var bestReasoningPresent = false;
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

                // PRIMARY: opencode's event stream (every event carries `type`).
                if (root.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String)
                {
                    var eventType = typeEl.GetString();
                    if (eventType == "text")
                    {
                        if (root.TryGetProperty("part", out var partEl)
                            && partEl.ValueKind == JsonValueKind.Object
                            && partEl.TryGetProperty("type", out var partTypeEl)
                            && partTypeEl.ValueKind == JsonValueKind.String
                            && partTypeEl.GetString() == "text"
                            && partEl.TryGetProperty("text", out var textEl)
                            && textEl.ValueKind == JsonValueKind.String)
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                bestContent = text;
                        }
                    }
                    else if (eventType == "step_finish")
                    {
                        if (root.TryGetProperty("part", out var partEl)
                            && partEl.ValueKind == JsonValueKind.Object
                            && partEl.TryGetProperty("tokens", out var tokensEl)
                            && tokensEl.ValueKind == JsonValueKind.Object)
                        {
                            if (tokensEl.TryGetProperty("input", out var inputEl))
                                bestPromptTokens = JsonElementExtensions.GetInt32OrDefault(inputEl);
                            if (tokensEl.TryGetProperty("output", out var outputEl))
                                bestCompletionTokens = JsonElementExtensions.GetInt32OrDefault(outputEl);
                            if (tokensEl.TryGetProperty("reasoning", out var reasoningEl)
                                && JsonElementExtensions.GetInt32OrDefault(reasoningEl) > 0)
                            {
                                bestReasoningPresent = true;
                            }
                            if (tokensEl.TryGetProperty("cache", out var cacheEl)
                                && cacheEl.ValueKind == JsonValueKind.Object
                                && cacheEl.TryGetProperty("read", out var cacheReadEl))
                            {
                                bestCachedTokens = JsonElementExtensions.GetInt32OrDefault(cacheReadEl);
                            }
                        }
                    }

                    continue;
                }

                // FALLBACK: legacy / OpenAI-shaped single-doc fixtures that
                // carry no event `type` (kept so older fixtures remain valid).
                if (root.TryGetProperty("content", out var legacyContentEl))
                {
                    var content = legacyContentEl.ValueKind == JsonValueKind.String
                        ? legacyContentEl.GetString()
                        : legacyContentEl.GetRawText();
                    // Prefer the last non-empty content (final response)
                    if (!string.IsNullOrEmpty(content))
                        bestContent = content;
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
        builder.PromptTokens = bestPromptTokens;
        builder.CompletionTokens = bestCompletionTokens;
        builder.CachedTokens = bestCachedTokens;
        builder.IsValidJson = anyValidJson;

        return builder.Build();
    }

    /// <summary>
    /// Pulls the opencode-created session id from the NDJSON event stream. Every
    /// event carries a <c>sessionID</c> field; the first non-empty value wins.
    /// </summary>
    internal static string? ExtractSessionId(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("sessionID", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id))
                        return id;
                }
            }
            catch (JsonException)
            {
                // Non-JSON line — skip
            }
        }

        return null;
    }
}
