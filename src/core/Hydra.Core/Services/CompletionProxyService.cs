using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Hydra.Core.Models;

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
	// The engine generates asynchronously after DECODE 0x43 returns Gate A validation.
	// GET /v1/decode/{id} returns:
	//   404 → decode entry absent mid-generation (NORMAL — entry is absent while
	//          GENERATING, re-inserted at DONE) or truly expired. Retry with
	//          backoff like the non-streaming path (#587); only a poll loop that
	//          exhausts maxAttempts is terminal (TimeoutException).
	//   202 → {state:"loading"|"restoring", model_load_ms?, restore_slot_ms?, model_alias}
	//          keep polling; record phase fields as they appear
	//   400 → {error, error_code, match{}} (terminal, abort — must never fall through
	//          to HTTP proxy; that decodes over an unvalidated slot = #469 hallucination)
	//   200 → text/event-stream, per-token OAI deltas
	public async IAsyncEnumerable<byte[]> PollDecodeStreamAsync(
		string nodeUrl, int decodeRequestId, string traceId,
		[EnumeratorCancellation] CancellationToken ct,
		WorkItem? item = null)
	{
		var url = $"{nodeUrl}/v1/decode/{decodeRequestId}";
		const int initialDelayMs = 100;
		const int maxDelayMs = 500;
		const int maxAttempts = 600; // 600 * 500ms = 300s max wait
		var delay = initialDelayMs;
		var prepareStartMs = item?.ElapsedMs ?? 0;
		try
		{
			for (int attempt = 0; attempt < maxAttempts; attempt++)
			{
				ct.ThrowIfCancellationRequested();
				HttpResponseMessage resp;
				var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.Add("Accept", "text/event-stream");
				resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

				if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
				{
					// #587: 404 is NOT terminal. The engine's decode entry is
					// absent while GENERATING and re-inserted at DONE, so a
					// mid-generation 404 must be retried with backoff exactly
					// like the non-streaming path (PollDecodeResultAsync).
					// Only exhausting maxAttempts is terminal (TimeoutException).
					resp.Dispose();
					await Task.Delay(delay, ct);
					delay = Math.Min(delay * 2, maxDelayMs);
					continue;
				}

				if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
				{
					// 202: still loading/restoring — record phase fields, keep polling.
					try
					{
						var body = await resp.Content.ReadAsStringAsync(ct);
						if (!string.IsNullOrEmpty(body) && item != null)
						{
							var doc = JsonDocument.Parse(body);
							var root = doc.RootElement;
							if (root.TryGetProperty("model_load_ms", out var mlm) && mlm.ValueKind == JsonValueKind.Number)
								item.Phases["model_load_ms"] = (long)mlm.GetDouble();
							if (root.TryGetProperty("restore_slot_ms", out var rsm) && rsm.ValueKind == JsonValueKind.Number)
								item.Phases["restore_slot_ms"] = (long)rsm.GetDouble();
						}
					}
					catch { /* non-fatal: phase fields are best-effort */ }
					finally { resp.Dispose(); }

					// Record decode_prepare_ms on first 202
					if (item != null && !item.Phases.ContainsKey("decode_prepare_ms") && prepareStartMs > 0)
						item.Phases["decode_prepare_ms"] = item.ElapsedMs - prepareStartMs;

					await Task.Delay(delay, ct);
					delay = Math.Min(delay * 2, maxDelayMs);
					continue;
				}

				if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
				{
					// 400: terminal error — abort. Must never fall through to HTTP
					// proxy; that would decode over an unvalidated or empty slot
					// (the #469 hallucination bug).
					try
					{
						var body = await resp.Content.ReadAsStringAsync(ct);
						Console.Error.WriteLine($"decode_poll_400 Sid={item?.SessionId ?? "?"} DecodeId={decodeRequestId} Body={body}");
					}
					catch { }
					finally { resp.Dispose(); }
					throw new InvalidOperationException(
						$"GET /v1/decode/{decodeRequestId} returned 400 — engine rejected the decode request");
				}

				resp.EnsureSuccessStatusCode();
				using var stream = await resp.Content.ReadAsStreamAsync(ct);
				using var reader = new StreamReader(stream);

				// Record ttft_ms on first byte of 200 response
				if (item != null && !item.Phases.ContainsKey("ttft_ms") && prepareStartMs > 0)
					item.Phases["ttft_ms"] = item.ElapsedMs - prepareStartMs;

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
		finally
		{
			if (ct.IsCancellationRequested)
				await CancelDecodeAsync(nodeUrl, decodeRequestId, traceId, CancellationToken.None);
		}
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
		try
		{
			for (int attempt = 0; attempt < maxAttempts; attempt++)
			{
				ct.ThrowIfCancellationRequested();
				HttpResponseMessage resp;
				resp = await _http.GetAsync(url, ct);
				if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
				{
					resp.Dispose();
					await Task.Delay(delay, ct);
					delay = Math.Min(delay * 2, maxDelayMs);
					continue;
				}
				resp.EnsureSuccessStatusCode();
				var json = await resp.Content.ReadAsStringAsync(ct);
				var body = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
				// #470: the engine serves 202-style LOADING bodies (state=loading)
				// while the async DECODE_APPLY is still generating. Only a
				// terminal state (done, or an error field) is the real result —
				// returning the loading body as the completion makes the
				// coordinator reply `{"state":"loading"}` instead of the text.
				var state = body.TryGetValue("state", out var sv) && sv is JsonElement se && se.ValueKind == JsonValueKind.String
					? se.GetString()
					: null;
				var hasError = body.ContainsKey("error");
				if (state == "done" || state == "error" || hasError || state == null)
				{
					return body;
				}
				// Still generating — retry with backoff.
				resp.Dispose();
				await Task.Delay(delay, ct);
				delay = Math.Min(delay * 2, maxDelayMs);
			}
			throw new TimeoutException($"GET /v1/decode/{decodeRequestId} timed out after {maxAttempts} attempts");
		}
		catch (OperationCanceledException)
		{
			await CancelDecodeAsync(nodeUrl, decodeRequestId, traceId, CancellationToken.None);
			throw;
		}
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
