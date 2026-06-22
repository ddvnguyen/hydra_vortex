using System.Text;
using System.Text.Json;
using Hydra.Core.Services;
using Hydra.Shared;

namespace Tests.Core;

/// <summary>
/// M-Perf.9 / #289: tests for the HydraEngineClient wrapper around the
/// engine control-plane RPCs (opcodes 0x40-0x46). The wire format is
/// verified by Tests.Shared.EngineOpcodeTests; these tests verify the
/// C# wrapper shapes the payloads correctly (model field, messages
/// field, etc.) and parses responses correctly (model_alias, model_hash,
/// model_path, model_fallback).
/// </summary>
public sealed class EngineClientTests
{
    private static RpcResponse MakeRpcResponse(string metaJson, byte[]? payload = null)
        => new RpcResponse(
            Status: (byte)StatusCode.Ok,
            Meta: metaJson,
            Payload: payload ?? Array.Empty<byte>());

    [Fact]
    public async Task EngineInfoAsync_ParsesCapabilitiesAndPresetAliases()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse(
            """{"engine":"llama-server-hydra","version":"E1","capabilities":["prefill","decode","preset","model_hash"],"preset_aliases":["mini","balanced"]}""");
        var client = new HydraEngineClient(rpc);

        var info = await client.EngineInfoAsync("trace-info", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("llama-server-hydra", info!.Engine);
        Assert.Equal("E1", info.Version);
        Assert.Contains("prefill", info.Capabilities);
        Assert.Contains("preset", info.Capabilities);
        Assert.Contains("model_hash", info.Capabilities);
        Assert.Contains("mini", info.PresetAliases);
        Assert.Contains("balanced", info.PresetAliases);
        Assert.True(info.HasCapability("preset"));
        Assert.False(info.HasCapability("swap_quant"));
    }

    [Fact]
    public async Task EngineInfoAsync_ReturnsNull_OnErrorResponse()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = new RpcResponse((byte)StatusCode.Error, "boom", Array.Empty<byte>());
        var client = new HydraEngineClient(rpc);

        var info = await client.EngineInfoAsync("trace-info", CancellationToken.None);

