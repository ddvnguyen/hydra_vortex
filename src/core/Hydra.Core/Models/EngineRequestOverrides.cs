using System.Text.Json;

namespace Hydra.Core.Models;

/// <summary>
/// Per-request engine overrides. Mirrors the T1 subset of
/// <see cref="EngineConfig"/> — fields that can be re-configured
/// mid-decode without a model or context rebuild. Phase 2b of
/// ddvnguyen/llama.cpp#36.
///
/// Extracted from the request body in
/// <c>WorkerSchedulerService.SubmitAsync</c> (mirrors the
/// <c>force_mode</c> extraction pattern) and emitted as a 0x40
/// EngineConfigure call in <c>DecodeAsync</c> before the
/// multi-engine peer activation.
///
/// All fields are optional. <see cref="IsEmpty"/> returns true
/// when no override is set (the caller should skip the 0x40 call).
/// </summary>
public sealed record EngineRequestOverrides(
    /// <summary>Sampling: temperature (0.0 = greedy).</summary>
    float? Temperature = null,
    /// <summary>Sampling: top_p (0.0-1.0).</summary>
    float? TopP = null,
    /// <summary>Sampling: top_k (≥0; 0 = disabled).</summary>
    int? TopK = null,
    /// <summary>Sampling: min_p (0.0-1.0).</summary>
    float? MinP = null,
    /// <summary>Sampling: penalty_repeat (1.0 = no penalty).</summary>
    float? RepeatPenalty = null,
    /// <summary>Sampling: RNG seed.</summary>
    uint? Seed = null,
    /// <summary>Antiprompt / stop strings (OpenAI "stop" key).</summary>
    IReadOnlyList<string>? Stop = null,
    /// <summary>Engine: n_predict (max tokens to generate; -1 = unlimited).</summary>
    int? NPredict = null,
    /// <summary>Engine: n_keep (tokens from prompt to keep when truncating).</summary>
    int? NKeep = null
)
{
    /// <summary>True when no override is set (skip the 0x40 call).</summary>
    public bool IsEmpty =>
        Temperature is null && TopP is null && TopK is null && MinP is null
        && RepeatPenalty is null && Seed is null
        && (Stop is null || Stop.Count == 0)
        && NPredict is null && NKeep is null;

    /// <summary>
    /// Extract T1 overrides from a chat-completions request body. The
    /// OpenAI keys (temperature, top_p, top_k, seed, stop) are
    /// documented at https://platform.openai.com/docs/api-reference/chat
    /// — the C# side has been silently forwarding them to llama-server's
    /// HTTP API; the 0x40 path is now the engine-mode equivalent.
    /// </summary>
    public static EngineRequestOverrides FromRequest(
        IDictionary<string, object>? body)
    {
        if (body is null || body.Count == 0) return new EngineRequestOverrides();

        float? f(string k) => body.TryGetValue(k, out var v) && v is not null
            ? v switch
            {
                float f => f,
                double d => (float)d,
                JsonElement je when je.ValueKind == JsonValueKind.Number => (float)je.GetDouble(),
                JsonElement je when je.ValueKind == JsonValueKind.String
                    && float.TryParse(je.GetString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var s) => s,
                _ => null
            }
            : null;

        int? i(string k) => body.TryGetValue(k, out var v) && v is not null
            ? v switch
            {
                int n => n,
                long l => (int)l,
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
                _ => null
            }
            : null;

        uint? u(string k)
        {
            if (!body.TryGetValue(k, out var v) || v is null) return null;
            return v switch
            {
                uint u => u,
                int n => (uint)n,
                long l => (uint)l,
                JsonElement je when je.ValueKind == JsonValueKind.Number => (uint)je.GetInt64(),
                _ => null
            };
        }

        IReadOnlyList<string>? s(string k)
        {
            if (!body.TryGetValue(k, out var v) || v is null) return null;
            if (v is string single) return new[] { single };
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in je.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                        list.Add(el.GetString()!);
                }
                return list.Count == 0 ? null : list;
            }
            if (v is IEnumerable<object> objs)
            {
                var list = new List<string>();
                foreach (var o in objs) if (o is not null) list.Add(o.ToString()!);
                return list.Count == 0 ? null : list;
            }
            return null;
        }

        return new EngineRequestOverrides(
            Temperature: f("temperature"),
            TopP: f("top_p"),
            TopK: i("top_k"),
            MinP: f("min_p"),
            RepeatPenalty: f("repeat_penalty"),
            Seed: u("seed"),
            Stop: s("stop"),
            NPredict: i("max_tokens") is int mt && mt > 0 ? mt : null,
            NKeep: i("n_keep")
        );
    }

    /// <summary>
    /// Serialize to the wire JSON shape the engine accepts on 0x40
    /// CONFIGURE. Skips null fields. Produces e.g.
    /// <c>{"sampling":{"temp":0.5,"top_p":0.9},"n_predict":500}</c>.
    /// </summary>
    public string ToWireJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        var first = true;

        // Sampling block (only emit if at least one sub-key is set)
        var hasSampling = Temperature is not null || TopP is not null
            || TopK is not null || MinP is not null
            || RepeatPenalty is not null || Seed is not null;
        if (hasSampling)
        {
            sb.Append(first ? "" : ",").Append("\"sampling\":{");
            first = false;
            var inner = true;
            if (Temperature is float t)
            { sb.Append(inner ? "" : ",").Append("\"temp\":").Append(F(t)); inner = false; }
            if (TopP is float tp)
            { sb.Append(inner ? "" : ",").Append("\"top_p\":").Append(F(tp)); inner = false; }
            if (TopK is int tk)
            { sb.Append(inner ? "" : ",").Append("\"top_k\":").Append(tk); inner = false; }
            if (MinP is float mp)
            { sb.Append(inner ? "" : ",").Append("\"min_p\":").Append(F(mp)); inner = false; }
            if (RepeatPenalty is float rp)
            { sb.Append(inner ? "" : ",").Append("\"penalty_repeat\":").Append(F(rp)); inner = false; }
            if (Seed is uint sd)
            { sb.Append(inner ? "" : ",").Append("\"seed\":").Append(sd); inner = false; }
            sb.Append('}');
        }
        if (Stop is IReadOnlyList<string> stp && stp.Count > 0)
        {
            sb.Append(first ? "" : ",").Append("\"antiprompt\":[");
            for (int i = 0; i < stp.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(System.Text.Json.JsonSerializer.Serialize(stp[i]));
            }
            sb.Append(']');
            first = false;
        }
        if (NPredict is int np)
        { sb.Append(first ? "" : ",").Append("\"n_predict\":").Append(np); first = false; }
        if (NKeep is int nk)
        { sb.Append(first ? "" : ",").Append("\"n_keep\":").Append(nk); first = false; }
        sb.Append('}');
        return sb.ToString();

        static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
