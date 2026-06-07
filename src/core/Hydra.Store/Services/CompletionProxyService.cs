using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Hydra.Store.Services;

public sealed class CompletionProxyService : ICompletionProxyService
{
    private readonly HttpClient _http;

    public CompletionProxyService(int readTimeoutSeconds = 1800)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(readTimeoutSeconds) };
    }

    public async Task<Dictionary<string, object>> ProxyCompletionAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{nodeUrl}/v1/chat/completions", content, ct);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<Dictionary<string, object>>(await resp.Content.ReadAsStringAsync(ct))!;
    }

    public async IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, $"{nodeUrl}/v1/chat/completions") { Content = content };
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(ct);
        if (!string.IsNullOrEmpty(line)) yield return Encoding.UTF8.GetBytes($"{line}\n\n");
        while ((line = await reader.ReadLineAsync(ct)) != null && line.Length > 0)
            yield return Encoding.UTF8.GetBytes($"{line}\n\n");
    }
}
