using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Prometheus;

namespace Hydra.Store;

public sealed class StoreServer : RpcServer
{
	private readonly StoreConfig _cfg;
	private readonly StorageEngine _engine;
	private readonly ChunkStore _chunkStore;

	public StoreServer(StoreConfig cfg, StorageEngine engine, ChunkStore chunkStore)
		 : base(cfg.Host, cfg.Port)
	{
		_cfg = cfg;
		_engine = engine;
		_chunkStore = chunkStore;
	}

	protected override async Task HandleAsync(
		OpCode op, string key, string traceId, long payloadLen,
		PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
	{
		switch (op)
		{
			case OpCode.Put:
				await HandlePutAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.Get:
				await HandleGetAsync(key, writer, client, ct);
				break;
			case OpCode.Del:
				await HandleDelAsync(key, writer, ct);
				break;
			case OpCode.Stat:
				await HandleStatAsync(key, writer, ct);
				break;
			case OpCode.List:
				await HandleListAsync(key, writer, ct);
				break;
			case OpCode.PutChunked:
				await HandlePutChunkedAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.GetChunked:
				await HandleGetChunkedAsync(key, payloadLen, reader, writer, client, ct);
				break;
			case OpCode.SyncPlan:
				await HandleSyncPlanAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.PushChunks:
				await HandlePushChunksAsync(key, payloadLen, reader, writer, ct);
				break;
			default:
				await WriteErrorAsync(writer, $"Unknown opcode: {op}", ct);
				break;
		}
	}

	private async Task HandlePutAsync(
		string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("put").NewTimer();
		try
		{
			await _engine.PutAsync(key, reader, payloadLen, ct);
			StoreMetrics.OpsTotal.WithLabels("put").Inc();
			StoreMetrics.BytesStored.Inc(payloadLen);

			var meta = """{"stored":true}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);
			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
			await WriteMetaAsync(writer, meta, ct);
		}
		catch (InvalidDataException ex)
		{
			await WriteErrorAsync(writer, ex.Message, ct);
		}
	}

	private async Task HandleGetAsync(
		string key, PipeWriter writer, TcpClient client, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("get").NewTimer();
		var file = await _engine.GetAsync(key, ct);
		if (file is null)
		{
			StoreMetrics.OpsTotal.WithLabels("get_not_found").Inc();
			await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
			return;
		}

		var meta = $$"""{"size":{{file.Length}}}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);

		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)file.Length, ct);
		await WriteMetaAsync(writer, meta, ct);

