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

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.TryGetProperty("content", out var contentEl))
            {
                builder.ResponseContent = contentEl.ValueKind == JsonValueKind.String
                    ? contentEl.GetString()
                    : contentEl.GetRawText();
            }

            builder.ReasoningContentPresent = root.TryGetProperty("reasoning_content", out _);

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt))
                    builder.PromptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ct2))
                    builder.CompletionTokens = ct2.GetInt32();
                if (usage.TryGetProperty("prompt_tokens_details", out var details)
                    && details.TryGetProperty("cached_tokens", out var cached))
                {
                    builder.CachedTokens = cached.GetInt32();
                }
            }

            builder.IsValidJson = true;
        }
        catch (JsonException)
        {
            builder.IsValidJson = false;
            builder.ResponseContent = output;
        }

        return builder.Build();
    }

    private static string EscapeForShell(string input)
    {
        // Escape double quotes for shell argument passing
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
