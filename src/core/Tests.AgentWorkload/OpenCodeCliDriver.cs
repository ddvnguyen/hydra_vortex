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

    public OpenCodeCliDriver(
        string model = "hydra/moe-35b-solo",
        string binPath = "opencode")
    {
        _model = model;
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
        // opencode run <prompt> --model <model> --session <id> --format json
        // For continuation: use -c / --continue to pick up an existing session
        var args = $"run \"{EscapeForShell(prompt)}\" --model {_model} --session {sessionId} -c --format json";
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

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        sw.Stop();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var completedAt = DateTimeOffset.UtcNow;
        var output = stdout + stderr;

        return ParseOutput(output, proc.ExitCode, sw.Elapsed, startedAt, completedAt);
    }

    /// <summary>
    /// Parse NDJSON output from <c>opencode --format json</c>. Each line is a
    /// separate JSON object. We scan for the line carrying the final response
    /// content and usage metrics (typically the last complete JSON object).
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

    private static string EscapeForShell(string input)
    {
        // Escape double quotes for shell argument passing
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
