using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hydra.Shared;

public abstract class RpcServer : IAsyncDisposable
{
	private readonly string _host;
	private readonly int _port;
	private TcpListener? _listener;
	private readonly CancellationTokenSource _stopCts = new();
	private readonly List<Task> _connections = [];
	private readonly object _connectionsLock = new();
	private bool _disposed;

	public int Port { get; private set; }
	public bool IsRunning => _listener is not null;

	protected RpcServer(string host, int port)
	{
		_host = host;
		_port = port;
	}

	public async Task RunAsync(CancellationToken ct)
	{
		var ip = IPAddress.Parse(_host);
		_listener = new TcpListener(ip, _port);
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
		var token = linkedCts.Token;

		while (!token.IsCancellationRequested)
		{
			TcpClient tcpClient;
			try
			{
				tcpClient = await _listener.AcceptTcpClientAsync(token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}

			var connTask = HandleConnectionAsync(tcpClient, token);
			lock (_connectionsLock)
			{
				_connections.Add(connTask);
			}
			_ = connTask.ContinueWith(_ => CleanConnections(), TaskScheduler.Default);
		}
	}

	private void CleanConnections()
	{
		lock (_connectionsLock)
		{
			_connections.RemoveAll(t => t.IsCompleted);
		}
	}

	private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken ct)
	{
		try
		{
			using (tcpClient)
			{
				var stream = tcpClient.GetStream();
				var readerOpts = new StreamPipeReaderOptions(MemoryPool<byte>.Shared, 65536, 65536, false);
				var writerOpts = new StreamPipeWriterOptions(MemoryPool<byte>.Shared, 65536, false);
				var reader = PipeReader.Create(stream, readerOpts);
				var writer = PipeWriter.Create(stream, writerOpts);

				var tmpHeaderBuf = new byte[Protocol.REQUEST_HEADER_SIZE];

				while (!ct.IsCancellationRequested)
				{
					var headerResult = await reader.ReadAtLeastAsync(Protocol.REQUEST_HEADER_SIZE, ct);
					if (headerResult.IsCanceled || headerResult.IsCompleted)
						break;

					var headerBuf = headerResult.Buffer;
					if (headerBuf.Length < Protocol.REQUEST_HEADER_SIZE)
						break;

					RequestHeader header;
					if (headerBuf.FirstSpan.Length >= Protocol.REQUEST_HEADER_SIZE)
					{
						header = Protocol.ReadRequest(headerBuf.FirstSpan);
					}
					else
					{
						headerBuf.Slice(0, Protocol.REQUEST_HEADER_SIZE).CopyTo(tmpHeaderBuf);
						header = Protocol.ReadRequest(tmpHeaderBuf);
					}
					reader.AdvanceTo(headerBuf.GetPosition(Protocol.REQUEST_HEADER_SIZE));

					if (header.Magic != Protocol.MAGIC)
						break;

					var key = await ReadStringAsync(reader, header.KeyLen, ct);
					var traceId = await ReadStringAsync(reader, header.TraceLen, ct);

					await HandleAsync(
						 (OpCode)header.Op, key, traceId, (long)header.PayloadLen,
						 reader, writer, tcpClient, ct);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (InvalidDataException)
		{
		}
		catch (IOException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	protected static async Task<string> ReadStringAsync(PipeReader reader, int length, CancellationToken ct)
	{
		if (length <= 0)
			return string.Empty;

		var result = await reader.ReadAtLeastAsync(length, ct);
		var buf = result.Buffer;
		string str;
		if (buf.FirstSpan.Length >= length)
		{
			str = Encoding.UTF8.GetString(buf.FirstSpan[..length]);
		}
		else
		{
			var rented = ArrayPool<byte>.Shared.Rent(length);
			try
			{
				buf.Slice(0, length).CopyTo(rented);
				str = Encoding.UTF8.GetString(rented, 0, length);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(rented);
			}
		}
		reader.AdvanceTo(buf.GetPosition(length));
		return str;
	}

	public static async Task<byte[]> ReadPayloadAsync(PipeReader reader, long payloadLen, CancellationToken ct)
	{
		if (payloadLen <= 0)
			return [];

		if (payloadLen > Array.MaxLength)
			throw new ArgumentOutOfRangeException(nameof(payloadLen),
				$"ReadPayloadAsync is for small JSON bodies only (max {Array.MaxLength} bytes); got {payloadLen}. Stream large payloads via pipe instead.");

		var data = new byte[payloadLen];
		int offset = 0;
		while (offset < data.Length)
		{
			var remaining = payloadLen - offset;
			var result = await reader.ReadAtLeastAsync((int)remaining, ct);
			var buf = result.Buffer;
			var toCopy = Math.Min(buf.Length, data.Length - offset);
			buf.Slice(0, toCopy).CopyTo(data.AsSpan(offset));
			offset += (int)toCopy;
			reader.AdvanceTo(buf.GetPosition(toCopy));
		}
		return data;
	}

	public static async Task WriteResponseHeaderAsync(
		 PipeWriter writer, byte status, uint metaLen, ulong payloadLen, CancellationToken ct)
	{
		var span = writer.GetSpan(Protocol.RESPONSE_HEADER_SIZE);
		Protocol.WriteResponse(span, status, metaLen, payloadLen);
		writer.Advance(Protocol.RESPONSE_HEADER_SIZE);
		await writer.FlushAsync(ct);
	}

	public static async Task WriteMetaAsync(PipeWriter writer, string meta, CancellationToken ct)
	{
		var bytes = Encoding.UTF8.GetBytes(meta);
		var span = writer.GetSpan(bytes.Length);
		bytes.CopyTo(span);
		writer.Advance(bytes.Length);
		await writer.FlushAsync(ct);
	}

	public static async Task WritePayloadAsync(PipeWriter writer, ReadOnlyMemory<byte> payload, CancellationToken ct)
	{
		if (payload.Length == 0)
			return;
		var span = writer.GetSpan(payload.Length);
		payload.Span.CopyTo(span);
		writer.Advance(payload.Length);
		await writer.FlushAsync(ct);
	}

	protected static async Task SendFileAsync(TcpClient client, string filePath, CancellationToken ct)
	{
		var socket = client.Client;
		await socket.SendFileAsync(filePath, ct);
	}

	protected abstract Task HandleAsync(
		 OpCode op, string key, string traceId, long payloadLen,
		 PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct);

	public async ValueTask DisposeAsync()
	{
		lock (_connectionsLock)
		{
			if (_disposed) return;
			_disposed = true;
		}

		await _stopCts.CancelAsync();
		_listener?.Stop();

		Task? waitTask;
		lock (_connectionsLock)
		{
			waitTask = _connections.Count > 0 ? Task.WhenAll(_connections) : null;
		}

		if (waitTask is not null)
		{
			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			try
			{
				await Task.WhenAll(_connections).WaitAsync(timeoutCts.Token);
			}
			catch (TimeoutException)
			{
			}
		}

		_stopCts.Dispose();
	}

	private readonly object _connectLock = new();
}
