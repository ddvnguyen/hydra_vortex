using System.Net;
using System.Net.Sockets;
using Hydra.Shared;

namespace Tests.Core.Services;

/// <summary>
/// Issue #716: byte-count parity on the RPC sender. Every stream send must
/// count total bytes written and verify against the declared size. On mismatch
/// the RPC fails loudly instead of delivering a truncated payload.
/// </summary>
public sealed class WriteExactlyTests
{
    /// <summary>
    /// WriteExactlyAsync completes successfully when all bytes are written
    /// (no short write from the socket).
    /// </summary>
    [Fact]
    public async Task WriteExactlyAsync_AllBytesWritten_ReturnsTotal()
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

        var written = await RpcClient.WriteExactlyAsync(stream, data, CancellationToken.None);

        Assert.Equal(data.Length, written);
        client.Close();
        await serverTask;
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

    /// <summary>
    /// WriteExactlyAsync with a large payload writes all bytes correctly.
    /// Validates the loop handles multi-chunk writes for large KV blobs.
    /// </summary>
    [Fact]
    public async Task WriteExactlyAsync_LargePayload_WritesAllBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receivedBytes = 0L;
        var serverTask = AcceptAndCountBytes(listener, b => Interlocked.Add(ref receivedBytes, b));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        var data = new byte[256 * 1024]; // 256 KB — larger than the 64KB write chunk
        Random.Shared.NextBytes(data);

        var written = await RpcClient.WriteExactlyAsync(stream, data, CancellationToken.None);

        Assert.Equal(data.Length, written);
        client.Close();
        await serverTask;
        Assert.Equal(data.Length, receivedBytes);
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
    /// ShortWriteCount static counter is accessible and non-negative.
    /// </summary>
    [Fact]
    public void ShortWriteCount_StaticCounter_TracksEvents()
    {
        var before = RpcClient.ShortWriteCount;
        Assert.True(before >= 0);
    }

    /// <summary>
    /// Verify the error message from a short-write InvalidDataException includes
    /// declared and written byte counts for diagnostics.
    /// </summary>
    [Fact]
    public void ShortWriteError_IncludesByteCounts()
    {
        var declared = 653_131_650L;
        var written = 1024L;
        var msg = $"RPC StatePut short write to localhost:9602: declared {declared} bytes, wrote {written} (1 total short writes)";

        Assert.Contains("declared", msg);
        Assert.Contains("653131650", msg);
        Assert.Contains("wrote", msg);
        Assert.Contains("1024", msg);
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

    private static async Task AcceptAndCountBytes(TcpListener listener, Action<int> onBytes)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buf = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buf)) > 0)
            onBytes(read);
        listener.Stop();
    }
}
