using System.Net;
using System.Text;
using System.Text.Json;
using Hydra.Agent;

namespace Tests.Agent;

public sealed class LlamaClientTests
{
    [Fact]
    public async Task GetStateAsync_ReturnsStream()
    {
        var stateData = new byte[100_000];
        new Random(42).NextBytes(stateData);

        var handler = new MockHttpHandler(async (request, ct) =>
        {
            Assert.Contains("/slots/0/state", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(stateData),
            };
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        var (stream, contentLength) = await client.GetStateAsync(0, CancellationToken.None);

        Assert.Equal(stateData.Length, contentLength);

        var memStream = new MemoryStream((int)contentLength);
        await stream.CopyToAsync(memStream);

        Assert.Equal(stateData, memStream.ToArray());
    }

    [Fact]
    public async Task PutStateAsync_SendsBinaryBodyAndParsesResponse()
    {
        var stateData = new byte[50_000];
        new Random(99).NextBytes(stateData);

        var responseJson = """{"restored":true,"n_past":2968,"bytes":50000}""";

        var handler = new MockHttpHandler(async (request, ct) =>
        {
            Assert.Contains("/slots/1/state", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("application/octet-stream", request.Content?.Headers.ContentType?.MediaType);

            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(stateData, body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        using var stream = new MemoryStream(stateData);

        var result = await client.PutStateAsync(1, stream, stateData.Length, CancellationToken.None);

        Assert.True(result.Restored);
        Assert.Equal(2968, result.NPast);
        Assert.Equal(50000, result.Bytes);
    }

    [Fact]
    public async Task GetStateMetaAsync_ParsesJsonResponse()
    {
        var metaJson = """{"slot_id":0,"n_past":2968,"state_size":847003648,"is_processing":false}""";

        var handler = new MockHttpHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(metaJson, Encoding.UTF8, "application/json"),
            });
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        var meta = await client.GetStateMetaAsync(0, CancellationToken.None);

        Assert.Equal(0, meta.SlotId);
        Assert.Equal(2968, meta.NPast);
        Assert.Equal(847003648, meta.StateSize);
        Assert.False(meta.IsProcessing);
    }

    [Fact]
    public async Task HealthAsync_ReturnsTrueOnSuccess()
    {
        var handler = new MockHttpHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        var healthy = await client.HealthAsync(CancellationToken.None);

        Assert.True(healthy);
    }

    [Fact]
    public async Task HealthAsync_ReturnsFalseOnFailure()
    {
        var handler = new MockHttpHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        var healthy = await client.HealthAsync(CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task GetSlotsAsync_ReturnsSlots()
    {
        var slotsJson = """[{"id":0,"n_past":2968,"is_processing":false},{"id":1,"n_past":0,"is_processing":true}]""";

        var handler = new MockHttpHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(slotsJson, Encoding.UTF8, "application/json"),
            });
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        var slots = await client.GetSlotsAsync(CancellationToken.None);

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Id);
        Assert.Equal(2968, slots[0].NPast);
        Assert.False(slots[0].IsProcessing);
        Assert.True(slots[1].IsProcessing);
    }

    [Fact]
    public async Task SlotsEndpoint_NestedJson_ReturnsSlots()
    {
        var slotsJson = """{"slots":[{"id":0,"n_past":100,"is_processing":false}]}""";

        var handler = new MockHttpHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(slotsJson, Encoding.UTF8, "application/json"),
            });
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        var slots = await client.GetSlotsAsync(CancellationToken.None);

        Assert.Single(slots);
        Assert.Equal(0, slots[0].Id);
    }

    [Fact]
    public async Task FindIdleSlot_ReturnsFirstNonProcessing()
    {
        var slotsJson = """[{"id":0,"n_past":2968,"is_processing":true},{"id":1,"n_past":0,"is_processing":false}]""";

        var handler = new MockHttpHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(slotsJson, Encoding.UTF8, "application/json"),
            });
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        var idle = await client.FindIdleSlotAsync(CancellationToken.None);

        Assert.Equal(1, idle);
    }

    [Fact]
    public async Task EraseSlotAsync_CallsEraseEndpoint()
    {
        var erased = false;

        var handler = new MockHttpHandler((request, _) =>
        {
            Assert.Contains("/slots/0?action=erase", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            erased = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var client = new LlamaClient(new HttpClient(handler), "http://localhost:8080");
        await client.EraseSlotAsync(0, CancellationToken.None);

        Assert.True(erased);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
