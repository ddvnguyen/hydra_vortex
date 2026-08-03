using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Shared;

namespace FakeLlamaEngine;

/// <summary>
/// Opcode-aware fake RPC responder for E2E testing. Subclasses the same
/// <see cref="RpcServer"/> base that <c>TestRpcServer</c> uses, but returns
/// deterministic, plausible responses for engine opcodes (0x40–0x46) rather
/// than pure echo.
/// </summary>
internal sealed class FakeRpcServer : RpcServer
{
    public FakeRpcServer(int port = 0)
        : base("0.0.0.0", port)
    {
    }

    protected override async Task HandleAsync(
        OpCode op, string key, string traceId, long payloadLen,
        PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
    {
        switch (op)
        {
            // ── Engine opcodes ──────────────────────────────────────────
            case OpCode.EngineConfigure:
                await HandleConfigureAsync(key, payloadLen, reader, writer, ct);
                break;
            case OpCode.EngineInfo:
                await HandleInfoAsync(writer, ct);
                break;
            case OpCode.EnginePrefill:
                await HandlePrefillAsync(key, payloadLen, reader, writer, ct);
                break;
            case OpCode.EngineDecode:
                await HandleDecodeAsync(key, payloadLen, reader, writer, ct);
                break;
            case OpCode.EngineSetExpertMode:
                await HandleSetExpertModeAsync(payloadLen, reader, writer, ct);
                break;
            case OpCode.EngineSwapQuant:
                await HandleSwapQuantAsync(writer, ct);
                break;
            case OpCode.EnginePipelineAttach:
                await HandlePipelineAttachAsync(writer, ct);
                break;

            // ── State opcodes (0x30–0x32) — lightweight stubs ──────────
            case OpCode.StateGet:
            case OpCode.StatePut:
            case OpCode.StateMeta:
                await WriteOkMetaAsync(writer, """{"stub":true}""", ct);
                break;

            // ── Store opcodes — echo fallback ────────────────────────────
            default:
                await EchoPayloadAsync(payloadLen, reader, writer, op, key, traceId, ct);
                break;
        }
    }

    /// <summary>0x40 CONFIGURE — accept any T1 config, return success.</summary>
    private static async Task HandleConfigureAsync(
        string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        var payload = payloadLen > 0
            ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
            : [];

        var paramsApplied = new Dictionary<string, object>();
        string tier = "T1";
        if (payload.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
                var root = doc.RootElement;
                foreach (var prop in root.EnumerateObject())
                    paramsApplied[prop.Name] = prop.Value.Clone();
            }
            catch { }
        }

        var meta = JsonSerializer.Serialize(new
        {
            success = true,
            tier,
            params_applied = paramsApplied,
            deferred_keys = Array.Empty<string>(),
            error = (string?)null
        });
        await WriteOkMetaAsync(writer, meta, ct);
    }

    /// <summary>0x41 INFO — return engine capabilities.</summary>
    private static async Task HandleInfoAsync(PipeWriter writer, CancellationToken ct)
    {
        var meta = JsonSerializer.Serialize(new
        {
            engine = "fake-llama-engine",
            version = "0.1.0-test",
            capabilities = new[] { "prefill", "decode", "state_transfer", "expert_mode", "preset", "model_hash" },
            preset_aliases = new[] { "fake-model" },
            solo_active = true,
            rpc_backend_active = false,
            mode = "solo",
            peer_addr = "",
            peer_reachable = false,
            layer_split = "",
            combined_head_attached = false,
            pipeline_capable = false
        });
        await WriteOkMetaAsync(writer, meta, ct);
    }

    /// <summary>0x42 PREFILL — parse token count from request JSON, return plausible meta.</summary>
    private static async Task HandlePrefillAsync(
        string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        var payload = payloadLen > 0
            ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
            : [];

        int tokenCount = 0;
        if (payload.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
                if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                    tokenCount = msgs.GetArrayLength() * 8;
                if (doc.RootElement.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
                    tokenCount = Math.Max(tokenCount, (prompt.GetString()?.Length ?? 0) / 4);
            }
            catch { }
        }
        tokenCount = Math.Max(tokenCount, 1);

        var meta = JsonSerializer.Serialize(new
        {
            n_past = tokenCount,
            tokens_processed = tokenCount,
            prefill_ms = 1.0,
            model_alias = "fake-model",
            model_hash = "fake_hash_sha256_" + key,
            model_path = "/fake/model.gguf",
            model_fallback = false,
            state_size = 0L
        });
        await WriteOkMetaAsync(writer, meta, ct);
    }

    /// <summary>0x43 DECODE — return a valid Gate-A response (synchronous).</summary>
    private static async Task HandleDecodeAsync(
        string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        if (payloadLen > 0)
            await RpcServer.ReadPayloadAsync(reader, payloadLen, ct);

        var meta = JsonSerializer.Serialize(new
        {
            valid = true,
            decode_request_id = 1,
            match = new
            {
                tokenizer_match = true,
                model_name_match = true,
                model_capabilities_match = true,
                capabilities_xor = 0u,
                model_quant_match = true,
                model_alias_match = true
            },
            notes = Array.Empty<string>()
        });
        await WriteOkMetaAsync(writer, meta, ct);
    }

    /// <summary>0x44 SET_EXPERT_MODE — echo the requested mode.</summary>
    private static async Task HandleSetExpertModeAsync(
        long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        var payload = payloadLen > 0
            ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
            : [];
        var mode = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "solo";
        var meta = JsonSerializer.Serialize(new { success = true, mode });
        await WriteOkMetaAsync(writer, meta, ct);
    }

    /// <summary>0x45 SWAP_QUANT — stubbed success.</summary>
    private static async Task HandleSwapQuantAsync(PipeWriter writer, CancellationToken ct)
    {
        var meta = JsonSerializer.Serialize(new { swapped = 0, bytes = 0L, swap_ms = 0.0, kv_preserved = true });
        await WriteOkMetaAsync(writer, meta, ct);
    }

    /// <summary>0x46 PIPELINE_ATTACH — NOT_IMPLEMENTED (matches production binary).</summary>
    private static async Task HandlePipelineAttachAsync(PipeWriter writer, CancellationToken ct)
    {
        await WriteResponseHeaderAsync(writer, (byte)StatusCode.NotImplemented, 0, 0, ct);
    }

    private static async Task WriteOkMetaAsync(PipeWriter writer, string meta, CancellationToken ct)
    {
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
        if (metaBytes.Length > 0)
        {
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        }
    }

    private static async Task EchoPayloadAsync(
        long payloadLen, PipeReader reader, PipeWriter writer,
        OpCode op, string key, string traceId, CancellationToken ct)
    {
        var payload = payloadLen > 0
            ? await RpcServer.ReadPayloadAsync(reader, payloadLen, ct)
            : [];
        var meta = $$"""{"op":"{{op}}","key":"{{key}}","trace":"{{traceId}}"}""";
        var metaBytes = Encoding.UTF8.GetBytes(meta);
        await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
        if (metaBytes.Length > 0)
        {
            var span = writer.GetSpan(metaBytes.Length);
            metaBytes.CopyTo(span);
            writer.Advance(metaBytes.Length);
            await writer.FlushAsync(ct);
        }
        if (payload.Length > 0)
        {
            var paySpan = writer.GetSpan(payload.Length);
            payload.AsSpan().CopyTo(paySpan);
            writer.Advance(payload.Length);
            await writer.FlushAsync(ct);
        }
    }
}
