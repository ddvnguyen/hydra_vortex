using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Xunit;

namespace Tests.EngineParity;

/// <summary>
/// Port of tests/e1_rpc_test.py — walks the full engine RPC lifecycle
/// (INFO → CONFIGURE → PREFILL → STATE_META → DECODE → SET_EXPERT_MODE)
/// against TestRpcServer, using the canonical Hydra.Shared.RpcClient instead
/// of hand-rolled struct.pack.
/// </summary>
public sealed class PortedERpcTest : IAsyncLifetime
{
    private TestRpcServer? _server;

    public async Task InitializeAsync()
    {
        _server = new TestRpcServer(0);
        _ = Task.Run(() => _server.RunAsync(CancellationToken.None));
        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task Test1_EngineInfo_ReturnsOkWithMetadata()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.EngineInfoAsync("0", "trace-info", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.NotNull(resp.Meta);
        // TestRpcServer echoes: {"op":"EngineInfo","key":"0","trace":"trace-info"}
        var doc = JsonDocument.Parse(resp.Meta);
        Assert.True(doc.RootElement.TryGetProperty("op", out var opEl),
            $"INFO metadata missing 'op' key: {resp.Meta}");
        Assert.Equal("EngineInfo", opEl.GetString());
    }

    [Fact]
    public async Task Test2_EngineConfigure_ReturnsOk()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var config = """{"test":"value"}""";
        var resp = await client.EngineConfigureAsync("0", config,
            "trace-cfg", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
    }

    [Fact]
    public async Task Test3_EnginePrefill_EchoesPayloadAsUtf8Json()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var requestJson = """{"messages":[{"role":"user","content":"hi"}]}""";
        var resp = await client.EnginePrefillAsync("0", requestJson,
            "trace-prefill", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        // TestRpcServer echoes the payload back
        var echoed = Encoding.UTF8.GetString(resp.Payload);
        Assert.Equal(requestJson, echoed);
    }

    [Fact]
    public async Task Test4_StateMeta_ReturnsOk()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.RequestAsync(
            OpCode.StateMeta, "0", ReadOnlyMemory<byte>.Empty,
            "trace-statemeta", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.NotNull(resp.Meta);
    }

    [Fact]
    public async Task Test5_EngineDecode_ReturnsOk()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.EngineDecodeAsync(
            "0", nPredict: 10,
            requestJson: """[{"role":"user","content":"hi"}]""",
            "trace-decode", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
    }

    [Fact]
    public async Task Test6_EngineSetExpertMode_ReturnsOk()
    {
        Assert.NotNull(_server);
        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.EngineSetExpertModeAsync("0", "solo",
            "trace-expert", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        // TestRpcServer echoes the payload back
        Assert.Equal("solo", Encoding.UTF8.GetString(resp.Payload));
    }
}
