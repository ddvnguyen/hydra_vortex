using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tests.Core.Harness;

namespace Tests.MiniFleet;

/// <summary>One executed scenario against real engines — mirrors the SHAPE of the
/// harness ScenarioRunResult (ScenarioId / Outcome / Error / Trace) so
/// legacy-vs-v2 A/B traces stay comparable, while the trace payload is the REAL
/// HTTP conversation instead of normalized scheduler RPC.</summary>
public sealed record MiniFleetScenarioRunResult(
    string ScenarioId,
    string Preset,
    string SchedulerImpl,
    string Outcome,
    string? Error,
    IReadOnlyList<MiniFleetTraceCall> Trace,
    int? UsagePromptTokens,
    int? UsageCompletionTokens)
{
    public static MiniFleetScenarioRunResult Empty(string scenarioId, string preset, string impl) => new(
        ScenarioId: scenarioId,
        Preset: preset,
        SchedulerImpl: impl,
        Outcome: "NotRun",
        Error: null,
        Trace: Array.Empty<MiniFleetTraceCall>(),
        UsagePromptTokens: null,
        UsageCompletionTokens: null);
}

/// <summary>One normalized HTTP call in the real-engine trace (the minifleet
/// counterpart of the harness TraceRpcCall/TraceProxyCall records).</summary>
public sealed record MiniFleetTraceCall(
    string Node,
    string Endpoint,
    bool Stream,
    int? HttpStatusCode,
    string? FinishReason,
    int? PromptTokens,
    int? CompletionTokens,
    long DurationMs,
    string? Error);

/// <summary>
/// Driver adapter (brief §Components 1): executes ScenarioCatalog specs from the
/// Tests.Core harness (REUSED, not forked) against REAL HTTP llama-engine
/// endpoints (POST {node}/v1/chat/completions) instead of the fake RPC client.
/// Smoke subset: cold_atomic_engine + chunked_save (consultant ruling 2026-08-28).
/// Assertions per brief:
///   - completion status OK,
///   - finish_reason present,
///   - usage tokens &gt; 0;
/// store side-effects are ignored (out of scope this PR).
///
/// Reasoning-model quirk (#4): Qwen3.5-9B-Q4_K_M is a REASONING model — reserve
/// ≥120 completion tokens or content comes back "" while thinking fills
/// reasoning_content. Treat that as PASS for smoke purposes.
///
/// A/B hooks (brief §Components 1): RunBothPassesAsync drives the legacy and v2
/// passes (HYDRA_SCHEDULER_IMPL) over identical topology and emits the trace
/// JSON pair via <see cref="Artifacts.WriteTracePairAsync"/>.
/// </summary>
public sealed class RealEngineScenarioRunner
{
    /// <summary>The smoke subset of the harness catalog (consultant ruling:
    /// reuse the REAL ScenarioCatalog specs — no local mirror). Internal member
    /// exposing the internal ScenarioSpec type via the Tests.Core IVT.</summary>
    internal static IReadOnlyList<ScenarioSpec> SmokeSubset { get; } =
        ScenarioCatalog.All.Where(s => s.Id is "cold_atomic_engine" or "chunked_save").ToList();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _presetName;

    public RealEngineScenarioRunner(HttpClient http, string presetName)
    {
        _http = http;
        _presetName = presetName;
    }

