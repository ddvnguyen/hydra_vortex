using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Hydra.Core.Services;

public sealed class CompletionProxyService : ICompletionProxyService
{
	private readonly HttpClient _http;

	public CompletionProxyService(int readTimeoutSeconds = 1800)
	{
		_http = new HttpClient { Timeout = TimeSpan.FromSeconds(readTimeoutSeconds) };
	}

	public async Task<bool> LoadModelAsync(string nodeUrl, string modelName, string traceId, CancellationToken ct)
	{
		var body = JsonSerializer.Serialize(new { model = modelName });
		var content = new StringContent(body, Encoding.UTF8, "application/json");
		var resp = await _http.PostAsync($"{nodeUrl}/models/load", content, ct);
		if (!resp.IsSuccessStatusCode) return false;
		var json = await resp.Content.ReadAsStringAsync(ct);
		var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
		return result?.TryGetValue("success", out var s) == true && s.GetBoolean();
	}

	public async Task<Dictionary<string, object>> ProxyCompletionAsync(string nodeUrl, Dictionary<string, object> body, string traceId, CancellationToken ct)
	{
		var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		var resp = await _http.PostAsync($"{nodeUrl}/v1/chat/completions", content, ct);
		resp.EnsureSuccessStatusCode();
		return JsonSerializer.Deserialize<Dictionary<string, object>>(await resp.Content.ReadAsStringAsync(ct))!;
	}

	public async IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId, [EnumeratorCancellation] CancellationToken ct)
	{
		using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		var req = new HttpRequestMessage(HttpMethod.Post, $"{nodeUrl}/v1/chat/completions") { Content = content };
		var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
		resp.EnsureSuccessStatusCode();
		using var stream = await resp.Content.ReadAsStreamAsync(ct);
		using var reader = new StreamReader(stream);
		string? line;
		while ((line = await reader.ReadLineAsync(ct)) != null)
		{
			// Match Python: skip empty lines, yield with \n\n (SSE event boundary)
			if (line.Length > 0)
				yield return Encoding.UTF8.GetBytes($"{line}\n\n");
		}
	}
}
