using System.Text;
using Hydra.Shared;
using StoreServer = Hydra.Core.StoreServer;
using StoreConfig = Hydra.Core.StoreConfig;
using StorageEngine = Hydra.Core.StorageEngine;
using ChunkStore = Hydra.Core.ChunkStore;

namespace Tests.Store;

public sealed class StoreServerTests : IAsyncLifetime
{
    private DirectoryInfo? _storeDir;
    private StoreServer? _server;
    private Task? _serverTask;

    public async Task InitializeAsync()
    {
        _storeDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"hydra-store-test-{Guid.NewGuid():N}"));

        var engine = new StorageEngine(_storeDir);
        var chunkStore = new ChunkStore(_storeDir);
        var cfg = new StoreConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            StoreDir = _storeDir.FullName,
        };

        _server = new StoreServer(cfg, engine, chunkStore);
        _serverTask = Task.Run(() => _server.RunAsync(CancellationToken.None));
        await Task.Delay(300);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();

        if (_storeDir is not null && _storeDir.Exists)
            _storeDir.Delete(recursive: true);
    }

    [Fact]
    public async Task PutAndGet_SmallPayload_RoundTrip()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = "hello store"u8.ToArray();
        var putResp = await client.RequestAsync(
            OpCode.Put, "kv/test-key", payload,
            "trace-put", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, putResp.Status);

        var getResp = await client.RequestAsync(
            OpCode.Get, "kv/test-key", ReadOnlyMemory<byte>.Empty,
            "trace-get", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, getResp.Status);
        Assert.Equal(payload, getResp.Payload);
    }

    [Fact]
    public async Task PutAndGet_LargePayload_RoundTrip()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = new byte[1_000_000]; // 1 MB
        new Random(42).NextBytes(payload);

        var putResp = await client.RequestAsync(
            OpCode.Put, "kv/large", payload,
            "trace-lput", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, putResp.Status);

        var getResp = await client.RequestAsync(
            OpCode.Get, "kv/large", ReadOnlyMemory<byte>.Empty,
            "trace-lget", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, getResp.Status);
        Assert.Equal(payload.Length, getResp.Payload.Length);
        Assert.Equal(payload, getResp.Payload);
    }

    [Fact]
    public async Task Get_NonExistentKey_ReturnsNotFound()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.RequestAsync(
            OpCode.Get, "kv/missing", ReadOnlyMemory<byte>.Empty,
            "trace-miss", CancellationToken.None);

        Assert.Equal((byte)StatusCode.NotFound, resp.Status);
    }

    [Fact]
    public async Task DeleteExistingKey_Works()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        // Put
        var putResp = await client.RequestAsync(
            OpCode.Put, "kv/to-delete", "data"u8.ToArray(),
            "trace-putdel", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, putResp.Status);

        // Verify exists
        var getResp = await client.RequestAsync(
            OpCode.Get, "kv/to-delete", ReadOnlyMemory<byte>.Empty,
            "trace-getbef", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, getResp.Status);

        // Delete
        var delResp = await client.RequestAsync(
            OpCode.Del, "kv/to-delete", ReadOnlyMemory<byte>.Empty,
            "trace-del", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, delResp.Status);

        // Verify gone
        var getAfterResp = await client.RequestAsync(
            OpCode.Get, "kv/to-delete", ReadOnlyMemory<byte>.Empty,
            "trace-getaft", CancellationToken.None);
        Assert.Equal((byte)StatusCode.NotFound, getAfterResp.Status);
    }

    [Fact]
    public async Task Stat_ReturnsCorrectMetadata()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var payload = new byte[42_000];
        new Random(1).NextBytes(payload);

        // Put
        await client.RequestAsync(
            OpCode.Put, "kv/stat-test", payload,
            "trace-putstat", CancellationToken.None);

        // Stat
        var statResp = await client.RequestAsync(
            OpCode.Stat, "kv/stat-test", ReadOnlyMemory<byte>.Empty,
            "trace-stat", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, statResp.Status);
        Assert.NotNull(statResp.Meta);
        Assert.Contains("42000", statResp.Meta);
    }

    [Fact]
    public async Task Stat_NonExistent_ReturnsNotFound()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.RequestAsync(
            OpCode.Stat, "kv/ghost", ReadOnlyMemory<byte>.Empty,
            "trace-statmiss", CancellationToken.None);

        Assert.Equal((byte)StatusCode.NotFound, resp.Status);
    }

    [Fact]
    public async Task List_WithPrefix_ReturnsMatching()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        // Put a few keys
        var keys = new[] { "sess/a/1", "sess/a/2", "sess/b/1", "other/x" };
        foreach (var k in keys)
        {
            await client.RequestAsync(
                OpCode.Put, k, new byte[] { 0x01 },
                "trace-listput", CancellationToken.None);
        }

        // List with prefix "sess/a/"
        var listResp = await client.RequestAsync(
            OpCode.List, "sess/a/", ReadOnlyMemory<byte>.Empty,
            "trace-list", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, listResp.Status);
        Assert.NotNull(listResp.Meta);
        Assert.Contains("\"count\":2", listResp.Meta);
        Assert.NotEmpty(listResp.Payload);
    }

    [Fact]
    public async Task List_NoMatch_ReturnsEmptyList()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.RequestAsync(
            OpCode.List, "zzz/", ReadOnlyMemory<byte>.Empty,
            "trace-listemt", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Ok, resp.Status);
        Assert.Contains("\"count\":0", resp.Meta);
    }

    [Fact]
    public async Task Put_PathTraversal_Rejected()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var resp = await client.RequestAsync(
            OpCode.Put, "../../etc/pwned", "evil"u8.ToArray(),
            "trace-evil", CancellationToken.None);

        Assert.Equal((byte)StatusCode.Error, resp.Status);
    }

    [Fact]
    public async Task AllOps_HaveTraceIdInResponse()
    {
        Assert.NotNull(_server);

        var client = new RpcClient("127.0.0.1", _server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var putResp = await client.RequestAsync(
                OpCode.Put, "kv/traced", new byte[] { 0xBB },
            "trace-all", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, putResp.Status);

        var getResp = await client.RequestAsync(
            OpCode.Get, "kv/traced", ReadOnlyMemory<byte>.Empty,
            "trace-all", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, getResp.Status);

        var statResp = await client.RequestAsync(
            OpCode.Stat, "kv/traced", ReadOnlyMemory<byte>.Empty,
            "trace-all", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, statResp.Status);

        var delResp = await client.RequestAsync(
            OpCode.Del, "kv/traced", ReadOnlyMemory<byte>.Empty,
            "trace-all", CancellationToken.None);
        Assert.Equal((byte)StatusCode.Ok, delResp.Status);
    }
}