		await SendFileAsync(client, file.FullName, ct);
		StoreMetrics.OpsTotal.WithLabels("get").Inc();
		StoreMetrics.BytesSent.Inc(file.Length);
	}

	private async Task HandleDelAsync(string key, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("del").NewTimer();
		var deleted = await _engine.DeleteAsync(key, ct);
		var meta = deleted ? """{"deleted":true}""" : """{"deleted":false}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);
		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
		await WriteMetaAsync(writer, meta, ct);
		StoreMetrics.OpsTotal.WithLabels("del").Inc(deleted ? 1 : 0);
	}

	private async Task HandleStatAsync(string key, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("stat").NewTimer();
		var stat = await _engine.StatAsync(key, ct);
		if (stat is null)
		{
			StoreMetrics.OpsTotal.WithLabels("stat_not_found").Inc();
			await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
			return;
		}

		var meta = $$"""{"name":"{{stat.Name}}","size":{{stat.Size}},"modified":"{{stat.LastModified:O}}"}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);
		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
		await WriteMetaAsync(writer, meta, ct);
		StoreMetrics.OpsTotal.WithLabels("stat").Inc();
	}

	private async Task HandleListAsync(string prefix, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("list").NewTimer();
		var files = new List<string>();
		await foreach (var f in _engine.ListAsync(prefix, ct))
			files.Add(f);
		StoreMetrics.OpsTotal.WithLabels("list").Inc();

		var payload = JsonSerializer.SerializeToUtf8Bytes(files);
		var meta = $$"""{"count":{{files.Count}}}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);

		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
		await WriteMetaAsync(writer, meta, ct);
		await writer.WriteAsync(payload, ct);
	}

	private async Task HandlePutChunkedAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("put_chunked").NewTimer();

		try
		{
			var chunks = new List<ChunkRef>();
			var totalNew = 0;

			await ChunkEngine.ChunkAndHashFromPipeAsync(reader, payloadLen,
				 async (chunkData, innerCt) =>
				 {
					 var hash = ChunkEngine.ComputeHash(chunkData);
					 var chunkRef = new ChunkRef(chunks.Count, hash, chunkData.Length);
					 chunks.Add(chunkRef);

					 if (_chunkStore.StoreChunk(hash, chunkData))
					 {
						 Interlocked.Increment(ref totalNew);
					 }
				 }, ct);

			var totalChunks = chunks.Count;
			var deduped = totalChunks - totalNew;

			var manifest = ChunkEngine.CreateManifest(key, 0, payloadLen, chunks);
			await _chunkStore.SaveManifestAsync(key, manifest, ct);

			StoreMetrics.OpsTotal.WithLabels("put_chunked").Inc();
			StoreMetrics.BytesStored.Inc(payloadLen);
			StoreMetrics.ChunksNew.Inc(totalNew);
			StoreMetrics.ChunksDeduped.Inc(deduped);

			var meta = $$"""{"new_chunks":{{totalNew}},"deduped_chunks":{{deduped}},"total_chunks":{{totalChunks}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);
			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
			await WriteMetaAsync(writer, meta, ct);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"put_chunked failed: {ex.Message}", ct);
		}
	}

	private async Task HandleGetChunkedAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("get_chunked").NewTimer();

		try
		{
			var manifest = await _chunkStore.LoadManifestAsync(key, ct);
			if (manifest is null)
			{
				StoreMetrics.OpsTotal.WithLabels("get_chunked_not_found").Inc();
				await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
				return;
			}

			List<string> knownHashes = [];
			if (payloadLen > 0)
			{
				var clientHashesJson = await ReadPayloadAsync(reader, payloadLen, ct);
				knownHashes = JsonSerializer.Deserialize<List<string>>(clientHashesJson) ?? [];
			}

			var missingHashes = _chunkStore.DiffPlan(manifest, knownHashes);

			long totalSize = 0;
			var chunkDataList = new List<(string Hash, byte[] Data)>();

			foreach (var hash in missingHashes)
			{
				var path = _chunkStore.GetChunkPath(hash);
				if (path is null) continue;
				var data = await File.ReadAllBytesAsync(path, ct);
				totalSize += data.Length;
				chunkDataList.Add((hash, data));
			}

			var missingMeta = JsonSerializer.SerializeToUtf8Bytes(new
			{
				missing_count = missingHashes.Count,
				total_size = totalSize,
				missing_hashes = missingHashes
			});

			var meta = $$"""{"missing_count":{{missingHashes.Count}},"total_size":{{totalSize}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);

			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)totalSize, ct);
			await WriteMetaAsync(writer, meta, ct);

			foreach (var (_, data) in chunkDataList)
			{
				await writer.WriteAsync(data, ct);
			}

			await writer.FlushAsync(ct);

			StoreMetrics.OpsTotal.WithLabels("get_chunked").Inc();
			StoreMetrics.BytesSent.Inc(totalSize);
			StoreMetrics.ChunksSent.Inc(missingHashes.Count);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"get_chunked failed: {ex.Message}", ct);
		}
	}

	private async Task HandleSyncPlanAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("sync_plan").NewTimer();

		try
		{
			var manifest = await _chunkStore.LoadManifestAsync(key, ct);
			if (manifest is null)
			{
				StoreMetrics.OpsTotal.WithLabels("sync_plan_not_found").Inc();
				await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
				return;
			}

			List<string> knownHashes = [];
			if (payloadLen > 0)
			{
				var clientHashesJson = await ReadPayloadAsync(reader, payloadLen, ct);
				knownHashes = JsonSerializer.Deserialize<List<string>>(clientHashesJson) ?? [];
			}

			var missingHashes = _chunkStore.DiffPlan(manifest, knownHashes);

			long totalSize = 0;
			foreach (var hash in missingHashes)
			{
				var path = _chunkStore.GetChunkPath(hash);
				if (path is not null)
					totalSize += new FileInfo(path).Length;
			}

			var payload = JsonSerializer.SerializeToUtf8Bytes(new { missing_hashes = missingHashes });
			var meta = $$"""{"missing_count":{{missingHashes.Count}},"total_size":{{totalSize}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);

			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
			await WriteMetaAsync(writer, meta, ct);
			await writer.WriteAsync(payload, ct);

			StoreMetrics.OpsTotal.WithLabels("sync_plan").Inc();
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"sync_plan failed: {ex.Message}", ct);
		}
	}

	private async Task HandlePushChunksAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("push_chunks").NewTimer();

		try
		{
			var pushedBytes = await ReadPayloadAsync(reader, payloadLen, ct);
			var offset = 0;
			var stored = 0;

			while (offset < pushedBytes.Length)
			{
				if (offset + 4 > pushedBytes.Length) break;
				var chunkSize = pushedBytes[offset] | (pushedBytes[offset + 1] << 8) |
									 (pushedBytes[offset + 2] << 16) | (pushedBytes[offset + 3] << 24);
				offset += 4;

				if (offset + chunkSize > pushedBytes.Length) break;
				var chunkData = pushedBytes.AsSpan(offset, chunkSize).ToArray();
				offset += chunkSize;

				var hash = ChunkEngine.ComputeHash(chunkData);
				if (_chunkStore.StoreChunk(hash, chunkData))
					stored++;
			}

			var meta = $$"""{"stored":{{stored}},"total":{{payloadLen}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);
			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
			await WriteMetaAsync(writer, meta, ct);

			StoreMetrics.OpsTotal.WithLabels("push_chunks").Inc();
			StoreMetrics.BytesStored.Inc(payloadLen);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"push_chunks failed: {ex.Message}", ct);
		}
	}

	private async Task<int> RunGCAsync(HashSet<string> keepSessions)
	{
		var removed = _chunkStore.GC(keepSessions);
		if (removed > 0)
			StoreMetrics.ChunksRemoved.Inc(removed);
		return removed;
	}

	private static async Task WriteErrorAsync(PipeWriter writer, string message, CancellationToken ct, StatusCode status = StatusCode.Error)
	{
		var meta = $$"""{"error":"{{message}}"}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);
		await WriteResponseHeaderAsync(writer, (byte)status, (uint)metaBytes.Length, 0, ct);
		await WriteMetaAsync(writer, meta, ct);
	}

	public Task StartDebugEndpointAsync(CancellationToken ct)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, $"http://{_cfg.Host}:{_cfg.DebugHttpPort}");

		var app = builder.Build();

		app.MapGet("/debug", async (HttpContext ctx) =>
		{
			var storeStats = await _engine.GetDebugStatsAsync(ct);
			var chunkStats = await _chunkStore.GetStatsAsync(ct);
			return Results.Json(new
			{
				raw = storeStats,
				chunks = chunkStats
			});
		});

		app.MapPost("/debug/gc", async (HttpContext ctx) =>
		{
			var keepSessions = new HashSet<string>();
			var body = await ctx.Request.ReadFromJsonAsync<GcRequest>(ct);
			if (body?.KeepSessions is not null)
				keepSessions = [.. body.KeepSessions];

			var removed = _chunkStore.GC(keepSessions);
			if (removed > 0)
				StoreMetrics.ChunksRemoved.Inc(removed);

			return Results.Json(new { chunks_removed = removed });
		});

		app.UseMetricServer();

		ct.Register(async () => await app.StopAsync());
		return app.RunAsync();
	}
}

public sealed record GcRequest
{
	public List<string>? KeepSessions { get; init; }
}
