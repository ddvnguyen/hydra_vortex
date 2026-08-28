using System.Text;

namespace Hydra.Core.Services.SchedulerV2;

/// <summary>
/// Typed, validated request DTO — the v2 replacement for passing the raw
/// <c>Dictionary&lt;string, object&gt;</c> around. Parsed once at submission;
/// everything downstream (classifier, router, phase handlers) reads structure,
/// not magic keys.
/// </summary>
public sealed record ChatRequest(
    string SessionId,
    string TraceId,
    string? Model,
    bool Stream,
    int MaxTokens,
    int EstimatedTokens,
    int EstimatedNewTokens,
    int SystemPromptTokens,
    string? PrefixHash,
    string ForceMode,
    IReadOnlyList<Dictionary<string, object>> Messages,
    Dictionary<string, object> Body)
{
    /// <summary>Full trace id (short enough for logs, unique per request).</summary>
    public string ShortTraceId => TraceId.Length <= 20 ? TraceId : TraceId[..20];

    public static ChatRequest FromSubmit(
        Dictionary<string, object> request,
        List<Dictionary<string, object>> messages,
        string sessionId,
        int estimatedTokens,
        int maxTokens,
        string? prefixHash,
        int systemPromptTokens)
    {
        var stream = TryTruthy(request, "stream");
        var model = request.TryGetValue("model", out var m) ? m as string : null;
        var forceMode = request.TryGetValue("force_mode", out var f) ? f as string ?? "" : "";
        var traceId = request.TryGetValue("trace_id", out var t) && t is string ts && ts.Length > 0
            ? ts
            : $"v2_{Guid.NewGuid():N}";

        return new ChatRequest(
            sessionId,
            traceId,
            model,
            stream,
            maxTokens,
            estimatedTokens,
            estimatedTokens > 0 ? estimatedTokens : maxTokens,
            systemPromptTokens,
            prefixHash,
            forceMode,
            messages,
            request);
    }

    private static bool TryTruthy(Dictionary<string, object> body, string key)
        => body.TryGetValue(key, out var v) && v switch
        {
            bool b => b,
            string s => s is "true" or "1",
            _ => false,
        };
}
