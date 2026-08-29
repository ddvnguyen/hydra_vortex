using System.Net;
using System.Net.Sockets;
using Hydra.Shared;

namespace Tests.Core.Services;

/// <summary>
/// Issue #716: byte-count parity on the RPC sender. Tests verify:
/// (a) WriteExactlyAsync happy path with real TCP loopback
/// (b) RpcShortWriteException typed properties for metric wiring
/// (c) Pre-write parity: header declares correct payload length
/// (d) WriteExactlyAsync cancellation
///
/// NOTE: The short-write failure path (sent == 0) cannot be reliably
/// triggered on a real TCP socket — .NET's TryCompleteSendTo loops
/// internally and only returns 0 on genuine peer EOF. The exception
/// type and properties are tested directly below.
/// </summary>
public sealed class WriteExactlyTests
{
    /// <summary>
    /// WriteExactlyAsync completes successfully when all bytes are written
    /// (healthy connection, single-iteration loop).
    /// </summary>
    [Fact]
    public async Task WriteExactlyAsync_AllBytesWritten_Completes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = AcceptAndDrain(listener);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        var data = new byte[1024];
        Random.Shared.NextBytes(data);

        // Should complete without throwing
        await RpcClient.WriteExactlyAsync(stream, data, CancellationToken.None);

        client.Close();
        await serverTask;
    }

    /// <summary>
    /// RpcShortWriteException carries structured properties (Op, Declared,
    /// Written, TotalShortWrites) that WorkerSchedulerV2 and WorkerSchedulerService
    /// catch on — NOT message text. This test verifies the contract.
    /// </summary>
    [Fact]
    public void RpcShortWriteException_Properties_AreCorrect()
    {
        var ex = new RpcShortWriteException("StatePut", 653_131_650L, 0L, 7);

        Assert.Equal("StatePut", ex.Op);
        Assert.Equal(653_131_650L, ex.Declared);
        Assert.Equal(0L, ex.Written);
        Assert.Equal(7, ex.TotalShortWrites);
        Assert.Contains("653131650", ex.Message);
        Assert.Contains("StatePut", ex.Message);
    }

    /// <summary>
    /// RpcShortWriteException with host/port overload constructs the expected message.
    /// </summary>
    [Fact]
    public void RpcShortWriteException_WithHostPort_MessageIncludesEndpoint()
    {
        var ex = new RpcShortWriteException("StatePut", "192.168.122.21", 9602, 1000L, 0L, 3);

        Assert.Contains("192.168.122.21:9602", ex.Message);
        Assert.Equal("StatePut", ex.Op);
        Assert.Equal(1000L, ex.Declared);
        Assert.Equal(0L, ex.Written);
        Assert.Equal(3, ex.TotalShortWrites);
    }

    /// <summary>
    /// RpcShortWriteException is a subclass of InvalidOperationException,
    /// so existing catch(Exception) blocks still work, while typed
    /// catch(RpcShortWriteException) can distinguish it for metrics.
    /// </summary>
    [Fact]
    public void RpcShortWriteException_IsSubclassOfInvalidOperationException()
    {
        var ex = new RpcShortWriteException("Test", 100L, 0L, 1);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    /// <summary>
    /// The request header declares the exact payload length — the framing
    /// layer never inflates or deflates the declared size.
    /// </summary>
    [Fact]
    public void RequestHeader_DeclaredPayloadLen_MatchesActualPayload()
    {
        var payloadSizes = new long[] { 0, 1, 255, 65536, 1_000_000, 653_131_650 };
        foreach (var size in payloadSizes)
        {
            var header = Protocol.CreateRequestHeader(OpCode.StatePut, 1, (ulong)size, 4);
            Assert.Equal((ulong)size, header.PayloadLen);
        }
    }

    /// <summary>
    /// WriteExactlyAsync handles cancellation by throwing OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task WriteExactlyAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = AcceptAndHold(listener);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        // Fill the send buffer to make the write block
        var data = new byte[16 * 1024 * 1024]; // 16 MB
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RpcClient.WriteExactlyAsync(stream, data, cts.Token));

        client.Close();
        await serverTask;
    }

    // ── Test helpers ──

    private static async Task AcceptAndDrain(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buf = new byte[65536];
        while (await stream.ReadAsync(buf) > 0) { }
        listener.Stop();
    }

    private static async Task AcceptAndHold(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await Task.Delay(TimeSpan.FromSeconds(10));
        listener.Stop();
    }
}
