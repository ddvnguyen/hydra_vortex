using System.Diagnostics;
using System.Text.Json;

namespace Tests.AgentWorkload;

/// <summary>
/// Drives the <c>pi</c> CLI non-interactively for agent workload testing.
/// Uses <c>--print</c>/<c>-p</c> for single-turn execution and <c>--session-id</c>
/// to keep context accumulating across turns.
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
        var args = $"-p {_provider}/{_model} --session-id {sessionId} --mode json";
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
