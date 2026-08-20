using System.Net;
using System.Text;
using Hydra.Core.Services;

namespace Tests.Core.Services;

/// <summary>
/// #587: the merged-decode streaming poll must treat mid-generation 404 as
/// transient (retry with backoff, like the non-streaming path) while real
/// terminal statuses (400) still abort immediately.
/// </summary>
public sealed class CompletionProxyPollTests
{
	/// <summary>Scripted handler: returns queued responses in order, then throws.</summary>
	private sealed class ScriptedHttpHandler : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> _responses;

		public int CallCount { get; private set; }

		public ScriptedHttpHandler(params HttpResponseMessage[] responses)
			=> _responses = new Queue<HttpResponseMessage>(responses);

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct)
		{
			CallCount++;
			if (_responses.Count == 0)
				throw new InvalidOperationException("No more scripted responses");
			return Task.FromResult(_responses.Dequeue());
		}
	}

	private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
	};

	[Fact]
	public async Task PollDecodeStream_RetriesTransient404_ThenStreams200()
	{
		// 404 → 404 → 200: mid-generation 404s are NORMAL in this engine build
		// (decode entry absent while GENERATING, re-inserted at DONE). The poll
		// must retry with backoff instead of aborting (#587).
		var handler = new ScriptedHttpHandler(
			new HttpResponseMessage(HttpStatusCode.NotFound),
			new HttpResponseMessage(HttpStatusCode.NotFound),
			SseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}"));
		var proxy = new CompletionProxyService(new HttpClient(handler));

		var poll = proxy.PollDecodeStreamAsync(
			"http://localhost:8080", decodeRequestId: 1367, traceId: "trace-404", ct: CancellationToken.None);
		var collected = new StringBuilder();
		await foreach (var chunk in poll)
			collected.Append(Encoding.UTF8.GetString(chunk));

		// Retried through the 404s, then streamed the 200 body.
		Assert.Equal(3, handler.CallCount);
		Assert.Contains("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}", collected.ToString());
	}

	[Fact]
	public async Task PollDecodeStream_ThrowsImmediately_On400()
	{
		// 400 = terminal (unvalidated slot, the #469 hallucination gate) —
		// must abort immediately, never retry or fall through to the proxy.
		var handler = new ScriptedHttpHandler(
			new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new StringContent("""{"error":"bad slot","error_code":7}""", Encoding.UTF8, "application/json")
			});
		var proxy = new CompletionProxyService(new HttpClient(handler));

		var poll = proxy.PollDecodeStreamAsync(
			"http://localhost:8080", decodeRequestId: 7, traceId: "trace-400", ct: CancellationToken.None);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await foreach (var _ in poll) { }
		});
		Assert.Contains("400", ex.Message);
		Assert.Equal(1, handler.CallCount); // no retry on terminal 400
	}
}
