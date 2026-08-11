using System.Collections.Concurrent;
using Hydra.Shared;

namespace Tests.Core.TestHelpers;

/// <summary>
/// Minimal fake for <see cref="RpcClient"/> that records calls and returns
/// configurable per-<see cref="OpCode"/> responses. Supports setting an
/// exception to throw for a given op.
/// </summary>
public sealed class FakeStoreClient : RpcClient
{
	private readonly ConcurrentDictionary<OpCode, (byte Status, string? Meta, byte[] Payload)> _responses = new();
	private readonly ConcurrentDictionary<OpCode, Exception?> _exceptions = new();
	private readonly ConcurrentBag<(OpCode Op, string Key, int PayloadLen)> _calls = new();

	public FakeStoreClient() : base("test", 0) { }

	/// <summary>All calls recorded (op, key, payload length).</summary>
	public IReadOnlyCollection<(OpCode Op, string Key, int PayloadLen)> Calls => _calls.ToArray();

	/// <summary>Number of calls with the given op.</summary>
	public int CallCount(OpCode op) => _calls.Count(c => c.Op == op);

	/// <summary>Set a fixed response for an op. When set, <see cref="RequestAsync"/>
	/// returns this instead of the default Ok. <paramref name="payload"/> is the
	/// response payload (defaults to empty — e.g. a store Get KV blob).</summary>
	public void SetResponse(OpCode op, byte status, string? meta = null, byte[]? payload = null)
		=> _responses[op] = (status, meta, payload ?? Array.Empty<byte>());

	/// <summary>Set an exception to throw for an op. When set, <see cref="RequestAsync"/>
	/// throws instead of returning a response.</summary>
	public void SetException(OpCode op, Exception ex)
		=> _exceptions[op] = ex;

	public override Task<RpcResponse> RequestAsync(
		OpCode op, string key, ReadOnlyMemory<byte> payload,
		string traceId, CancellationToken ct)
	{
		_calls.Add((op, key, payload.Length));

		if (_exceptions.TryGetValue(op, out var ex) && ex != null)
			throw ex;

		if (_responses.TryGetValue(op, out var r))
			return Task.FromResult(new RpcResponse(r.Status, r.Meta, r.Payload));

		return Task.FromResult(new RpcResponse(
			(byte)StatusCode.Ok, null, []));
	}
}
