using System.Text.Json;
using System.Text.Json.Serialization;
using FakeLlamaEngine;

var builder = WebApplication.CreateSlimBuilder(args);

// Configurable ports via env vars or CLI args
var httpPort = int.TryParse(builder.Configuration["HTTP_PORT"], out var hp) ? hp
    : int.TryParse(Environment.GetEnvironmentVariable("FAKE_ENGINE_HTTP_PORT"), out var hep) ? hep
    : 8080;

var rpcPort = int.TryParse(builder.Configuration["RPC_PORT"], out var rp) ? rp
    : int.TryParse(Environment.GetEnvironmentVariable("FAKE_ENGINE_RPC_PORT"), out var rep) ? rep
    : 9601;

builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");

var app = builder.Build();

// ── HTTP Endpoints ───────────────────────────────────────────────────

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.MapGet("/version", () => Results.Json(new
{
    version = "0.1.0-test",
    engine = "fake-llama-engine"
}));

app.MapGet("/slots", () => Results.Json(Array.Empty<object>()));

app.MapGet("/slots/{id:int}/state/meta", (int id) =>
    Results.Json(new
    {
        slot_id = id,
        n_past = 0,
        state_size = 0
    }));

app.MapDelete("/slots/{id:int}", (int id) =>
    Results.Json(new { deleted = true, slot_id = id }));

app.MapPost("/v1/chat/completions", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    string model = "fake-model";
    string content = "This is a fake response from the test engine.";
    int promptTokens = 8;
    int completionTokens = 16;

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            model = m.GetString() ?? model;
        if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            promptTokens = msgs.GetArrayLength() * 8;
    }
    catch { }

    var response = new
    {
        id = $"chatcmpl-fake-{Guid.NewGuid():N}",
        @object = "chat.completion",
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        model,
        choices = new[]
        {
            new
            {
                index = 0,
                message = new { role = "assistant", content },
                finish_reason = "stop"
            }
        },
        usage = new
        {
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            total_tokens = promptTokens + completionTokens
        }
    };

    return Results.Json(response);
});

// ── RPC Server (background) ──────────────────────────────────────────

var rpcServer = new FakeRpcServer(rpcPort);
var rpcTask = rpcServer.RunAsync(app.Lifetime.ApplicationStopping);
_ = rpcTask.ContinueWith(t =>
{
    if (t.IsFaulted)
        Console.Error.WriteLine($"[FakeEngine] RPC server faulted: {t.Exception?.InnerException?.Message}");
}, TaskContinuationOptions.OnlyOnFaulted);

Console.Error.WriteLine($"[FakeEngine] HTTP listening on :{httpPort}");
Console.Error.WriteLine($"[FakeEngine] RPC listening on :{rpcPort}");
Console.Error.Flush();

await app.RunAsync();
