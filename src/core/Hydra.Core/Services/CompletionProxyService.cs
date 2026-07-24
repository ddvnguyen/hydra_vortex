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
		try
		{
			var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
			var resp = await _http.PostAsync($"{nodeUrl}/v1/chat/completions", content, ct);
			resp.EnsureSuccessStatusCode();
			return JsonSerializer.Deserialize<Dictionary<string, object>>(await resp.Content.ReadAsStringAsync(ct))!;
		}
		catch (OperationCanceledException)
		{
			CoordinatorMetrics.UpstreamTimeouts.Inc();
			throw;
		}
	}

	public async IAsyncEnumerable<byte[]> ProxyCompletionStreamAsync(string nodeUrl, Dictionary<string, object> body, string traceId, [EnumeratorCancellation] CancellationToken ct)
	{
		using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		var req = new HttpRequestMessage(HttpMethod.Post, $"{nodeUrl}/v1/chat/completions") { Content = content };
		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
		}
		catch (OperationCanceledException)
		{
			CoordinatorMetrics.UpstreamTimeouts.Inc();
			throw;
		}
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

	// #470: Poll GET /v1/decode/{id} for streaming merged-decode result.
	// The engine generates asynchronously after DECODE 0x43 returns the validation
	// response. GET returns 404 until the generation completes, then returns the
	// full SSE response in one shot.
	public async IAsyncEnumerable<byte[]> PollDecodeStreamAsync(
		string nodeUrl, int decodeRequestId, string traceId,
		[EnumeratorCancellation] CancellationToken ct)
	{
		var url = $"{nodeUrl}/v1/decode/{decodeRequestId}";
		const int initialDelayMs = 100;
		const int maxDelayMs = 500;
		const int maxAttempts = 600; // 600 * 500ms = 300s max wait
		var delay = initialDelayMs;
		for (int attempt = 0; attempt < maxAttempts; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			HttpResponseMessage resp;
			try
			{
				var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.Add("Accept", "text/event-stream");
				resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
			}
			catch (OperationCanceledException)
			{
				await CancelDecodeAsync(nodeUrl, decodeRequestId, traceId, CancellationToken.None);
				throw;
			}
			if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				resp.Dispose();
				await Task.Delay(delay, ct);
				delay = Math.Min(delay * 2, maxDelayMs);
				continue;
			}
			resp.EnsureSuccessStatusCode();
			using var stream = await resp.Content.ReadAsStreamAsync(ct);
			using var reader = new StreamReader(stream);
			string? line;
			while ((line = await reader.ReadLineAsync(ct)) != null)
			{
				if (line.Length > 0)
					yield return Encoding.UTF8.GetBytes($"{line}\n\n");
			}
			yield break;
		}
		throw new TimeoutException($"GET /v1/decode/{decodeRequestId} timed out after {maxAttempts} attempts");
	}

	// #470: Poll GET /v1/decode/{id} for buffered (non-streaming) merged-decode result.
	public async Task<Dictionary<string, object>> PollDecodeResultAsync(
		string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
	{
		var url = $"{nodeUrl}/v1/decode/{decodeRequestId}";
		const int initialDelayMs = 100;
		const int maxDelayMs = 500;
		const int maxAttempts = 600;
		var delay = initialDelayMs;
		for (int attempt = 0; attempt < maxAttempts; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			HttpResponseMessage resp;
			try
			{
				resp = await _http.GetAsync(url, ct);
			}
			catch (OperationCanceledException)
			{
				await CancelDecodeAsync(nodeUrl, decodeRequestId, traceId, CancellationToken.None);
				throw;
			}
			if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				resp.Dispose();
				await Task.Delay(delay, ct);
				delay = Math.Min(delay * 2, maxDelayMs);
				continue;
			}
			resp.EnsureSuccessStatusCode();
			var json = await resp.Content.ReadAsStringAsync(ct);
			return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
		}
		throw new TimeoutException($"GET /v1/decode/{decodeRequestId} timed out after {maxAttempts} attempts");
	}

	// #470: DELETE /v1/decode/{id} to cancel orphaned generation on abort.
	public async Task CancelDecodeAsync(
		string nodeUrl, int decodeRequestId, string traceId, CancellationToken ct)
	{
		try
		{
			var url = $"{nodeUrl}/v1/decode/{decodeRequestId}";
			using var resp = await _http.DeleteAsync(url, ct);
		}
		catch
		{
			// Best-effort cancellation — ignore errors
		}
	}
}