        Assert.Null(info);
    }

    [Fact]
    public async Task EngineInfoAsync_ReturnsNull_OnInvalidJson()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse("not valid json{");
        var client = new HydraEngineClient(rpc);

        var info = await client.EngineInfoAsync("trace-info", CancellationToken.None);

        Assert.Null(info);
    }

    [Fact]
    public async Task EnginePrefillAsync_InjectsModelWhenProvided()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse(
            """{"n_past":42,"state_size":1234567,"model_alias":"balanced","model_hash":"deadbeef00000000000000000000000000000000000000000000000000000000","model_path":"/models/Balanced.gguf","model_fallback":false}""");
        var client = new HydraEngineClient(rpc);

        var messages = """[{"role":"user","content":"hi"}]""";
        var result = await client.EnginePrefillAsync(slotId: 0, model: "balanced",
            requestJson: $$"""{"messages":{{messages}}}""",
            traceId: "trace-prefill", ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(42, result!.NPast);
        Assert.Equal(1234567L, result.StateSize);
        Assert.Equal("balanced", result.ModelAlias);
        Assert.Equal("deadbeef00000000000000000000000000000000000000000000000000000000", result.ModelHash);
        Assert.Equal("/models/Balanced.gguf", result.ModelPath);
        Assert.False(result.ModelFallback);

        // Verify the wire payload: model key was injected into the request,
        // and the slot id went in as the RPC key (not in the JSON body).
        Assert.NotNull(rpc.LastRequest);
        var sent = Encoding.UTF8.GetString(rpc.LastRequest!.Value.Payload.Span);
        Assert.Contains("\"model\":\"balanced\"", sent);
        Assert.Equal(OpCode.EnginePrefill, rpc.LastRequest!.Value.Op);
        Assert.Equal("0", rpc.LastRequest!.Value.Key);
    }

    [Fact]
    public async Task EnginePrefillAsync_OmitsModelKeyWhenNull()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse("""{"n_past":1,"state_size":0,"model_fallback":false}""");
        var client = new HydraEngineClient(rpc);

        var messages = """[{"role":"user","content":"hi"}]""";
        var result = await client.EnginePrefillAsync(slotId: 0, model: null,
            requestJson: $$"""{"messages":{{messages}}}""",
            traceId: "trace-prefill", ct: CancellationToken.None);

        Assert.NotNull(result);
        var sent = Encoding.UTF8.GetString(rpc.LastRequest!.Value.Payload.Span);
        // The model key must NOT be present — the engine treats absent as
        // "use the current resident model" and a literal "" would be a
        // contract violation.
        Assert.DoesNotContain("\"model\"", sent);
    }

    [Fact]
    public async Task EnginePrefillAsync_OmitsModelKeyWhenEmpty()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse("""{"n_past":1,"state_size":0,"model_fallback":false}""");
        var client = new HydraEngineClient(rpc);

        var messages = """[{"role":"user","content":"hi"}]""";
        var result = await client.EnginePrefillAsync(slotId: 0, model: "",
            requestJson: $$"""{"messages":{{messages}}}""",
            traceId: "trace-prefill", ct: CancellationToken.None);

        Assert.NotNull(result);
        var sent = Encoding.UTF8.GetString(rpc.LastRequest!.Value.Payload.Span);
        Assert.DoesNotContain("\"model\"", sent);
    }

    [Fact]
    public async Task EnginePrefillAsync_PrefersExistingModelKeyInRequest()
    {
        // When the caller has already put a `model` in the JSON body, we
        // must not overwrite it — the model value is passed through as-is.
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse("""{"n_past":1,"state_size":0}""");
        var client = new HydraEngineClient(rpc);

        var bodyWithModel = """{"model":"caller-set","messages":[{"role":"user","content":"hi"}]}""";
        var result = await client.EnginePrefillAsync(slotId: 0, model: "should-not-override",
            requestJson: bodyWithModel, traceId: "trace", ct: CancellationToken.None);

        Assert.NotNull(result);
        var sent = Encoding.UTF8.GetString(rpc.LastRequest!.Value.Payload.Span);
        Assert.Contains("\"model\":\"caller-set\"", sent);
        Assert.DoesNotContain("should-not-override", sent);
    }

    [Fact]
    public async Task EnginePrefillAsync_PropagatesModelFallback()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse(
            """{"n_past":10,"state_size":100,"model_alias":"balanced","model_hash":"abc","model_path":"/x","model_fallback":true}""");
        var client = new HydraEngineClient(rpc);

        var messages = """[{"role":"user","content":"hi"}]""";
        var result = await client.EnginePrefillAsync(slotId: 0, model: "unknown_alias",
            requestJson: $$"""{"messages":{{messages}}}""",
            traceId: "trace", ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.ModelFallback);
        Assert.Equal("balanced", result.ModelAlias); // engine reported the actual model used
    }

    [Fact]
    public async Task EnginePrefillAsync_ReturnsNull_OnNonOkResponse()
    {
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = new RpcResponse((byte)StatusCode.Error, "nope", Array.Empty<byte>());
        var client = new HydraEngineClient(rpc);

        var messages = """[{"role":"user","content":"hi"}]""";
        var result = await client.EnginePrefillAsync(slotId: 0, model: "x",
            requestJson: $$"""{"messages":{{messages}}}""",
            traceId: "trace", ct: CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnginePrefillAsync_DefaultsModelFieldsToEmpty_WhenMetaOmits()
    {
        // Pre-feature build: meta has no model fields. Result still parses,
        // fields are empty strings (back-compat).
        var rpc = new CapturingRpcClient();
        rpc.NextResponse = MakeRpcResponse("""{"n_past":5,"state_size":50}""");
        var client = new HydraEngineClient(rpc);

        var messages = """[{"role":"user","content":"hi"}]""";
        var result = await client.EnginePrefillAsync(slotId: 0, model: null,
            requestJson: $$"""{"messages":{{messages}}}""",
            traceId: "trace", ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("", result!.ModelAlias);
        Assert.Equal("", result.ModelHash);
        Assert.Equal("", result.ModelPath);
        Assert.False(result.ModelFallback);
    }
}

/// <summary>
/// Minimal RpcClient stand-in for testing HydraEngineClient without a real
/// TCP connection. Records the last request and returns a preset response.
/// </summary>
internal sealed class CapturingRpcClient : RpcClient
{
    public RpcResponse NextResponse { get; set; } = new((byte)StatusCode.Ok, "", Array.Empty<byte>());
    public (OpCode Op, string Key, ReadOnlyMemory<byte> Payload, string Trace)? LastRequest { get; private set; }

    public CapturingRpcClient() : base("127.0.0.1", 0) { }

    public override async Task<RpcResponse> RequestAsync(
        OpCode op, string key, ReadOnlyMemory<byte> payload, string traceId, CancellationToken ct)
    {
        LastRequest = (op, key, payload, traceId);
        return await Task.FromResult(NextResponse);
    }
}
