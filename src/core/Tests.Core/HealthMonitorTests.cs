using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Core.Models;
using Hydra.Core.Repositories;
using Hydra.Core.Services;
using Hydra.Shared;
using Xunit;

namespace Tests.Core;

// ═══════════════════════════════════════════════════════════════════════
// #635 fix 1: RPC-aware worker health.
//
// Live repro (smoke #8, 2026-08-12): the rtx engine ggml_abort'd on a stale
// ggml-RPC socket, yet the health monitor kept reporting Healthy=True —
// health_poll_engine_info_failed was a best-effort WARN and a dead RPC/prefill
// path never flipped the flag, so the scheduler dispatched cold_atomic into the
// zombie (observed: cold_atomic_try Est=471 Free=True Healthy=True).
//
// Fix under test: the EngineInfo (0x41) RPC failing N consecutive polls while
// HTTP /slots succeeds marks the node UNHEALTHY (the HTTP-alive-but-RPC-dead
// signature of a crashed engine). One successful INFO RPC (engine restarted)
// resets the counter and flips the node back healthy.
// ═══════════════════════════════════════════════════════════════════════

public sealed class HealthMonitorTests
{
	private sealed class StubHttpClientFactory : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new();
	}

	/// <summary>RPC double for the EngineInfo health poll: succeeds (with or
	/// without preset aliases + capabilities) or throws ConnectionRefused.
	/// withCaps:false models a node redeployed with an engine build that no
	/// longer advertises merged_decode — INFO succeeds, capability set empty.</summary>
	private sealed class EngineInfoRpcStub : RpcClient
	{
		private readonly bool _succeed;
		private readonly bool _withCaps;
		public EngineInfoRpcStub(bool succeed, bool withCaps = true) : base("test", 0)
		{
			_succeed = succeed;
			_withCaps = withCaps;
		}

		public override Task<RpcResponse> RequestAsync(
			OpCode op, string key, ReadOnlyMemory<byte> payload,
			string traceId, CancellationToken ct, TimeSpan? requestTimeoutOverride, TimeSpan? payloadIdleBudget)
		{
			if (!_succeed)
				throw new SocketException((int)SocketError.ConnectionRefused);
			var meta = JsonSerializer.Serialize(new
			{
				preset_aliases = _withCaps ? new[] { "nano" } : Array.Empty<string>(),
				capabilities = _withCaps ? new[] { Protocol.CapMergedDecode } : Array.Empty<string>(),
			});
			return Task.FromResult(new RpcResponse((byte)StatusCode.Ok, meta, []));
		}
	}

	/// <summary>Tiny loopback HTTP server serving the two endpoints the health
	/// poll hits: /slots (one idle slot) and /health.</summary>
	private sealed class FakeEngineHttpServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly CancellationTokenSource _cts = new();
		private Task _acceptLoop = Task.CompletedTask;
		public int Port { get; }

		public FakeEngineHttpServer()
		{
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		}

		public void Start()
		{
			_acceptLoop = Task.Run(AcceptLoopAsync);
		}

		private async Task AcceptLoopAsync()
		{
			while (!_cts.IsCancellationRequested)
			{
				TcpClient client;
				try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
				catch (OperationCanceledException) { return; }
				_ = Task.Run(() => ServeAsync(client, _cts.Token));
			}
		}

		private static async Task ServeAsync(TcpClient client, CancellationToken ct)
		{
			try
			{
				using (client)
				using (var stream = client.GetStream())
				{
					var buf = new byte[4096];
					var read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
					var request = Encoding.ASCII.GetString(buf, 0, read);
					var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";

					string body;
					string contentType;
					if (path == "/slots")
					{
						body = """[{"id":0,"is_processing":false,"n_past":0,"n_remain":0}]""";
						contentType = "application/json";
					}
					else if (path == "/health")
					{
						body = "OK";
						contentType = "text/plain";
					}
					else
					{
						body = "not found";
						contentType = "text/plain";
						var notFound = Encoding.UTF8.GetBytes($"HTTP/1.1 404 Not Found\r\nContent-Length: {body.Length}\r\n\r\n{body}");
						await stream.WriteAsync(notFound, ct);
						return;
					}

					var bodyBytes = Encoding.UTF8.GetBytes(body);
					var response = Encoding.UTF8.GetBytes(
						$"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\n\r\n{body}");
					await stream.WriteAsync(response, ct);
				}
			}
			catch { /* client disconnected — ignore */ }
		}

		public async ValueTask DisposeAsync()
		{
			_cts.Cancel();
			_listener.Stop();
			try { await _acceptLoop; } catch { }
			_cts.Dispose();
		}
	}

	private static (HealthMonitorService Health, CoordinatorConfig Cfg, WorkerTracker Tracker, FakeEngineHttpServer Server) CreateMonitor(
		Func<bool> engineInfoSucceeds)
	{
		var server = new FakeEngineHttpServer();
		server.Start();

		var cfg = new CoordinatorConfig
		{
			HealthPollIntervalS = 1,
			HealthPollTimeoutS = 5,
			StuckSlotCycles = 3,
			Workers = new List<WorkerConfig>
			{
				new()
				{
					Name = "rtx", Host = "127.0.0.1", RpcPort = 1, // dead port — factory overrides the client anyway
					LlamaUrl = $"http://127.0.0.1:{server.Port}",
					WorkerType = 3, Slots = 1, PrefillPriority = 1, DecodePriority = 1,
				},
			}
		};
		var tracker = new WorkerTracker();
		tracker.InitWorker("rtx", 1);

		var health = new HealthMonitorService(cfg, cfg.Workers, tracker,
			new StubHttpClientFactory(), Serilog.Log.Logger);
		health.EngineInfoRpcClientFactory = (_, _) => new EngineInfoRpcStub(engineInfoSucceeds());
		return (health, cfg, tracker, server);
	}

	[Fact]
	public async Task RepeatedEngineInfoRpcFailures_FlipNodeUnhealthy_AfterThreshold()
	{
		// EngineInfo RPC always fails; /slots + /health always succeed — the
		// exact HTTP-alive-but-RPC-dead crash signature from smoke #8.
		var (health, _, _, server) = CreateMonitor(() => false);
		await using var _ = server;

		// 1-2 failures: still healthy (transient RPC blips must not flip).
		await health.PollForTestAsync(CancellationToken.None);
		Assert.True(health.IsHealthy("rtx"), "one RPC failure must not flip health");
		await health.PollForTestAsync(CancellationToken.None);
		Assert.True(health.IsHealthy("rtx"), "two RPC failures must not flip health");

		// 3rd consecutive failure: node flips unhealthy.
		await health.PollForTestAsync(CancellationToken.None);
		Assert.False(health.IsHealthy("rtx"),
			"3 consecutive EngineInfo RPC failures with a working HTTP poll must mark the node unhealthy");
		var info = health.GetNodeInfo("rtx")!;
		Assert.Equal(3, info.RpcConsecutiveFailures);
		Assert.Equal(1, info.SlotsTotal); // slots still observed — only the RPC path is dead
	}

	[Fact]
	public async Task EngineInfoRecovery_ResetsCounter_AndFlipsHealthy()
	{
		// Engine down for 3 polls → unhealthy.
		var (health, _, _, server) = CreateMonitor(() => false);
		await using var _ = server;
		for (var i = 0; i < 3; i++)
			await health.PollForTestAsync(CancellationToken.None);
		Assert.False(health.IsHealthy("rtx"));

		// Engine restarts: INFO RPC now succeeds → counter resets, node flips back.
		health.EngineInfoRpcClientFactory = (_, _) => new EngineInfoRpcStub(succeed: true);
		await health.PollForTestAsync(CancellationToken.None);

		Assert.True(health.IsHealthy("rtx"), "successful EngineInfo RPC must restore health");
		Assert.Equal(0, health.GetNodeInfo("rtx")!.RpcConsecutiveFailures);
	}

	[Fact]
	public async Task RpcFailures_FireHealthyChanged_OnlyOnRealFlip()
	{
		var (health, _, _, server) = CreateMonitor(() => false);
		await using var _ = server;
		var flips = 0;
		health.HealthyChanged += () => flips++;

		// First two failures: no flip (still healthy) → no event.
		await health.PollForTestAsync(CancellationToken.None);
		await health.PollForTestAsync(CancellationToken.None);
		Assert.Equal(0, flips);

		// Third failure: healthy→unhealthy flip fires exactly once.
		await health.PollForTestAsync(CancellationToken.None);
		Assert.Equal(1, flips);
		Assert.False(health.IsHealthy("rtx"));

		// Subsequent failures while already unhealthy: no additional flip.
		await health.PollForTestAsync(CancellationToken.None);
		Assert.Equal(1, flips);
	}

	[Fact]
	public async Task EngineInfoFailure_CarriesLastKnownCapabilities_Forward()
	{
		// #712: a single failed/empty 0x41 INFO poll must not silently drop the
		// node's last-known engine capabilities. PollWorkerAsync builds a FRESH
		// NodeInfo each cycle, so without carry-forward one bad poll (observed:
		// RPC channel busy behind a multi-hundred-MB state transfer) flips the
		// next decode off the merged-decode path with no log line at all — the
		// A/B T4 turn then hit the HTTP fallback and a destructive T3 model
		// rebuild attempt wiped the restored KV (144s TTFT vs ~27s expected).
		var infoSucceeds = true;
		var (health, _, _, server) = CreateMonitor(() => infoSucceeds);
		await using var _ = server;

		// Poll 1: INFO OK → capabilities + preset aliases learned.
		await health.PollForTestAsync(CancellationToken.None);
		var first = health.GetNodeInfo("rtx")!;
		Assert.Contains(Protocol.CapMergedDecode, first.EngineCapabilities);
		Assert.Contains("nano", first.PresetAliases);

		// Poll 2: INFO fails (RPC busy). Node stays healthy (below threshold),
		// but the fresh NodeInfo would blank the capabilities — carry-forward
		// must preserve them so decode path selection stays stable.
		infoSucceeds = false;
		await health.PollForTestAsync(CancellationToken.None);
		var second = health.GetNodeInfo("rtx")!;
		Assert.True(second.Healthy, "one INFO failure is below the unhealthy threshold");
		Assert.True(second.EngineCapabilities.Contains(Protocol.CapMergedDecode),
			"last-known capabilities must survive a failed INFO poll");
		Assert.True(second.PresetAliases.Contains("nano"),
			"last-known preset aliases must survive a failed INFO poll");

		// Poll 3: INFO recovers → the real advertisement wins again.
		infoSucceeds = true;
		await health.PollForTestAsync(CancellationToken.None);
		Assert.Contains(Protocol.CapMergedDecode, health.GetNodeInfo("rtx")!.EngineCapabilities);
	}

	[Fact]
	public async Task EngineInfoSucceedsWithEmptyCaps_ClearsStaleCapabilities()
	{
		// #712 review finding 4: carry-forward must be gated on the INFO query
		// FAILING. A node redeployed with an engine build that no longer
		// advertises merged_decode returns an EMPTY cap set on a SUCCESSFUL
		// INFO — carrying the stale capability forward would route 0x43 into
		// a gateRejected 503 with no fallback. Successful empty = authoritative.
		var (health, _, _, server) = CreateMonitor(() => true);
		await using var _ = server;

		// Poll 1: INFO OK with capabilities → learned.
		await health.PollForTestAsync(CancellationToken.None);
		Assert.Contains(Protocol.CapMergedDecode, health.GetNodeInfo("rtx")!.EngineCapabilities);

		// Poll 2: INFO succeeds but advertises NOTHING (build without merged_decode).
		health.EngineInfoRpcClientFactory = (_, _) => new EngineInfoRpcStub(succeed: true, withCaps: false);
		await health.PollForTestAsync(CancellationToken.None);

		var second = health.GetNodeInfo("rtx")!;
		Assert.True(second.EngineCapabilities.Count == 0,
			"a successful INFO with an empty cap set must clear stale capabilities");
		Assert.True(second.PresetAliases.Count == 0);
	}
}
