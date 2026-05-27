using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Prometheus;

namespace Hydra.Store;

public sealed class StoreServer : RpcServer
{
    private readonly StoreConfig _cfg;
    private readonly StorageEngine _engine;

    public StoreServer(StoreConfig cfg, StorageEngine engine)
        : base(cfg.Host, cfg.Port)
    {
        _cfg = cfg;
        _engine = engine;
    }

    protected override async Task HandleAsync(
        OpCode op, string key, string traceId, long payloadLen,
        PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
    {
        switch (op)
        {
            case OpCode.Put:
                await HandlePutAsync(key, payloadLen, reader, writer, ct);
                break;
            case OpCode.Get:
                await HandleGetAsync(key, writer, client, ct);
                break;
            case OpCode.Del:
                await HandleDelAsync(key, writer, ct);
                break;
            case OpCode.Stat:
                await HandleStatAsync(key, writer, ct);
                break;
            case OpCode.List:
                await HandleListAsync(key, writer, ct);
                break;
            default:
                await WriteErrorAsync(writer, $"Unknown opcode: {op}", ct);
                break;
        }
    }

     private async Task HandlePutAsync(
        string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        using var _ = StoreMetrics.OpDuration.WithLabels("put").NewTimer();
        try
        {
            await _engine.PutAsync(key, reader, payloadLen, ct);
            StoreMetrics.OpsTotal.WithLabels("put").Inc();
            StoreMetrics.BytesStored.Inc(payloadLen);

            var meta = """{"stored":true}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
            await WriteMetaAsync(writer, meta, ct);
        }
        catch (InvalidDataException ex)
        {
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

    private async Task HandleGetAsync(
        string key, PipeWriter writer, TcpClient client, CancellationToken ct)
    {
        using var _ = StoreMetrics.OpDuration.WithLabels("get").NewTimer();
        var file = await _engine.GetAsync(key, ct);
        if (file is null)
        {
            StoreMetrics.OpsTotal.WithLabels("get_not_found").Inc();
            await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
            return;
        }

        var meta = $$"""{"size":{{file.Length}}}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);

        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)file.Length, ct);
        await WriteMetaAsync(writer, meta, ct);

        await SendFileAsync(client, file.FullName, ct);
        StoreMetrics.OpsTotal.WithLabels("get").Inc();
        StoreMetrics.BytesSent.Inc(file.Length);
    }

    private async Task HandleDelAsync(string key, PipeWriter writer, CancellationToken ct)
    {
        using var _ = StoreMetrics.OpDuration.WithLabels("del").NewTimer();
        var deleted = await _engine.DeleteAsync(key, ct);
        var meta = deleted ? """{"deleted":true}""" : """{"deleted":false}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
        await WriteMetaAsync(writer, meta, ct);
        StoreMetrics.OpsTotal.WithLabels("del").Inc(deleted ? 1 : 0);
    }

    private async Task HandleStatAsync(string key, PipeWriter writer, CancellationToken ct)
    {
        using var _ = StoreMetrics.OpDuration.WithLabels("stat").NewTimer();
        var stat = await _engine.StatAsync(key, ct);
        if (stat is null)
        {
            StoreMetrics.OpsTotal.WithLabels("stat_not_found").Inc();
            await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
            return;
        }

        var meta = $$"""{"name":"{{stat.Name}}","size":{{stat.Size}},"modified":"{{stat.LastModified:O}}"}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
        await WriteMetaAsync(writer, meta, ct);
        StoreMetrics.OpsTotal.WithLabels("stat").Inc();
    }

   private async Task HandleListAsync(string prefix, PipeWriter writer, CancellationToken ct)
    {
        using var _ = StoreMetrics.OpDuration.WithLabels("list").NewTimer();
        var files = new List<string>();
        await foreach (var f in _engine.ListAsync(prefix, ct))
            files.Add(f);
        StoreMetrics.OpsTotal.WithLabels("list").Inc();

        var payload = JsonSerializer.SerializeToUtf8Bytes(files);
        var meta = $$"""{"count":{{files.Count}}}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);

        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
        await WriteMetaAsync(writer, meta, ct);
        await writer.WriteAsync(payload, ct);
    }

    private static async Task WriteErrorAsync(PipeWriter writer, string message, CancellationToken ct, StatusCode status = StatusCode.Error)
    {
        var meta = $$"""{"error":"{{message}}"}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)status, (uint)metaBytes.Length, 0, ct);
        await WriteMetaAsync(writer, meta, ct);
    }

    public Task StartDebugEndpointAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, $"http://{_cfg.Host}:{_cfg.DebugHttpPort}");

        var app = builder.Build();

        app.MapGet("/debug", async (HttpContext ctx) =>
        {
            var stats = await _engine.GetDebugStatsAsync(ct);
            return Results.Json(stats);
        });

        app.UseMetricServer();

        ct.Register(async () => await app.StopAsync());
        return app.RunAsync();
    }
}