    /// <summary>Executes one harness scenario spec against a routed node URL,
    /// applying the smoke assertions. Transport errors are captured in the trace
    /// call; assertion misses set Outcome=Failed with the reason in Error.
    /// Internal: ScenarioSpec is internal to Tests.Core (IVT-granted).</summary>
    internal async Task<MiniFleetScenarioRunResult> RunAsync(
        ScenarioSpec scenario,
        string schedulerImpl,
        IReadOnlyList<string> engineUrls,
        CancellationToken ct = default)
    {
        if (engineUrls.Count == 0)
        {
            throw new ArgumentException("At least one engine URL is required.", nameof(engineUrls));
        }

        var trace = new List<MiniFleetTraceCall>();
        var messages = new List<object>
        {
            // The harness specs drive token-estimated conversations; the real-engine
            // adapter represents each spec as a short deterministic chat script.
            new { role = "system", content = $"Scenario: {scenario.Id}. {scenario.Description}" },
            new { role = "user", content = "Reply with exactly: ok" },
        };

        int? promptTokens = null, completionTokens = null;
        var outcome = "Done";
        string? error = null;

        var nodeUrl = engineUrls[0];
        var call = await PostChatCompletionAsync(nodeUrl, messages, MaxCompletionTokens, ct)
            .ConfigureAwait(false);
        trace.Add(call);

        if (call.Error is not null || call.HttpStatusCode is null || call.HttpStatusCode >= 400)
        {
            outcome = "Failed";
            error = call.Error ?? $"HTTP {call.HttpStatusCode} from {nodeUrl}";
        }
        else if (call.FinishReason is null)
        {
            outcome = "Failed";
            error = "finish_reason missing in completion response";
        }
        else if ((call.PromptTokens ?? 0) == 0 || (call.CompletionTokens ?? 0) == 0)
        {
            outcome = "Failed";
            error = "usage tokens must be > 0 (brief §Components 1)";
        }
        else
        {
            promptTokens = call.PromptTokens;
            completionTokens = call.CompletionTokens;
        }

        return new MiniFleetScenarioRunResult(
            ScenarioId: scenario.Id,
            Preset: _presetName,
            SchedulerImpl: schedulerImpl,
            Outcome: outcome,
            Error: error,
            Trace: trace,
            UsagePromptTokens: promptTokens,
            UsageCompletionTokens: completionTokens);
    }

    /// <summary>Completion-token reservation (quirk #4): reasoning model needs
    /// ≥120 tokens or content comes back empty while thinking fills
    /// reasoning_content.</summary>
    public const int MaxCompletionTokens = 160;

    /// <summary>A/B hook (brief §Components 1): run the same scenario for both
    /// HYDRA_SCHEDULER_IMPL values where feasible and emit the legacy-vs-v2
    /// trace pair to tests/minifleet-artifacts/&lt;preset&gt;/&lt;scenario&gt;.json.
    /// Internal: ScenarioSpec is internal to Tests.Core (IVT-granted).</summary>
    internal async Task<(MiniFleetScenarioRunResult Legacy, MiniFleetScenarioRunResult? V2)> RunBothPassesAsync(
        ScenarioSpec scenario,
        IReadOnlyList<string> engineUrls,
        Func<string, Task<IReadOnlyList<string>>>? implLaneFactory = null,
        CancellationToken ct = default)
    {
        var legacy = await RunAsync(scenario, "legacy", engineUrls, ct).ConfigureAwait(false);
        MiniFleetScenarioRunResult? v2 = null;
        try
        {
            var v2Urls = implLaneFactory is not null
                ? await implLaneFactory("v2").ConfigureAwait(false)
                : engineUrls;
            v2 = await RunAsync(scenario, "v2", v2Urls, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[minifleet] v2 pass skipped: {ex.Message}");
        }
        await Artifacts.WriteTracePairAsync(
            _presetName, scenario.Id,
            SerializeResult(legacy), v2 is null ? null : SerializeResult(v2))
            .ConfigureAwait(false);
        return (legacy, v2);
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────

    private async Task<MiniFleetTraceCall> PostChatCompletionAsync(
        string nodeUrl, IReadOnlyList<object> messages, int maxTokens, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var body = new
            {
                model = "qwen-2node",
                messages,
                max_tokens = maxTokens,
                stream = false,
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{nodeUrl}/v1/chat/completions", content, ct)
                .ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new MiniFleetTraceCall(nodeUrl, "/v1/chat/completions", false,
                    (int)response.StatusCode, null, null, null, sw.ElapsedMilliseconds,
                    $"non-success status; body: {Truncate(json)}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var choices = root.GetProperty("choices");
            var finishReason = choices[0].TryGetProperty("finish_reason", out var fr)
                ? (fr.ValueKind == JsonValueKind.String ? fr.GetString() : fr.ToString())
                : null;
            int? promptTokens = null, completionTokens = null;
            if (root.TryGetProperty("usage", out var usage))
            {
                promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : null;
                completionTokens = usage.TryGetProperty("completion_tokens", out var cti) ? cti.GetInt32() : null;
            }
            return new MiniFleetTraceCall(nodeUrl, "/v1/chat/completions", false,
                (int)response.StatusCode, finishReason, promptTokens, completionTokens,
                sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            sw.Stop();
            return new MiniFleetTraceCall(nodeUrl, "/v1/chat/completions", false,
                null, null, null, null, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private static string Truncate(string s) =>
        s.Length <= 400 ? s : s[..400] + "…";

    private static string SerializeResult(MiniFleetScenarioRunResult result) =>
        JsonSerializer.Serialize(result, JsonOpts);
}
