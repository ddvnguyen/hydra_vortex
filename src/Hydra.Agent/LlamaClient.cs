using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hydra.Agent;

public sealed class SlotInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("n_past")]
    public int NPast { get; init; }
    [JsonPropertyName("is_processing")]
    public bool IsProcessing { get; init; }
}

public sealed class SlotMeta
{
    [JsonPropertyName("slot_id")]
    public int SlotId { get; init; }
    [JsonPropertyName("n_past")]
    public int NPast { get; init; }
    [JsonPropertyName("state_size")]
    public long StateSize { get; init; }
    [JsonPropertyName("is_processing")]
    public bool IsProcessing { get; init; }
}

public sealed class RestoreResult
{
    [JsonPropertyName("restored")]
    public bool Restored { get; init; }
    [JsonPropertyName("n_past")]
    public int NPast { get; init; }
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }
}

public sealed class LlamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public LlamaClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(300),
        };
    }

    public LlamaClient(HttpClient http, string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http;
    }

    public async Task<Stream> GetStateAsync(int slotId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"{_baseUrl}/slots/{slotId}/state",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<RestoreResult> PutStateAsync(int slotId, Stream data, long contentLength, CancellationToken ct)
    {
        var content = new StreamContent(data, 65536);
        content.Headers.ContentLength = contentLength;
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var response = await _http.PutAsync(
            $"{_baseUrl}/slots/{slotId}/state", content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RestoreResult>(
            (JsonSerializerOptions?)null, ct);
        return result ?? new RestoreResult { Restored = false };
    }

    public async Task<SlotMeta> GetStateMetaAsync(int slotId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"{_baseUrl}/slots/{slotId}/state/meta", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SlotMeta>(
            (JsonSerializerOptions?)null, ct);
        return result ?? new SlotMeta();
    }

    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<SlotInfo>> GetSlotsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync($"{_baseUrl}/slots", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseSlots(json);
    }

    public async Task EraseSlotAsync(int slotId, CancellationToken ct)
    {
        var response = await _http.PostAsync(
            $"{_baseUrl}/slots/{slotId}?action=erase", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int?> FindIdleSlotAsync(CancellationToken ct)
    {
        var slots = await GetSlotsAsync(ct);
        return slots.FirstOrDefault(s => !s.IsProcessing)?.Id;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private static List<SlotInfo> ParseSlots(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(e => new SlotInfo
            {
                Id = e.GetProperty("id").GetInt32(),
                NPast = e.TryGetProperty("n_past", out var np) ? np.GetInt32() : 0,
                IsProcessing = e.TryGetProperty("is_processing", out var ip) && ip.GetBoolean(),
            }).ToList();
        }

        if (root.TryGetProperty("slots", out var slots))
        {
            return slots.EnumerateArray().Select(e => new SlotInfo
            {
                Id = e.GetProperty("id").GetInt32(),
                NPast = e.TryGetProperty("n_past", out var np) ? np.GetInt32() : 0,
                IsProcessing = e.TryGetProperty("is_processing", out var ip) && ip.GetBoolean(),
            }).ToList();
        }

        return [];
    }
}
