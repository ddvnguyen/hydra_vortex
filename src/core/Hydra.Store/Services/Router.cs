using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydra.Store.Models;
using Hydra.Store.Repositories;

namespace Hydra.Store.Services;

public static class Router
{
    public const int PrefillOnly = 1, DecodeOnly = 2, Mixed = 3;

    public static string DeriveSessionId(List<Dictionary<string, object>> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages) sb.Append($"{m.GetValueOrDefault("role","")}:{m.GetValueOrDefault("content","")}\n");
        return $"sess_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24]}";
    }

    public static int EstimateRequestTokens(List<Dictionary<string, object>> messages, double charsPerToken = 4.0)
    {
        long total = 0;
        foreach (var m in messages) total += m.GetValueOrDefault("content")?.ToString()?.Length ?? 0;
        return (int)Math.Max(1, total / charsPerToken);
    }

    public static string? ComputePrefixHash(List<Dictionary<string, object>> messages)
    {
        foreach (var m in messages)
            if (m.GetValueOrDefault("role")?.ToString() == "system" && m.GetValueOrDefault("content")?.ToString() is { } c)
                return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c)))[..16];
        return null;
    }

    public static WorkerConfig? PickBestPrefillWorker(List<WorkerConfig> workers, IWorkerTracker tracker, int? maxTokens = null, string? exclude = null)
        => workers.Where(w => w.CanPrefill && tracker.IsFree(w.Name) && w.Name != exclude)
            .Where(w => maxTokens is null or < 0 || w.MaxPrefillTokens < 1 || maxTokens <= w.MaxPrefillTokens)
            .OrderBy(w => w.PrefillPriority).FirstOrDefault();

    public static WorkerConfig? PickBestDecodeWorker(List<WorkerConfig> workers, IWorkerTracker tracker, string? exclude = null)
        => workers.Where(w => w.CanDecode && tracker.IsFree(w.Name) && w.Name != exclude)
            .OrderBy(w => w.DecodePriority).FirstOrDefault();

    public static string? PrefillModel(WorkerConfig w) => w.PrefillModelName ?? w.RouterModelName;
    public static string? DecodeModel(WorkerConfig w) => w.DecodeModelName ?? w.RouterModelName;

    public static async Task<int?> PickIdleSlot(string llamaUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var slots = JsonSerializer.Deserialize<List<JsonElement>>(await http.GetStringAsync($"{llamaUrl}/slots", ct));
            foreach (var s in slots ?? [])
                if (!s.TryGetProperty("is_processing", out var p) || !p.GetBoolean())
                    return s.GetProperty("id").GetInt32();
        }
        catch { }
        return null;
    }

    public static string NewTraceId() => Guid.NewGuid().ToString("N")[..16];
}
