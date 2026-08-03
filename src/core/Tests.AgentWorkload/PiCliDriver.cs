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
        string model = "moe-35b-solo",
        string binPath = "pi")
    {
        _provider = provider;
        _model = model;
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
    /// JSON object. We scan for the line carrying the final response content
    /// and usage metrics (typically the last complete JSON object).
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

                if (root.TryGetProperty("content", out var contentEl))
                {
                    var content = contentEl.ValueKind == JsonValueKind.String
                        ? contentEl.GetString()
                        : contentEl.GetRawText();
                    // Prefer the last non-empty content (final response)
                    if (!string.IsNullOrEmpty(content))
                        bestContent = content;
                }

                if (root.TryGetProperty("reasoning_content", out _))
                    bestReasoningPresent = true;

                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt))
                        bestPromptTokens = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct2))
                        bestCompletionTokens = ct2.GetInt32();
                    if (usage.TryGetProperty("prompt_tokens_details", out var details)
                        && details.TryGetProperty("cached_tokens", out var cached))
                    {
                        bestCachedTokens = cached.GetInt32();
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
        PromptTokens = PromptTokens,
        CompletionTokens = CompletionTokens,
        CachedTokens = CachedTokens,
        IsValidJson = IsValidJson,
    };
}
