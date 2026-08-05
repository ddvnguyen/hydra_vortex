using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hydra.Core;

public sealed class LlamaSlotInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("n_past")]
    public int NPast { get; init; }
    [JsonPropertyName("is_processing")]
    public bool IsProcessing { get; init; }
    [JsonPropertyName("n_remain")]
    public int NRemain { get; init; }
    [JsonPropertyName("n_decoded")]
    public int NDecoded { get; init; }
    [JsonPropertyName("id_task")]
    public int IdTask { get; init; }
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
    /// <summary>Alias of the model that built the KV in this slot (e.g. "balanced"). Empty if unknown / single-model mode without aliases.</summary>
    [JsonPropertyName("model_alias")]
    public string ModelAlias { get; init; } = "";
    /// <summary>GGUF-derived tokenizer family (e.g. "llama", "gpt2"). Empty if the server did not provide one.</summary>
    [JsonPropertyName("tokenizer")]
    public string Tokenizer { get; init; } = "";
    /// <summary>GGUF display name (from general.base_model.0.name or general.name). Empty if unknown.</summary>
    [JsonPropertyName("model_name")]
    public string ModelName { get; init; } = "";
    /// <summary>GGUF quantization label (e.g. "Q5_K"). Empty if unknown.</summary>
    [JsonPropertyName("model_quant")]
    public string ModelQuant { get; init; } = "";
    /// <summary>Bitwise capabilities from GGUF metadata (bit0=MTP, bit1=VISION, etc.).</summary>
    [JsonPropertyName("model_capabilities")]
    public uint ModelCapabilities { get; init; }
    /// <summary>Full path of the GGUF file the KV was built with.</summary>
    [JsonPropertyName("model_path")]
    public string ModelPath { get; init; } = "";
    /// <summary>Current operation: "prefill", "decode", "save", "restore", "idle".</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";
    /// <summary>Progress 0.0-1.0 for the current operation.</summary>
    [JsonPropertyName("progress")]
    public float Progress { get; init; }
    /// <summary>Tokens processed so far in the current operation.</summary>
    [JsonPropertyName("tokens_processed")]
    public int TokensProcessed { get; init; }
    /// <summary>Total tokens expected for the current operation.</summary>
    [JsonPropertyName("tokens_total")]
    public int TokensTotal { get; init; }
    /// <summary>Elapsed time in milliseconds for the current operation.</summary>
    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; init; }
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

public sealed class StateStreamResult : IDisposable
{
    private readonly HttpResponseMessage _response;
    public Stream Content { get; }
    public long ContentLength { get; }

    public StateStreamResult(HttpResponseMessage response, Stream content, long contentLength)
    {
        _response = response;
        Content = content;
        ContentLength = contentLength;
    }

    public void Dispose()
    {
        _response.Dispose();
        Content.Dispose();
    }
}

public class LlamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private static readonly Serilog.ILogger _log = Serilog.Log.ForContext<LlamaClient>();

    public LlamaClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public LlamaClient(HttpClient http, string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http;
    }

    public async Task<StateStreamResult> GetStateAsync(int slotId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"{_baseUrl}/slots/{slotId}/state",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength
            ?? throw new InvalidOperationException("Missing Content-Length in state response");
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new StateStreamResult(response, stream, contentLength);
    }

    public async Task<RestoreResult> PutStateAsync(int slotId, Stream data, long contentLength, CancellationToken ct)
    {
        var content = new StreamContent(data, 65536);
        content.Headers.ContentLength = contentLength;
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var response = await _http.PutAsync(
            $"{_baseUrl}/slots/{slotId}/state?erase_existing=true", content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrEmpty(body))
            return new RestoreResult { Restored = false };
        var result = JsonSerializer.Deserialize<RestoreResult>(body);
        return result ?? new RestoreResult { Restored = false };
    }

    public virtual async Task<SlotMeta> GetStateMetaAsync(int slotId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"{_baseUrl}/slots/{slotId}/state/meta", ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrEmpty(body) || body.TrimStart().StartsWith('['))
            return new SlotMeta { StateSize = 0 };

        var result = JsonSerializer.Deserialize<SlotMeta>(body);
        return result ?? new SlotMeta { StateSize = 0 };
    }

    public virtual async Task<bool> HealthAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Health check failed for {BaseUrl}", _baseUrl);
            return false;
        }
    }

    public async Task<List<LlamaSlotInfo>> GetSlotsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync($"{_baseUrl}/slots", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseSlots(json);
    }

    public virtual async Task EraseSlotAsync(int slotId, CancellationToken ct)
    {
        var response = await _http.PostAsync(
            $"{_baseUrl}/slots/{slotId}?action=erase", null, ct);
        // 404/501: server doesn't support slot erase (no --slot-save-path) — treat as success
        if ((int)response.StatusCode == 404 || (int)response.StatusCode == 501)
        {
            return;
        }
        response.EnsureSuccessStatusCode();
    }

    public async Task<int?> FindIdleSlotAsync(CancellationToken ct)
    {
        var slots = await GetSlotsAsync(ct);
        foreach (var s in slots)
        {
            if (!s.IsProcessing)
                return s.Id;
        }
        return null;
    }

    public async Task<int?> WaitForIdleSlotAsync(int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var slot = await FindIdleSlotAsync(ct);
            if (slot.HasValue) return slot.Value;
            await Task.Delay(500, ct);
        }
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private static int ParseNRemain(JsonElement slot)
    {
        // Try top-level first (upstream llama.cpp schema)
        if (slot.TryGetProperty("n_remain", out var nr))
            return nr.GetInt32();

        // Hydra fork puts n_remain inside next_token[0] (#342)
        if (slot.TryGetProperty("next_token", out var nt) &&
            nt.ValueKind == JsonValueKind.Array &&
            nt.GetArrayLength() > 0)
        {
            var first = nt[0];
            if (first.TryGetProperty("n_remain", out var nestedNr))
                return nestedNr.GetInt32();
        }

        return 0;
    }

    private static List<LlamaSlotInfo> ParseSlots(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(e => new LlamaSlotInfo
            {
                Id = e.GetProperty("id").GetInt32(),
                NPast = e.TryGetProperty("n_past", out var np) ? np.GetInt32() : 0,
                IsProcessing = e.TryGetProperty("is_processing", out var ip) && ip.GetBoolean(),
                NRemain = ParseNRemain(e),
            }).ToList();
        }

        if (root.TryGetProperty("slots", out var slots))
        {
            return slots.EnumerateArray().Select(e => new LlamaSlotInfo
            {
                Id = e.GetProperty("id").GetInt32(),
                NPast = e.TryGetProperty("n_past", out var np) ? np.GetInt32() : 0,
                IsProcessing = e.TryGetProperty("is_processing", out var ip) && ip.GetBoolean(),
                NRemain = ParseNRemain(e),
            }).ToList();
        }

        return [];
    }
}
