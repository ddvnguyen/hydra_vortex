using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Services;
using Hydra.Shared;
using Xunit;

namespace Tests.Core;

/// <summary>
/// Unit tests for <see cref="EngineConfigureResult"/> parsing and
/// <see cref="HydraEngineClient.ParseConfigureResponse"/>. Phase 2b of
/// ddvnguyen/llama.cpp#36 — wire schema from ddvnguyen/hydra_vortex#406.
/// </summary>
public class EngineConfigureResultTests
{
    [Fact]
    public void ParseConfigureResponse_T1_AllFields()
    {
        const string meta = """
            {
              "success": true,
              "tier": "T1",
              "params_applied": {
                "sampling.temp": 0.5,
                "sampling.top_p": 0.9,
                "n_predict": 256,
                "state_chunk_size": 2097152
              },
              "deferred_keys": []
            }
            """;
        var resp = MakeOkResponse(meta);
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.True(r.Success);
        Assert.Equal("T1", r.Tier);
        Assert.True(r.IsT1);
        Assert.False(r.HasDeferredChanges);
        Assert.Equal(4, r.ParamsApplied.Count);
        Assert.Contains("sampling.temp", r.ParamsApplied.Keys);
        Assert.Contains("state_chunk_size", r.ParamsApplied.Keys);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ParseConfigureResponse_T3_DeferredKeys()
    {
        const string meta = """
            {
              "success": true,
              "tier": "T3",
              "params_applied": { "state_chunk_size": 2097152 },
              "deferred_keys": ["n_gpu_layers", "split_mode", "tensor_split"]
            }
            """;
        var resp = MakeOkResponse(meta);
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.True(r.Success);
        Assert.True(r.IsT3);
        Assert.True(r.HasDeferredChanges);
        Assert.Equal(3, r.DeferredKeys.Count);
        Assert.Contains("split_mode", r.DeferredKeys);
    }

    [Fact]
    public void ParseConfigureResponse_LegacyStateChunkSizeEcho()
    {
        // Backward compat: legacy {state_chunk_size_applied: N} field
        // (hydra#334) is still parsed into StateChunkSizeApplied for
        // single-source-of-truth with ParamsApplied.
        const string meta = """
            {
              "success": true,
              "tier": "T1",
              "params_applied": { "state_chunk_size": 2097152 },
              "deferred_keys": [],
              "state_chunk_size_applied": 2097152
            }
            """;
        var resp = MakeOkResponse(meta);
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.True(r.Success);
        Assert.Equal(2097152, r.StateChunkSizeApplied);
    }

    [Fact]
    public void ParseConfigureResponse_FailureWithError()
    {
        const string meta = """{"success": false, "tier": "T1", "error": "invalid JSON"}""";
        var resp = MakeStatusResponse((byte)StatusCode.BadRequest, meta);
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.False(r.Success);
        Assert.Equal("invalid JSON", r.Error);
    }

    [Fact]
    public void ParseConfigureResponse_MalformedJson_ReturnsFailure()
    {
        var resp = MakeOkResponse("not valid json");
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.False(r.Success);
        Assert.Equal("malformed configure response", r.Error);
    }

    [Fact]
    public void ParseConfigureResponse_NonOkStatus_ReturnsFailure()
    {
        var resp = MakeStatusResponse((byte)StatusCode.Error, "");
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.False(r.Success);
    }

    [Fact]
    public void ParseConfigureResponse_EmptyMeta_OnNonOk()
    {
        var resp = MakeStatusResponse((byte)StatusCode.Error, "");
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ParseConfigureResponse_T2Tier()
    {
        const string meta = """
            { "success": true, "tier": "T2", "params_applied": {},
              "deferred_keys": ["n_ctx", "cache_type_k"] }
            """;
        var resp = MakeOkResponse(meta);
        var r = HydraEngineClient.ParseConfigureResponse(resp);
        Assert.True(r.IsT2);
        Assert.False(r.IsT1);
        Assert.False(r.IsT3);
    }

    private static RpcResponse MakeOkResponse(string meta) =>
        new RpcResponse((byte)StatusCode.Ok, meta, Array.Empty<byte>());

    private static RpcResponse MakeStatusResponse(byte status, string meta) =>
        new RpcResponse(status, meta, Array.Empty<byte>());
}
