using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Prometheus;
using Serilog;
using Serilog.Context;

namespace Hydra.Agent;

public sealed class AgentServer : RpcServer
{
    private readonly AgentConfig _cfg;
    private readonly StateHandler _handler;
    private readonly LlamaClient _llama;
    private readonly ILogger _log;

    public AgentServer(AgentConfig cfg, StateHandler handler, LlamaClient llama, ILogger log)
        : base(cfg.Host, cfg.Port)
    {
        _cfg = cfg;
        _handler = handler;
        _llama = llama;
        _log = log;
    }

    protected override async Task HandleAsync(
        OpCode op, string key, string traceId, long payloadLen,
        PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
    {
        using var _ = _log.TraceScope(traceId);

        switch (op)
        {
            case OpCode.SaveState:
                await HandleSaveStateAsync(key, writer, ct);
                break;
            case OpCode.RestoreState:
                await HandleRestoreStateAsync(key, writer, ct);
                break;
            case OpCode.SlotStatus:
                await HandleSlotStatusAsync(key, writer, ct);
                break;
            case OpCode.NodeHealth:
                await HandleNodeHealthAsync(writer, ct);
                break;
            case OpCode.SlotErase:
                await HandleSlotEraseAsync(key, writer, ct);
                break;
            case OpCode.PutChunked:
                await WriteErrorAsync(writer, "Store opcode not supported on agent", ct);
                break;
            case OpCode.GetChunked:
                await WriteErrorAsync(writer, "Store opcode not supported on agent", ct);
                break;
            case OpCode.SaveStateChunked:
                await HandleSaveStateChunkedAsync(key, writer, ct);
                break;
            case OpCode.RestoreStateChunked:
                await HandleRestoreStateChunkedAsync(key, writer, ct);
                break;
            default:
                await WriteErrorAsync(writer, $"Unknown opcode: {op}", ct);
                break;
        }
    }

   private async Task HandleSaveStateAsync(string sessionId, PipeWriter writer, CancellationToken ct)
    {
        var nodeLabel = _cfg.NodeName;
        var sessionTypeLabel = "non_chunked";
        using var _ = AgentMetrics.SaveDuration.WithLabels(nodeLabel, sessionTypeLabel).NewTimer();
        try
        {
            var parts = sessionId.Split(':');
            var sid = parts[0];
            var slotId = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : await _llama.WaitForIdleSlotAsync(30_000, ct);

            var result = await _handler.SaveToStoreAsync(
                ExtractSessionId(sid, slotId), slotId,
                "agent-save", ct);

            AgentMetrics.SaveOpsTotal.Inc();
            if (result.Size > 0)
                AgentMetrics.SaveBytesTotal.WithLabels(nodeLabel, sessionTypeLabel).Inc(result.Size);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SaveState failed for session {SessionId}", sessionId);
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

    private async Task HandleRestoreStateAsync(string sessionId, PipeWriter writer, CancellationToken ct)
    {
        var nodeLabel = _cfg.NodeName;
        var sessionTypeLabel = "non_chunked";
        using var _ = AgentMetrics.RestoreDuration.WithLabels(nodeLabel, sessionTypeLabel).NewTimer();
        try
        {
            var parts = sessionId.Split(':');
            var sid = parts[0];
            var slotId = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;

            var result = await _handler.RestoreFromStoreAsync(sid, slotId, "agent-restore", ct);

            AgentMetrics.RestoreOpsTotal.Inc();
            if (result.Size > 0)
                AgentMetrics.RestoreBytesTotal.WithLabels(nodeLabel, sessionTypeLabel).Inc(result.Size);

            var meta = $$"""{"session_id":"{{result.SessionId}}","slot_id":{{result.SlotId}},"n_past":{{result.NPast}},"restored":{{result.Restored.ToString().ToLowerInvariant()}},"restore_ms":{{result.ElapsedMs}}}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
            AgentMetrics.RestoreOpsTotal.Inc();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "RestoreState failed for session {SessionId}", sessionId);
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

    private async Task HandleSlotStatusAsync(string slotIdStr, PipeWriter writer, CancellationToken ct)
    {
        try
        {
            var slots = await _llama.GetSlotsAsync(ct);

            if (!string.IsNullOrEmpty(slotIdStr) && int.TryParse(slotIdStr, out var specificSlot))
            {
                slots = slots.Where(s => s.Id == specificSlot).ToList();
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(slots);
            var meta = $$"""{"count":{{slots.Count}}}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);

            await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
            var mSpan = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(mSpan);
            writer.Advance(metaBytes.Length);
            var pSpan = writer.GetSpan(payload.Length);
            payload.AsSpan().CopyTo(pSpan);
            writer.Advance(payload.Length);
            await writer.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SlotStatus failed");
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

    private async Task HandleNodeHealthAsync(PipeWriter writer, CancellationToken ct)
    {
        try
        {
            var isHealthy = await _llama.HealthAsync(ct);
            var slots = await _llama.GetSlotsAsync(ct);

            AgentMetrics.LlamaHealthy.WithLabels(_cfg.NodeName).Set(isHealthy ? 1 : 0);
            AgentMetrics.SlotsIdle.WithLabels(_cfg.NodeName).Set(slots.Count(s => !s.IsProcessing));

            var stuckSlotsCount = slots.Count(s => s.IsProcessing && s.NRemain == 0);
            var healthPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                healthy = isHealthy,
                node_name = _cfg.NodeName,
                version = HydraLogging.ServiceVersion,
                slots_total = slots.Count,
                slots_idle = slots.Count(s => !s.IsProcessing),
                stuck_slots = stuckSlotsCount,
                llama_url = _cfg.LlamaUrl,
                slots = slots.Select(s => new
                {
                    id = s.Id,
                    n_past = s.NPast,
                    is_processing = s.IsProcessing,
                    n_remain = s.NRemain,
                    n_decoded = s.NDecoded,
                    id_task = s.IdTask,
                }).ToList(),
            });

            var meta = """{"component":"health"}"""u8.ToArray();
            await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)meta.Length, (ulong)healthPayload.Length, ct);
            var mSpan = writer.GetSpan(meta.Length);
            meta.AsSpan().CopyTo(mSpan);
            writer.Advance(meta.Length);
            var pSpan = writer.GetSpan(healthPayload.Length);
            healthPayload.AsSpan().CopyTo(pSpan);
            writer.Advance(healthPayload.Length);
            await writer.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "NodeHealth failed");
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

  private async Task HandleSaveStateChunkedAsync(string sessionId, PipeWriter writer, CancellationToken ct)
    {
        var nodeLabel = _cfg.NodeName;
        var sessionTypeLabel = "chunked";
        using var _ = AgentMetrics.SaveDuration.WithLabels(nodeLabel, sessionTypeLabel).NewTimer();
        try
        {
            var parts = sessionId.Split(':');
            var sid = parts[0];
            var slotId = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : await _llama.WaitForIdleSlotAsync(30_000, ct);

            var result = await _handler.SaveToStoreChunkedAsync(
                ExtractSessionId(sid, slotId), slotId,
                "agent-save-chunked", ct);

            AgentMetrics.SaveOpsTotal.Inc();
            if (result.Size > 0)
                AgentMetrics.SaveBytesTotal.WithLabels(nodeLabel, sessionTypeLabel).Inc(result.Size);

            var meta = $$"""{"session_id":"{{result.SessionId}}","slot_id":{{result.SlotId}},"n_past":{{result.NPast}},"size":{{result.Size}},"save_ms":{{result.ElapsedMs}},"chunked":true}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        }
        catch (IOException ex)
        {
            _log.Debug(ex, "SaveStateChunked: client disconnected after save for {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            _log.Debug("SaveStateChunked: cancelled after save for {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SaveStateChunked failed for session {SessionId}", sessionId);
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

 private async Task HandleRestoreStateChunkedAsync(string sessionId, PipeWriter writer, CancellationToken ct)
    {
        var nodeLabel = _cfg.NodeName;
        var sessionTypeLabel = "chunked";
        using var _ = AgentMetrics.RestoreDuration.WithLabels(nodeLabel, sessionTypeLabel).NewTimer();
        try
        {
            var parts = sessionId.Split(':');
            var sid = parts[0];
            var slotId = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;

            var result = await _handler.RestoreFromStoreChunkedAsync(sid, slotId, "agent-restore-chunked", ct);

            AgentMetrics.RestoreOpsTotal.Inc();
            if (result.Size > 0)
                AgentMetrics.RestoreBytesTotal.WithLabels(nodeLabel, sessionTypeLabel).Inc(result.Size);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "RestoreStateChunked failed for session {SessionId}", sessionId);
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

    private async Task HandleSlotEraseAsync(string slotIdStr, PipeWriter writer, CancellationToken ct)
    {
        try
        {
            if (!int.TryParse(slotIdStr, out var slotId))
            {
                await WriteErrorAsync(writer, $"Invalid slot id: {slotIdStr}", ct);
                return;
            }

            await _llama.EraseSlotAsync(slotId, ct);
            var meta = $$"""{"slot_id":{{slotId}},"erased":true}""";
            var metaBytes = Encoding.UTF8.GetBytes(meta);
            await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SlotErase failed for slot {SlotId}", slotIdStr);
            await WriteErrorAsync(writer, ex.Message, ct);
        }
    }

    private readonly long _startedAt = Stopwatch.GetTimestamp();

    public Task StartDebugEndpointAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var builder = WebApplication.CreateBuilder();
                    builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, $"http://{_cfg.Host}:{_cfg.DebugHttpPort}");
                    var app = builder.Build();

                    app.UseMetricServer();

                    app.MapGet("/version", () => Results.Json(new
                    {
                        service = "hydra-agent",
                        version = HydraLogging.ServiceVersion,
                    }));

                    app.MapGet("/debug", async (HttpContext _ctx) =>
                    {
                        var uptimeS = (Stopwatch.GetTimestamp() - _startedAt) / Stopwatch.Frequency;
                        var isHealthy = await _llama.HealthAsync(ct);
                        var slots = await _llama.GetSlotsAsync(ct);

                        AgentMetrics.LlamaHealthy.WithLabels(_cfg.NodeName).Set(isHealthy ? 1 : 0);
                        AgentMetrics.SlotsIdle.WithLabels(_cfg.NodeName).Set(slots.Count(s => !s.IsProcessing));

                        return Results.Json(new
                        {
                            status = isHealthy ? "ok" : "degraded",
                            version = HydraLogging.ServiceVersion,
                            node_name = _cfg.NodeName,
                            llama_healthy = isHealthy,
                            slots = slots.Select(s => new { s.Id, s.NPast, s.IsProcessing }),
                            pending_ops = 0,
                            local_kv_files = 0,
                            store_connected = true,
                            uptime_s = (int)uptimeS,
                        });
                    });

                    ct.Register(async () => await app.StopAsync());
                    await app.RunAsync();
                    break; // clean shutdown via ct
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Debug HTTP endpoint crashed, restarting in 5s");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    private static string ExtractSessionId(string key, int slotId)
    {
        if (string.IsNullOrEmpty(key) || key == slotId.ToString())
            return $"slot-{slotId}";
        return key;
    }

    private static async Task WriteErrorAsync(PipeWriter writer, string message, CancellationToken ct, StatusCode status = StatusCode.Error)
    {
        var meta = $$"""{"error":"{{message}}"}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)status, (uint)metaBytes.Length, 0, ct);
        var span = writer.GetSpan(metaBytes.Length);
        metaBytes.CopyTo(span);
        writer.Advance(metaBytes.Length);
        await writer.FlushAsync(ct);
    }
}
